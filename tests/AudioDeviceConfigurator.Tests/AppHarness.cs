using System.Text.Json;
using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Application;
using AudioDeviceConfigurator.Cli;
using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Tests.Fakes;
using AudioDeviceConfigurator.Tests.Fixtures;

namespace AudioDeviceConfigurator.Tests;

/// <summary>
/// Drives the full application through its CLI seam with every Windows boundary replaced by a
/// deterministic fake, so tests assert observable behaviour rather than internal structure.
/// </summary>
public sealed class AppHarness
{
    public const string AppDirectory = @"C:\App";
    public const string SvclPath = @"C:\App\svcl.exe";
    public const string ReportsDirectory = @"C:\App\Reports";
    public const string DefaultEndpointId = "{0.0.0.00000000}.{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}";

    public FakeDisplayProvider Displays { get; } = new();
    public FakeEndpointProvider Endpoints { get; } = new();
    public FakeWasapiProbe Wasapi { get; } = new();
    public FakeFileSystem FileSystem { get; } = new();
    public FakeClock Clock { get; } = new();
    public FakeConsole Console { get; } = new();
    public FakeSystemInfoProvider SystemInfo { get; } = new();
    public FakeSvclRunner Svcl { get; }
    public CancellationTokenSource Cancellation { get; } = new();

    public AppHarness()
    {
        Svcl = new FakeSvclRunner(FileSystem, Clock);
        FileSystem.Files[SvclPath] = [0x4D, 0x5A];
        FileSystem.FileVersions[SvclPath] = "1.28.0.0";
    }

    public AppHarness WithSvclVersion(string? version)
    {
        if (version is null)
        {
            FileSystem.FileVersions.Remove(SvclPath);
        }
        else
        {
            FileSystem.FileVersions[SvclPath] = version;
        }

        return this;
    }

    public AppHarness WithoutSvclExecutable()
    {
        FileSystem.Files.Remove(SvclPath);
        return this;
    }

    public AppHarness WithEndpoint(
        string? endpointId = null,
        string name = "Digital Display Audio",
        bool isDefault = true,
        string? svclId = null)
    {
        Endpoints.Endpoints.Add(new EndpointInfo(
            EndpointId: endpointId ?? DefaultEndpointId,
            FriendlyName: name,
            DeviceDescription: "HDMI output",
            SvclCommandLineId: svclId,
            DriverName: "NVIDIA High Definition Audio",
            DriverVersion: "1.4.4.1",
            IsDefault: isDefault,
            ContainerId: "{11111111-2222-3333-4444-555555555555}"));
        return this;
    }

    public AppHarness WithDisplay(byte[] edid, string monitorId = @"\\?\DISPLAY#DEL4321#5&1234#{guid}", string name = "DELL U2723QE")
    {
        Displays.Displays.Add(new DisplayInfo(
            MonitorId: monitorId,
            FriendlyName: name,
            AdapterName: "NVIDIA GeForce RTX 4070",
            GpuDriverVersion: "32.0.15.6094",
            RawEdid: edid));
        return this;
    }

    public AppHarness WithLpcmDisplay(int maxChannels = 8, int[]? rates = null, int[]? depths = null, string monitorId = @"\\?\DISPLAY#DEL4321#5&1234#{guid}", string name = "DELL U2723QE", string? monitorName = "U2723QE")
    {
        var edid = new EdidBuilder()
            .WithMonitorName(monitorName)
            .WithCtaAudioBlock(SadSpec.Lpcm(maxChannels, rates ?? [48000], depths ?? [16]))
            .Build();
        return WithDisplay(edid, monitorId, name);
    }

    public AppHarness WithOriginalFormat(int channels, int sampleRate, int effectiveBits)
    {
        Svcl.CurrentFormat = (channels, sampleRate, effectiveBits);
        return this;
    }

    public ExitCode Run(params string[] args)
    {
        var env = new AppEnvironment(
            Displays, Endpoints, Wasapi, Svcl, FileSystem, Clock, Console, SystemInfo, AppDirectory);
        var runner = new ValidationRunner(env, Cancellation.Token);
        return runner.Run(CliOptions.Parse(args));
    }

    public string ConsoleText => Console.Text;

    public string ErrorText => Console.ErrorText;

    public IEnumerable<string> ReportFiles =>
        FileSystem.Files.Keys.Where(k => k.StartsWith(ReportsDirectory, StringComparison.OrdinalIgnoreCase));

    public string JsonReportPath => ReportFiles.Single(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

    public string CsvReportPath => ReportFiles.Single(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));

    public JsonElement JsonReport => JsonDocument.Parse(FileSystem.ReadText(JsonReportPath)).RootElement;

    public string CsvReport => FileSystem.ReadText(CsvReportPath);

    public string[] CsvRows => CsvReport
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.TrimEnd('\r'))
        .ToArray();
}
