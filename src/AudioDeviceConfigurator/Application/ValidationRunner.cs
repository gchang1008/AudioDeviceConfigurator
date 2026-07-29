using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Candidates;
using AudioDeviceConfigurator.Cli;
using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Edid;
using AudioDeviceConfigurator.Pairing;
using AudioDeviceConfigurator.Reporting;
using AudioDeviceConfigurator.Svcl;

namespace AudioDeviceConfigurator.Application;

/// <summary>Everything the application needs from the outside world.</summary>
public sealed record AppEnvironment(
    IDisplayProvider Displays,
    IAudioEndpointProvider Endpoints,
    IWasapiFormatProbe Wasapi,
    IProcessRunner ProcessRunner,
    IFileSystem FileSystem,
    IClock Clock,
    IConsole Console,
    ISystemInfoProvider SystemInfo,
    IDriverMetadataProvider DriverMetadata,
    string ApplicationDirectory);

/// <summary>
/// Runs the whole validation workflow. This is the seam the tests drive: everything below it is
/// deterministic, and every Windows boundary is injected.
/// </summary>
public sealed class ValidationRunner(AppEnvironment env, CancellationToken cancellationToken = default)
{
    public static readonly TimeSpan ReadbackPollInterval = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan ReadbackTimeout = TimeSpan.FromSeconds(3);

    private readonly List<string> _errors = [];
    private readonly List<CandidateReport> _candidateReports = [];

    private SavedFormat? _originalFormat;
    private bool _formatModified;
    private bool _restoreFailed;
    private RestoreReport _restore = new(Attempted: false, Succeeded: false, Trigger: null, VerifiedFormat: null, FailureDetail: null);
    private SvclClient? _svcl;
    private EndpointReport? _endpointReport;
    private MonitorReport? _monitorReport;
    private string _monitorNameForFile = "UnknownMonitor";

    public ExitCode Run(CliOptions options)
    {
        var startedAt = env.Clock.LocalNow;
        var system = env.SystemInfo.GetSystemInfo();

        if (options.Error is not null)
        {
            env.Console.WriteError(options.Error);
            env.Console.WriteLine();
            env.Console.WriteLine(CliOptions.HelpText);
            return ExitCode.SystemError;
        }

        if (options.ShowHelp)
        {
            env.Console.WriteLine(CliOptions.HelpText);
            return ExitCode.Pass;
        }

        if (options.ListDevices)
        {
            return RunList();
        }

        var status = ExitCode.Pass;
        try
        {
            status = Execute(options);
        }
        catch (SelectionCancelledException ex)
        {
            _errors.Add(ex.Message);
            env.Console.WriteLine();
            env.Console.WriteLine("Cancelled by user.");
            status = ExitCode.Cancelled;
        }
        catch (OperationCanceledException)
        {
            _errors.Add("The run was cancelled (Ctrl+C).");
            env.Console.WriteLine();
            env.Console.WriteLine("Cancelled by user (Ctrl+C).");
            status = ExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            _errors.Add(ex.Message);
            env.Console.WriteError($"ERROR: {ex.Message}");
            status = ExitCode.SystemError;
        }

        // Restoration is attempted for every path that reached a modification, including errors.
        // A restore that has already failed is never retried: the first failure is the reportable
        // outcome, and continuing to touch a system we cannot restore is what story 46 forbids.
        if (_formatModified && !_restoreFailed && !_restore.Succeeded)
        {
            var trigger = status switch
            {
                ExitCode.Cancelled => "cancellation",
                ExitCode.SystemError => "error",
                _ => "completion",
            };

            if (!TryRestore(trigger))
            {
                status = ExitCodePrecedence.Max(status, ExitCode.SystemError);
            }
        }

        return Finish(status, startedAt, system);
    }

