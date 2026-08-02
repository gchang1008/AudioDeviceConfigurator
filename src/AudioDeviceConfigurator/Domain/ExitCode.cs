namespace AudioDeviceConfigurator.Domain;

/// <summary>Process exit codes defined by the specification.</summary>
public enum ExitCode
{
    Pass = 0,
    FormatMismatch = 1,
    SystemError = 2,
    Cancelled = 3,
    NotApplicable = 4,
}
