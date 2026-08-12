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
    private int _disposed;

    public bool IsPlaying => _isPlaying;

    public event Action<Exception>? PlaybackFailed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Start(EndpointInfo endpoint, WaveSource source)
    {
        if (endpoint is null) throw new ArgumentNullException(nameof(endpoint));
        if (source is null) throw new ArgumentNullException(nameof(source));

        // Force-stop any in-flight playback before opening a new stream. If
        // the previous render thread refused to exit we refuse to start so
        // two render threads cannot race for the same endpoint.
        if (!StopAndRelease())
        {
            throw new InvalidOperationException(
                "The previous WASAPI render thread did not exit; cannot start a new stream.");
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(WasapiAudioPlaybackService));
        }

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
            if (Volatile.Read(ref _disposed) != 0)
            {
                stream.Dispose();
                throw new ObjectDisposedException(nameof(WasapiAudioPlaybackService));
            }
            _stream = stream;
            _renderThread = thread;
        }
        _isPlaying = true;
        OnPropertyChanged(nameof(IsPlaying));
        thread.Start();
    }

    public void Stop()
    {
        StopAndRelease();
    }

    public void Dispose()
    {
        WasapiRenderStream? stream;
        Thread? thread;
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            stream = _stream;
            thread = _renderThread;
            _stream = null;
            _renderThread = null;
        }
        ReleaseStream(stream, thread, forceful: true);
    }

    /// <summary>
    /// Stops the render loop, joins the render thread, and disposes the
    /// stream. Returns <c>true</c> when the previous stream is fully released
    /// and a new stream may be opened safely. Returns <c>false</c> when the
    /// render thread is wedged and the stream remains owned by the still-running
    /// thread; callers (notably <see cref="Start"/>) must refuse to allocate a
    /// new stream in that case.
    /// </summary>
    private bool StopAndRelease()
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
        return ReleaseStream(stream, thread, forceful: false);
    }

    private bool ReleaseStream(WasapiRenderStream? stream, Thread? thread, bool forceful)
    {
        if (stream is null)
        {
            return true;
        }

        try { stream.RequestStop(); } catch { }

        if (thread is not null && thread.IsAlive)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }

        if (thread is not null && thread.IsAlive)
        {
            // Render thread is wedged. Don't Dispose its stream — the thread
            // may still be inside a COM call. Re-publish ownership and report
            // that the stream is still busy.
            lock (_gate)
            {
                if (_stream is null)
                {
                    _stream = stream;
                    _renderThread = thread;
                }
            }
            if (!forceful)
            {
                try
                {
                    PlaybackFailed?.Invoke(new InvalidOperationException(
                        "WASAPI render thread did not stop within the timeout."));
                }
                catch { }
            }
            return false;
        }

        try { stream.Dispose(); } catch { }
        if (_isPlaying)
        {
            _isPlaying = false;
            OnPropertyChanged(nameof(IsPlaying));
        }
        return true;
    }

    private void RenderLoop(WasapiRenderStream stream)
    {
        try
        {
            stream.RunLoop();
        }
        catch (Exception ex)
        {
            try { PlaybackFailed?.Invoke(ex); } catch { }
        }
        finally
        {
            if (_isPlaying)
            {
                _isPlaying = false;
                OnPropertyChanged(nameof(IsPlaying));
            }
        }
    }
}

internal sealed class WasapiRenderStream : IDisposable
{
    private const int WaitObject0 = 0;
    private const int WaitTimeout = 0x00000102;

    private readonly object _device;
    private readonly object _audioClient;
    private readonly object _renderClient;
    private readonly WaveSource _source;
    private readonly uint _bufferFrameCount;
    private readonly uint _frameSize;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly IntPtr _renderEventHandle;
    private int _pcmPosition;
    private int _disposed;

    private WasapiRenderStream(
        object device,
        object audioClient,
        object renderClient,
        WaveSource source,
        uint bufferFrameCount,
        IntPtr renderEventHandle)
    {
        _device = device;
        _audioClient = audioClient;
        _renderClient = renderClient;
        _source = source;
        _bufferFrameCount = bufferFrameCount;
        _frameSize = (uint)source.Format.BytesPerFrame;
        _renderEventHandle = renderEventHandle;
    }