    private ExitCode RunList()
    {
        try
        {
            env.Console.WriteLine("Active render endpoints:");
            foreach (var endpoint in env.Endpoints.GetActiveRenderEndpoints())
            {
                var marker = endpoint.IsDefault ? " (default)" : "";
                env.Console.WriteLine($"  {endpoint.FriendlyName}{marker}");
                env.Console.WriteLine($"    Endpoint ID : {endpoint.EndpointId}");
                env.Console.WriteLine($"    Description : {endpoint.DeviceDescription}");
                env.Console.WriteLine($"    Container   : {endpoint.ContainerId ?? "Unknown"}");
            }

            env.Console.WriteLine();
            env.Console.WriteLine("Active displays:");
            foreach (var display in env.Displays.GetActiveDisplays())
            {
                env.Console.WriteLine($"  {display.FriendlyName}");
                env.Console.WriteLine($"    Monitor ID  : {display.MonitorId}");
                env.Console.WriteLine($"    Adapter     : {display.AdapterName ?? "Unknown"}");
                env.Console.WriteLine($"    Container   : {display.ContainerId ?? "Unknown"}");
            }

            return ExitCode.Pass;
        }
        catch (Exception ex)
        {
            env.Console.WriteError($"ERROR: {ex}");
            return ExitCode.SystemError;
        }
    }

    private ExitCode Execute(CliOptions options)
    {
        _svcl = new SvclClient(
            env.ProcessRunner,
            env.FileSystem,
            Path.Combine(env.ApplicationDirectory, "svcl.exe"));
        _svcl.VerifyInstallation();

        var target = new TargetSelector(env.Console).Select(
            env.Endpoints.GetActiveRenderEndpoints(),
            env.Displays.GetActiveDisplays(),
            options.DeviceId,
            options.MonitorId);

        _endpointReport = ToReport(target.Endpoint);

        var parsed = EdidParser.Parse(target.Display.RawEdid);
        _monitorNameForFile = parsed.MonitorName ?? target.Display.FriendlyName;

        var candidates = CandidateGenerator.Generate(parsed);
        _monitorReport = ToReport(target.Display, parsed, candidates, target.PairingMethod);

        env.Console.WriteLine($"Endpoint : {target.Endpoint.FriendlyName}");
        env.Console.WriteLine($"           {target.Endpoint.EndpointId}");
        env.Console.WriteLine($"Monitor  : {target.Display.FriendlyName}");
        env.Console.WriteLine($"           {target.Display.MonitorId}");
        env.Console.WriteLine();

        if (candidates.Count == 0)
        {
            env.Console.WriteLine("The monitor's EDID is valid but declares no LPCM audio capability.");
            return ExitCode.NotApplicable;
        }

        var deviceSelector = target.Endpoint.SvclCommandLineId ?? target.Endpoint.EndpointId;
        _originalFormat = _svcl.SaveDeviceFormat(deviceSelector);
        env.Console.WriteLine($"Original default format: {_originalFormat}");
        env.Console.WriteLine($"Testing {candidates.Count} EDID-derived format(s).");
        env.Console.WriteLine();

        return TestCandidates(candidates, deviceSelector);
    }

    private ExitCode TestCandidates(IReadOnlyList<CandidateFormat> candidates, string deviceSelector)
    {
        var status = ExitCode.Pass;

        for (var i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[i];
            var report = TestCandidate(i + 1, candidate, deviceSelector, out var needsRestore);
            _candidateReports.Add(report);

            if (report.Status != CandidateStatus.Pass)
            {
                status = ExitCodePrecedence.Max(status, ExitCode.FormatMismatch);
            }

            // A failed apply must be undone before the next candidate so failures cannot contaminate results.
            if (needsRestore && !TryRestore("candidate failure"))
            {
                MarkRemainingNotTested(candidates, i + 1);
                return ExitCode.SystemError;
            }
        }

        return status;
    }

