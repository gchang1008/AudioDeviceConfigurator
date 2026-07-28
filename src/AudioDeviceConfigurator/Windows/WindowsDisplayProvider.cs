using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AudioDeviceConfigurator.Abstractions;
using Microsoft.Win32;

namespace AudioDeviceConfigurator.Windows;

/// <summary>
/// Enumerates currently ACTIVE display paths through the Connecting and Configuring Displays API,
/// then reads each monitor's EDID from its own device key. Historical registry records are never
/// used as a fallback, so a stale monitor cannot be tested by accident.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDisplayProvider : IDisplayProvider
{
    private const int ErrorSuccess = 0;
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const int DisplayconfigDeviceInfoGetTargetName = 2;

    public IReadOnlyList<DisplayInfo> GetActiveDisplays()
    {
        var status = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount);
        if (status != ErrorSuccess)
        {
            throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed with error {status}.");
        }

        var paths = new DisplayconfigPathInfo[pathCount];
        var modes = new DisplayconfigModeInfo[modeCount];
        status = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (status != ErrorSuccess)
        {
            throw new InvalidOperationException($"QueryDisplayConfig failed with error {status}.");
        }

        var displays = new List<DisplayInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < pathCount; i++)
        {
            var targetName = new DisplayconfigTargetDeviceName
            {
                header = new DisplayconfigDeviceInfoHeader
                {
                    type = DisplayconfigDeviceInfoGetTargetName,
                    size = Marshal.SizeOf<DisplayconfigTargetDeviceName>(),
                    adapterId = paths[i].targetInfo.adapterId,
                    id = paths[i].targetInfo.id,
                },
            };

            if (DisplayConfigGetDeviceInfo(ref targetName) != ErrorSuccess)
            {
                continue;
            }

            var devicePath = targetName.monitorDevicePath;
            if (string.IsNullOrEmpty(devicePath) || !seen.Add(devicePath))
            {
                continue;
            }

            var edid = ReadEdidForDevicePath(devicePath);
            if (edid is null)
            {
                throw new InvalidOperationException(
                    $"Unable to read EDID for the active display '{devicePath}'.");
            }

            var adapterName = GetAdapterName(paths[i].sourceInfo.adapterId, paths[i].sourceInfo.id);

            displays.Add(new DisplayInfo(
                MonitorId: devicePath,
                FriendlyName: string.IsNullOrWhiteSpace(targetName.monitorFriendlyDeviceName)
                    ? devicePath
                    : targetName.monitorFriendlyDeviceName,
                AdapterName: adapterName,
                GpuDriverVersion: adapterName is null ? null : GetGpuDriverVersion(adapterName),
                RawEdid: edid));
        }

        return displays;
    }

    /// <summary>
    /// A monitor device path looks like \\?\DISPLAY#DEL4321#5&amp;abc&amp;0&amp;UID256#{guid}. The middle
    /// components map directly onto the SYSTEM\CurrentControlSet\Enum\DISPLAY key holding the EDID.
    /// </summary>
    private static byte[]? ReadEdidForDevicePath(string devicePath)
    {
        var trimmed = devicePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? devicePath[4..] : devicePath;
        var parts = trimmed.Split('#');
        if (parts.Length < 3)
        {
            return null;
        }

        var keyPath = $@"SYSTEM\CurrentControlSet\Enum\{parts[0]}\{parts[1]}\{parts[2]}\Device Parameters";
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        return key?.GetValue("EDID") as byte[];
    }

    private static string? GetAdapterName(Luid adapterId, uint sourceId)
    {
        var sourceName = new DisplayconfigSourceDeviceName
        {
            header = new DisplayconfigDeviceInfoHeader
            {
                type = 1, // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                size = Marshal.SizeOf<DisplayconfigSourceDeviceName>(),
                adapterId = adapterId,
                id = sourceId,
            },
        };

        if (DisplayConfigGetDeviceInfo(ref sourceName) != ErrorSuccess)
        {
            return null;
        }

        var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        return EnumDisplayDevices(sourceName.viewGdiDeviceName, 0, ref device, 0)
            ? device.DeviceString
            : null;
    }

    /// <summary>Optional diagnostic metadata: an inaccessible driver key must not fail the run.</summary>
    private static string? GetGpuDriverVersion(string adapterName)
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (root is null)
            {
                return null;
            }

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                using var subKey = root.OpenSubKey(subKeyName);
                var description = subKey?.GetValue("DriverDesc") as string;
                if (string.Equals(description, adapterName, StringComparison.OrdinalIgnoreCase))
                {
                    return subKey?.GetValue("DriverVersion") as string;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayconfigPathSourceInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayconfigPathTargetInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DisplayconfigRational refreshRate;
        public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayconfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayconfigPathInfo
    {
        public DisplayconfigPathSourceInfo sourceInfo;
        public DisplayconfigPathTargetInfo targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayconfigModeInfo
    {
        public uint infoType;
        public uint id;
        public Luid adapterId;
        public ModeUnion mode;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct ModeUnion
    {
        [FieldOffset(0)] public long placeholder;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayconfigDeviceInfoHeader
    {
        public int type;
        public int size;
        public Luid adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayconfigTargetDeviceName
    {
        public DisplayconfigDeviceInfoHeader header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayconfigSourceDeviceName
    {
        public DisplayconfigDeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out int pathCount, out int modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref int pathCount,
        [Out] DisplayconfigPathInfo[] paths,
        ref int modeCount,
        [Out] DisplayconfigModeInfo[] modes,
        IntPtr currentTopology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayconfigTargetDeviceName request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayconfigSourceDeviceName request);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint deviceIndex, ref DisplayDevice displayDevice, uint flags);

    static WindowsDisplayProvider() => _ = Encoding.Unicode;
}
