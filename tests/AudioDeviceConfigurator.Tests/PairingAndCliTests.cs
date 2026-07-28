using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Tests.Fixtures;

namespace AudioDeviceConfigurator.Tests;

public class PairingAndCliTests
{
    [Fact]
    public void Pairs_the_default_endpoint_with_the_only_active_display_without_prompting()
    {
        var h = new AppHarness()
            .WithEndpoint(name: "Digital Display Audio")
            .WithLpcmDisplay(maxChannels: 2, depths: [16], name: "DELL U2723QE");

        var exit = h.Run();

        Assert.Equal(ExitCode.Pass, exit);
        Assert.Contains("Endpoint : Digital Display Audio", h.ConsoleText);
        Assert.Contains("Monitor  : DELL U2723QE", h.ConsoleText);
        Assert.DoesNotContain("Select the monitor", h.ConsoleText);
    }

    [Fact]
    public void Selects_the_default_endpoint_when_several_are_active()
    {
        var h = new AppHarness()
            .WithEndpoint(endpointId: "ep-a", name: "Speakers", isDefault: false)
            .WithEndpoint(endpointId: "ep-b", name: "HDMI", isDefault: true)
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);

        var exit = h.Run();

        Assert.Equal(ExitCode.Pass, exit);
        Assert.Equal("ep-b", h.JsonReport.GetProperty("Endpoint").GetProperty("EndpointId").GetString());
    }

    [Fact]
    public void Prompts_when_more_than_one_display_is_active()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: "mon-a", name: "Monitor A")
            .WithLpcmDisplay(maxChannels: 8, depths: [16], monitorId: "mon-b", name: "Monitor B");
        h.Console.EnqueueInput("2");

        var exit = h.Run();

        Assert.Equal(ExitCode.Pass, exit);
        Assert.Contains("Select the monitor", h.ConsoleText);
        Assert.Contains("[1] Monitor A", h.ConsoleText);
        Assert.Contains("[2] Monitor B", h.ConsoleText);
        Assert.Equal("mon-b", h.JsonReport.GetProperty("Monitor").GetProperty("MonitorId").GetString());
    }

    [Fact]
    public void Distinguishes_two_monitors_of_the_same_model_by_id()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: "mon-left", name: "DELL U2723QE")
            .WithLpcmDisplay(maxChannels: 8, depths: [16], monitorId: "mon-right", name: "DELL U2723QE");
        h.Console.EnqueueInput("1");

        h.Run();

        Assert.Contains("mon-left", h.ConsoleText);
        Assert.Contains("mon-right", h.ConsoleText);
        Assert.Equal("mon-left", h.JsonReport.GetProperty("Monitor").GetProperty("MonitorId").GetString());
    }

    [Fact]
    public void Re_prompts_after_an_invalid_selection()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: "mon-a", name: "Monitor A")
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: "mon-b", name: "Monitor B");
        h.Console.EnqueueInput("9", "abc", "1");

        var exit = h.Run();

        Assert.Equal(ExitCode.Pass, exit);
        Assert.Contains("Invalid selection", h.ConsoleText);
    }

    [Fact]
    public void Cancels_with_exit_code_three_when_the_user_declines_to_choose()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: "mon-a", name: "Monitor A")
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: "mon-b", name: "Monitor B");
        h.Console.EnqueueInput("C");

        var exit = h.Run();

        Assert.Equal(ExitCode.Cancelled, exit);
        Assert.Contains("Cancelled by user", h.ConsoleText);
        Assert.Empty(h.Svcl.SetCommands);
    }

    [Fact]
    public void Cancels_when_the_input_stream_ends()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: "mon-a", name: "Monitor A")
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: "mon-b", name: "Monitor B");

        Assert.Equal(ExitCode.Cancelled, h.Run());
    }

    [Fact]
    public void Runs_unattended_when_both_ids_are_supplied()
    {
        var h = new AppHarness()
            .WithEndpoint(endpointId: "ep-a", isDefault: false)
            .WithEndpoint(endpointId: "ep-b", isDefault: false)
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: "mon-a")
            .WithLpcmDisplay(maxChannels: 8, depths: [16], monitorId: "mon-b");

        var exit = h.Run("--device-id", "ep-a", "--monitor-id", "mon-b");

        Assert.Equal(ExitCode.Pass, exit);
        Assert.DoesNotContain("Select the monitor", h.ConsoleText);
        Assert.Equal("mon-b", h.JsonReport.GetProperty("Monitor").GetProperty("MonitorId").GetString());
    }

    [Theory]
    [InlineData(@"\\?\DISPLAY#ACI22E5#5&c1713af&0&UID45312#{e6f07b5f}")]
    [InlineData(@"DISPLAY#ACI22E5#5&c1713af&0&UID45312#{e6f07b5f}")]
    public void Accepts_a_monitor_id_with_or_without_the_win32_prefix(string requestedId)
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: "other-monitor", name: "Other")
            .WithLpcmDisplay(
                maxChannels: 2,
                depths: [16],
                monitorId: @"\\?\DISPLAY#ACI22E5#5&c1713af&0&UID45312#{e6f07b5f}",
                name: "VX229");

        var exit = h.Run("--monitor-id", requestedId);

        Assert.Equal(ExitCode.Pass, exit);
        Assert.Equal(
            @"\\?\DISPLAY#ACI22E5#5&c1713af&0&UID45312#{e6f07b5f}",
            h.JsonReport.GetProperty("Monitor").GetProperty("MonitorId").GetString());
        Assert.DoesNotContain("Select the monitor", h.ConsoleText);
    }

    [Fact]
    public void Still_rejects_an_unknown_monitor_id_after_prefix_normalization()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: @"\\?\DISPLAY#ACI22E5#UID45312");

        var exit = h.Run("--monitor-id", @"DISPLAY#OTHER99#UID00000");

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("DISPLAY#OTHER99#UID00000", h.ErrorText);
    }

    [Fact]
    public void Distinguishes_monitors_that_differ_only_after_the_win32_prefix()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: @"\\?\DISPLAY#ACI22E5#UID45312", name: "Left")
            .WithLpcmDisplay(maxChannels: 8, depths: [16], monitorId: @"\\?\DISPLAY#ACI22E5#UID45313", name: "Right");

        var exit = h.Run("--monitor-id", @"DISPLAY#ACI22E5#UID45313");

        Assert.Equal(ExitCode.Pass, exit);
        Assert.Equal("Right", h.JsonReport.GetProperty("Monitor").GetProperty("FriendlyName").GetString());
    }

    [Fact]
    public void Reports_a_system_error_for_an_unknown_endpoint_id()
    {
        var h = new AppHarness()
            .WithEndpoint(endpointId: "ep-a")
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);

        var exit = h.Run("--device-id", "does-not-exist");

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("does-not-exist", h.ErrorText);
    }

    [Fact]
    public void Reports_a_system_error_for_an_unknown_monitor_id()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: "mon-a");

        var exit = h.Run("--monitor-id", "does-not-exist");

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("does-not-exist", h.ErrorText);
    }

    [Fact]
    public void Reports_a_system_error_when_no_active_display_exists()
    {
        var h = new AppHarness().WithEndpoint();

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("No active displays", h.ErrorText);
    }

    [Fact]
    public void Reports_a_system_error_when_no_active_render_endpoint_exists()
    {
        var h = new AppHarness().WithLpcmDisplay(maxChannels: 2, depths: [16]);

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("No active render endpoints", h.ErrorText);
    }

    [Fact]
    public void Reports_a_system_error_when_no_default_endpoint_exists_and_none_was_specified()
    {
        var h = new AppHarness()
            .WithEndpoint(endpointId: "ep-a", isDefault: false)
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("--device-id", h.ErrorText);
    }

    [Fact]
    public void Lists_active_endpoints_and_displays()
    {
        var h = new AppHarness()
            .WithEndpoint(endpointId: "ep-a", name: "HDMI Out")
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: "mon-a", name: "Monitor A");

        var exit = h.Run("--list");

        Assert.Equal(ExitCode.Pass, exit);
        Assert.Contains("HDMI Out", h.ConsoleText);
        Assert.Contains("ep-a", h.ConsoleText);
        Assert.Contains("Monitor A", h.ConsoleText);
        Assert.Contains("mon-a", h.ConsoleText);
        Assert.Empty(h.Svcl.Invocations);
    }

    [Fact]
    public void Marks_the_default_endpoint_in_the_list_output()
    {
        var h = new AppHarness()
            .WithEndpoint(endpointId: "ep-a", name: "Speakers", isDefault: false)
            .WithEndpoint(endpointId: "ep-b", name: "HDMI", isDefault: true)
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);

        h.Run("--list");

        Assert.Contains("HDMI (default)", h.ConsoleText);
        Assert.DoesNotContain("Speakers (default)", h.ConsoleText);
    }

    [Fact]
    public void Shows_help_that_documents_commands_side_effects_and_exit_codes()
    {
        var h = new AppHarness();

        var exit = h.Run("--help");

        Assert.Equal(ExitCode.Pass, exit);
        var text = h.ConsoleText;
        Assert.Contains("--list", text);
        Assert.Contains("--device-id", text);
        Assert.Contains("--monitor-id", text);
        Assert.Contains("SIDE EFFECTS", text);
        Assert.Contains("REPORTS", text);
        Assert.Contains("EXIT CODES", text);
        foreach (var code in new[] { "0  PASS", "1  FAIL", "2  ERROR", "3  CANCELLED", "4  N/A" })
        {
            Assert.Contains(code, text);
        }
    }

    [Fact]
    public void Rejects_an_unknown_argument_as_a_system_error()
    {
        var h = new AppHarness();

        var exit = h.Run("--nonsense");

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("Unknown argument", h.ErrorText);
    }

    [Fact]
    public void Rejects_a_missing_argument_value_as_a_system_error()
    {
        var h = new AppHarness();

        var exit = h.Run("--device-id");

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("requires a value", h.ErrorText);
    }
}

