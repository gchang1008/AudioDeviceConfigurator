using System.Diagnostics;
using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Application;
using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Tests.Fakes;

namespace AudioDeviceConfigurator.Tests;

/// <summary>Behavior tests for the shared device configuration service used by the CLI and GUI.</summary>
public sealed class DeviceConfigurationServiceTests
{
    [Fact]
    public async Task Lists_only_active_render_endpoints()
    {
        var harness = Service();

        var endpoints = await harness.CreateService().ListEndpointsAsync(CancellationToken.None);

        Assert.Equal(new[] { "endpoint-1", "endpoint-2" }, endpoints.Select(item => item.EndpointId).ToArray());
        Assert.True(endpoints.Single(item => item.EndpointId == "endpoint-1").IsDefault);
    }

    [Fact]
    public async Task GetOptions_does_not_block_the_calling_thread()
    {
        var harness = Service();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        harness.ControlPanel.Provider = (_, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return new ControlPanelFormatResult(
                Array.Empty<ControlPanelFormatItem>(),
                Array.Empty<ControlPanelSpeakerConfigurationItem>(),
                null,
                new ControlPanelFormatSnapshot(
                    DateTimeOffset.MinValue, DateTimeOffset.MinValue, "fake", true, true, null));
        };

        var service = harness.CreateService();
        var started = Stopwatch.GetTimestamp();
        var returnedTask = service.GetOptionsAsync(Endpoint("endpoint-1"), CancellationToken.None);
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.True(elapsed < TimeSpan.FromMilliseconds(500), $"Call blocked for {elapsed}.");
        Assert.False(returnedTask.IsCompleted);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        release.Set();
        await returnedTask;
    }

    [Fact]
    public async Task GetOptions_returns_distinct_channels_and_parsed_formats()
    {
        var harness = Service();
        SeedOptions(harness, items: new[]
        {
            ParsedItem(0, "16 bit, 44100 Hz", channels: 2, bits: 16, rate: 44100),
            ParsedItem(1, "16 bit, 48000 Hz", channels: 2, bits: 16, rate: 48000),
            ParsedItem(2, "24 bit, 96000 Hz", channels: 4, bits: 24, rate: 96000),
        }, speakers: new[]
        {
            new ControlPanelSpeakerConfigurationItem(0, "Stereo", 2),
            new ControlPanelSpeakerConfigurationItem(1, "Quadraphonic", 4),
            new ControlPanelSpeakerConfigurationItem(2, "5.1 Surround", 6),
        });

        var result = await harness.CreateService().GetOptionsAsync(Endpoint("endpoint-1"), CancellationToken.None);

        var available = Assert.IsType<EndpointOptionsResult.Available>(result);
        Assert.Equal(new[] { 2, 4, 6 }, available.Options.Channels);
        Assert.Equal(3, available.Options.Formats.Count);
        Assert.All(available.Options.Formats, item => Assert.Equal(ControlPanelParseStatus.Parsed, item.ParseStatus));
    }

    [Fact]
    public async Task GetOptions_returns_not_applicable_when_catalog_is_empty()
    {
        var harness = Service();
        SeedOptions(harness, items: Array.Empty<ControlPanelFormatItem>(), speakers: Array.Empty<ControlPanelSpeakerConfigurationItem>());

        var result = await harness.CreateService().GetOptionsAsync(Endpoint("endpoint-1"), CancellationToken.None);

        Assert.IsType<EndpointOptionsResult.NotApplicable>(result);
    }

