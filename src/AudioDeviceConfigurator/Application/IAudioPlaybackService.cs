using System.ComponentModel;
using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Audio;

namespace AudioDeviceConfigurator.Application;

/// <summary>
/// Plays a fixed WAV through a chosen endpoint in WASAPI Shared Mode. The implementation owns the
/// render thread, exposes Start/Stop, and raises <see cref="PlaybackFailed"/> if the stream dies.
/// Errors must never roll back a previously verified audio configuration.
/// </summary>
public interface IAudioPlaybackService : INotifyPropertyChanged
{
    bool IsPlaying { get; }

    event Action<Exception>? PlaybackFailed;

    void Start(EndpointInfo endpoint, WaveSource source);

    void Stop();
}

/// <summary>Default no-op implementation used when WASAPI cannot be initialised (CI, sandbox).</summary>
public sealed class NoOpAudioPlaybackService : IAudioPlaybackService
{
    public bool IsPlaying => false;
    public event Action<Exception>? PlaybackFailed { add { } remove { } }
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public void Start(EndpointInfo endpoint, WaveSource source) { }
    public void Stop() { }
}