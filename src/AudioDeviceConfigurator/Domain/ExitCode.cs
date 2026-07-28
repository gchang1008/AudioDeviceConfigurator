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

public static class ExitCodePrecedence
{
    /// <summary>Higher rank wins when several outcomes are possible.</summary>
    public static int Rank(ExitCode code) => code switch
    {
        ExitCode.SystemError => 4,
        ExitCode.Cancelled => 3,
        ExitCode.FormatMismatch => 2,
        ExitCode.NotApplicable => 1,
        ExitCode.Pass => 0,
        _ => 0,
    };

    public static ExitCode Max(ExitCode a, ExitCode b) => Rank(a) >= Rank(b) ? a : b;
}
