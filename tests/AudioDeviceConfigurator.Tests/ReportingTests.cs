using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Reporting;
using AudioDeviceConfigurator.Tests.Fixtures;

namespace AudioDeviceConfigurator.Tests;

public class ReportingTests
{
    [Fact]
    public void Writes_both_reports_under_a_reports_directory_beside_the_executable()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);

        h.Run();

        Assert.Contains(AppHarness.ReportsDirectory, h.FileSystem.CreatedDirectories);
        Assert.StartsWith(AppHarness.ReportsDirectory, h.JsonReportPath);
        Assert.StartsWith(AppHarness.ReportsDirectory, h.CsvReportPath);
    }

    [Fact]
    public void Names_reports_with_a_sanitized_pc_name_monitor_name_and_local_timestamp()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorName: "U2723QE");
        h.SystemInfo.Info = h.SystemInfo.Info with { MachineName = "TEST-PC" };
        h.Clock.LocalNow = new DateTimeOffset(2026, 7, 28, 14, 30, 45, TimeSpan.FromHours(8));

        h.Run();

        Assert.Contains("TEST-PC_U2723QE_20260728_1430", Path.GetFileName(h.JsonReportPath));
    }

    [Theory]
    [InlineData(@"Bad:Name\With/Chars", "Bad_Name_With_Chars")]
    [InlineData("  spaced name  ", "spaced_name")]
    [InlineData("", "Unknown")]
    [InlineData("Ok.Name-1_2", "Ok.Name-1_2")]
    public void Sanitizes_filename_components(string input, string expected) =>
        Assert.Equal(expected, ReportWriter.Sanitize(input));

    [Fact]
    public void Adds_a_sequence_suffix_when_names_collide_within_the_same_second()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);
        var baseName = ReportWriter.BuildBaseName("TEST-PC", "U2723QE", h.Clock.LocalNow);
        h.FileSystem.Files[Path.Combine(AppHarness.ReportsDirectory, baseName + ".json")] = [];
        h.FileSystem.Files[Path.Combine(AppHarness.ReportsDirectory, baseName + ".csv")] = [];

        h.Run();

        Assert.Contains(h.FileSystem.Files.Keys, k => k.EndsWith("_2.json"));
        Assert.Contains(h.FileSystem.Files.Keys, k => k.EndsWith("_2.csv"));
    }

    [Fact]
    public void Uses_local_time_in_report_timestamps()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);
        h.Clock.LocalNow = new DateTimeOffset(2026, 7, 28, 9, 5, 0, TimeSpan.FromHours(8));

        h.Run();

        Assert.StartsWith("2026-07-28 09:05:00 +08:00", h.JsonReport.GetProperty("StartedAtLocal").GetString());
    }

    [Fact]
    public void Includes_system_endpoint_monitor_and_driver_metadata_in_json()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);

        h.Run();

        var json = h.JsonReport;
        Assert.Equal("TEST-PC", json.GetProperty("System").GetProperty("MachineName").GetString());
        Assert.Equal("10.0.26100", json.GetProperty("System").GetProperty("OsVersion").GetString());
        Assert.Equal("NVIDIA High Definition Audio", json.GetProperty("Endpoint").GetProperty("DriverName").GetString());
        Assert.Equal("1.4.4.1", json.GetProperty("Endpoint").GetProperty("DriverVersion").GetString());
        Assert.Equal("NVIDIA GeForce RTX 4070", json.GetProperty("Monitor").GetProperty("AdapterName").GetString());
        Assert.Equal("32.0.15.6094", json.GetProperty("Monitor").GetProperty("GpuDriverVersion").GetString());
    }

    [Fact]
    public void Includes_raw_edid_bytes_and_parsed_capabilities_in_json()
    {
        var edid = new EdidBuilder()
            .WithMonitorName("U2723QE")
            .WithCtaAudioBlock(SadSpec.Lpcm(8, [48000, 96000], [16, 24]))
            .Build();
        var h = new AppHarness().WithEndpoint().WithDisplay(edid);

        h.Run();

        var monitor = h.JsonReport.GetProperty("Monitor");
        Assert.Equal(Convert.ToHexString(edid), monitor.GetProperty("RawEdidHex").GetString());
        var sad = monitor.GetProperty("AudioDescriptors")[0];
        Assert.Equal("Lpcm", sad.GetProperty("FormatCode").GetString());
        Assert.Equal(8, sad.GetProperty("MaxChannels").GetInt32());
        Assert.Equal([48000, 96000], sad.GetProperty("SampleRates").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal([16, 24], sad.GetProperty("BitDepths").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void Preserves_source_sad_references_for_every_candidate()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithDisplay(new EdidBuilder()
                .WithCtaAudioBlock(
                    SadSpec.Lpcm(2, [48000], [16]),
                    SadSpec.Lpcm(2, [48000], [16]))
                .Build());

        h.Run();

        var sources = h.JsonReport.GetProperty("Candidates")[0]
            .GetProperty("SourceSadReferences").EnumerateArray().Select(e => e.GetString());
        Assert.Equal(["ext1/adb0/sad0", "ext1/adb0/sad1"], sources);
    }

    [Fact]
    public void Retains_every_candidate_including_unsupported_and_failed_ones()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16]);
        h.Wasapi.Set(6, 48000, 16, FormatSupportResult.AudclntUnsupportedFormat);
        h.Svcl.SubstituteOnSet = f => f.Channels == 8 ? (2, f.SampleRate, f.EffectiveBits) : f;

        h.Run();

        var statuses = h.JsonReport.GetProperty("Candidates").EnumerateArray()
            .Select(c => c.GetProperty("Status").GetString()).ToList();
        Assert.Equal(["Pass", "Pass", "ApplyFailed"], statuses);
    }

    [Fact]
    public void Writes_one_csv_row_per_candidate_plus_a_header()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16, 24]);

        h.Run();

        Assert.Equal(7, h.CsvRows.Length); // header + 6 candidates
        Assert.StartsWith("RunId,", h.CsvRows[0]);
    }

    [Fact]
    public void Repeats_system_monitor_endpoint_and_driver_metadata_in_every_csv_row()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16]);

        h.Run();

        foreach (var row in h.CsvRows.Skip(1))
        {
            Assert.Contains("TEST-PC", row);
            Assert.Contains("DELL U2723QE", row);
            Assert.Contains("NVIDIA High Definition Audio", row);
        }
    }

    [Fact]
    public void Writes_a_summary_row_when_no_candidates_were_generated()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithDisplay(new EdidBuilder().WithMonitorName("NOAUDIO").Build());

        var exit = h.Run();

        Assert.Equal(ExitCode.NotApplicable, exit);
        Assert.Equal(2, h.CsvRows.Length); // header + summary
        Assert.Contains("N/A", h.CsvRows[1]);
        Assert.Contains("TEST-PC", h.CsvRows[1]);
    }

    [Fact]
    public void Writes_reports_for_a_cancelled_run()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: "mon-a", name: "Monitor A")
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: "mon-b", name: "Monitor B");
        h.Console.EnqueueInput("C");

        var exit = h.Run();

        Assert.Equal(ExitCode.Cancelled, exit);
        Assert.Equal("CANCELLED", h.JsonReport.GetProperty("OverallStatus").GetString());
        Assert.Equal(2, h.CsvRows.Length);
    }

    [Fact]
    public void Writes_reports_for_a_system_error()
    {
        var h = new AppHarness()
            .WithoutSvclExecutable()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);

        h.Run();

        Assert.Equal("ERROR", h.JsonReport.GetProperty("OverallStatus").GetString());
        Assert.Contains(h.JsonReport.GetProperty("Errors").EnumerateArray(),
            e => e.GetString()!.Contains("svcl.exe was not found"));
    }

    [Fact]
    public void Escapes_csv_fields_containing_commas_and_quotes()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);
        h.SystemInfo.Info = h.SystemInfo.Info with { OsDescription = @"Windows 11, ""Pro""" };

        h.Run();

        Assert.Contains(@"""Windows 11, """"Pro""""""", h.CsvRows[1]);
    }

    [Fact]
    public void Formats_hresults_as_eight_digit_hex_in_both_reports()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);
        h.Wasapi.DefaultHResult = FormatSupportResult.AudclntUnsupportedFormat;

        h.Run();

        Assert.Equal("0x88890008", h.JsonReport.GetProperty("Candidates")[0].GetProperty("WasapiHResult").GetString());
        Assert.Contains("0x88890008", h.CsvRows[1]);
    }

    [Fact]
    public void Reports_a_system_error_when_the_reports_directory_cannot_be_created()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);
        h.FileSystem.CreateDirectoryFailure = _ => new UnauthorizedAccessException("Access denied.");

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("Unable to create the Reports directory", h.ErrorText);
    }

    [Fact]
    public void Reports_a_system_error_when_the_json_report_cannot_be_written()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);
        h.FileSystem.WriteFailure = p => p.EndsWith(".json") ? new IOException("Disk full.") : null;

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("Unable to write the JSON report", h.ErrorText);
    }

    [Fact]
    public void Reports_a_system_error_when_the_csv_report_cannot_be_written()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);
        h.FileSystem.WriteFailure = p => p.EndsWith(".csv") ? new IOException("Disk full.") : null;

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Contains("Unable to write the CSV report", h.ErrorText);
    }

    [Fact]
    public void Prints_report_paths_and_a_concise_summary_on_the_console()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16]);
        h.Wasapi.Set(8, 48000, 16, FormatSupportResult.AudclntUnsupportedFormat);

        h.Run();

        var text = h.ConsoleText;
        Assert.Contains("Supported formats:", text);
        Assert.Contains("Channels  Sample rate  Bit depth", text);
        Assert.Contains("Passed                       : 3", text);
        Assert.Contains("Unsupported by WASAPI        : 0", text);
        Assert.Contains("Apply/readback failed        : 0", text);
        Assert.Contains("JSON report : ", text);
        Assert.Contains("CSV report  : ", text);
    }

    [Fact]
    public void Displays_channel_counts_numerically_without_speaker_configuration_terms()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16]);

        h.Run();

        var text = h.ConsoleText;
        Assert.DoesNotContain("5.1", text);
        Assert.DoesNotContain("7.1", text);
        Assert.DoesNotContain("Surround", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Quadraphonic", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Includes_the_schema_version_and_run_id()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);

        h.Run();

        Assert.Equal("1.0", h.JsonReport.GetProperty("SchemaVersion").GetString());
        Assert.Contains("TEST-PC", h.JsonReport.GetProperty("RunId").GetString());
    }
}