    [Fact]
    public async Task Apply_returns_pass_when_svcl_verifies_readback()
    {
        var harness = Service();
        SeedOptions(harness, items: new[]
        {
            ParsedItem(0, "16 bit, 44100 Hz", channels: 2, bits: 16, rate: 44100),
        }, speakers: new[]
        {
            new ControlPanelSpeakerConfigurationItem(0, "Stereo", 2),
        });
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3));

        var result = await Apply(harness, Endpoint("endpoint-1"), 2, 0, CancellationToken.None);

        Assert.Equal(SwitchStatus.Pass, result.Status);
        Assert.Contains("SetSpeakers:endpoint-1:3", harness.Svcl.Operations);
        Assert.Contains("SetFormat:endpoint-1:2:16:44100", harness.Svcl.Operations);
    }

    [Fact]
    public async Task Apply_restores_original_settings_when_readback_mismatches()
    {
        var harness = Service();
        SeedOptions(harness, items: new[]
        {
            ParsedItem(0, "16 bit, 44100 Hz", channels: 2, bits: 16, rate: 44100),
        }, speakers: new[]
        {
            new ControlPanelSpeakerConfigurationItem(0, "Stereo", 2),
        });
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x33))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3));

        var result = await Apply(harness, Endpoint("endpoint-1"), 2, 0, CancellationToken.None);

        Assert.Equal(SwitchStatus.FormatMismatch, result.Status);
        Assert.True(result.RollbackVerified);
        // rollback set the original mask (0x3) a second time after the readback mismatch.
        Assert.Equal(2, harness.Svcl.Operations.Count(op => op.StartsWith("SetSpeakers:endpoint-1:3", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Apply_returns_system_error_when_original_mask_is_missing()
    {
        var harness = Service();
        SeedOptions(harness, items: new[]
        {
            ParsedItem(0, "16 bit, 44100 Hz", channels: 2, bits: 16, rate: 44100),
        }, speakers: new[]
        {
            new ControlPanelSpeakerConfigurationItem(0, "Stereo", 2),
        });
        harness.Svcl.EnqueueSavedFormat(Format(2, 16, 44100, 0));

        var result = await Apply(harness, Endpoint("endpoint-1"), 2, 0, CancellationToken.None);

        Assert.Equal(SwitchStatus.SystemError, result.Status);
        Assert.NotNull(result.Message);
        Assert.Contains("mask", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(harness.Svcl.Operations, op => op.StartsWith("Set", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetOptions_returns_not_applicable_when_endpoint_has_no_selectable_options()
    {
        var harness = Service();
        SeedOptions(harness, items: Array.Empty<ControlPanelFormatItem>(), speakers: Array.Empty<ControlPanelSpeakerConfigurationItem>());

        var result = await harness.CreateService()
            .GetOptionsAsync(Endpoint("endpoint-1"), CancellationToken.None);

        Assert.IsType<EndpointOptionsResult.NotApplicable>(result);
    }

    [Fact]
    public async Task Apply_reports_cancellation_when_token_cancelled_after_first_setter()
    {
        var harness = Service();
        SeedOptions(harness, items: new[]
        {
            ParsedItem(0, "16 bit, 44100 Hz", channels: 2, bits: 16, rate: 44100),
        }, speakers: new[]
        {
            new ControlPanelSpeakerConfigurationItem(0, "Stereo", 2),
        });
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3));
        var source = new CancellationTokenSource();
        harness.Svcl.AfterSetSpeakers = () => source.Cancel();

        var result = await Apply(harness, Endpoint("endpoint-1"), 2, 0, source.Token);

        Assert.Equal(SwitchStatus.Cancelled, result.Status);
        Assert.True(result.RollbackVerified);
        Assert.Equal(2, harness.Svcl.Operations.Count(op => op.StartsWith("SetSpeakers:endpoint-1:3", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Apply_returns_system_error_with_unverified_rollback_when_readback_mismatches_and_restore_mismatches()
    {
        var harness = Service();
        SeedOptions(harness, items: new[]
        {
            ParsedItem(0, "16 bit, 44100 Hz", channels: 2, bits: 16, rate: 44100),
        }, speakers: new[]
        {
            new ControlPanelSpeakerConfigurationItem(0, "Stereo", 2),
        });
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x33)) // mismatch after apply
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x33)); // mismatch after rollback

        var result = await Apply(harness, Endpoint("endpoint-1"), 2, 0, CancellationToken.None);

        Assert.Equal(SwitchStatus.SystemError, result.Status);
        Assert.False(result.RollbackVerified);
        Assert.Contains("may not have been completely restored", result.Message!);
    }

    private static async Task<SwitchResult> Apply(
        ServiceHarness harness,
        EndpointInfo endpoint,
        int channels,
        int formatIndex,
        CancellationToken cancellationToken)
    {
        var service = harness.CreateService();
        var result = await service.GetOptionsAsync(endpoint, cancellationToken);
        var available = Assert.IsType<EndpointOptionsResult.Available>(result);
        return await service.ApplyAsync(
            endpoint,
            available.Options,
            channels,
            formatIndex,
            cancellationToken);
    }

    private static ServiceHarness Service()
    {
        var harness = new ServiceHarness();
        harness.Endpoints.Endpoints.AddRange(new[]
        {
            new EndpointInfo("endpoint-1", "Speaker A", "A", null, null, true),
            new EndpointInfo("endpoint-2", "Speaker B", "B", null, null, false),
        });
        return harness;
    }

    private static EndpointInfo Endpoint(string id, string name = "Speaker A") =>
        new(id, name, name, null, null, true);

    private static void SeedOptions(
        ServiceHarness harness,
        IReadOnlyList<ControlPanelFormatItem> items,
        IReadOnlyList<ControlPanelSpeakerConfigurationItem> speakers)
    {
        harness.ControlPanel.Items.Clear();
        harness.ControlPanel.SpeakerConfigurations.Clear();
        harness.ControlPanel.Items.AddRange(items);
        harness.ControlPanel.SpeakerConfigurations.AddRange(speakers);
    }

    private static ControlPanelFormatItem ParsedItem(int index, string text, int channels, int bits, int rate) =>
        new(index, text, channels, rate, bits, bits, ControlPanelParseStatus.Parsed, null);

    private static SavedFormat Format(int channels, int bits, int rate, uint mask) =>
        new(SavedFormat.WaveFormatExtensible, channels, rate, 32, bits, mask, new byte[40]);

    private sealed class ServiceHarness
    {
        public FakeEndpointProvider Endpoints { get; } = new();
        public FakeControlPanelFormatProvider ControlPanel { get; } = new();
        public FakeSvclClient Svcl { get; } = new();

        public DeviceConfigurationService CreateService() =>
            new(Endpoints, ControlPanel, Svcl);
    }
}