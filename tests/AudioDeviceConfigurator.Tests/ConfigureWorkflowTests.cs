using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Svcl;

namespace AudioDeviceConfigurator.Tests;

public sealed class ConfigureWorkflowTests
{
    [Fact]
    public void Selected_control_panel_options_are_applied_and_verified()
    {
        var harness = ConfiguredHarness();
        harness.Console.EnqueueInput("2", "2", "Y");
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(4, 24, 48000, 0x33));

        var result = harness.Run();

        Assert.Equal(ExitCode.Pass, result);
        Assert.Equal([
            "VerifyInstallation",
            $"Save:{AppHarness.DefaultEndpointId}",
            $"SetSpeakers:{AppHarness.DefaultEndpointId}:33",
            $"SetFormat:{AppHarness.DefaultEndpointId}:4:24:48000",
            $"Save:{AppHarness.DefaultEndpointId}",
        ], harness.Svcl.Operations);
        Assert.Contains("Switch completed and verified", harness.ConsoleText);
        Assert.Single(harness.ControlPanelFormats.EndpointIds);
    }

    [Fact]
    public void Duplicate_four_channel_configurations_are_one_channel_choice()
    {
        var harness = ConfiguredHarness();
        harness.ControlPanelFormats.SpeakerConfigurations.Insert(2, new(2, "環場", 4));
        harness.Console.EnqueueInput("C");

        Assert.Equal(ExitCode.Cancelled, harness.Run());
        Assert.Equal(1, harness.Console.Lines.Count(line => line.Contains("4 channels", StringComparison.Ordinal)));
        Assert.Empty(harness.Svcl.Operations);
    }

    [Fact]
    public void Empty_confirmation_applies_selected_settings()
    {
        var harness = ConfiguredHarness();
        harness.Console.EnqueueInput("1", "1", "");
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3));

        Assert.Equal(ExitCode.Pass, harness.Run());
        Assert.Contains($"SetFormat:{AppHarness.DefaultEndpointId}:2:16:44100", harness.Svcl.Operations);
    }

    [Fact]
    public void Confirmation_other_than_y_cancels_without_svcl()
    {
        var harness = ConfiguredHarness();
        harness.Console.EnqueueInput("1", "1", "N");

        Assert.Equal(ExitCode.Cancelled, harness.Run());
        Assert.Empty(harness.Svcl.Operations);
    }

    [Fact]
    public void Missing_original_channel_mask_stops_before_setter()
    {
        var harness = ConfiguredHarness();
        harness.Console.EnqueueInput("1", "1", "Y");
        harness.Svcl.EnqueueSavedFormat(Format(2, 16, 44100, 0));

        Assert.Equal(ExitCode.SystemError, harness.Run());
        Assert.DoesNotContain(harness.Svcl.Operations, operation => operation.StartsWith("Set", StringComparison.Ordinal));
    }

    [Fact]
    public void Readback_mismatch_restores_original_settings()
    {
        var harness = ConfiguredHarness();
        harness.Console.EnqueueInput("2", "2", "Y");
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(4, 16, 48000, 0x33))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3));

        var result = harness.Run();

        Assert.Equal(ExitCode.FormatMismatch, result);
        Assert.Equal([
            "VerifyInstallation",
            $"Save:{AppHarness.DefaultEndpointId}",
            $"SetSpeakers:{AppHarness.DefaultEndpointId}:33",
            $"SetFormat:{AppHarness.DefaultEndpointId}:4:24:48000",
            $"Save:{AppHarness.DefaultEndpointId}",
            $"SetSpeakers:{AppHarness.DefaultEndpointId}:3",
            $"SetFormat:{AppHarness.DefaultEndpointId}:2:16:44100",
            $"Save:{AppHarness.DefaultEndpointId}",
        ], harness.Svcl.Operations);
        Assert.Contains("Original settings were restored and verified", harness.ConsoleText);
    }

    [Fact]
    public void Cancellation_after_first_setter_restores_original_settings()
    {
        var harness = ConfiguredHarness();
        harness.Console.EnqueueInput("2", "2", "Y");
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3));
        harness.Svcl.AfterSetSpeakers = () => harness.Cancellation.Cancel();

        var result = harness.Run();

        Assert.Equal(ExitCode.Cancelled, result);
        Assert.Contains($"SetSpeakers:{AppHarness.DefaultEndpointId}:3", harness.Svcl.Operations);
        Assert.Contains($"SetFormat:{AppHarness.DefaultEndpointId}:2:16:44100", harness.Svcl.Operations);
        Assert.Contains("Original settings were restored and verified", harness.ConsoleText);
    }

    [Fact]
    public void Rollback_verification_failure_is_system_error()
    {
        var harness = ConfiguredHarness();
        harness.Console.EnqueueInput("2", "2", "Y");
        harness.Svcl
            .EnqueueSavedFormat(Format(2, 16, 44100, 0x3))
            .EnqueueSavedFormat(Format(4, 16, 48000, 0x33))
            .EnqueueSavedFormat(Format(4, 16, 48000, 0x33));

        Assert.Equal(ExitCode.SystemError, harness.Run());
        Assert.Contains("may not have been completely restored", harness.ErrorText);
    }

    private static AppHarness ConfiguredHarness()
    {
        var harness = new AppHarness().WithEndpoint();
        harness.ControlPanelFormats.Items.AddRange([
            new(0, "16 bit, 44100 Hz", null, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
            new(1, "24 bit, 48000 Hz", null, 48000, 24, 32, ControlPanelParseStatus.Parsed, null),
        ]);
        harness.ControlPanelFormats.SpeakerConfigurations.AddRange([
            new(0, "Stereo", 2),
            new(1, "Quadraphonic", 4),
            new(2, "5.1 Surround", 6),
        ]);
        return harness;
    }

    private static SavedFormat Format(int channels, int bits, int rate, uint mask) =>
        new(SavedFormat.WaveFormatExtensible, channels, rate, 32, bits, mask, new byte[40]);
}
