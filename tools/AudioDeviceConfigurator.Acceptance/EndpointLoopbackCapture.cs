using System.Runtime.InteropServices;

namespace AudioDeviceConfigurator.Acceptance;

internal sealed class EndpointLoopbackCapture : IDisposable
{
    private const int ClsctxAll = 0x17;
    private const int SharemodeShared = 0;
    private const int StreamflagsLoopback = 0x00020000;
    private const int BufferflagsSilent = 0x00000002;

    private readonly IMMDeviceEnumerator _enumerator;
    private readonly IMMDevice _device;
    private readonly IAudioClient _audioClient;
    private readonly IAudioCaptureClient _captureClient;
    private readonly IntPtr _mixFormat;
    private readonly WaveFormatEx _format;
    private readonly bool _isFloat;
    private bool _started;

    public EndpointLoopbackCapture(string endpointId)
    {
        var type = Type.GetTypeFromCLSID(
            new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!;
        _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
        ThrowIfFailed(_enumerator.GetDevice(endpointId, out _device),
            "IMMDeviceEnumerator.GetDevice");

        var audioClientIid = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        ThrowIfFailed(_device.Activate(ref audioClientIid, ClsctxAll, IntPtr.Zero, out var client),
            "IMMDevice.Activate(IAudioClient)");
        _audioClient = (IAudioClient)client;
        ThrowIfFailed(_audioClient.GetMixFormat(out _mixFormat), "IAudioClient.GetMixFormat");
        _format = Marshal.PtrToStructure<WaveFormatEx>(_mixFormat);
        var tag = Marshal.ReadInt16(_mixFormat, 0);
        _isFloat = tag == 0x0003
            || (tag == unchecked((short)0xfffe)
                && Marshal.ReadInt32(_mixFormat, 24) == 0x00000003);

        ThrowIfFailed(_audioClient.Initialize(
            SharemodeShared,
            StreamflagsLoopback,
            10_000_000,
            0,
            _mixFormat,
            IntPtr.Zero), "IAudioClient.Initialize(loopback)");

        var captureIid = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
        ThrowIfFailed(_audioClient.GetService(ref captureIid, out var capture),
            "IAudioClient.GetService(IAudioCaptureClient)");
        _captureClient = (IAudioCaptureClient)capture;
    }

    public PeakSampleSet Sample(TimeSpan duration, TimeSpan interval)
    {
        if (!_started)
        {
            ThrowIfFailed(_audioClient.Start(), "IAudioClient.Start(loopback)");
            _started = true;
        }

        var samples = new List<float>();
        var deadline = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var intervalPeak = 0f;
            while (true)
            {
                ThrowIfFailed(_captureClient.GetNextPacketSize(out var packetFrames),
                    "IAudioCaptureClient.GetNextPacketSize");
                if (packetFrames == 0)
                {
                    break;
                }

                ThrowIfFailed(_captureClient.GetBuffer(
                    out var data,
                    out var frames,
                    out var flags,
                    out _,
                    out _), "IAudioCaptureClient.GetBuffer");
                try
                {
                    if ((flags & BufferflagsSilent) == 0 && data != IntPtr.Zero)
                    {
                        intervalPeak = Math.Max(intervalPeak, ReadPeak(data, frames));
                    }
                }
                finally
                {
                    ThrowIfFailed(_captureClient.ReleaseBuffer(frames),
                        "IAudioCaptureClient.ReleaseBuffer");
                }
            }

            samples.Add(intervalPeak);
            Thread.Sleep(interval);
        }
        return new PeakSampleSet(samples);
    }

    public void Dispose()
    {
        if (_started)
        {
            try { _audioClient.Stop(); } catch { }
        }
        Release(_captureClient);
        if (_mixFormat != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_mixFormat);
        }
        Release(_audioClient);
        Release(_device);
        Release(_enumerator);
    }

    private float ReadPeak(IntPtr data, uint frames)
    {
        var sampleCount = checked((int)(frames * _format.Channels));
        var bytesPerSample = _format.BitsPerSample / 8;
        var peak = 0f;
        for (var index = 0; index < sampleCount; index++)
        {
            var pointer = data + index * bytesPerSample;
            float value;
            if (_isFloat && _format.BitsPerSample == 32)
            {
                value = Math.Abs(BitConverter.Int32BitsToSingle(Marshal.ReadInt32(pointer)));
            }
            else
            {
                value = _format.BitsPerSample switch
                {
                    16 => Math.Abs(Marshal.ReadInt16(pointer) / 32768f),
                    24 => Math.Abs(ReadInt24(pointer) / 8388608f),
                    32 => Math.Abs(Marshal.ReadInt32(pointer) / 2147483648f),
                    _ => 0,
                };
            }
            peak = Math.Max(peak, value);
        }
        return peak;
    }

    private static int ReadInt24(IntPtr pointer)
    {
        var value = Marshal.ReadByte(pointer)
            | Marshal.ReadByte(pointer, 1) << 8
            | Marshal.ReadByte(pointer, 2) << 16;
        return (value & 0x800000) == 0 ? value : value | unchecked((int)0xff000000);
    }

    private static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException($"{operation} failed (HRESULT 0x{hr:X8}).");
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
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
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int flags, long bufferDuration,
            long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out int flags,
            out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
