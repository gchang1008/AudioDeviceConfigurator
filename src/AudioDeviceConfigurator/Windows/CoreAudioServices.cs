using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AudioDeviceConfigurator.Abstractions;

namespace AudioDeviceConfigurator.Windows;

/// <summary>Enumerates active render endpoints through Core Audio.</summary>
[SupportedOSPlatform("windows")]
public sealed class CoreAudioEndpointProvider : IAudioEndpointProvider
{
    public IReadOnlyList<EndpointInfo> GetActiveRenderEndpoints()
    {
        var enumerator = CreateEnumerator();
        string? defaultId = null;
        if (enumerator.GetDefaultAudioEndpoint(CoreAudio.EDataFlowRender, CoreAudio.ERoleConsole, out var defaultDevice) == CoreAudio.SOk)
        {
            defaultDevice.GetId(out defaultId);
            Marshal.ReleaseComObject(defaultDevice);
        }

        Check(enumerator.EnumAudioEndpoints(CoreAudio.EDataFlowRender, CoreAudio.DeviceStateActive, out var collection),
            "Unable to enumerate active render endpoints.");
        Check(collection.GetCount(out var count), "Unable to count active render endpoints.");

        var results = new List<EndpointInfo>(count);
        for (var i = 0; i < count; i++)
        {
            if (collection.Item(i, out var device) != CoreAudio.SOk)
            {
                continue;
            }

            try
            {
                device.GetId(out var id);
                var name = ReadProperty(device, CoreAudio.PkeyDeviceFriendlyName) ?? id;
                var description = ReadProperty(device, CoreAudio.PkeyDeviceDeviceDesc) ?? "";
                var containerId = ReadProperty(device, CoreAudio.PkeyDeviceContainerId);

                results.Add(new EndpointInfo(
                    EndpointId: id,
                    FriendlyName: name,
                    DeviceDescription: description,
                    SvclCommandLineId: null,
                    DriverName: ExtractDeviceName(name, description),
                    DriverVersion: null,
                    IsDefault: string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase),
                    ContainerId: containerId));
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }

        Marshal.ReleaseComObject(collection);
        Marshal.ReleaseComObject(enumerator);
        return results;
    }

    public EndpointInfo? GetDefaultRenderEndpoint() =>
        GetActiveRenderEndpoints().FirstOrDefault(e => e.IsDefault);

    internal static CoreAudio.IMMDeviceEnumerator CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(CoreAudio.ClsidMMDeviceEnumerator)
            ?? throw new InvalidOperationException("The Core Audio device enumerator is unavailable.");
        return (CoreAudio.IMMDeviceEnumerator)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Unable to create the Core Audio device enumerator."));
    }

        /// <summary>
    /// Reads a property, returning null when it is absent. String and GUID properties are both
    /// needed: ContainerId arrives as VT_CLSID rather than a string.
    /// </summary>
    private static string? ReadProperty(CoreAudio.IMMDevice device, CoreAudio.PropertyKey key)
    {
        if (device.OpenPropertyStore(CoreAudio.StgmRead, out var store) != CoreAudio.SOk || store is null)
        {
            return null;
        }

        try
        {
            if (store.GetValue(ref key, out var value) != CoreAudio.SOk)
            {
                return null;
            }

            var text = value.AsString() ?? value.AsGuid();
            CoreAudio.PropVariantClear(ref value);
            return text;
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>The friendly name is "Endpoint (Device)"; the parenthesised part names the driver device.</summary>
    private static string ExtractDeviceName(string friendlyName, string description)
    {
        var open = friendlyName.LastIndexOf('(');
        var close = friendlyName.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            return friendlyName[(open + 1)..close];
        }

        return description;
    }

    private static void Check(int hr, string message)
    {
        if (hr != CoreAudio.SOk)
        {
            throw new InvalidOperationException($"{message} (HRESULT 0x{hr:X8})");
        }
    }
}

/// <summary>
/// Queries WASAPI Exclusive-mode format support. Deliberately never calls Initialize, so no
/// stream is created and no audio is played.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiFormatProbe : IWasapiFormatProbe
{
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid KsDataFormatSubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private const int ClsCtxAll = 23;

    public FormatSupportResult IsExclusiveFormatSupported(string endpointId, Domain.WaveFormat format)
    {
        var enumerator = CoreAudioEndpointProvider.CreateEnumerator();
        try
        {
            var hr = enumerator.GetDevice(endpointId, out var device);
            if (hr != CoreAudio.SOk)
            {
                return new FormatSupportResult(hr);
            }

            try
            {
                var iid = IidAudioClient;
                hr = device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var instance);
                if (hr != CoreAudio.SOk)
                {
                    return new FormatSupportResult(hr);
                }

                var client = (CoreAudio.IAudioClient)instance;
                try
                {
                    var buffer = BuildFormatBuffer(format);
                    try
                    {
                        return new FormatSupportResult(
                            client.IsFormatSupported(CoreAudio.AudclntSharemodeExclusive, buffer, IntPtr.Zero));
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(client);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Builds a WAVEFORMATEXTENSIBLE describing the candidate PCM format.</summary>
    private static IntPtr BuildFormatBuffer(Domain.WaveFormat format)
    {
        const int size = 40;
        var bytes = new byte[size];
        BitConverter.GetBytes((ushort)0xFFFE).CopyTo(bytes, 0);                     // wFormatTag
        BitConverter.GetBytes((ushort)format.Channels).CopyTo(bytes, 2);
        BitConverter.GetBytes((uint)format.SampleRate).CopyTo(bytes, 4);
        BitConverter.GetBytes((uint)format.AverageBytesPerSecond).CopyTo(bytes, 8);
        BitConverter.GetBytes((ushort)format.BlockAlign).CopyTo(bytes, 12);
        BitConverter.GetBytes((ushort)format.ContainerBits).CopyTo(bytes, 14);
        BitConverter.GetBytes((ushort)22).CopyTo(bytes, 16);                        // cbSize
        BitConverter.GetBytes((ushort)format.ValidBits).CopyTo(bytes, 18);
        BitConverter.GetBytes(format.ChannelMask).CopyTo(bytes, 20);
        KsDataFormatSubtypePcm.ToByteArray().CopyTo(bytes, 24);

        var buffer = Marshal.AllocHGlobal(size);
        Marshal.Copy(bytes, 0, buffer, size);
        return buffer;
    }
}
