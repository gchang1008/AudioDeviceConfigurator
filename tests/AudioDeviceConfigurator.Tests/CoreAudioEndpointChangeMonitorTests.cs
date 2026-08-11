using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Windows;

namespace AudioDeviceConfigurator.Tests;

public sealed class CoreAudioEndpointChangeMonitorTests
{
    [Fact]
    public void Notification_client_publishes_device_lifecycle_changes()
    {
        var changes = new List<AudioEndpointChange>();
        var client = new CoreAudioNotificationClient(changes.Add);

        client.OnDeviceAdded("ep-added");
        client.OnDeviceStateChanged("ep-state", CoreAudio.DeviceStateActive);
        client.OnDeviceRemoved("ep-removed");

        Assert.Equal(
        [
            new AudioEndpointChange(AudioEndpointChangeKind.Added, "ep-added"),
            new AudioEndpointChange(AudioEndpointChangeKind.StateChanged, "ep-state"),
            new AudioEndpointChange(AudioEndpointChangeKind.Removed, "ep-removed"),
        ], changes);
    }

    [Fact]
    public void Notification_client_only_publishes_console_render_default_changes()
    {
        var changes = new List<AudioEndpointChange>();
        var client = new CoreAudioNotificationClient(changes.Add);

        client.OnDefaultDeviceChanged(CoreAudio.EDataFlowCapture, CoreAudio.ERoleConsole, "capture");
        client.OnDefaultDeviceChanged(CoreAudio.EDataFlowRender, CoreAudio.ERoleMultimedia, "multimedia");
        client.OnDefaultDeviceChanged(CoreAudio.EDataFlowRender, CoreAudio.ERoleConsole, "render");

        Assert.Equal(
            [new AudioEndpointChange(AudioEndpointChangeKind.DefaultChanged, "render")],
            changes);
    }

    [Fact]
    public void Notification_client_ignores_property_changes()
    {
        var changes = new List<AudioEndpointChange>();
        var client = new CoreAudioNotificationClient(changes.Add);
        var key = CoreAudio.PkeyDeviceFriendlyName;

        client.OnPropertyValueChanged("ep-1", key);

        Assert.Empty(changes);
    }

    [Fact]
    public void Monitor_registers_and_unregisters_callback_once()
    {
        var enumerator = new FakeDeviceEnumerator();
        var releases = 0;
        var monitor = new CoreAudioEndpointChangeMonitor(enumerator, _ => releases++);

        monitor.Dispose();
        monitor.Dispose();

        Assert.Equal(1, enumerator.RegisterCalls);
        Assert.Equal(1, enumerator.UnregisterCalls);
        Assert.Equal(1, releases);
    }

    private sealed class FakeDeviceEnumerator : CoreAudio.IMMDeviceEnumerator
    {
        public int RegisterCalls { get; private set; }
        public int UnregisterCalls { get; private set; }

        public int EnumAudioEndpoints(int dataFlow, int stateMask, out CoreAudio.IMMDeviceCollection devices)
        {
            devices = null!;
            return unchecked((int)0x80004001);
        }

        public int GetDefaultAudioEndpoint(int dataFlow, int role, out CoreAudio.IMMDevice device)
        {
            device = null!;
            return unchecked((int)0x80004001);
        }

        public int GetDevice(string id, out CoreAudio.IMMDevice device)
        {
            device = null!;
            return unchecked((int)0x80004001);
        }

        public int RegisterEndpointNotificationCallback(CoreAudio.IMMNotificationClient client)
        {
            RegisterCalls++;
            return CoreAudio.SOk;
        }

        public int UnregisterEndpointNotificationCallback(CoreAudio.IMMNotificationClient client)
        {
            UnregisterCalls++;
            return CoreAudio.SOk;
        }
    }
}
