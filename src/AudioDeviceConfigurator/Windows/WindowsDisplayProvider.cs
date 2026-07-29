using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AudioDeviceConfigurator.Abstractions;
using Microsoft.Win32;

namespace AudioDeviceConfigurator.Windows;

/// <summary>
/// Enumerates currently ACTIVE display paths through the Connecting and Configuring Displays API,
/// then reads each monitor's EDID from its own device key. Historical registry records are never
/// used as a fallback, so a stale monitor cannot be tested by accident.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDisplayProvider(IDriverMetadataProvider driverMetadata) : IDisplayProvider
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

        var drivers = driverMetadata.GetDriverMetadata();
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

            displays.Add(new DisplayInfo(
                MonitorId: devicePath,
                FriendlyName: string.IsNullOrWhiteSpace(targetName.monitorFriendlyDeviceName)
                    ? devicePath
                    : targetName.monitorFriendlyDeviceName,
                AdapterName: drivers.GpuName,
                GpuDriverName: drivers.GpuName,
                GpuDriverProvider: drivers.GpuDriverProvider,
                GpuDriverVersion: drivers.GpuDriverVersion,
                RawEdid: edid,
                ContainerId: ReadContainerId(devicePath)));
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

    /// <summary>
    /// Reads the device container the monitor belongs to, which is the same GUID its HDMI/DP audio
    /// endpoint reports. This is what lets an endpoint be paired with its own monitor.
    /// </summary>
    private static string? ReadContainerId(string devicePath)
    {
        var instanceId = ToDeviceInstanceId(devicePath);
        if (instanceId is null || CM_Locate_DevNodeW(out var devInst, instanceId, 0) != CrSuccess)
        {
            return null;
        }

        var key = DevpkeyDeviceContainerId;
        var size = 16;
        var buffer = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out var propertyType, buffer, ref size, 0) != CrSuccess
            || propertyType != DevpropTypeGuid
            || size != 16)
        {
            return null;
        }

        return new Guid(buffer).ToString("B").ToUpperInvariant();
    }

    /// <summary>
    /// Turns \\?\DISPLAY#ACI22E5#5&amp;c1713af&amp;0&amp;UID45312#{guid} into the device instance ID
    /// DISPLAY\ACI22E5\5&amp;c1713af&amp;0&amp;UID45312 that the Configuration Manager expects.
    /// </summary>
    private static string? ToDeviceInstanceId(string devicePath)
    {
        var trimmed = devicePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? devicePath[4..] : devicePath;
        var parts = trimmed.Split('#');
        return parts.Length < 3 ? null : $@"{parts[0]}\{parts[1]}\{parts[2]}";
    }

    private const int CrSuccess = 0;
    private const int DevpropTypeGuid = 0x0000000D;

    private static DevpropKey DevpkeyDeviceContainerId =>
        new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DevpropKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(
        uint devInst,
        ref DevpropKey propertyKey,
        out int propertyType,
        [Out] byte[] propertyBuffer,
        ref int propertyBufferSize,
        uint flags);

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
}
