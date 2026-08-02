using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Application;
using AudioDeviceConfigurator.Cli;
using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Tests.Fakes;

namespace AudioDeviceConfigurator.Tests;

/// <summary>Drives the read-only Control Panel workflow through the CLI seam.</summary>
public sealed class AppHarness
{
    public const string AppDirectory = @"C:\App";
    public const string DefaultEndpointId = "{0.0.0.00000000}.{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}";

    public FakeEndpointProvider Endpoints { get; } = new();
    public FakeFileSystem FileSystem { get; } = new();
    public FakeClock Clock { get; } = new();
    public FakeConsole Console { get; } = new();
    public FakeControlPanelFormatProvider ControlPanelFormats { get; } = new();
    public FakeSvclClient Svcl { get; } = new();
    public CancellationTokenSource Cancellation { get; } = new();

    public AppHarness WithEndpoint(
        string? endpointId = null,
        string name = "Digital Display Audio",
        bool isDefault = true)
    {
        Endpoints.Endpoints.Add(new EndpointInfo(
            EndpointId: endpointId ?? DefaultEndpointId,
            FriendlyName: name,
            DeviceDescription: "HDMI output",
            DriverName: "NVIDIA High Definition Audio",
            DriverVersion: null,
            IsDefault: isDefault));
        return this;
    }

    public ExitCode Run(params string[] args)
    {
        var env = new AppEnvironment(
            Endpoints, FileSystem, Clock, Console, ControlPanelFormats, Svcl);
        return new ValidationRunner(env, Cancellation.Token).Run(CliOptions.Parse(args));
    }

    public string ConsoleText => Console.Text;
    public string ErrorText => Console.ErrorText;
}