    private CandidateReport TestCandidate(
        int index,
        CandidateFormat candidate,
        string deviceSelector,
        out bool needsRestore)
    {
        needsRestore = false;

        var support = env.Wasapi.IsExclusiveFormatSupported(_endpointReport!.EndpointId, candidate.Format);
        if (!support.IsSupported)
        {
            var isUnsupported = support.HResult == FormatSupportResult.AudclntUnsupportedFormat;
            return BuildReport(
                index,
                candidate,
                isUnsupported ? WasapiStageResult.Unsupported : WasapiStageResult.Error,
                support.HResultText,
                ApplyStageResult.Skipped,
                readback: null,
                elapsed: null,
                failure: isUnsupported
                    ? "WASAPI Exclusive mode reported the format as unsupported."
                    : $"WASAPI Exclusive format query returned {support.HResultText}.",
                status: isUnsupported ? CandidateStatus.UnsupportedByWasapi : CandidateStatus.WasapiError);
        }

        // Only S_OK candidates are allowed to change any system setting.
        needsRestore = true;
        _formatModified = true;
        string? failure = null;
        SavedFormat? readback = null;
        var applyResult = ApplyStageResult.Failed;
        double elapsed = 0;

        try
        {
            _svcl!.SetDefaultFormat(deviceSelector, candidate.EffectiveBits, candidate.SampleRate, candidate.Channels);
            (readback, elapsed) = PollForReadback(deviceSelector, candidate);

            if (Matches(readback, candidate))
            {
                applyResult = ApplyStageResult.Matched;
                needsRestore = false;
            }
            else
            {
                applyResult = ApplyStageResult.Mismatched;
                failure = $"Readback did not match: expected {candidate}, got {readback}.";
            }
        }
        catch (SvclException ex)
        {
            failure = ex.Message;
        }

        return BuildReport(
            index,
            candidate,
            WasapiStageResult.Supported,
            support.HResultText,
            applyResult,
            readback,
            elapsed,
            failure,
            applyResult == ApplyStageResult.Matched ? CandidateStatus.Pass : CandidateStatus.ApplyFailed);
    }

    /// <summary>Polls the saved format every 200 ms for up to 3 s so slower drivers are not falsely failed.</summary>
    private (SavedFormat Format, double ElapsedMs) PollForReadback(string deviceSelector, CandidateFormat candidate)
    {
        var startedAt = env.Clock.Elapsed;
        var deadline = startedAt + ReadbackTimeout;
        SavedFormat? last = null;
        Exception? lastError = null;

        while (true)
        {
            try
            {
                last = _svcl!.SaveDeviceFormat(deviceSelector);
                lastError = null;
                if (Matches(last, candidate))
                {
                    return (last, (env.Clock.Elapsed - startedAt).TotalMilliseconds);
                }
            }
            catch (SvclException ex)
            {
                lastError = ex;
            }

            if (env.Clock.Elapsed >= deadline)
            {
                break;
            }

            env.Clock.Sleep(ReadbackPollInterval);
        }

        if (last is null)
        {
            throw lastError ?? new SvclException("The format could not be read back within the timeout.");
        }

        return (last, (env.Clock.Elapsed - startedAt).TotalMilliseconds);
    }

    private static bool Matches(SavedFormat saved, CandidateFormat candidate) =>
        saved.Channels == candidate.Channels
        && saved.SampleRate == candidate.SampleRate
        && saved.EffectiveBits == candidate.EffectiveBits;

    private bool TryRestore(string trigger)
    {
        if (_originalFormat is null || _svcl is null || _endpointReport is null)
        {
            return true;
        }

        var deviceSelector = _endpointReport.SvclCommandLineId ?? _endpointReport.EndpointId;
        try
        {
            _svcl.SetDefaultFormat(
                deviceSelector,
                _originalFormat.EffectiveBits,
                _originalFormat.SampleRate,
                _originalFormat.Channels);

            var verified = PollForRestoreVerification(deviceSelector);
            if (verified is null)
            {
                throw new SvclException(
                    $"The original format could not be verified after restoration (expected {_originalFormat}).");
            }

            _restore = new RestoreReport(
                Attempted: true,
                Succeeded: true,
                Trigger: trigger,
                VerifiedFormat: FormatSnapshot.From(verified),
                FailureDetail: null);
            return true;
        }
        catch (Exception ex)
        {
            var detail = $"Restoration failed ({trigger}): {ex.Message}";
            _errors.Add(detail);
            _restoreFailed = true;
            _restore = new RestoreReport(
                Attempted: true,
                Succeeded: false,
                Trigger: trigger,
                VerifiedFormat: null,
                FailureDetail: detail);
            env.Console.WriteError(detail);
            return false;
        }
    }

