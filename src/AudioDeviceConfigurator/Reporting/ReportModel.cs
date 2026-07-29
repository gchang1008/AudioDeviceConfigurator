using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Svcl;

namespace AudioDeviceConfigurator.Reporting;

/// <summary>Outcome of the WASAPI Exclusive stage for one candidate.</summary>
public enum WasapiStageResult
{
    NotRun,
    Supported,
    Unsupported,
    Error,
}

/// <summary>Outcome of the SVCL apply/readback stage for one candidate.</summary>
public enum ApplyStageResult
{
    NotRun,
    Skipped,
    Matched,
    Mismatched,
    Failed,
}

/// <summary>Aggregate verdict for one candidate format.</summary>
public enum CandidateStatus
{
    Pass,
    UnsupportedByWasapi,
    ApplyFailed,
    WasapiError,
    NotTested,
}

public sealed record CandidateReport(
    int Index,
    int Channels,
    int SampleRate,
    int EffectiveBits,
    int ContainerBits,
    uint ChannelMask,
    IReadOnlyList<string> SourceSadReferences,
    WasapiStageResult WasapiResult,
    string WasapiHResult,
    ApplyStageResult ApplyResult,
    string? ReadbackSummary,
    int? ReadbackChannels,
    int? ReadbackSampleRate,
    int? ReadbackEffectiveBits,
    int? ReadbackContainerBits,
    double? ApplyElapsedMilliseconds,
    string? FailureDetail,
    CandidateStatus Status);

public sealed record MonitorReport(
    string MonitorId,
    string FriendlyName,
    string ManufacturerId,
    int ProductCode,
    uint SerialNumber,
    string? MonitorName,
    int EdidVersion,
    int EdidRevision,
    int ExtensionCount,
    string? AdapterName,
    string? GpuDriverName,
    string? GpuDriverProvider,
    string? GpuDriverVersion,
    string? ContainerId,
    string PairingMethod,
    string RawEdidHex,
    IReadOnlyList<SadReport> AudioDescriptors);

public sealed record SadReport(
    string SourceReference,
    string FormatCode,
    bool IsLpcm,
    int MaxChannels,
    IReadOnlyList<int> SampleRates,
    IReadOnlyList<int> BitDepths,
    int MaxBitRateKbps,
    string RawHex,
    bool Tested);

public sealed record EndpointReport(
    string EndpointId,
    string FriendlyName,
    string DeviceDescription,
    string? SvclCommandLineId,
    string? DriverName,
    string? DriverVersion,
    bool IsDefault,
    string? ContainerId);

public sealed record FormatSnapshot(
    int Channels,
    int SampleRate,
    int EffectiveBits,
    int ContainerBits,
    uint ChannelMask,
    string FormatTag,
    string RawHex)
{
    public static FormatSnapshot From(SavedFormat format) => new(
        format.Channels,
        format.SampleRate,
        format.EffectiveBits,
        format.ContainerBits,
        format.ChannelMask,
        $"0x{format.FormatTag:X4}",
        Convert.ToHexString(format.RawBytes));
}

public sealed record RestoreReport(
    bool Attempted,
    bool Succeeded,
    string? Trigger,
    FormatSnapshot? VerifiedFormat,
    string? FailureDetail);

public sealed record RunReport(
    string SchemaVersion,
    string RunId,
    string StartedAtLocal,
    string CompletedAtLocal,
    string OverallStatus,
    int ExitCode,
    string ExitCodeMeaning,
    SystemInfo System,
    EndpointReport? Endpoint,
    MonitorReport? Monitor,
    FormatSnapshot? OriginalFormat,
    string? SvclPath,
    string? SvclVersion,
    IReadOnlyList<CandidateReport> Candidates,
    IReadOnlyList<SvclCommandLog> SvclCommands,
    RestoreReport Restore,
    IReadOnlyList<string> Errors)
{
    public const string CurrentSchemaVersion = "1.0";

    public int PassCount => Candidates.Count(c => c.Status == CandidateStatus.Pass);

    public int UnsupportedCount => Candidates.Count(c => c.Status == CandidateStatus.UnsupportedByWasapi);

    public int FailedCount => Candidates.Count(c =>
        c.Status is CandidateStatus.ApplyFailed or CandidateStatus.WasapiError);

    public int NotTestedCount => Candidates.Count(c => c.Status == CandidateStatus.NotTested);
}