public class EdidFailureTests
{
    [Fact]
    public void Stops_with_a_system_error_when_the_edid_header_is_invalid()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithDisplay(new EdidBuilder().WithCorruptHeader().Build());

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("header", h.ErrorText, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Svcl.SetCommands);
    }

    [Fact]
    public void Stops_with_a_system_error_when_a_block_checksum_fails()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithDisplay(new EdidBuilder()
                .WithCtaAudioBlock(SadSpec.Lpcm(8, [48000], [16]))
                .WithCorruptChecksum(1)
                .Build());

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("checksum", h.ErrorText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stops_with_a_system_error_when_the_edid_is_truncated()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithDisplay(new EdidBuilder()
                .WithCtaAudioBlock(SadSpec.Lpcm(8, [48000], [16]))
                .TruncatedTo(200)
                .Build());

        Assert.Equal(ExitCode.SystemError, h.Run());
    }

    [Fact]
    public void Returns_not_applicable_for_a_valid_edid_with_no_lpcm_capability()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithDisplay(new EdidBuilder().WithMonitorName("NOAUDIO").Build());

        var exit = h.Run();

        Assert.Equal(ExitCode.NotApplicable, exit);
        Assert.Contains("no LPCM audio capability", h.ConsoleText);
        Assert.Empty(h.Svcl.SetCommands);
    }

    [Fact]
    public void Returns_not_applicable_when_only_non_lpcm_formats_are_declared()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithDisplay(new EdidBuilder()
                .WithMonitorName("AC3ONLY")
                .WithCtaAudioBlock(SadSpec.NonLpcm(formatCode: 2, maxChannels: 6, rates: [48000]))
                .Build());

        var exit = h.Run();

        Assert.Equal(ExitCode.NotApplicable, exit);
        var sad = h.JsonReport.GetProperty("Monitor").GetProperty("AudioDescriptors")[0];
        Assert.Equal("Ac3", sad.GetProperty("FormatCode").GetString());
        Assert.False(sad.GetProperty("Tested").GetBoolean());
    }

    [Fact]
    public void Records_non_lpcm_descriptors_as_untested_diagnostics()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithDisplay(new EdidBuilder()
                .WithCtaAudioBlock(
                    SadSpec.Lpcm(2, [48000], [16]),
                    SadSpec.NonLpcm(formatCode: 7, maxChannels: 8, rates: [48000]))
                .Build());

        var exit = h.Run();

        Assert.Equal(ExitCode.Pass, exit);
        var sads = h.JsonReport.GetProperty("Monitor").GetProperty("AudioDescriptors").EnumerateArray().ToList();
        Assert.True(sads[0].GetProperty("Tested").GetBoolean());
        Assert.False(sads[1].GetProperty("Tested").GetBoolean());
        Assert.Equal("Dts", sads[1].GetProperty("FormatCode").GetString());
        Assert.Single(h.JsonReport.GetProperty("Candidates").EnumerateArray());
    }

    [Fact]
    public void Does_not_use_a_stale_display_when_the_provider_fails()
    {
        var h = new AppHarness().WithEndpoint();
        h.Displays.ThrowOnGet = new InvalidOperationException("EDID acquisition failed.");

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("EDID acquisition failed", h.ErrorText);
    }
}