    public static WasapiRenderStream Open(string endpointId, WaveSource source)
    {
        object? device = null;
        object? audioClient = null;
        object? renderClient = null;
        IntPtr mixPtr = IntPtr.Zero;
        IntPtr renderEventHandle = IntPtr.Zero;
        bool renderEventCommitted = false;

        try
        {
            device = ActivateDevice(endpointId);
            var deviceIf = (CoreAudio.IMMDevice)device;

            var audioClientIid = typeof(CoreAudio.IAudioClient).GUID;
            var hrActivate = deviceIf.Activate(ref audioClientIid, CoreAudio.ClsctxAll, IntPtr.Zero, out var audioClientObj);
            ThrowOnFailure(hrActivate, "IMMDevice.Activate");
            audioClient = audioClientObj;
            var audio = (CoreAudio.IAudioClient)audioClient;

            var mixHr = audio.GetMixFormat(out mixPtr);
            ThrowOnFailure(mixHr, "IAudioClient.GetMixFormat");

            var mixTag = Marshal.ReadInt16(mixPtr, 0);
            var mix = Marshal.PtrToStructure<CoreAudio.WaveFormatEx>(mixPtr);
            var mixFormat = new WaveFormat(
                (int)mix.nSamplesPerSec,
                mix.nChannels,
                mix.wBitsPerSample,
                mix.nBlockAlign);
            var isFloat = mixTag == 0x0003
                || (mixTag == unchecked((short)0xFFFE)
                    && Marshal.ReadInt32(mixPtr, 24) == 0x00000003);
            var converted = SharedModePcmConverter.Convert(source, mixFormat, isFloat);

            var hrInit = audio.Initialize(
                shareMode: CoreAudio.AudclntSharemodeShared,
                streamFlags: CoreAudio.AudclntStreamflagsEventcallback,
                bufferDuration: 0,
                periodicity: 0,
                format: mixPtr,
                audioSessionGuid: IntPtr.Zero);
            ThrowOnFailure(hrInit, "IAudioClient.Initialize");

            Marshal.FreeHGlobal(mixPtr);
            mixPtr = IntPtr.Zero;

            ThrowOnFailure(audio.GetBufferSize(out var bufferFrameCount), "IAudioClient.GetBufferSize");

            var renderClientIid = typeof(CoreAudio.IAudioRenderClient).GUID;
            var hrSvc = audio.GetService(ref renderClientIid, out var renderClientObj);
            ThrowOnFailure(hrSvc, "IAudioClient.GetService");
            renderClient = renderClientObj;

            renderEventHandle = CreateEvent(IntPtr.Zero, false, false, null);
            if (renderEventHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateEvent failed for WASAPI render callback.");
            }

            var hrSet = audio.SetEventHandle(renderEventHandle);
            ThrowOnFailure(hrSet, "IAudioClient.SetEventHandle");
            renderEventCommitted = true;

            var ownedDevice = device;
            var ownedAudioClient = audioClient;
            var ownedRenderClient = renderClient;
            device = null;
            audioClient = null;
            renderClient = null;
            return new WasapiRenderStream(
                ownedDevice,
                ownedAudioClient,
                ownedRenderClient,
                converted,
                bufferFrameCount,
                renderEventHandle);
        }
        catch
        {
            if (renderEventHandle != IntPtr.Zero && !renderEventCommitted)
            {
                CloseHandle(renderEventHandle);
            }
            if (mixPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(mixPtr);
            }
            if (renderClient is not null)
            {
                try { Marshal.ReleaseComObject(renderClient); } catch { }
            }
            if (audioClient is not null)
            {
                try { Marshal.ReleaseComObject(audioClient); } catch { }
            }
            if (device is not null)
            {
                try { Marshal.ReleaseComObject(device); } catch { }
            }
            throw;
        }
    }

    public void Start()
    {
        ThrowIfDisposed();
        WriteAvailableFrames();
        var hr = ((CoreAudio.IAudioClient)_audioClient).Start();
        ThrowOnFailure(hr, "IAudioClient.Start");
    }

    public void RequestStop() => _stop.Set();

    public void RunLoop()
    {
        ThrowIfDisposed();
        while (!_stop.IsSet)
        {
            if (!WriteAvailableFrames())
            {
                // Wait on the WASAPI render event so the audio engine wakes
                // us the moment a buffer slot is free. The 100 ms timeout
                // bounds the latency of observing an external Stop request.
                var signaled = WaitForSingleObject(_renderEventHandle, 100);
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }
                if (signaled == WaitObject0 || signaled == WaitTimeout)
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"WaitForSingleObject returned {signaled} (error {Marshal.GetLastWin32Error()}).");
            }
        }
    }

    private bool WriteAvailableFrames()
    {
        ThrowIfDisposed();
        var paddingHr = ((CoreAudio.IAudioClient)_audioClient).GetCurrentPadding(out var padding);
        ThrowOnFailure(paddingHr, "IAudioClient.GetCurrentPadding");
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
        ThrowOnFailure(getBufferHr, "IAudioRenderClient.GetBuffer");
        if (bufferPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("IAudioRenderClient.GetBuffer returned null.");
        }

        Marshal.Copy(_source.PcmData, _pcmPosition, bufferPtr, bytesToWrite);
        var releaseHr = ((CoreAudio.IAudioRenderClient)_renderClient).ReleaseBuffer(framesToWrite, 0);
        ThrowOnFailure(releaseHr, "IAudioRenderClient.ReleaseBuffer");

        _pcmPosition += bytesToWrite;
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try { _stop.Set(); } catch { }
        try { ((CoreAudio.IAudioClient)_audioClient).Stop(); } catch { }
        try { Marshal.ReleaseComObject(_renderClient); } catch { }
        try { Marshal.ReleaseComObject(_audioClient); } catch { }
        try { Marshal.ReleaseComObject(_device); } catch { }
        try { CloseHandle(_renderEventHandle); } catch { }
        _stop.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(WasapiRenderStream));
        }
    }

    private static CoreAudio.IMMDevice ActivateDevice(string endpointId)
    {
        var enumerator = CoreAudioEndpointProvider.CreateEnumerator();
        var hr = enumerator.GetDevice(endpointId, out var device);
        try { Marshal.ReleaseComObject(enumerator); } catch { }
        if (hr < 0)
        {
            throw new InvalidOperationException($"IMMDeviceEnumerator.GetDevice failed (HRESULT 0x{hr:X8}).");
        }
        return device;
    }

    private static void ThrowOnFailure(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException($"{operation} failed (HRESULT 0x{hr:X8}).");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int WaitForSingleObject(IntPtr hHandle, int dwMilliseconds);
}