using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Application;
using AudioDeviceConfigurator.Audio;

namespace AudioDeviceConfigurator.Windows;

/// <summary>
/// Loops a decoded WAV through the chosen endpoint using WASAPI Shared Mode.
/// One background render thread per active stream. Errors surface via
/// <see cref="IAudioPlaybackService.PlaybackFailed"/>; the audio configuration that
/// was already verified by <see cref="DeviceConfigurationService"/> is not touched.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioPlaybackService : IAudioPlaybackService, IDisposable
{
    private readonly object _gate = new();
    private WasapiRenderStream? _stream;
    private Thread? _renderThread;
    private volatile bool _isPlaying;
    private Exception? _lastError;

    public bool IsPlaying => _isPlaying;

    public event Action<Exception>? PlaybackFailed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Start(EndpointInfo endpoint, WaveSource source)
    {
        if (endpoint is null) throw new ArgumentNullException(nameof(endpoint));
        if (source is null) throw new ArgumentNullException(nameof(source));

        StopInternal();

        var stream = WasapiRenderStream.Open(endpoint.EndpointId, source);
        try
        {
            stream.Start();
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        var thread = new Thread(() => RenderLoop(stream))
        {
            IsBackground = true,
            Name = "AudioDeviceConfigurator.WasapiRender",
        };
        lock (_gate)
        {
            _stream = stream;
            _renderThread = thread;
            _lastError = null;
        }
        _isPlaying = true;
        OnPropertyChanged(nameof(IsPlaying));
        thread.Start();
    }

    public void Stop()
    {
        StopInternal();
    }

    public void Dispose() => StopInternal();

    private void StopInternal()
    {
        WasapiRenderStream? stream;
        Thread? thread;
        lock (_gate)
        {
            stream = _stream;
            thread = _renderThread;
            _stream = null;
            _renderThread = null;
        }
        try { stream?.RequestStop(); } catch { }
        if (thread is not null && thread.IsAlive)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
        try { stream?.Dispose(); } catch { }
        _isPlaying = false;
        OnPropertyChanged(nameof(IsPlaying));
    }

    private void RenderLoop(WasapiRenderStream stream)
    {
        try
        {
            stream.RunLoop();
        }
        catch (Exception ex)
        {
            _lastError = ex;
            try { PlaybackFailed?.Invoke(ex); } catch { }
        }
        finally
        {
            _isPlaying = false;
            try { stream?.Dispose(); } catch { }
            OnPropertyChanged(nameof(IsPlaying));
        }
    }
}

internal sealed class WasapiRenderStream : IDisposable
{
    private readonly object _audioClient;
    private readonly object _renderClient;
    private readonly WaveFormat _format;
    private readonly WaveSource _source;
    private readonly uint _bufferFrameCount;
    private readonly uint _frameSize;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly IntPtr _renderEventHandle;
    private readonly GCHandle _eventHandlePin;
    private int _pcmPosition;

    private WasapiRenderStream(
        object audioClient,
        object renderClient,
        WaveSource source,
        uint bufferFrameCount,
        IntPtr renderEventHandle)
    {
        _audioClient = audioClient;
        _renderClient = renderClient;
        _source = source;
        _format = source.Format;
        _bufferFrameCount = bufferFrameCount;
        _frameSize = (uint)source.Format.BytesPerFrame;
        _renderEventHandle = renderEventHandle;
        _eventHandlePin = GCHandle.Alloc(renderEventHandle, GCHandleType.Normal);
    }

    public static WasapiRenderStream Open(string endpointId, WaveSource source)
    {
        var device = ActivateDevice(endpointId);
        try
        {
            var audioClientIid = typeof(CoreAudio.IAudioClient).GUID;
            var hrActivate = device.Activate(ref audioClientIid, CoreAudio.ClsctxAll, IntPtr.Zero, out var audioClientObj);
            if (hrActivate < 0)
            {
                throw new InvalidOperationException($"IMMDevice.Activate failed (HRESULT 0x{hrActivate:X8}).");
            }
            var audioClient = (CoreAudio.IAudioClient)audioClientObj;

            var mixHr = audioClient.GetMixFormat(out var mixPtr);
            if (mixHr < 0)
            {
                throw new InvalidOperationException($"IAudioClient.GetMixFormat failed (HRESULT 0x{mixHr:X8}).");
            }

            WaveSource converted;
            CoreAudio.WaveFormatEx mix;
            var mixTag = Marshal.ReadInt16(mixPtr, 0);
            mix = Marshal.PtrToStructure<CoreAudio.WaveFormatEx>(mixPtr);
            var mixFormat = new WaveFormat(
                (int)mix.nSamplesPerSec,
                mix.nChannels,
                mix.wBitsPerSample,
                mix.nBlockAlign);
            var isFloat = mixTag == 0x0003
                || (mixTag == unchecked((short)0xFFFE)
                    && Marshal.ReadInt32(mixPtr, 24) == 0x00000003);
            converted = SharedModePcmConverter.Convert(source, mixFormat, isFloat);

            try
            {
                var hr = audioClient.Initialize(
                    shareMode: CoreAudio.AudclntSharemodeShared,
                    streamFlags: CoreAudio.AudclntStreamflagsEventcallback,
                    bufferDuration: 0,
                    periodicity: 0,
                    format: mixPtr,
                    audioSessionGuid: IntPtr.Zero);
                if (hr < 0)
                {
                    throw new InvalidOperationException($"IAudioClient.Initialize failed (HRESULT 0x{hr:X8}).");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(mixPtr);
            }

            uint bufferFrameCount;
            audioClient.GetBufferSize(out bufferFrameCount);

            // 3. Get the render client.
            var renderClientIid = typeof(CoreAudio.IAudioRenderClient).GUID;
            var hrSvc = audioClient.GetService(ref renderClientIid, out var renderClientObj);
            if (hrSvc < 0)
            {
                throw new InvalidOperationException($"IAudioClient.GetService failed (HRESULT 0x{hrSvc:X8}).");
            }
            var renderClient = (CoreAudio.IAudioRenderClient)renderClientObj;

            // 4. Create the kernel event handle for buffer completion callbacks.
            var handle = CreateEvent(IntPtr.Zero, false, false, null);
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateEvent failed for WASAPI render callback.");
            }

            var setHr = audioClient.SetEventHandle(handle);
            if (setHr < 0)
            {
                CloseHandle(handle);
                throw new InvalidOperationException($"IAudioClient.SetEventHandle failed (HRESULT 0x{setHr:X8}).");
            }

            return new WasapiRenderStream(audioClient, renderClient, converted, bufferFrameCount, handle);
        }
        catch
        {
            try { Marshal.ReleaseComObject(device); } catch { }
            throw;
        }
    }

    public void Start()
    {
        WriteAvailableFrames();
        var hr = ((CoreAudio.IAudioClient)_audioClient).Start();
        if (hr < 0)
        {
            throw new InvalidOperationException($"IAudioClient.Start failed (HRESULT 0x{hr:X8}).");
        }
    }

    public void RequestStop() => _stop.Set();

    public void RunLoop()
    {
        while (!_stop.IsSet)
        {
            if (!WriteAvailableFrames())
            {
                WaitForEvent(TimeSpan.FromSeconds(1));
            }
        }
    }

    private bool WriteAvailableFrames()
    {
        var paddingHr = ((CoreAudio.IAudioClient)_audioClient).GetCurrentPadding(out var padding);
        if (paddingHr < 0)
        {
            throw new InvalidOperationException(
                $"IAudioClient.GetCurrentPadding failed (HRESULT 0x{paddingHr:X8}).");
        }
        var framesAvailable = _bufferFrameCount - padding;
        if (framesAvailable == 0)
        {
            return false;
        }

        var bytesPerFrame = (int)_frameSize;
        var bytesRemaining = _source.PcmData.Length - _pcmPosition;
        if (bytesRemaining <= 0)
        {
            _pcmPosition = 0;
            bytesRemaining = _source.PcmData.Length;
        }

        var bytesToWrite = Math.Min((int)(framesAvailable * _frameSize), bytesRemaining);
        var framesToWrite = (uint)(bytesToWrite / bytesPerFrame);
        if (framesToWrite == 0)
        {
            return false;
        }
        bytesToWrite = (int)(framesToWrite * _frameSize);

        var getBufferHr = ((CoreAudio.IAudioRenderClient)_renderClient)
            .GetBuffer(framesToWrite, out var bufferPtr);
        if (getBufferHr < 0)
        {
            throw new InvalidOperationException(
                $"IAudioRenderClient.GetBuffer failed (HRESULT 0x{getBufferHr:X8}).");
        }
        if (bufferPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("IAudioRenderClient.GetBuffer returned null.");
        }

        Marshal.Copy(_source.PcmData, _pcmPosition, bufferPtr, bytesToWrite);
        var releaseHr = ((CoreAudio.IAudioRenderClient)_renderClient).ReleaseBuffer(framesToWrite, 0);
        if (releaseHr < 0)
        {
            throw new InvalidOperationException($"IAudioRenderClient.ReleaseBuffer failed (HRESULT 0x{releaseHr:X8}).");
        }

        _pcmPosition += bytesToWrite;
        return true;
    }

    public void Dispose()
    {
        try { ((CoreAudio.IAudioClient)_audioClient).Stop(); } catch { }
        try { Marshal.ReleaseComObject(_renderClient); } catch { }
        try { Marshal.ReleaseComObject(_audioClient); } catch { }
        try { CloseHandle(_renderEventHandle); } catch { }
        if (_eventHandlePin.IsAllocated) _eventHandlePin.Free();
        _stop.Dispose();
    }

    private void WaitForEvent(TimeSpan timeout)
    {
        var start = Environment.TickCount;
        var ms = (int)Math.Min(int.MaxValue, timeout.TotalMilliseconds);
        var signaled = WaitForSingleObject(_renderEventHandle, ms);
        if (signaled != 0 && signaled != 258 /* WAIT_TIMEOUT */)
        {
            throw new InvalidOperationException($"WaitForSingleObject returned {signaled}.");
        }
    }

    private static CoreAudio.IMMDevice ActivateDevice(string endpointId)
    {
        var enumerator = CoreAudioEndpointProvider.CreateEnumerator();
        try
        {
            enumerator.GetDevice(endpointId, out var device);
            return device;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int WaitForSingleObject(IntPtr hHandle, int dwMilliseconds);
}