public class SvclDeploymentTests
{
    [Fact]
    public void Reports_a_system_error_when_svcl_is_missing()
    {
        var h = new AppHarness()
            .WithoutSvclExecutable()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("svcl.exe was not found", h.ErrorText);
    }

    [Theory]
    [InlineData("1.28.0.0")]
    [InlineData("1.2.8.0")]  // NirSoft ships v1.28 with this file version
    [InlineData("1.30.0.0")]
    [InlineData("1.3.0.0")]  // v1.30
    public void Accepts_a_svcl_version_at_or_above_the_minimum(string version)
    {
        var h = new AppHarness()
            .WithSvclVersion(version)
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);

        Assert.Equal(ExitCode.Pass, h.Run());
    }

    [Theory]
    [InlineData("1.27.0.0")]
    [InlineData("1.2.7.0")]  // v1.27
    [InlineData("1.2.0.0")]  // v1.20
    public void Rejects_a_svcl_version_below_the_minimum(string version)
    {
        var h = new AppHarness()
            .WithSvclVersion(version)
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("older than the required 1.28", h.ErrorText);
    }

    [Fact]
    public void Reports_a_system_error_when_the_svcl_version_cannot_be_read()
    {
        var h = new AppHarness()
            .WithSvclVersion(null)
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("Unable to read the file version", h.ErrorText);
    }

    [Fact]
    public void Records_the_detected_svcl_version_and_path()
    {
        var h = new AppHarness()
            .WithSvclVersion("1.28.0.0")
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);

        h.Run();

        Assert.Equal("1.28.0.0", h.JsonReport.GetProperty("SvclVersion").GetString());
        Assert.Equal(AppHarness.SvclPath, h.JsonReport.GetProperty("SvclPath").GetString());
    }
}
