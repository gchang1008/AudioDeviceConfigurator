using AudioDeviceConfigurator.Domain;

namespace AudioDeviceConfigurator.Abstractions;

/// <summary>An active Windows render endpoint.</summary>
public sealed record EndpointInfo(
    string EndpointId,
    string FriendlyName,
    string DeviceDescription,
    string? DriverName,
    string? DriverVersion,
    bool IsDefault);

/// <summary>Status of parsing a Control Panel format item.</summary>
public enum ControlPanelParseStatus
{
    Parsed,
    Unparsed,
}

/// <summary>One format item read from the target endpoint's Control Panel Advanced page.</summary>
public sealed record ControlPanelFormatItem(
    int Index,
    string DisplayText,
    int? Channels,
    int? SampleRate,
    int? EffectiveBits,
    int? ContainerBits,
    ControlPanelParseStatus ParseStatus,
    string? ParseFailure);

/// <summary>One speaker configuration option read from the target endpoint's speaker setup page.</summary>
public sealed record ControlPanelSpeakerConfigurationItem(
    int Index,
    string DisplayText,
    int Channels);

/// <summary>Evidence and lifecycle details for one Control Panel read.</summary>
public sealed record ControlPanelFormatSnapshot(
    DateTimeOffset Started,
    DateTimeOffset Completed,
    string NavigationStrategy,
    bool CleanupAttempted,
    bool CleanupSucceeded,
    string? CleanupFailureDetail);

public sealed record ControlPanelFormatResult(
    IReadOnlyList<ControlPanelFormatItem> Items,
    IReadOnlyList<ControlPanelSpeakerConfigurationItem> SpeakerConfigurations,
    int? MaxSupportedChannels,
    ControlPanelFormatSnapshot Snapshot);

/// <summary>Reads the target endpoint's read-only Default Format choices from Windows Control Panel.</summary>
public interface IControlPanelFormatProvider
{
    ControlPanelFormatResult ReadDefaultFormats(EndpointInfo endpoint, TimeSpan timeout);
}

/// <summary>Describes a top-level Win32 window without requiring UI Automation.</summary>
public sealed record Win32WindowInfo(IntPtr Handle, string ClassName, string Title, bool IsVisible = true);

public sealed record Win32FormatReadResult(int TabIndex, int OriginalTabIndex, IReadOnlyList<string> Items);

public interface IWin32ControlApi
{
    bool IsWindowUnicode(IntPtr handle);

    bool IsWindow(IntPtr handle);

    bool TrySendMessageTimeout(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam, TimeSpan timeout, out IntPtr result);

    bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    bool IsWindowVisible(IntPtr handle);

    string GetWindowClassName(IntPtr handle);

    string GetWindowText(IntPtr handle);

    IReadOnlyList<IntPtr> EnumerateChildWindows(IntPtr parent);

    IReadOnlyList<Win32WindowInfo> EnumerateTopLevelWindows();

    bool TrySelectAdvancedTab(IntPtr dialog, TimeSpan timeout, out string observed);

    bool TryReadComboBoxItems(IntPtr handle, TimeSpan timeout, out IReadOnlyList<string> items, out string diagnostic);

    bool TryReadFormatItemsAcrossTabs(IntPtr dialog, TimeSpan timeout, out Win32FormatReadResult result, out string observed);
}

/// <summary>Enumerates active render endpoints via Core Audio.</summary>
public interface IAudioEndpointProvider
{
    IReadOnlyList<EndpointInfo> GetActiveRenderEndpoints();

    EndpointInfo? GetDefaultRenderEndpoint();
}

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

    void DeleteFile(string path);

    string? GetFileVersion(string path);

    string GetTempFilePath(string suffix);
}

/// <summary>Time source, so polling windows are deterministic in tests.</summary>
public interface IClock
{
    void Sleep(TimeSpan duration);
}

/// <summary>Console I/O for output and interactive selection.</summary>
public interface IConsole
{
    void WriteLine(string text = "");

    void WriteError(string text);

    string? ReadLine();
}
