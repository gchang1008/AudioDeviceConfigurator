using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Tests.Fakes;

namespace AudioDeviceConfigurator.Tests;

public class RestorationTests
{
    [Fact]
    public void Saves_the_original_format_before_any_modification()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16])
            .WithOriginalFormat(2, 44100, 24);

        h.Run();

        Assert.Equal("/SaveDeviceFormat", h.Svcl.Invocations[0].Command);
        var original = h.JsonReport.GetProperty("OriginalFormat");
        Assert.Equal(2, original.GetProperty("Channels").GetInt32());
        Assert.Equal(44100, original.GetProperty("SampleRate").GetInt32());
        Assert.Equal(24, original.GetProperty("EffectiveBits").GetInt32());
    }

    [Fact]
    public void Restores_the_original_format_after_a_fully_passing_run()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16, 24])
            .WithOriginalFormat(2, 44100, 16);

        var exit = h.Run();

        Assert.Equal(ExitCode.Pass, exit);
        Assert.Equal((2, 44100, 16), h.Svcl.CurrentFormat);
        Assert.Equal((16, 44100, 2), h.Svcl.AppliedFormats.Last());
        Assert.True(h.JsonReport.GetProperty("Restore").GetProperty("Succeeded").GetBoolean());
    }

    [Fact]
    public void Restores_the_original_format_after_a_failing_run()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16])
            .WithOriginalFormat(2, 44100, 16);
        h.Wasapi.Set(8, 48000, 16, FormatSupportResult.AudclntUnsupportedFormat);

        var exit = h.Run();

        Assert.Equal(ExitCode.FormatMismatch, exit);
        Assert.Equal((2, 44100, 16), h.Svcl.CurrentFormat);
    }

    [Fact]
    public void Restores_immediately_after_a_failed_apply_before_the_next_candidate()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16])
            .WithOriginalFormat(2, 44100, 16);
        // 2-channel apply is substituted, so it must be restored before 6-channel is tried.
        h.Svcl.SubstituteOnSet = f => f is { Channels: 2, SampleRate: 48000 } ? (2, 32000, 16) : f;

        var exit = h.Run();

        Assert.Equal(ExitCode.FormatMismatch, exit);
        var applied = h.Svcl.AppliedFormats.ToList();
        Assert.Equal((16, 48000, 2), applied[0]);
        Assert.Equal((16, 44100, 2), applied[1]); // immediate restore
        Assert.Equal((16, 48000, 6), applied[2]); // testing continues afterwards
    }

    [Fact]
    public void Does_not_restore_between_consecutive_successful_candidates()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16])
            .WithOriginalFormat(2, 44100, 16);

        h.Run();

        var applied = h.Svcl.AppliedFormats.ToList();
        Assert.Equal(
        [
            (16, 48000, 2), (16, 48000, 6), (16, 48000, 8),
            (16, 44100, 2), // single final restore
        ], applied);
    }

    [Fact]
    public void Stops_testing_and_reports_a_system_error_when_restoration_fails()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16])
            .WithOriginalFormat(2, 44100, 16);
        h.Svcl.SubstituteOnSet = f => f.Channels == 2 && f.SampleRate == 48000 ? (2, 32000, 16) : f;
        // The restore attempt (invocation 2) fails outright.
        h.Svcl.FailSetOnInvocation = n => n == 2;

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.DoesNotContain(h.Svcl.AppliedFormats, f => f.Channels is 6 or 8);
        Assert.Contains("Restoration failed", h.ErrorText);
    }

    [Fact]
    public void Marks_untested_candidates_when_restoration_aborts_the_run()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16])
            .WithOriginalFormat(2, 44100, 16);
        h.Svcl.SubstituteOnSet = f => f.Channels == 2 && f.SampleRate == 48000 ? (2, 32000, 16) : f;
        h.Svcl.FailSetOnInvocation = n => n == 2;

        h.Run();

        var candidates = h.JsonReport.GetProperty("Candidates").EnumerateArray().ToList();
        Assert.Equal(3, candidates.Count);
        Assert.Equal("ApplyFailed", candidates[0].GetProperty("Status").GetString());
        Assert.Equal("NotTested", candidates[1].GetProperty("Status").GetString());
        Assert.Equal("NotTested", candidates[2].GetProperty("Status").GetString());
    }

    [Fact]
    public void Reports_a_system_error_when_the_restored_format_cannot_be_verified()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16])
            .WithOriginalFormat(2, 44100, 16);
        // The restore command silently applies a different format.
        h.Svcl.SubstituteOnSet = f => f.SampleRate == 44100 ? (2, 32000, 16) : f;

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        var restore = h.JsonReport.GetProperty("Restore");
        Assert.True(restore.GetProperty("Attempted").GetBoolean());
        Assert.False(restore.GetProperty("Succeeded").GetBoolean());
    }

    [Fact]
    public void Attempts_restoration_after_a_handled_exception()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16])
            .WithOriginalFormat(2, 44100, 16);
        var setCount = 0;
        h.Svcl.OnSet = () =>
        {
            if (++setCount == 2)
            {
                throw new InvalidOperationException("Unexpected driver failure.");
            }
        };

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        Assert.Equal((2, 44100, 16), h.Svcl.CurrentFormat);
        Assert.Equal("error", h.JsonReport.GetProperty("Restore").GetProperty("Trigger").GetString());
    }

    [Fact]
    public void Attempts_restoration_when_cancelled_by_ctrl_c()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16])
            .WithOriginalFormat(2, 44100, 16);
        h.Svcl.OnSet = () =>
        {
            if (h.Svcl.SetCommands.Count() >= 2)
            {
                h.Cancellation.Cancel();
            }
        };

        var exit = h.Run();

        Assert.Equal(ExitCode.Cancelled, exit);
        Assert.Equal((2, 44100, 16), h.Svcl.CurrentFormat);
        Assert.Equal("cancellation", h.JsonReport.GetProperty("Restore").GetProperty("Trigger").GetString());
    }

    [Fact]
    public void Never_reports_a_failed_restore_as_succeeded_after_a_later_retry()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 8, depths: [16])
            .WithOriginalFormat(2, 44100, 16);
        h.Svcl.SubstituteOnSet = f => f.Channels == 2 && f.SampleRate == 48000 ? (2, 32000, 16) : f;
        // Only the first restore attempt fails; a retry would succeed and mask the failure.
        h.Svcl.FailSetOnInvocation = n => n == 2;

        var exit = h.Run();

        Assert.Equal(ExitCode.SystemError, exit);
        var restore = h.JsonReport.GetProperty("Restore");
        Assert.True(restore.GetProperty("Attempted").GetBoolean());
        Assert.False(restore.GetProperty("Succeeded").GetBoolean());
        Assert.Contains("Restoration failed", restore.GetProperty("FailureDetail").GetString());
        Assert.Equal(2, h.Svcl.SetCommands.Count()); // the failed candidate plus one restore attempt
    }

    [Fact]
    public void Records_the_verified_restored_format_in_the_report()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16])
            .WithOriginalFormat(6, 96000, 24);

        h.Run();

        var verified = h.JsonReport.GetProperty("Restore").GetProperty("VerifiedFormat");
        Assert.Equal(6, verified.GetProperty("Channels").GetInt32());
        Assert.Equal(96000, verified.GetProperty("SampleRate").GetInt32());
        Assert.Equal(24, verified.GetProperty("EffectiveBits").GetInt32());
    }

    [Fact]
    public void Does_not_attempt_restoration_when_nothing_was_modified()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16]);
        h.Wasapi.DefaultHResult = FormatSupportResult.AudclntUnsupportedFormat;

        var exit = h.Run();

        Assert.Equal(ExitCode.FormatMismatch, exit);
        Assert.Empty(h.Svcl.SetCommands);
    }

    [Fact]
    public void Warns_on_the_console_when_the_original_format_was_not_restored()
    {
        var h = new AppHarness()
            .WithEndpoint()
            .WithLpcmDisplay(maxChannels: 2, depths: [16])
            .WithOriginalFormat(2, 44100, 16);
        h.Svcl.SubstituteOnSet = f => f.SampleRate == 44100 ? (2, 32000, 16) : f;

        h.Run();

        Assert.Contains("was NOT restored", h.ConsoleText);
    }
}
