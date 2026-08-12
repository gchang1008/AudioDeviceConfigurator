using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AudioDeviceConfigurator.Abstractions;

namespace AudioDeviceConfigurator.Windows;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class CoreAudioNotificationClient(Action<AudioEndpointChange> publish)
    : CoreAudio.IMMNotificationClient
{
    public int OnDeviceStateChanged(string deviceId, int newState)
    {
        Publish(new AudioEndpointChange(AudioEndpointChangeKind.StateChanged, deviceId));
        return CoreAudio.SOk;
    }

    public int OnDeviceAdded(string deviceId)
    {
        Publish(new AudioEndpointChange(AudioEndpointChangeKind.Added, deviceId));
        return CoreAudio.SOk;
    }

    public int OnDeviceRemoved(string deviceId)
    {
        Publish(new AudioEndpointChange(AudioEndpointChangeKind.Removed, deviceId));
        return CoreAudio.SOk;
    }

    public int OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId)
    {
        if (flow == CoreAudio.EDataFlowRender && role == CoreAudio.ERoleConsole)
        {
            Publish(new AudioEndpointChange(AudioEndpointChangeKind.DefaultChanged, defaultDeviceId));
        }
        return CoreAudio.SOk;
    }

    public int OnPropertyValueChanged(string deviceId, CoreAudio.PropertyKey key) => CoreAudio.SOk;

    private void Publish(AudioEndpointChange change)
    {
        try
        {
            publish(change);
        }
        catch
        {
            // Exceptions must never cross the COM callback boundary.
        }
    }
}

/// <summary>Publishes Core Audio endpoint lifecycle notifications for the WPF GUI.</summary>
[SupportedOSPlatform("windows")]
public sealed class CoreAudioEndpointChangeMonitor : IAudioEndpointChangeMonitor
{
    private readonly CoreAudio.IMMDeviceEnumerator _enumerator;
    private readonly CoreAudioNotificationClient _client;
    private readonly Action<object> _release;
    private int _disposed;

    public CoreAudioEndpointChangeMonitor()
        : this(CoreAudioEndpointProvider.CreateEnumerator(), value => Marshal.ReleaseComObject(value))
    {
    }

    internal CoreAudioEndpointChangeMonitor(
        CoreAudio.IMMDeviceEnumerator enumerator,
        Action<object> release)
    {
        _enumerator = enumerator;
        _release = release;
        _client = new CoreAudioNotificationClient(Publish);
        var hr = _enumerator.RegisterEndpointNotificationCallback(_client);
        if (hr != CoreAudio.SOk)
        {
            _release(_enumerator);
            throw new InvalidOperationException(
                $"Unable to monitor Core Audio endpoint changes. (HRESULT 0x{hr:X8})");
        }
    }

    public event EventHandler<AudioEndpointChange>? Changed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Changed = null;
        _enumerator.UnregisterEndpointNotificationCallback(_client);
        _release(_enumerator);
    }

    private void Publish(AudioEndpointChange change)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Changed?.Invoke(this, change);
    }
}
