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
    public async Task GUI_endpoint_selection_loads_options_from_real_control_panel()
    {
        if (!CanRunLive() || !IsSelfContained())
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
        var optionsResult = await service.GetOptionsAsync(defaultEndpoint!, CancellationToken.None);
        Assert.NotNull(optionsResult);
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