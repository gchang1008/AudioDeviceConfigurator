using AudioDeviceConfigurator.Svcl;
using AudioDeviceConfigurator.Windows;

namespace AudioDeviceConfigurator.Acceptance;

internal enum AcceptanceOutcome
{
    Pass,
    Fail,
    Inconclusive,
    Error,
    Cancelled,
}

internal static class Program
{
    private static readonly TimeSpan SampleDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(75);

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h")
            {
                PrintUsage();
                return 0;
            }

            var command = args[0];
            var options = ParseOptions(args.Skip(1).ToArray());
            var executable = ResolveExecutable(options);
            var outcome = command switch
            {
                "inspect" => Inspect(executable, options),
                "run" => Run(executable, options),
                _ => throw new ArgumentException($"Unknown command '{command}'."),
            };
            Console.WriteLine($"Outcome: {outcome.ToString().ToUpperInvariant()}");
            return (int)outcome;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.WriteLine("Outcome: ERROR");
            return (int)AcceptanceOutcome.Error;
        }
    }

    private static AcceptanceOutcome Inspect(
        string executable,
        IReadOnlyDictionary<string, string> options)
    {
        using var gui = GuiAutomation.Launch(executable);
        Console.WriteLine("Endpoints exposed by the published GUI:");
        var endpoints = WaitForEndpoints(gui);
        foreach (var endpoint in endpoints)
        {
            Console.WriteLine($"- {endpoint.Name}");
            Console.WriteLine($"  ID: {endpoint.HelpText}");
        }

        Console.WriteLine();
        Console.WriteLine("Use inspect --endpoint-id <id> to read one endpoint's channels and formats.");
        if (!options.TryGetValue("endpoint-id", out var endpointId))
        {
            return AcceptanceOutcome.Pass;
        }

        var selected = endpoints.SingleOrDefault(endpoint =>
            string.Equals(endpoint.HelpText, endpointId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The GUI does not expose endpoint '{endpointId}'.");
        gui.SelectEndpoint(endpointId);
        var channels = gui.WaitForChannels();
        var sampleRates = gui.WaitForSampleRates();
        var bitDepths = gui.WaitForBitDepths();
        Console.WriteLine();
        Console.WriteLine($"Selected endpoint: {selected.Name}");
        PrintSwitches("Channels", channels);
        PrintSwitches("Sample rates", sampleRates);
        PrintSwitches("Bit depths", bitDepths);

        return AcceptanceOutcome.Pass;
    }

    private static AcceptanceOutcome Run(string executable, IReadOnlyDictionary<string, string> options)
    {
        var endpointId = Require(options, "endpoint-id");
        if (!int.TryParse(Require(options, "channels"), out var channels)
            || channels is not (2 or 4 or 6 or 8))
        {
            throw new ArgumentException("--channels must be 2, 4, 6, or 8.");
        }
        if (!int.TryParse(Require(options, "sample-rate"), out var sampleRate)
            || sampleRate is < 8000 or > 384000)
        {
            throw new ArgumentException("--sample-rate must be between 8000 and 384000.");
        }
        if (!int.TryParse(Require(options, "bit-depth"), out var bitDepth)
            || bitDepth is not (16 or 20 or 24 or 32))
        {
            throw new ArgumentException("--bit-depth must be 16, 20, 24, or 32.");
        }

        var appDirectory = Path.GetDirectoryName(executable)!;
        var svcl = new SvclClient(
            new SystemProcessRunner(),
            new SystemFileSystem(),
            Path.Combine(appDirectory, "svcl.exe"));
        svcl.VerifyInstallation();
        var before = svcl.SaveDeviceFormat(endpointId);

        using var gui = GuiAutomation.Launch(executable);
        var endpoints = WaitForEndpoints(gui);
        var endpoint = endpoints.SingleOrDefault(candidate =>
            string.Equals(candidate.HelpText, endpointId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The GUI does not expose endpoint '{endpointId}'.");
        gui.SelectEndpoint(endpointId);
        var availableChannels = gui.WaitForChannels();
        var availableSampleRates = gui.WaitForSampleRates();
        var availableBitDepths = gui.WaitForBitDepths();
        RequireEnabled(availableChannels, $"ChannelOption-{channels}");
        RequireEnabled(availableSampleRates, $"SampleRateOption-{sampleRate}");
        RequireEnabled(availableBitDepths, $"BitDepthOption-{bitDepth}");

        Console.WriteLine("The acceptance run will permanently keep a successful setting:");
        Console.WriteLine($"  Endpoint : {endpoint.Name}");
        Console.WriteLine($"  ID       : {endpointId}");
        Console.WriteLine($"  Channels   : {channels}");
        Console.WriteLine($"  Sample rate: {sampleRate}");
        Console.WriteLine($"  Bit depth  : {bitDepth}");
        Console.WriteLine($"  Original : {before}");
        Console.Write("Type APPLY to continue: ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "APPLY", StringComparison.Ordinal))
        {
            return AcceptanceOutcome.Cancelled;
        }

        gui.SelectChannel(channels);
        gui.SelectSampleRate(sampleRate);
        gui.SelectBitDepth(bitDepth);
        if (!gui.IsEnabled("ApplyButton"))
        {
            throw new InvalidOperationException("Apply did not become enabled after selecting valid options.");
        }

        PeakSampleSet baseline;
        using (var capture = new EndpointLoopbackCapture(endpointId))
        {
            baseline = capture.Sample(SampleDuration, SampleInterval);
        }
        PrintSamples("Baseline", baseline);

        var settersMayHaveStarted = false;
        var switchVerified = false;
        try
        {
            settersMayHaveStarted = true;
            gui.Invoke("ApplyButton");
            gui.WaitForStatus(
                status => status.Contains("Switch verified; playing test audio.", StringComparison.Ordinal),
                "The GUI did not report a verified switch with automatic playback.");

            var after = svcl.SaveDeviceFormat(endpointId);
            var expectedMask = SvclClient.GetSpeakerMask(channels);
            if (after.Channels != channels
                || after.EffectiveBits != bitDepth
                || after.SampleRate != sampleRate
                || after.ChannelMask != expectedMask)
            {
                throw new InvalidOperationException($"SVCL readback mismatch. Actual: {after}, mask=0x{after.ChannelMask:x}.");
            }
            switchVerified = true;

            var automatic = SampleEndpoint(endpointId);
            PrintSamples("Automatic playback", automatic);
            RequireButtonStates(gui, apply: false, play: false, stop: true, "automatic playback");

            gui.Invoke("StopButton");
            WaitForButtonStates(gui, apply: true, play: true, stop: false, "stopped");
            var stopped = SampleEndpoint(endpointId);
            PrintSamples("Stopped", stopped);

            gui.Invoke("PlayButton");
            WaitForButtonStates(gui, apply: false, play: false, stop: true, "replay");
            var replay = SampleEndpoint(endpointId);
            PrintSamples("Replay", replay);
            gui.Invoke("StopButton");

            var automaticSignal = HasDistinctSignal(baseline, stopped, automatic);
            var replaySignal = HasDistinctSignal(baseline, stopped, replay);
            if (automaticSignal && replaySignal)
            {
                return AcceptanceOutcome.Pass;
            }
            if (automatic.Maximum <= Math.Max(baseline.Percentile95, stopped.Percentile95) + 0.02f)
            {
                return baseline.Maximum > 0.05f
                    ? AcceptanceOutcome.Inconclusive
                    : AcceptanceOutcome.Fail;
            }
            return AcceptanceOutcome.Fail;
        }
        catch
        {
            if (settersMayHaveStarted && !switchVerified)
            {
                Restore(svcl, endpointId, before);
            }
            throw;
        }
    }

    private static PeakSampleSet SampleEndpoint(string endpointId)
    {
        using var capture = new EndpointLoopbackCapture(endpointId);
        return capture.Sample(SampleDuration, SampleInterval);
    }

    private static void Restore(SvclClient svcl, string endpointId, AudioDeviceConfigurator.Domain.SavedFormat before)
    {
        Console.Error.WriteLine("The switch outcome is uncertain; restoring the original settings.");
        svcl.SetSpeakersConfig(endpointId, before.ChannelMask);
        svcl.SetDefaultFormat(endpointId, before.EffectiveBits, before.SampleRate, before.Channels);
        Thread.Sleep(500);
        var restored = svcl.SaveDeviceFormat(endpointId);
        if (restored.Channels != before.Channels
            || restored.EffectiveBits != before.EffectiveBits
            || restored.SampleRate != before.SampleRate
            || restored.ChannelMask != before.ChannelMask)
        {
            throw new InvalidOperationException("The original settings did not match after acceptance rollback.");
        }
    }

    private static bool HasDistinctSignal(PeakSampleSet baseline, PeakSampleSet stopped, PeakSampleSet playback)
    {
        var reference = Math.Max(baseline.Percentile95, stopped.Percentile95);
        return playback.Maximum >= Math.Max(0.05f, reference + 0.02f)
               && playback.Percentile95 > reference + 0.01f;
    }

    private static void PrintSamples(string label, PeakSampleSet samples) =>
        Console.WriteLine($"{label}: count={samples.Values.Count}, max={samples.Maximum:F4}, p95={samples.Percentile95:F4}");

    private static IReadOnlyList<GuiOption> WaitForEndpoints(GuiAutomation gui)
    {
        gui.WaitForStatus(
            status => status is "Select an endpoint to load its options."
                or "No active render endpoints found.",
            "The GUI did not finish its initial endpoint load.");
        var options = gui.GetEndpointOptions();
        if (options.Count > 0)
        {
            return options;
        }
        throw new InvalidOperationException($"The GUI exposed no endpoints. Status: {gui.Status}");
    }

    private static void RequireButtonStates(
        GuiAutomation gui, bool apply, bool play, bool stop, string stage)
    {
        if (gui.IsEnabled("ApplyButton") != apply
            || gui.IsEnabled("PlayButton") != play
            || gui.IsEnabled("StopButton") != stop)
        {
            throw new InvalidOperationException($"Unexpected button state during {stage}: "
                + $"Apply={gui.IsEnabled("ApplyButton")}, Play={gui.IsEnabled("PlayButton")}, "
                + $"Stop={gui.IsEnabled("StopButton")}.");
        }
    }

    private static void WaitForButtonStates(
        GuiAutomation gui, bool apply, bool play, bool stop, string stage)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                RequireButtonStates(gui, apply, play, stop, stage);
                return;
            }
            catch (InvalidOperationException)
            {
                Thread.Sleep(100);
            }
        }
        RequireButtonStates(gui, apply, play, stop, stage);
    }

    private static void PrintSwitches(string label, IReadOnlyList<GuiSwitchOption> options)
    {
        Console.WriteLine($"{label}:");
        foreach (var option in options)
        {
            Console.WriteLine($"  {option.Name} [{(option.IsEnabled ? "enabled" : "disabled")}]");
        }
    }

    private static void RequireEnabled(IReadOnlyList<GuiSwitchOption> options, string automationId)
    {
        var option = options.SingleOrDefault(item => item.AutomationId == automationId)
            ?? throw new InvalidOperationException($"'{automationId}' is not exposed by the GUI.");
        if (!option.IsEnabled)
        {
            throw new InvalidOperationException($"'{automationId}' is disabled by the GUI.");
        }
    }

    private static IReadOnlyDictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Options must use --name value pairs.");
            }
            options[args[i][2..]] = args[i + 1];
        }
        return options;
    }

    private static string ResolveExecutable(IReadOnlyDictionary<string, string> options)
    {
        var path = options.TryGetValue("app", out var configured)
            ? configured
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "publish", "AudioDeviceConfigurator.exe"));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Published AudioDeviceConfigurator.exe was not found.", path);
        }
        return Path.GetFullPath(path);
    }

    private static string Require(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"--{name} is required.");

    private static void PrintUsage() => Console.WriteLine(
        """
        AudioDeviceConfigurator.Acceptance inspect [--endpoint-id <id>] [--app <path>]
        AudioDeviceConfigurator.Acceptance run --endpoint-id <id> --channels <2|4|6|8> --sample-rate <hz> --bit-depth <16|20|24|32> [--app <path>]

        inspect never clicks Apply. run requires typing APPLY immediately before any setter can start.
        """);
}
