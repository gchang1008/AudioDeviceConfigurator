namespace AudioDeviceConfigurator.Cli;

public sealed record CliOptions(
    bool ShowHelp,
    bool ListDevices,
    string? DeviceId,
    string? MonitorId,
    string? Error)
{
    public static CliOptions Parse(string[] args)
    {
        var showHelp = false;
        var list = false;
        string? deviceId = null;
        string? monitorId = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--help":
                case "-h":
                case "-?":
                case "/?":
                    showHelp = true;
                    break;

                case "--list":
                    list = true;
                    break;

                case "--device-id":
                    if (i + 1 >= args.Length)
                    {
                        return Invalid("--device-id requires a value.");
                    }

                    deviceId = args[++i];
                    break;

                case "--monitor-id":
                    if (i + 1 >= args.Length)
                    {
                        return Invalid("--monitor-id requires a value.");
                    }

                    monitorId = args[++i];
                    break;

                default:
                    return Invalid($"Unknown argument '{arg}'.");
            }
        }

        return new CliOptions(showHelp, list, deviceId, monitorId, Error: null);

        static CliOptions Invalid(string error) => new(false, false, null, null, error);
    }

    public const string HelpText = """
        AudioDeviceConfigurator - EDID-driven audio capability validator

        USAGE
          AudioDeviceConfigurator [options]

        OPTIONS
          (no options)          Test the current default active render endpoint against the
                                EDID of its paired active monitor.
          --device-id <id>      Test a specific active render endpoint by its endpoint ID.
          --monitor-id <id>     Use a specific active monitor by its device ID.
          --list                List active displays and active render endpoints, then exit.
          --help, -h            Show this help text and exit.

        Supplying both --device-id and --monitor-id makes the run fully unattended.
        When pairing is ambiguous and IDs are not supplied, an interactive prompt is shown.

        WHAT IT DOES
          1. Reads and strictly validates the monitor's EDID (header, block lengths, checksums).
          2. Parses CTA-861 LPCM Short Audio Descriptors into candidate formats
             (2/6/8 channels, 16/20/24-bit, 32-192 kHz - only what EDID declares).
          3. Queries WASAPI Exclusive IAudioClient::IsFormatSupported for every candidate.
          4. For S_OK candidates, applies the format with svcl.exe /SetDefaultFormat, then polls
             /SaveDeviceFormat every 200 ms for up to 3 s and requires an exact readback match.
          5. Restores the original default format and writes JSON and CSV reports.

        SIDE EFFECTS
          - Temporarily changes the DEFAULT FORMAT of the selected render endpoint.
          - The original format is restored on completion, on handled errors, and on Ctrl+C.
          - Does NOT change the default playback device, speaker configuration, or any other
            setting, does not play audio, and does not close other applications.
          - Requires svcl.exe 1.28 or newer in the application directory.
          - Administrator privileges are not required.

        REPORTS
          Written to the Reports directory beside this executable as
          <PcName>_<MonitorName>_<yyyyMMdd_HHmmss>.json and .csv (local time, with a
          sequence suffix when names collide). Reports are written for every outcome.

        EXIT CODES
          0  PASS      Every EDID-declared format was supported, applied and read back.
          1  FAIL      One or more formats were unsupported or could not be applied.
          2  ERROR     EDID, pairing, SVCL, restore, reporting or other system error.
          3  CANCELLED The user cancelled an interactive selection.
          4  N/A       The monitor's EDID is valid but declares no LPCM audio capability.
        """;
}