    private SavedFormat? PollForRestoreVerification(string deviceSelector)
    {
        var deadline = env.Clock.Elapsed + ReadbackTimeout;
        while (true)
        {
            var current = _svcl!.SaveDeviceFormat(deviceSelector);
            if (current.Channels == _originalFormat!.Channels
                && current.SampleRate == _originalFormat.SampleRate
                && current.EffectiveBits == _originalFormat.EffectiveBits)
            {
                return current;
            }

            if (env.Clock.Elapsed >= deadline)
            {
                return null;
            }

            env.Clock.Sleep(ReadbackPollInterval);
        }
    }

    private void MarkRemainingNotTested(IReadOnlyList<CandidateFormat> candidates, int startIndex)
    {
        for (var i = startIndex; i < candidates.Count; i++)
        {
            _candidateReports.Add(BuildReport(
                i + 1,
                candidates[i],
                WasapiStageResult.NotRun,
                "",
                ApplyStageResult.NotRun,
                readback: null,
                elapsed: null,
                failure: "Testing stopped because the original format could not be restored.",
                status: CandidateStatus.NotTested));
        }
    }

    private static CandidateReport BuildReport(
        int index,
        CandidateFormat candidate,
        WasapiStageResult wasapi,
        string hresult,
        ApplyStageResult apply,
        SavedFormat? readback,
        double? elapsed,
        string? failure,
        CandidateStatus status) =>
        new(
            Index: index,
            Channels: candidate.Channels,
            SampleRate: candidate.SampleRate,
            EffectiveBits: candidate.EffectiveBits,
            ContainerBits: candidate.ContainerBits,
            ChannelMask: candidate.Format.ChannelMask,
            SourceSadReferences: candidate.SourceSadReferences,
            WasapiResult: wasapi,
            WasapiHResult: hresult,
            ApplyResult: apply,
            ReadbackSummary: readback?.ToString(),
            ReadbackChannels: readback?.Channels,
            ReadbackSampleRate: readback?.SampleRate,
            ReadbackEffectiveBits: readback?.EffectiveBits,
            ReadbackContainerBits: readback?.ContainerBits,
            ApplyElapsedMilliseconds: elapsed,
            FailureDetail: failure,
            Status: status);

    private ExitCode Finish(ExitCode status, DateTimeOffset startedAt, SystemInfo system)
    {
        var completedAt = env.Clock.LocalNow;
        var report = new RunReport(
            SchemaVersion: RunReport.CurrentSchemaVersion,
            RunId: $"{startedAt:yyyyMMdd_HHmmss}_{system.MachineName}",
            StartedAtLocal: startedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            CompletedAtLocal: completedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            OverallStatus: DescribeStatus(status),
            ExitCode: (int)status,
            ExitCodeMeaning: DescribeExitCode(status),
            System: system,
            Endpoint: _endpointReport,
            Monitor: _monitorReport,
            OriginalFormat: _originalFormat is null ? null : FormatSnapshot.From(_originalFormat),
            SvclPath: _svcl?.ExecutablePath,
            SvclVersion: _svcl?.DetectedVersion,
            Candidates: _candidateReports,
            SvclCommands: _svcl?.CommandLog ?? [],
            Restore: _restore,
            Errors: _errors);

        PrintSummary(report);

        try
        {
            var writer = new ReportWriter(env.FileSystem, Path.Combine(env.ApplicationDirectory, "Reports"));
            var paths = writer.Write(report, system.MachineName, _monitorNameForFile, completedAt);
            env.Console.WriteLine($"JSON report : {paths.JsonPath}");
            env.Console.WriteLine($"CSV report  : {paths.CsvPath}");
        }
        catch (Exception ex)
        {
            env.Console.WriteError($"ERROR: {ex.Message}");
            status = ExitCodePrecedence.Max(status, ExitCode.SystemError);
        }

        env.Console.WriteLine();
        env.Console.WriteLine($"Overall status: {DescribeStatus(status)}");
        env.Console.WriteLine($"Exit code {(int)status}: {DescribeExitCode(status)}");
        return status;
    }

