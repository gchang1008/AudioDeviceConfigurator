using AudioDeviceConfigurator.Domain;

namespace AudioDeviceConfigurator.Abstractions;

/// <summary>An active display path Windows currently recognizes, with its raw EDID.</summary>
public sealed record DisplayInfo(
    string MonitorId,
    string FriendlyName,
    string? AdapterName,
    string? GpuDriverVersion,
    byte[] RawEdid);

/// <summary>An active WASAPI render endpoint.</summary>
public sealed record EndpointInfo(
    string EndpointId,
    string FriendlyName,
    string DeviceDescription,
    string? SvclCommandLineId,
    string? DriverName,
    string? DriverVersion,
    bool IsDefault,
    string? ContainerId);

/// <summary>Enumerates active display paths and their EDID from the current Windows environment.</summary>
public interface IDisplayProvider
{
    IReadOnlyList<DisplayInfo> GetActiveDisplays();
}

/// <summary>Enumerates active render endpoints via Core Audio.</summary>
public interface IAudioEndpointProvider
{
    IReadOnlyList<EndpointInfo> GetActiveRenderEndpoints();

    EndpointInfo? GetDefaultRenderEndpoint();
}

/// <summary>Result of a WASAPI Exclusive-mode IsFormatSupported query.</summary>
public sealed record FormatSupportResult(int HResult)
{
    public const int SOk = 0;
    public const int AudclntUnsupportedFormat = unchecked((int)0x88890008);

    public bool IsSupported => HResult == SOk;

    public string HResultText => $"0x{HResult:X8}";
}

/// <summary>Queries endpoint format support in WASAPI Exclusive mode without initializing a stream.</summary>
public interface IWasapiFormatProbe
{
    FormatSupportResult IsExclusiveFormatSupported(string endpointId, WaveFormat format);
}

/// <summary>Raw outcome of running the SVCL executable.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs the external SVCL process.</summary>
public interface IProcessRunner
{
    ProcessResult Run(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout);
}

/// <summary>Filesystem operations the application performs, isolated for testability.</summary>
public interface IFileSystem
{
    bool FileExists(string path);

    byte[] ReadAllBytes(string path);

    void WriteAllText(string path, string contents);

    void DeleteFile(string path);

    void CreateDirectory(string path);

    string? GetFileVersion(string path);

    string GetTempFilePath(string suffix);
}

/// <summary>Time source, so polling windows are deterministic in tests.</summary>
public interface IClock
{
    DateTimeOffset LocalNow { get; }

    DateTimeOffset UtcNow { get; }

    void Sleep(TimeSpan duration);

    TimeSpan Elapsed { get; }
}

/// <summary>Console I/O for output and interactive selection.</summary>
public interface IConsole
{
    void WriteLine(string text = "");

    void WriteError(string text);

    string? ReadLine();
}

/// <summary>Static machine facts included in every report.</summary>
public sealed record SystemInfo(
    string MachineName,
    string OsDescription,
    string OsVersion,
    string Architecture,
    string UserName,
    string ApplicationVersion);

public interface ISystemInfoProvider
{
    SystemInfo GetSystemInfo();
}
