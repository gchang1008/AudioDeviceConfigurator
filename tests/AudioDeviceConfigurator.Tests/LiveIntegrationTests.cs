using AudioDeviceConfigurator.Application;
using AudioDeviceConfigurator.Svcl;
using AudioDeviceConfigurator.Windows;

namespace AudioDeviceConfigurator.Tests;

/// <summary>
/// Live integration tests that touch real Windows subsystems. Tests skip themselves (return early)
/// when the host cannot satisfy the prerequisites (e.g. non-interactive CI).
/// </summary>
public sealed class LiveIntegrationTests
{
    [Fact]
    public async Task ListEndpoints_returns_active_render_endpoints_from_real_core_audio()
    {
        if (!CanRunLive())
        {
            return;
        }

        var provider = new CoreAudioEndpointProvider();
        var service = new DeviceConfigurationService(
            provider,
            new ControlPanelFormatProvider(),
            new SvclClient(new SystemProcessRunner(), new SystemFileSystem(), "svcl.exe"));

        var endpoints = await service.ListEndpointsAsync(CancellationToken.None);

        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, item => Assert.False(string.IsNullOrEmpty(item.EndpointId)));
        Assert.Contains(endpoints, item => item.IsDefault);
    }

    [Fact]
    public async Task GetOptions_returns_options_from_real_control_panel_for_default_endpoint()
    {
        if (!CanRunLive())
        {
            return;
        }

        // The Control Panel worker spawns Environment.ProcessPath as a separate process; xUnit's
        // testhost is framework-dependent, so the worker launch fails outside a self-contained
        // build. Skip this test in this configuration and rely on the in-process GUI smoke run
        // plus the publish-time manual verification to cover this path.
        if (!IsSelfContained())
        {
            return;
        }

        var provider = new CoreAudioEndpointProvider();
        var defaultEndpoint = provider.GetDefaultRenderEndpoint();
        Assert.NotNull(defaultEndpoint);

        var service = new DeviceConfigurationService(
            provider,
            new ControlPanelFormatProvider(),
            new SvclClient(new SystemProcessRunner(), new SystemFileSystem(), "svcl.exe"));

        var result = await service.GetOptionsAsync(defaultEndpoint!, CancellationToken.None);

        Assert.NotNull(result);
        var available = Assert.IsType<EndpointOptionsResult.Available>(result);
        Assert.NotEmpty(available.Options.Channels);
        Assert.NotEmpty(available.Options.Formats);
    }

    private static bool IsSelfContained()
    {
        var processPath = Environment.ProcessPath;
        return processPath is not null && File.Exists(Path.Combine(Path.GetDirectoryName(processPath)!, "AudioDeviceConfigurator.runtimeconfig.json"))
            ? File.ReadAllText(Path.Combine(Path.GetDirectoryName(processPath)!, "AudioDeviceConfigurator.runtimeconfig.json"))
                .Contains("\"SelfContained\": true", StringComparison.OrdinalIgnoreCase)
            : false;
    }

    private static bool CanRunLive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return Environment.UserInteractive
                && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SESSIONNAME"));
        }
        catch
        {
            return false;
        }
    }
}