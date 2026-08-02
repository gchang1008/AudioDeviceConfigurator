using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Application;
using AudioDeviceConfigurator.Audio;
using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Gui;
using AudioDeviceConfigurator.Tests.Fakes;

namespace AudioDeviceConfigurator.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task LoadEndpointsAsync_populates_endpoints_collection()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1", "ep-2");

        await vm.LoadEndpointsAsync(CancellationToken.None);

        Assert.Equal(2, vm.Endpoints.Count);
        Assert.Equal("ep-1", vm.Endpoints[0].EndpointId);
    }

    [Fact]
    public async Task EndpointChangedAsync_populates_channels_and_formats_and_unlocks_apply()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1");
        SeedOptions(harness, channels: new[] { 2, 4, 6 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
            new ControlPanelFormatItem(1, "24 bit, 96000 Hz", 4, 96000, 24, 32, ControlPanelParseStatus.Parsed, null),
        });

        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);

        Assert.Equal(new[] { 2, 4, 6 }, vm.Channels);
        Assert.Equal(2, vm.Formats.Count);
        Assert.False(vm.CanApply);
        vm.SelectChannelIndex(0);
        vm.SelectFormatIndex(1);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task EndpointChangedAsync_replaces_options_when_switching_endpoint()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1", "ep-2");
        SeedOptions(harness, "ep-1", channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "A", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        });
        SeedOptions(harness, "ep-2", channels: new[] { 4 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "B", 4, 48000, 24, 32, ControlPanelParseStatus.Parsed, null),
        });

        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        Assert.Equal(new[] { 2 }, vm.Channels);
        await vm.EndpointChangedAsync(vm.Endpoints[1], CancellationToken.None);
        Assert.Equal(new[] { 4 }, vm.Channels);
    }

    [Fact]
    public async Task ApplyAsync_disables_controls_during_apply_then_reenables()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1");
        SeedOptions(harness, channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        });
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3));
        bool busyDuringApply = false;
        harness.Svcl.AfterSetSpeakers = () => busyDuringApply = vm.IsBusy;

        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        vm.SelectChannelIndex(0);
        vm.SelectFormatIndex(0);

        var result = await vm.ApplyAsync(CancellationToken.None);

        Assert.True(busyDuringApply);
        Assert.False(vm.IsBusy);
        Assert.Equal(SwitchStatus.Pass, result.Status);
        Assert.False(vm.CanPlay);
    }

    [Fact]
    public async Task ApplyAsync_reports_rollback_message_on_mismatch()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1");
        SeedOptions(harness, channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        });
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x33))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3));

        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        vm.SelectChannelIndex(0);
        vm.SelectFormatIndex(0);

        var result = await vm.ApplyAsync(CancellationToken.None);

        Assert.Equal(SwitchStatus.FormatMismatch, result.Status);
        Assert.Contains("restored", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.CanPlay);
    }

    [Fact]
    public async Task ApplyAsync_starts_playback_when_pass_and_source_resolved()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1");
        SeedOptions(harness, channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        });
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3));

        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        vm.SelectChannelIndex(0);
        vm.SelectFormatIndex(0);

        var result = await vm.ApplyAsync(CancellationToken.None);

        Assert.Equal(SwitchStatus.Pass, result.Status);
        Assert.True(harness.Playback.IsPlaying);
        Assert.Equal("ep-1", harness.Playback.LastEndpoint);
        Assert.False(vm.CanApply); // apply locked during playback
        Assert.True(vm.CanStop);
        Assert.False(vm.CanPlay);
    }

    [Fact]
    public async Task ApplyAsync_reports_playback_failure_without_rolling_back_audio_settings()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1");
        SeedOptions(harness, channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        });
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3));
        harness.Playback.StartFailure = new FileNotFoundException("WAV missing");

        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        vm.SelectChannelIndex(0);
        vm.SelectFormatIndex(0);

        var result = await vm.ApplyAsync(CancellationToken.None);

        Assert.Equal(SwitchStatus.Pass, result.Status); // audio settings verified
        Assert.False(harness.Playback.IsPlaying);
        Assert.Contains("playback failed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyAsync_stops_playback_before_starting_next_apply()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1");
        SeedOptions(harness, channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        });
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3));

        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        vm.SelectChannelIndex(0);
        vm.SelectFormatIndex(0);
        await vm.ApplyAsync(CancellationToken.None);
        Assert.True(harness.Playback.IsPlaying);

        // Switch to a different setting — service must stop existing stream first.
        await vm.ApplyAsync(CancellationToken.None);
        Assert.True(harness.Playback.StopCalls >= 1);
    }

    private static MainViewModel NewViewModel(out GuiHarness harness)
    {
        harness = new GuiHarness();
        return harness.CreateViewModel();
    }

    private static SavedFormat Format(int channels, int bits, int rate, uint mask) =>
        new(SavedFormat.WaveFormatExtensible, channels, rate, 32, bits, mask, new byte[40]);

    private static void SeedEndpoints(GuiHarness harness, params string[] ids)
    {
        foreach (var id in ids)
        {
            harness.Endpoints.Endpoints.Add(new EndpointInfo(id, id, id, null, null, true));
        }
    }

    private static void SeedOptions(
        GuiHarness harness,
        string? endpointId = null,
        IReadOnlyList<int>? channels = null,
        IReadOnlyList<ControlPanelFormatItem>? formats = null)
    {
        harness.OptionsByEndpoint[endpointId ?? "*"] = new ControlPanelFormatResult(
            formats ?? Array.Empty<ControlPanelFormatItem>(),
            (channels ?? Array.Empty<int>()).Select((value, index) =>
                new ControlPanelSpeakerConfigurationItem(index, $"{value} channels", value)).ToList(),
            channels?.Max(),
            new ControlPanelFormatSnapshot(DateTimeOffset.MinValue, DateTimeOffset.MinValue, "fake", true, true, null));
    }

    private sealed class GuiHarness
    {
        public FakeEndpointProvider Endpoints { get; } = new();
        public FakeControlPanelFormatProvider ControlPanel { get; } = new();
        public FakeSvclClient Svcl { get; } = new();
        public FakePlaybackService Playback { get; } = new();
        public Dictionary<string, ControlPanelFormatResult> OptionsByEndpoint { get; } = new();

        public GuiHarness()
        {
            ControlPanel.Provider = (endpoint, _) =>
            {
                if (OptionsByEndpoint.TryGetValue(endpoint.EndpointId, out var specific))
                {
                    return specific;
                }
                if (OptionsByEndpoint.TryGetValue("*", out var any))
                {
                    return any;
                }
                return new ControlPanelFormatResult(
                    Array.Empty<ControlPanelFormatItem>(),
                    Array.Empty<ControlPanelSpeakerConfigurationItem>(),
                    null,
                    new ControlPanelFormatSnapshot(DateTimeOffset.MinValue, DateTimeOffset.MinValue, "fake", true, true, null));
            };
        }

        public MainViewModel CreateViewModel()
        {
            var service = new DeviceConfigurationService(Endpoints, ControlPanel, Svcl);
            return new MainViewModel(
                service,
                Playback,
                createCancellation: null,
                resolveWaveSource: _ => new WaveSource(new WaveFormat(48000, 2, 16, 4), new byte[48000]),
                dispatcher: null);
        }
    }

    private sealed class FakePlaybackService : IAudioPlaybackService
    {
        public bool IsPlaying { get; private set; }
        public string? LastEndpoint { get; private set; }
        public int StopCalls { get; private set; }
        public Exception? StartFailure { get; set; }
        public event Action<Exception>? PlaybackFailed;

        public void Start(EndpointInfo endpoint, WaveSource source)
        {
            if (StartFailure is not null)
            {
                throw StartFailure;
            }
            IsPlaying = true;
            LastEndpoint = endpoint.EndpointId;
        }

        public void Stop()
        {
            StopCalls++;
            IsPlaying = false;
        }
    }
}