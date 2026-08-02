using System.Text.RegularExpressions;
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
        var formats = gui.WaitForFormats();
        Console.WriteLine();
        Console.WriteLine($"Selected endpoint: {selected.Name}");
        Console.WriteLine($"Channels: {string.Join(", ", channels)}");
        Console.WriteLine("Formats:");
        foreach (var format in formats)
        {
            Console.WriteLine($"  {format}");
        }

        return AcceptanceOutcome.Pass;
    }

    private static AcceptanceOutcome Run(string executable, IReadOnlyDictionary<string, string> options)
    {
        var endpointId = Require(options, "endpoint-id");
        var formatText = Require(options, "format-text");
        if (!int.TryParse(Require(options, "channels"), out var channels)
            || channels is not (2 or 4 or 6 or 8))
        {
            throw new ArgumentException("--channels must be 2, 4, 6, or 8.");
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
        var formats = gui.WaitForFormats();
        if (!availableChannels.Contains(channels.ToString(), StringComparer.Ordinal)
            || !formats.Contains(formatText, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The requested channel or format is not exposed by the GUI.");
        }

        Console.WriteLine("The acceptance run will permanently keep a successful setting:");
        Console.WriteLine($"  Endpoint : {endpoint.Name}");
        Console.WriteLine($"  ID       : {endpointId}");
        Console.WriteLine($"  Channels : {channels}");
        Console.WriteLine($"  Format   : {formatText}");
        Console.WriteLine($"  Original : {before}");
        Console.Write("Type APPLY to continue: ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "APPLY", StringComparison.Ordinal))
        {
            return AcceptanceOutcome.Cancelled;
        }

        gui.SelectChannel(channels);
        gui.SelectFormat(formatText);
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

            var requested = ParseFormat(formatText);
            var after = svcl.SaveDeviceFormat(endpointId);
            var expectedMask = SvclClient.GetSpeakerMask(channels);
            if (after.Channels != channels
                || after.EffectiveBits != requested.EffectiveBits
                || after.SampleRate != requested.SampleRate
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

    private static (int EffectiveBits, int SampleRate) ParseFormat(string text)
    {
        var numbers = Regex.Matches(text, @"\d+")
            .Select(match => int.Parse(match.Value))
            .ToArray();
        var bits = numbers.FirstOrDefault(value => value is 16 or 20 or 24 or 32);
        var rate = numbers.FirstOrDefault(value => value is >= 8000 and <= 384000 && value != bits);
        if (bits == 0 || rate == 0)
        {
            throw new InvalidOperationException("The selected GUI format text could not be parsed for readback verification.");
        }
        return (bits, rate);
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
        AudioDeviceConfigurator.Acceptance run --endpoint-id <id> --channels <2|4|6|8> --format-text <text> [--app <path>]

        inspect never clicks Apply. run requires typing APPLY immediately before any setter can start.
        """);
}
