namespace AudioDeviceConfigurator.Cli;

public sealed record CliOptions(
    bool ShowHelp,
    bool ListDevices,
    string? DeviceId,
    string? Error)
{
    public static CliOptions Parse(string[] args)
    {
        var showHelp = false;
        var list = false;
        string? deviceId = null;

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

                default:
                    return Invalid($"Unknown argument '{arg}'.");
            }
        }

        return new CliOptions(showHelp, list, deviceId, Error: null);

        static CliOptions Invalid(string error) => new(false, false, null, error);
    }

    public const string HelpText = """
        AudioDeviceConfigurator - Windows playback format configurator

        USAGE
          AudioDeviceConfigurator [options]

        OPTIONS
          (no options)          Read the current default render endpoint's Control Panel options,
                                then interactively select and apply speaker channels and audio format.
          --device-id <id>      Configure a specific active render endpoint by endpoint ID.
          --list                List active render endpoints, then exit without changing settings.
          --help, -h            Show this help text and exit.

        WORKFLOW
          1. Reads speaker-channel and Default Format options from the legacy Sound Control Panel.
          2. Lets you select only values that Windows Control Panel exposes.
          3. Shows a final summary; pressing Enter accepts the default Y confirmation.
          4. Uses svcl.exe to apply both settings and reads them back for exact verification.
          5. Keeps verified settings active; on failure, restores and verifies the original settings.

        REQUIREMENTS AND SIDE EFFECTS
          - Requires svcl.exe beside this executable and an interactive, unlocked Windows desktop.
          - Opens the legacy Sound Control Panel briefly. Do not interact with it during enumeration.
          - A confirmed operation permanently changes the selected endpoint's speaker configuration
            and Default Format. It does not change the default playback endpoint or play audio.
          - If the original speaker channel mask cannot be read, no setting is changed.

        EXIT CODES
          0  PASS      The selected settings were applied and read back successfully.
          1  FAIL      Apply/readback failed, but the original settings were restored and verified.
          2  ERROR     Discovery, SVCL, or rollback failed.
          3  CANCELLED The user cancelled before settings were changed.
          4  N/A       No selectable Control Panel speaker channels or formats were found.
        """;
}
