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
                results.Add(new EndpointInfo(
                    EndpointId: id,
                    FriendlyName: name,
                    DeviceDescription: description,
                    DriverName: ExtractDeviceName(name, description),
                    DriverVersion: null,
                    IsDefault: string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
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

    /// <summary>Reads a string property, returning null when it is absent.</summary>
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
