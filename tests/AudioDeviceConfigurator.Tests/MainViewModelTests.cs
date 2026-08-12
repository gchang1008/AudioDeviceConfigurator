using System.ComponentModel;
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
    public async Task EndpointChangedAsync_builds_fixed_switch_options_and_disables_unsupported_values()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1");
        SeedOptions(harness, channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(7, "16 bit, 32000 Hz", null, 32000, 16, 16, ControlPanelParseStatus.Parsed, null),
            new ControlPanelFormatItem(12, "24 bit, 48000 Hz", null, 48000, 24, 32, ControlPanelParseStatus.Parsed, null),
            new ControlPanelFormatItem(19, "16 bit, 12345 Hz", null, 12345, 16, 16, ControlPanelParseStatus.Parsed, null),
        });

        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);

        Assert.Equal(new[] { 2, 4, 6, 8 }, vm.ChannelOptions.Select(item => item.Value));
        Assert.True(vm.ChannelOptions.Single(item => item.Value == 2).IsEnabled);
        Assert.False(vm.ChannelOptions.Single(item => item.Value == 4).IsEnabled);
        Assert.Contains(vm.SampleRateOptions, item => item.Value == 32000 && item.IsEnabled);
        Assert.True(vm.SampleRateOptions.Single(item => item.Value == 32000).IsEnabled);
        Assert.False(vm.SampleRateOptions.Single(item => item.Value == 44100).IsEnabled);
        // Sample rates outside the 32k–192k band (e.g. 12345) are filtered out.
        Assert.DoesNotContain(vm.SampleRateOptions, item => item.Value == 12345);
        Assert.Equal(new[] { 16, 20, 24, 32 }, vm.BitDepthOptions.Select(item => item.Value));
        Assert.False(vm.BitDepthOptions.Single(item => item.Value == 20).IsEnabled);
    }

    [Fact]
    public async Task Sample_rate_and_bit_depth_switches_clear_invalid_selection_without_auto_selecting()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1");
        SeedOptions(harness, channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "16 bit, 32000 Hz", null, 32000, 16, 16, ControlPanelParseStatus.Parsed, null),
            new ControlPanelFormatItem(1, "24 bit, 48000 Hz", null, 48000, 24, 32, ControlPanelParseStatus.Parsed, null),
        });

        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        vm.SelectSampleRate(48000);
        vm.SelectBitDepth(24);

        vm.SelectSampleRate(32000);

        Assert.Equal(32000, vm.SelectedSampleRate);
        Assert.Null(vm.SelectedBitDepth);
        Assert.False(vm.BitDepthOptions.Single(item => item.Value == 24).IsEnabled);
        Assert.True(vm.BitDepthOptions.Single(item => item.Value == 16).IsEnabled);
        Assert.DoesNotContain(vm.BitDepthOptions, item => item.IsSelected);
    }

    [Fact]
    public async Task LoadActiveSettingsAsync_seeds_active_values_from_service()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1");
        harness.Svcl.EnqueueSavedFormat(Format(2, 16, 44100, 0x3));

        await vm.LoadActiveSettingsAsync(harness.Endpoints.Endpoints[0], CancellationToken.None);

        Assert.Equal(2, vm.ActiveChannel);
        Assert.Equal(44100, vm.ActiveSampleRate);
        Assert.Equal(16, vm.ActiveBitDepth);
    }

    [Fact]
    public async Task LoadActiveSettingsAsync_selects_exact_current_endpoint_options_without_applying()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1");
        SeedOptions(harness, channels: new[] { 2, 4 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
            new ControlPanelFormatItem(1, "24 bit, 48000 Hz", 4, 48000, 24, 32, ControlPanelParseStatus.Parsed, null),
        });
        harness.Svcl.EnqueueSavedFormat(Format(4, 24, 48000, 0x33));

        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        await vm.LoadActiveSettingsAsync(vm.Endpoints[0], CancellationToken.None);

        Assert.Equal(4, vm.SelectedChannel);
        Assert.Equal(48000, vm.SelectedSampleRate);
        Assert.Equal(24, vm.SelectedBitDepth);
        Assert.True(vm.ChannelOptions.Single(item => item.Value == 4).IsSelected);
        Assert.True(vm.SampleRateOptions.Single(item => item.Value == 48000).IsSelected);
        Assert.True(vm.BitDepthOptions.Single(item => item.Value == 24).IsSelected);
        Assert.True(vm.CanApply);
        Assert.False(harness.Playback.IsPlaying);
        Assert.DoesNotContain(harness.Svcl.Operations, operation => operation.StartsWith("Set", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadActiveSettingsAsync_leaves_unsupported_current_values_unselected()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1");
        SeedOptions(harness, channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "16 bit, 48000 Hz", 2, 48000, 16, 16, ControlPanelParseStatus.Parsed, null),
            new ControlPanelFormatItem(1, "24 bit, 44100 Hz", 2, 44100, 24, 32, ControlPanelParseStatus.Parsed, null),
        });
        harness.Svcl.EnqueueSavedFormat(Format(6, 24, 48000, 0x3f));

        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        await vm.LoadActiveSettingsAsync(vm.Endpoints[0], CancellationToken.None);

        Assert.Equal(6, vm.ActiveChannel);
        Assert.Equal(48000, vm.ActiveSampleRate);
        Assert.Equal(24, vm.ActiveBitDepth);
        Assert.Null(vm.SelectedChannel);
        Assert.Null(vm.SelectedSampleRate);
        Assert.Null(vm.SelectedBitDepth);
        Assert.DoesNotContain(vm.ChannelOptions, option => option.IsSelected);
        Assert.DoesNotContain(vm.SampleRateOptions, option => option.IsSelected);
        Assert.DoesNotContain(vm.BitDepthOptions, option => option.IsSelected);
        Assert.False(vm.CanApply);
    }

    [Fact]
    public async Task LoadActiveSettingsAsync_clears_when_service_returns_null()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1");
        // Enqueue a format with ChannelMask = 0 to simulate the "unavailable" case.
        harness.Svcl.EnqueueSavedFormat(new SavedFormat(SavedFormat.WaveFormatExtensible, 2, 44100, 16, 16, 0, new byte[40]));

        await vm.LoadActiveSettingsAsync(harness.Endpoints.Endpoints[0], CancellationToken.None);

        Assert.Null(vm.ActiveChannel);
        Assert.Null(vm.ActiveSampleRate);
        Assert.Null(vm.ActiveBitDepth);
    }

    [Fact]
    public async Task ApplyAsync_records_active_settings_and_clears_on_endpoint_change()
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
        Assert.Null(vm.ActiveChannel);

        vm.SelectChannelIndex(0);
        vm.SelectFormatIndex(0);
        await vm.ApplyAsync(CancellationToken.None);

        Assert.Equal(2, vm.ActiveChannel);
        Assert.Equal(44100, vm.ActiveSampleRate);
        Assert.Equal(16, vm.ActiveBitDepth);
        Assert.Equal("Channel: 2 ch", vm.ActiveChannelDisplay);
        Assert.Equal("Sample Rate: 44,100 Hz", vm.ActiveSampleRateDisplay);
        Assert.Equal("Bit Depth: 16-bit", vm.ActiveBitDepthDisplay);

        // Switching endpoint clears the active settings.
        SeedEndpoints(harness, "ep-2");
        await vm.EndpointChangedAsync(harness.Endpoints.Endpoints[^1], CancellationToken.None);
        Assert.Null(vm.ActiveChannel);
        Assert.Null(vm.ActiveSampleRate);
        Assert.Null(vm.ActiveBitDepth);
    }

    [Fact]
    public void Common_switch_options_are_seeded_before_endpoint_selected()
    {
        var vm = NewViewModel(out _);
        Assert.Equal(new[] { 2, 4, 6, 8 }, vm.ChannelOptions.Select(item => item.Value));
        Assert.False(vm.ChannelOptions.Any(item => item.IsEnabled));
        Assert.Equal(new[] { 32000, 44100, 48000, 88200, 96000, 176400, 192000 },
            vm.SampleRateOptions.Select(item => item.Value));
        Assert.False(vm.SampleRateOptions.Any(item => item.IsEnabled));
        Assert.Equal(new[] { 16, 20, 24, 32 }, vm.BitDepthOptions.Select(item => item.Value));
        Assert.False(vm.BitDepthOptions.Any(item => item.IsEnabled));
    }

    [Fact]
    public async Task SampleRateOptions_filters_endpoint_formats_outside_32k_to_192k_band()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1");
        SeedOptions(harness, channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "low", null, 8000, 16, 16, ControlPanelParseStatus.Parsed, null),
            new ControlPanelFormatItem(1, "in band", null, 96000, 16, 16, ControlPanelParseStatus.Parsed, null),
            new ControlPanelFormatItem(2, "above", null, 384000, 16, 16, ControlPanelParseStatus.Parsed, null),
        });

        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);

        Assert.Contains(vm.SampleRateOptions, item => item.Value == 96000);
        Assert.DoesNotContain(vm.SampleRateOptions, item => item.Value == 8000);
        Assert.DoesNotContain(vm.SampleRateOptions, item => item.Value == 384000);
        Assert.DoesNotContain(vm.SampleRateOptions, item => item.Value == 352800);
    }

    [Fact]
    public async Task Exact_switch_selection_enables_apply_and_maps_to_filtered_format_index()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1");
        SeedOptions(harness, channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(8, "duplicate", null, 32000, 16, 16, ControlPanelParseStatus.Parsed, null),
            new ControlPanelFormatItem(21, "duplicate", null, 48000, 24, 32, ControlPanelParseStatus.Parsed, null),
        });
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 32000, 0x3))
            .EnqueueSavedFormat(Format(2, 24, 48000, 0x3));

        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        vm.SelectChannel(2);
        vm.SelectSampleRate(48000);
        vm.SelectBitDepth(24);

        Assert.True(vm.CanApply);
        var result = await vm.ApplyAsync(CancellationToken.None);

        Assert.Equal(SwitchStatus.Pass, result.Status);
        Assert.Contains("SetFormat:ep-1:2:24:48000", harness.Svcl.Operations);
    }

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
        bool selectionLockedDuringApply = false;
        harness.Svcl.AfterSetSpeakers = () =>
        {
            busyDuringApply = vm.IsBusy;
            selectionLockedDuringApply = !vm.CanChangeSelection;
        };

        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        vm.SelectChannelIndex(0);
        vm.SelectFormatIndex(0);

        var result = await vm.ApplyAsync(CancellationToken.None);

        Assert.True(busyDuringApply);
        Assert.True(selectionLockedDuringApply);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanChangeSelection);
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
        Assert.True(vm.CanPlay);
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
        Assert.True(vm.CanApply); // apply is available while playing; ApplyAsync stops playback first
        Assert.True(vm.CanStop);
        Assert.False(vm.CanPlay);
        Assert.Single(harness.ControlPanel.EndpointIds);
    }

    [Fact]
    public async Task ApplyAsync_does_not_start_playback_or_update_active_when_token_cancelled()
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
        var startCallsBefore = harness.Playback.StartCalls;

        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        vm.SelectChannelIndex(0);
        vm.SelectFormatIndex(0);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await vm.ApplyAsync(cts.Token);

        Assert.Equal(SwitchStatus.Cancelled, result.Status);
        Assert.Equal(startCallsBefore, harness.Playback.StartCalls);
        Assert.False(harness.Playback.IsPlaying);
        Assert.Null(vm.ActiveChannel);
        Assert.Null(vm.ActiveSampleRate);
        Assert.Null(vm.ActiveBitDepth);
    }

    [Fact]
    public async Task Play_starts_the_verified_endpoint_again_after_stop()
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
        await vm.ApplyAsync(CancellationToken.None);
        vm.StopPlayback();

        vm.Play();

        Assert.True(harness.Playback.IsPlaying);
        Assert.Equal(2, harness.Playback.StartCalls);
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
        Assert.True(vm.CanPlay);
        Assert.Contains("playback failed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);

        harness.Playback.StartFailure = null;
        vm.Play();

        Assert.True(harness.Playback.IsPlaying);
        Assert.False(vm.CanPlay);
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

    [Fact]
    public async Task RefreshEndpointsAsync_preserves_unaffected_selection_options_and_playback()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1");
        SeedOptions(harness, "ep-1", channels: new[] { 2 }, formats:
        [
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        ]);
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3));
        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        vm.SelectChannel(2);
        vm.SelectSampleRate(44100);
        vm.SelectBitDepth(16);
        await vm.ApplyAsync(CancellationToken.None);
        var stopCalls = harness.Playback.StopCalls;
        var optionReads = harness.ControlPanel.EndpointIds.Count;

        harness.Endpoints.Endpoints[0] = harness.Endpoints.Endpoints[0] with { IsDefault = false };
        harness.Endpoints.Endpoints.Add(new EndpointInfo("ep-2", "ep-2", "ep-2", null, null, true));
        await vm.RefreshEndpointsAsync(
        [
            new AudioEndpointChange(AudioEndpointChangeKind.Added, "ep-2"),
            new AudioEndpointChange(AudioEndpointChangeKind.DefaultChanged, "ep-2"),
        ], CancellationToken.None);

        Assert.Equal(new[] { "ep-1", "ep-2" }, vm.Endpoints.Select(item => item.EndpointId));
        Assert.Equal("ep-1", vm.SelectedEndpoint?.EndpointId);
        Assert.False(vm.SelectedEndpoint?.IsDefault);
        Assert.Equal(2, vm.SelectedChannel);
        Assert.Equal(44100, vm.SelectedSampleRate);
        Assert.Equal(16, vm.SelectedBitDepth);
        Assert.True(harness.Playback.IsPlaying);
        Assert.Equal(stopCalls, harness.Playback.StopCalls);
        Assert.Equal(optionReads, harness.ControlPanel.EndpointIds.Count);
    }

    [Fact]
    public async Task RefreshEndpointsAsync_clears_removed_selected_endpoint_and_stops_playback()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1");
        SeedOptions(harness, channels: new[] { 2 }, formats:
        [
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        ]);
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3));
        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        vm.SelectChannel(2);
        vm.SelectSampleRate(44100);
        vm.SelectBitDepth(16);
        await vm.ApplyAsync(CancellationToken.None);

        harness.Endpoints.Endpoints.Clear();
        await vm.RefreshEndpointsAsync(
            [new AudioEndpointChange(AudioEndpointChangeKind.Removed, "ep-1")],
            CancellationToken.None);

        Assert.Empty(vm.Endpoints);
        Assert.Null(vm.SelectedEndpoint);
        Assert.Null(vm.SelectedEndpointOptions);
        Assert.Null(vm.SelectedChannel);
        Assert.Null(vm.SelectedSampleRate);
        Assert.Null(vm.SelectedBitDepth);
        Assert.Null(vm.ActiveChannel);
        Assert.False(harness.Playback.IsPlaying);
        Assert.False(vm.CanApply);
        Assert.False(vm.CanPlay);
        Assert.All(vm.ChannelOptions, option => Assert.False(option.IsEnabled));
    }

    [Fact]
    public async Task RefreshEndpointsAsync_reloads_an_affected_endpoint_that_is_still_active()
    {
        var vm = NewViewModel(out var harness);
        SeedEndpoints(harness, "ep-1");
        SeedOptions(harness, "ep-1", channels: new[] { 2 }, formats:
        [
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        ]);
        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);

        SeedOptions(harness, "ep-1", channels: new[] { 4 }, formats:
        [
            new ControlPanelFormatItem(0, "24 bit, 48000 Hz", 4, 48000, 24, 32, ControlPanelParseStatus.Parsed, null),
        ]);
        harness.Svcl.EnqueueSavedFormat(Format(4, 24, 48000, 0x33));
        await vm.RefreshEndpointsAsync(
            [new AudioEndpointChange(AudioEndpointChangeKind.StateChanged, "EP-1")],
            CancellationToken.None);

        Assert.Equal("ep-1", vm.SelectedEndpoint?.EndpointId);
        Assert.Equal(new[] { 4 }, vm.Channels);
        Assert.Equal(4, vm.ActiveChannel);
        Assert.Equal(48000, vm.ActiveSampleRate);
        Assert.Equal(24, vm.ActiveBitDepth);
        Assert.Equal(4, vm.SelectedChannel);
        Assert.Equal(48000, vm.SelectedSampleRate);
        Assert.Equal(24, vm.SelectedBitDepth);
        Assert.Equal(2, harness.ControlPanel.EndpointIds.Count);
    }

    [Fact]
    public async Task RefreshEndpointsAsync_moves_active_playback_to_default_without_loading_options()
    {
        var vm = NewViewModel(out var harness);
        harness.Endpoints.Endpoints.Add(new EndpointInfo("ep-selected", "Selected", "Selected", null, null, false));
        harness.Endpoints.Endpoints.Add(new EndpointInfo("ep-default", "Default", "Default", null, null, true));
        SeedOptions(harness, "ep-selected", channels: new[] { 2 }, formats:
        [
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        ]);
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 24, 48000, 0x3));
        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        vm.SelectChannel(2);
        vm.SelectSampleRate(44100);
        vm.SelectBitDepth(16);
        await vm.ApplyAsync(CancellationToken.None);
        var optionReads = harness.ControlPanel.EndpointIds.Count;

        harness.Endpoints.Endpoints.RemoveAt(0);
        await vm.RefreshEndpointsAsync(
            [new AudioEndpointChange(AudioEndpointChangeKind.Removed, "ep-selected")],
            CancellationToken.None);

        Assert.Null(vm.SelectedEndpoint);
        Assert.Null(vm.SelectedEndpointOptions);
        Assert.True(harness.Playback.IsPlaying);
        Assert.Equal("ep-default", harness.Playback.LastEndpoint);
        Assert.Equal(2, harness.Playback.StartCalls);
        Assert.Equal(2, vm.ActiveChannel);
        Assert.Equal(48000, vm.ActiveSampleRate);
        Assert.Equal(24, vm.ActiveBitDepth);
        Assert.Equal(optionReads, harness.ControlPanel.EndpointIds.Count);
        Assert.False(vm.CanApply);
        Assert.All(vm.ChannelOptions, option => Assert.False(option.IsEnabled));
    }

    [Fact]
    public async Task RefreshEndpointsAsync_reads_default_active_format_without_starting_if_playback_was_stopped()
    {
        var vm = NewViewModel(out var harness);
        harness.Endpoints.Endpoints.Add(new EndpointInfo("ep-selected", "Selected", "Selected", null, null, false));
        harness.Endpoints.Endpoints.Add(new EndpointInfo("ep-default", "Default", "Default", null, null, true));
        SeedOptions(harness, "ep-selected", channels: new[] { 2 }, formats:
        [
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        ]);
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(6, 24, 96000, 0x3f));
        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        vm.SelectChannel(2);
        vm.SelectSampleRate(44100);
        vm.SelectBitDepth(16);
        await vm.ApplyAsync(CancellationToken.None);
        vm.StopPlayback();
        var optionReads = harness.ControlPanel.EndpointIds.Count;

        harness.Endpoints.Endpoints.RemoveAt(0);
        await vm.RefreshEndpointsAsync(
            [new AudioEndpointChange(AudioEndpointChangeKind.Removed, "ep-selected")],
            CancellationToken.None);

        Assert.Null(vm.SelectedEndpoint);
        Assert.False(harness.Playback.IsPlaying);
        Assert.Equal(1, harness.Playback.StartCalls);
        Assert.Equal(6, vm.ActiveChannel);
        Assert.Equal(96000, vm.ActiveSampleRate);
        Assert.Equal(24, vm.ActiveBitDepth);
        Assert.Equal(optionReads, harness.ControlPanel.EndpointIds.Count);
    }

    [Fact]
    public async Task RefreshEndpointsAsync_reports_default_playback_failure_but_still_reads_active_format()
    {
        var vm = NewViewModel(out var harness);
        harness.Endpoints.Endpoints.Add(new EndpointInfo("ep-selected", "Selected", "Selected", null, null, false));
        harness.Endpoints.Endpoints.Add(new EndpointInfo("ep-default", "Default", "Default", null, null, true));
        SeedOptions(harness, "ep-selected", channels: new[] { 2 }, formats:
        [
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        ]);
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 24, 48000, 0x3));
        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        vm.SelectChannel(2);
        vm.SelectSampleRate(44100);
        vm.SelectBitDepth(16);
        await vm.ApplyAsync(CancellationToken.None);
        harness.Playback.StartFailure = new InvalidOperationException("default unavailable");

        harness.Endpoints.Endpoints.RemoveAt(0);
        await vm.RefreshEndpointsAsync(
            [new AudioEndpointChange(AudioEndpointChangeKind.Removed, "ep-selected")],
            CancellationToken.None);

        Assert.False(harness.Playback.IsPlaying);
        Assert.Equal(2, vm.ActiveChannel);
        Assert.Equal(48000, vm.ActiveSampleRate);
        Assert.Equal(24, vm.ActiveBitDepth);
        Assert.Contains("default playback failed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshEndpointsAsync_preserves_playback_intent_when_render_fails_before_device_notification()
    {
        var vm = NewViewModel(out var harness);
        harness.Endpoints.Endpoints.Add(new EndpointInfo("ep-selected", "Selected", "Selected", null, null, false));
        harness.Endpoints.Endpoints.Add(new EndpointInfo("ep-default", "Default", "Default", null, null, true));
        SeedOptions(harness, "ep-selected", channels: new[] { 2 }, formats:
        [
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        ]);
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 24, 48000, 0x3));
        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        vm.SelectChannel(2);
        vm.SelectSampleRate(44100);
        vm.SelectBitDepth(16);
        await vm.ApplyAsync(CancellationToken.None);

        harness.Playback.Fail(new InvalidOperationException("device invalidated"));
        Assert.False(harness.Playback.IsPlaying);
        harness.Endpoints.Endpoints.RemoveAt(0);
        await vm.RefreshEndpointsAsync(
            [new AudioEndpointChange(AudioEndpointChangeKind.Removed, "ep-selected")],
            CancellationToken.None);

        Assert.True(harness.Playback.IsPlaying);
        Assert.Equal("ep-default", harness.Playback.LastEndpoint);
        Assert.Equal(2, harness.Playback.StartCalls);
        Assert.Contains("playing on default endpoint", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshEndpointsAsync_moves_default_follow_playback_when_original_default_returns()
    {
        var vm = NewViewModel(out var harness);
        harness.Endpoints.Endpoints.Add(new EndpointInfo("external", "External", "External", null, null, true));
        harness.Endpoints.Endpoints.Add(new EndpointInfo("laptop", "Laptop", "Laptop", null, null, false));
        SeedOptions(harness, "external", channels: new[] { 2 }, formats:
        [
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        ]);
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 24, 48000, 0x3))
            .EnqueueSavedFormat(Format(2, 24, 96000, 0x3));
        await vm.LoadEndpointsAsync(CancellationToken.None);
        await vm.EndpointChangedAsync(vm.Endpoints[0], CancellationToken.None);
        vm.SelectChannel(2);
        vm.SelectSampleRate(44100);
        vm.SelectBitDepth(16);
        await vm.ApplyAsync(CancellationToken.None);

        harness.Endpoints.Endpoints.RemoveAt(0);
        harness.Endpoints.Endpoints[0] = harness.Endpoints.Endpoints[0] with { IsDefault = true };
        await vm.RefreshEndpointsAsync(
            [new AudioEndpointChange(AudioEndpointChangeKind.Removed, "external")],
            CancellationToken.None);
        Assert.Equal("laptop", harness.Playback.LastEndpoint);

        harness.Endpoints.Endpoints[0] = harness.Endpoints.Endpoints[0] with { IsDefault = false };
        harness.Endpoints.Endpoints.Add(new EndpointInfo("external", "External", "External", null, null, true));
        await vm.RefreshEndpointsAsync(
        [
            new AudioEndpointChange(AudioEndpointChangeKind.Added, "external"),
            new AudioEndpointChange(AudioEndpointChangeKind.DefaultChanged, "external"),
        ], CancellationToken.None);

        Assert.Null(vm.SelectedEndpoint);
        Assert.True(harness.Playback.IsPlaying);
        Assert.Equal("external", harness.Playback.LastEndpoint);
        Assert.Equal(3, harness.Playback.StartCalls);
        Assert.Equal(96000, vm.ActiveSampleRate);
        Assert.False(vm.CanApply);
    }

    [Fact]
    public async Task Play_and_Stop_use_default_endpoint_when_no_endpoint_is_selected()
    {
        var vm = NewViewModel(out var harness);
        harness.Endpoints.Endpoints.Add(new EndpointInfo("default", "Default", "Default", null, null, true));
        await vm.LoadEndpointsAsync(CancellationToken.None);

        Assert.True(vm.CanPlay);
        vm.Play();

        Assert.True(harness.Playback.IsPlaying);
        Assert.Equal("default", harness.Playback.LastEndpoint);
        Assert.True(vm.CanStop);
        Assert.False(vm.CanPlay);

        vm.StopPlayback();

        Assert.False(harness.Playback.IsPlaying);
        Assert.True(vm.CanPlay);
        Assert.False(vm.CanStop);
    }

    [Fact]
    public async Task Default_follow_updates_Active_without_resuming_after_Stop()
    {
        var vm = NewViewModel(out var harness);
        harness.Endpoints.Endpoints.Add(new EndpointInfo("laptop", "Laptop", "Laptop", null, null, true));
        await vm.LoadEndpointsAsync(CancellationToken.None);
        vm.Play();
        vm.StopPlayback();

        harness.Endpoints.Endpoints[0] = harness.Endpoints.Endpoints[0] with { IsDefault = false };
        harness.Endpoints.Endpoints.Add(new EndpointInfo("external", "External", "External", null, null, true));
        harness.Svcl.EnqueueSavedFormat(Format(2, 24, 96000, 0x3));
        await vm.RefreshEndpointsAsync(
            [new AudioEndpointChange(AudioEndpointChangeKind.DefaultChanged, "external")],
            CancellationToken.None);

        Assert.Null(vm.SelectedEndpoint);
        Assert.False(harness.Playback.IsPlaying);
        Assert.Equal(1, harness.Playback.StartCalls);
        Assert.Equal("laptop", harness.Playback.LastEndpoint);
        Assert.Equal(96000, vm.ActiveSampleRate);
        Assert.True(vm.CanPlay);
        Assert.False(vm.CanStop);
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
        private bool _isPlaying;
        public bool IsPlaying
        {
            get => _isPlaying;
            private set
            {
                _isPlaying = value;
                PropertyChanged?.Invoke(this, new(nameof(IsPlaying)));
            }
        }
        public string? LastEndpoint { get; private set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public Exception? StartFailure { get; set; }
        public event Action<Exception>? PlaybackFailed;
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Start(EndpointInfo endpoint, WaveSource source)
        {
            if (StartFailure is not null)
            {
                throw StartFailure;
            }
            LastEndpoint = endpoint.EndpointId;
            StartCalls++;
            IsPlaying = true;
        }

        public void Fail(Exception exception)
        {
            IsPlaying = false;
            PlaybackFailed?.Invoke(exception);
        }

        public void Stop()
        {
            StopCalls++;
            IsPlaying = false;
        }
    }
}