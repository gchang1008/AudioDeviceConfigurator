using System.Runtime.InteropServices;

namespace AudioDeviceConfigurator.Acceptance;

internal sealed record PeakSampleSet(IReadOnlyList<float> Values)
{
    public float Maximum => Values.Count == 0 ? 0 : Values.Max();

    public float Percentile95
    {
        get
        {
            if (Values.Count == 0)
            {
                return 0;
            }
            var ordered = Values.OrderBy(value => value).ToArray();
            return ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1];
        }
    }
}

internal sealed class EndpointPeakMeter : IDisposable
{
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly IMMDevice _device;
    private readonly IAudioMeterInformation _meter;

    public EndpointPeakMeter(string endpointId)
    {
        var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!;
        _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
        ThrowIfFailed(_enumerator.GetDevice(endpointId, out _device), "IMMDeviceEnumerator.GetDevice");
        var iid = new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064");
        ThrowIfFailed(_device.Activate(ref iid, 0x17, IntPtr.Zero, out var instance),
            "IMMDevice.Activate(IAudioMeterInformation)");
        _meter = (IAudioMeterInformation)instance;
    }

    public PeakSampleSet Sample(TimeSpan duration, TimeSpan interval)
    {
        var samples = new List<float>();
        var deadline = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ThrowIfFailed(_meter.GetPeakValue(out var value), "IAudioMeterInformation.GetPeakValue");
            samples.Add(value);
            Thread.Sleep(interval);
        }
        return new PeakSampleSet(samples);
    }

    public void Dispose()
    {
        Release(_meter);
        Release(_device);
        Release(_enumerator);
    }

    private static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr, new IntPtr(-1));
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
        [PreserveSig] int GetMeteringChannelCount(out uint channelCount);
        [PreserveSig] int GetChannelsPeakValues(uint channelCount, [Out] float[] peakValues);
        [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
    }
}
