using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Tests.Fakes;
using AudioDeviceConfigurator.Tests.Fixtures;

namespace AudioDeviceConfigurator.Tests;

public class WorkflowTests
{
    [Fact]
    public void Passes_when_every_edid_declared_format_is_supported_and_applied()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, rates: [48000, 96000], depths: [16, 24]);

        var exit = h.Run();

        Assert.Equal(ExitCode.Pass, exit);
        Assert.Contains("Overall status: PASS", h.ConsoleText);
    }

    [Fact]
    public void Tests_candidates_in_ascending_order()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 6, rates: [96000, 48000], depths: [24, 16]);

        h.Run();

        Assert.Equal(
        [
            (16, 48000, 2), (24, 48000, 2), (16, 96000, 2), (24, 96000, 2),
            (16, 48000, 6), (24, 48000, 6), (16, 96000, 6), (24, 96000, 6),
        ], h.Svcl.AppliedFormats.Take(8));
    }

    [Fact]
    public void Sends_effective_bit_depth_not_container_depth_to_svcl()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [20, 24]);

        h.Run();

        Assert.Equal([(20, 48000, 2), (24, 48000, 2)], h.Svcl.AppliedFormats.Take(2));
    }

    [Fact]
    public void Never_calls_svcl_for_candidates_wasapi_did_not_approve()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16]);
        h.Wasapi.Set(6, 48000, 16, FormatSupportResult.AudclntUnsupportedFormat);

        var exit = h.Run();

        Assert.Equal(ExitCode.FormatMismatch, exit);
        Assert.DoesNotContain(h.Svcl.AppliedFormats, f => f.Channels == 6);
        Assert.Contains(h.Svcl.AppliedFormats, f => f.Channels == 2);
        Assert.Contains(h.Svcl.AppliedFormats, f => f.Channels == 8);
    }

    [Fact]
    public void Records_the_exact_hresult_for_every_candidate()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);
        h.Wasapi.DefaultHResult = unchecked((int)0x8889000A); // AUDCLNT_E_DEVICE_IN_USE

        var exit = h.Run();

        Assert.Equal(ExitCode.FormatMismatch, exit);
        var candidate = h.JsonReport.GetProperty("Candidates")[0];
        Assert.Equal("0x8889000A", candidate.GetProperty("WasapiHResult").GetString());
        Assert.Equal("Error", candidate.GetProperty("WasapiResult").GetString());
        Assert.Equal("WasapiError", candidate.GetProperty("Status").GetString());
    }

    [Fact]
    public void Distinguishes_unsupported_format_from_other_hresults()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);
        h.Wasapi.DefaultHResult = FormatSupportResult.AudclntUnsupportedFormat;

        h.Run();

        var candidate = h.JsonReport.GetProperty("Candidates")[0];
        Assert.Equal("Unsupported", candidate.GetProperty("WasapiResult").GetString());
        Assert.Equal("UnsupportedByWasapi", candidate.GetProperty("Status").GetString());
        Assert.Equal("Skipped", candidate.GetProperty("ApplyResult").GetString());
    }

    [Fact]
    public void Fails_when_svcl_substitutes_a_different_channel_count()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16]);
        h.Svcl.SubstituteOnSet = f => f.Channels == 8 ? (2, f.SampleRate, f.EffectiveBits) : f;

        var exit = h.Run();

        Assert.Equal(ExitCode.FormatMismatch, exit);
        var failed = h.JsonReport.GetProperty("Candidates")
            .EnumerateArray().Single(c => c.GetProperty("Channels").GetInt32() == 8);
        Assert.Equal("Mismatched", failed.GetProperty("ApplyResult").GetString());
        Assert.Equal(2, failed.GetProperty("ReadbackChannels").GetInt32());
    }

    [Fact]
    public void Fails_when_svcl_substitutes_a_different_sample_rate()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, rates: [96000], depths: [16]);
        h.Svcl.SubstituteOnSet = f => (f.Channels, 48000, f.EffectiveBits);

        Assert.Equal(ExitCode.FormatMismatch, h.Run());
    }

    [Fact]
    public void Fails_when_svcl_substitutes_a_different_bit_depth()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [24]);
        h.Svcl.SubstituteOnSet = f => (f.Channels, f.SampleRate, 16);

        Assert.Equal(ExitCode.FormatMismatch, h.Run());
    }

    [Fact]
    public void Accepts_a_delayed_readback_within_the_three_second_window()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16])
            .WithOriginalFormat(2, 44100, 16);
        h.Svcl.DelayedReadbackPolls = 3;

        var exit = h.Run();

        Assert.Equal(ExitCode.Pass, exit);
        Assert.Contains(h.Clock.Sleeps, s => s == TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void Fails_a_readback_that_never_arrives_within_three_seconds()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, rates: [96000], depths: [16]);
        h.Svcl.DelayedReadbackPolls = 1000;

        var exit = h.Run();

        Assert.Equal(ExitCode.FormatMismatch, exit);
        var candidate = h.JsonReport.GetProperty("Candidates")[0];
        Assert.Equal("Mismatched", candidate.GetProperty("ApplyResult").GetString());
    }

    [Fact]
    public void Polls_no_more_than_the_three_second_budget()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, rates: [96000], depths: [16]);
        h.Svcl.DelayedReadbackPolls = 1000;

        h.Run();

        var polls = h.Clock.Sleeps.Count(s => s == TimeSpan.FromMilliseconds(200));
        Assert.InRange(polls, 10, 16);
    }

    [Fact]
    public void Reads_effective_bit_depth_from_valid_bits_of_an_extensible_format()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [24]);

        var exit = h.Run();

        Assert.Equal(ExitCode.Pass, exit);
        var candidate = h.JsonReport.GetProperty("Candidates")[0];
        Assert.Equal(24, candidate.GetProperty("ReadbackEffectiveBits").GetInt32());
        Assert.Equal(32, candidate.GetProperty("ReadbackContainerBits").GetInt32());
    }

    [Fact]
    public void Reads_effective_bit_depth_from_container_bits_of_a_plain_waveformatex()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);
        h.Svcl.WriteExtensible = false;

        var exit = h.Run();

        Assert.Equal(ExitCode.Pass, exit);
        var candidate = h.JsonReport.GetProperty("Candidates")[0];
        Assert.Equal(16, candidate.GetProperty("ReadbackEffectiveBits").GetInt32());
    }

    [Fact]
    public void Distinguishes_twenty_valid_bits_in_a_twenty_four_bit_container()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [20]);

        var exit = h.Run();

        Assert.Equal(ExitCode.Pass, exit);
        var candidate = h.JsonReport.GetProperty("Candidates")[0];
        Assert.Equal(20, candidate.GetProperty("EffectiveBits").GetInt32());
        Assert.Equal(24, candidate.GetProperty("ContainerBits").GetInt32());
        Assert.Equal(20, candidate.GetProperty("ReadbackEffectiveBits").GetInt32());
        Assert.Equal(24, candidate.GetProperty("ReadbackContainerBits").GetInt32());
    }

    [Fact]
    public void Fails_when_a_thirty_two_bit_container_reports_thirty_two_effective_bits()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [24]);
        // Driver reports a true 32-bit format instead of 24 valid bits in a 32-bit container,
        // but still restores the original 16-bit format correctly.
        h.Svcl.SubstituteOnSet = f => f.EffectiveBits == 24 ? (f.Channels, f.SampleRate, 32) : f;
        h.Svcl.ContainerBitsOverride = 32;

        var exit = h.Run();

        Assert.Equal(ExitCode.FormatMismatch, exit);
        Assert.Equal(32, h.JsonReport.GetProperty("Candidates")[0].GetProperty("ReadbackEffectiveBits").GetInt32());
    }

    [Fact]
    public void Treats_no_items_found_with_exit_code_zero_as_a_failure()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);
        h.Svcl.SaveStdout = "No items found";

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("No items found", h.ErrorText);
    }

    [Fact]
    public void Treats_a_missing_saved_format_file_as_a_failure()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);
        h.Svcl.SaveWritesNoFile = true;

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("did not write", h.ErrorText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Treats_a_nonzero_svcl_exit_code_as_a_failure()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);
        h.Svcl.SaveExitCode = 5;

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("exited with code 5", h.ErrorText);
    }

    [Fact]
    public void Treats_a_truncated_waveformatex_as_malformed()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);
        h.Svcl.SaveRawOverride = new byte[8];

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("incomplete", h.ErrorText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Treats_a_truncated_waveformatextensible_as_malformed()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);
        var truncated = new byte[17];
        BitConverter.GetBytes((ushort)0xFFFE).CopyTo(truncated, 0);
        BitConverter.GetBytes((ushort)2).CopyTo(truncated, 2);
        BitConverter.GetBytes(48000u).CopyTo(truncated, 4);
        BitConverter.GetBytes((ushort)16).CopyTo(truncated, 14);
        h.Svcl.SaveRawOverride = truncated;

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("WAVEFORMATEXTENSIBLE", h.ErrorText);
    }

    [Fact]
    public void Treats_a_zero_channel_saved_format_as_malformed()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);
        h.Svcl.SaveRawOverride = FakeSvclRunner.BuildWaveFormatEx(0, 48000, 16);

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("malformed", h.ErrorText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Retains_svcl_diagnostics_in_the_json_report()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);
        h.Svcl.SaveStdout = "diagnostic output";

        h.Run();

        var commands = h.JsonReport.GetProperty("SvclCommands");
        Assert.True(commands.GetArrayLength() > 0);
        Assert.Contains(commands.EnumerateArray(),
            c => c.GetProperty("StandardOutput").GetString() == "diagnostic output");
        Assert.Contains(commands.EnumerateArray(),
            c => c.GetProperty("Command").GetString() == "/SetDefaultFormat");
    }

    [Fact]
    public void Uses_the_svcl_command_line_id_when_the_endpoint_provides_one()
    {
        var h = new AppHarness()
            .WithEndpoint(svclId: @"NVIDIA\Device\Display Audio\Render")
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);

        h.Run();

        Assert.All(h.Svcl.Invocations, i =>
            Assert.Equal(@"NVIDIA\Device\Display Audio\Render", i.Arguments[1]));
    }

    [Fact]
    public void Falls_back_to_the_endpoint_id_when_no_svcl_id_is_available()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);

        h.Run();

        Assert.All(h.Svcl.Invocations, i =>
            Assert.Equal(AppHarness.DefaultEndpointId, i.Arguments[1]));
    }

    [Fact]
    public void Never_calls_set_speakers_config_or_changes_the_default_device()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16, 24]);

        h.Run();

        Assert.DoesNotContain(h.Svcl.Invocations, i => i.Command.Contains("SetSpeakersConfig", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(h.Svcl.Invocations, i => i.Command.Contains("SetDefault\"", StringComparison.OrdinalIgnoreCase));
        Assert.All(h.Svcl.Invocations, i =>
            Assert.Contains(i.Command, new[] { "/SetDefaultFormat", "/SaveDeviceFormat" }));
    }

    [Fact]
    public void Reports_the_non_default_endpoint_selected_by_id_without_changing_the_default()
    {
        var h = new AppHarness()
            .WithEndpoint(endpointId: "ep-default", name: "Speakers", isDefault: true)
            .WithEndpoint(endpointId: "ep-hdmi", name: "HDMI", isDefault: false)
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);

        var exit = h.Run("--device-id", "ep-hdmi");

        Assert.Equal(ExitCode.Pass, exit);
        Assert.All(h.Svcl.Invocations, i => Assert.Equal("ep-hdmi", i.Arguments[1]));
        Assert.False(h.JsonReport.GetProperty("Endpoint").GetProperty("IsDefault").GetBoolean());
    }
}