public class ExitCodeTests
{
    [Fact]
    public void Returns_zero_for_a_complete_pass()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16, 24]);

        Assert.Equal(0, (int)h.Run());
    }

    [Fact]
    public void Returns_one_when_any_candidate_is_unsupported()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16]);
        h.Wasapi.Set(8, 48000, 16, FormatSupportResult.AudclntUnsupportedFormat);

        Assert.Equal(0, (int)h.Run());
    }

    [Fact]
    public void Returns_two_for_a_system_error()
    {
        var h = new AppHarness()
            .WithoutSvclExecutable()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);

        Assert.Equal(2, (int)h.Run());
    }

    [Fact]
    public void Returns_three_for_user_cancellation()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: "a", name: "A")
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: "b", name: "B");
        h.Console.EnqueueInput("C");

        Assert.Equal(3, (int)h.Run());
    }

    [Fact]
    public void Returns_four_for_a_valid_edid_with_no_lpcm()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithDisplay(new EdidBuilder().Build());

        Assert.Equal(4, (int)h.Run());
    }

    [Fact]
    public void Prefers_a_restore_error_over_a_format_mismatch()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16])
            .WithOriginalFormat(2, 44100, 16);
        h.Svcl.SubstituteOnSet = f => f.Channels == 2 && f.SampleRate == 48000 ? (2, 32000, 16) : f;
        h.Svcl.FailSetOnInvocation = n => n == 2;

        Assert.Equal(2, (int)h.Run());
    }

    [Fact]
    public void Prefers_a_report_write_error_over_a_format_mismatch()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16]);
        h.Wasapi.Set(8, 48000, 16, FormatSupportResult.AudclntUnsupportedFormat);
        h.FileSystem.WriteFailure = p => p.EndsWith(".json") ? new IOException("Disk full.") : null;

        Assert.Equal(2, (int)h.Run());
    }

    [Fact]
    public void Prefers_a_report_write_error_over_not_applicable()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithDisplay(new EdidBuilder().Build());
        h.FileSystem.WriteFailure = p => p.EndsWith(".csv") ? new IOException("Disk full.") : null;

        Assert.Equal(2, (int)h.Run());
    }

    [Fact]
    public void Explains_the_exit_code_meaning_on_the_console()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);

        h.Run();

        Assert.Contains("Exit code 0: every EDID-declared format was supported", h.ConsoleText);
    }

    [Theory]
    [InlineData(ExitCode.SystemError, ExitCode.FormatMismatch, ExitCode.SystemError)]
    [InlineData(ExitCode.Cancelled, ExitCode.FormatMismatch, ExitCode.Cancelled)]
    [InlineData(ExitCode.FormatMismatch, ExitCode.NotApplicable, ExitCode.FormatMismatch)]
    [InlineData(ExitCode.NotApplicable, ExitCode.Pass, ExitCode.NotApplicable)]
    [InlineData(ExitCode.SystemError, ExitCode.Cancelled, ExitCode.SystemError)]
    public void Applies_exit_code_precedence(ExitCode a, ExitCode b, ExitCode expected)
    {
        Assert.Equal(expected, ExitCodePrecedence.Max(a, b));
        Assert.Equal(expected, ExitCodePrecedence.Max(b, a));
    }
}