    private void PrintSummary(RunReport report)
    {
        var passing = report.Candidates.Where(c => c.Status == CandidateStatus.Pass).ToList();

        env.Console.WriteLine();
        if (passing.Count > 0)
        {
            env.Console.WriteLine("Supported formats:");
            env.Console.WriteLine("  Channels  Sample rate  Bit depth  Container");
            foreach (var c in passing)
            {
                env.Console.WriteLine(
                    $"  {c.Channels,8}  {c.SampleRate,11}  {c.EffectiveBits,9}  {c.ContainerBits,9}");
            }
        }
        else if (report.Candidates.Count > 0)
        {
            env.Console.WriteLine("Supported formats: none");
        }

        if (report.Candidates.Count > 0)
        {
            env.Console.WriteLine();
            env.Console.WriteLine($"Passed                       : {report.PassCount}");
            env.Console.WriteLine($"Unsupported by WASAPI        : {report.UnsupportedCount}");
            env.Console.WriteLine($"Apply/readback failed        : {report.FailedCount}");
            if (report.NotTestedCount > 0)
            {
                env.Console.WriteLine($"Not tested                   : {report.NotTestedCount}");
            }
        }

        if (_formatModified)
        {
            env.Console.WriteLine();
            env.Console.WriteLine(_restore.Succeeded
                ? $"Original format restored: {_originalFormat}"
                : "WARNING: the original default format was NOT restored.");
        }

        env.Console.WriteLine();
    }

    private static string DescribeStatus(ExitCode status) => status switch
    {
        ExitCode.Pass => "PASS",
        ExitCode.FormatMismatch => "FAIL",
        ExitCode.SystemError => "ERROR",
        ExitCode.Cancelled => "CANCELLED",
        ExitCode.NotApplicable => "N/A",
        _ => "UNKNOWN",
    };

    private static string DescribeExitCode(ExitCode status) => status switch
    {
        ExitCode.Pass => "every EDID-declared format was supported, applied and read back",
        ExitCode.FormatMismatch => "one or more formats were unsupported or could not be applied",
        ExitCode.SystemError => "EDID, pairing, SVCL, restore, reporting or other system error",
        ExitCode.Cancelled => "cancelled by the user",
        ExitCode.NotApplicable => "the monitor declares no LPCM audio capability",
        _ => "unknown",
    };

    private static EndpointReport ToReport(EndpointInfo e) => new(
        e.EndpointId, e.FriendlyName, e.DeviceDescription, e.SvclCommandLineId,
        e.DriverName, e.DriverVersion, e.IsDefault, e.ContainerId);

    private static MonitorReport ToReport(
        DisplayInfo display,
        ParsedEdid parsed,
        IReadOnlyList<CandidateFormat> candidates,
        PairingMethod pairingMethod)
    {
        var testedSources = candidates.SelectMany(c => c.SourceSadReferences).ToHashSet();

        return new MonitorReport(
            MonitorId: display.MonitorId,
            FriendlyName: display.FriendlyName,
            ManufacturerId: parsed.ManufacturerId,
            ProductCode: parsed.ProductCode,
            SerialNumber: parsed.SerialNumber,
            MonitorName: parsed.MonitorName,
            EdidVersion: parsed.EdidVersion,
            EdidRevision: parsed.EdidRevision,
            ExtensionCount: parsed.ExtensionCount,
            AdapterName: display.AdapterName,
            GpuDriverName: display.GpuDriverName,
            GpuDriverProvider: display.GpuDriverProvider,
            GpuDriverVersion: display.GpuDriverVersion,
            ContainerId: display.ContainerId,
            PairingMethod: pairingMethod.ToString(),
            RawEdidHex: Convert.ToHexString(parsed.RawBytes),
            AudioDescriptors: parsed.AudioDescriptors.Select(d => new SadReport(
                d.SourceReference,
                d.FormatCode.ToString(),
                d.IsLpcm,
                d.MaxChannels,
                d.SampleRates,
                d.BitDepths,
                d.MaxBitRateKbps,
                Convert.ToHexString(d.RawBytes),
                testedSources.Contains(d.SourceReference))).ToList());
    }
}
