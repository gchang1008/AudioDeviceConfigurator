using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Domain;

namespace AudioDeviceConfigurator.Tests;

public sealed class ReadOnlyWorkflowTests
{
    [Fact]
    public void Uses_default_endpoint_without_displays()
    {
        var harness = ConfiguredHarness()
            .WithEndpoint(endpointId: "other", name: "Other", isDefault: false)
            .WithEndpoint(endpointId: "default", name: "Default", isDefault: true);

        harness.Console.EnqueueInput("C");
        Assert.Equal(ExitCode.Cancelled, harness.Run());
        Assert.Equal("default", harness.ControlPanelFormats.LastEndpoint?.EndpointId);
    }

    [Fact]
    public void Device_id_selects_specific_endpoint()
    {
        var harness = ConfiguredHarness()
            .WithEndpoint(endpointId: "default", isDefault: true)
            .WithEndpoint(endpointId: "requested", name: "Requested", isDefault: false);

        harness.Console.EnqueueInput("C");
        Assert.Equal(ExitCode.Cancelled, harness.Run("--device-id", "requested"));
        Assert.Equal("requested", harness.ControlPanelFormats.LastEndpoint?.EndpointId);
    }

    [Fact]
    public void Monitor_id_is_rejected_as_unknown()
    {
        var harness = ConfiguredHarness().WithEndpoint();

        Assert.Equal(ExitCode.SystemError, harness.Run("--monitor-id", "monitor"));
        Assert.Contains("Unknown argument '--monitor-id'", harness.ErrorText);
    }

    [Fact]
    public void List_outputs_only_render_endpoints()
    {
        var harness = ConfiguredHarness().WithEndpoint();

        Assert.Equal(ExitCode.Pass, harness.Run("--list"));
        Assert.Contains("Active render endpoints:", harness.ConsoleText);
        Assert.DoesNotContain("Active displays:", harness.ConsoleText);
    }

    [Fact]
    public void Help_describes_read_only_endpoint_flow()
    {
        var harness = ConfiguredHarness();

        Assert.Equal(ExitCode.Pass, harness.Run("--help"));
        Assert.Contains("--device-id", harness.ConsoleText);
        Assert.DoesNotContain("--monitor-id", harness.ConsoleText);
        Assert.DoesNotContain("EDID", harness.ConsoleText);
    }

    [Fact]
    public void Preserves_control_panel_items_and_does_not_query_other_capability_sources()
    {
        var harness = ConfiguredHarness().WithEndpoint();

        harness.Console.EnqueueInput("C");
        Assert.Equal(ExitCode.Cancelled, harness.Run());
        Assert.Equal(2, harness.ControlPanelFormats.Items.Count);
        Assert.Equal(3, harness.ControlPanelFormats.SpeakerConfigurations.Count);
        Assert.Equal("first raw", harness.ControlPanelFormats.Items[0].DisplayText);
        Assert.Contains("Supported speaker channels=2, 4, 6", harness.ConsoleText);
        Assert.Contains("Max supported channels=6", harness.ConsoleText);
    }

    [Fact]
    public void Empty_control_panel_list_returns_not_applicable()
    {
        var harness = new AppHarness().WithEndpoint();

        Assert.Equal(ExitCode.NotApplicable, harness.Run());
    }

    [Fact]
    public void Unknown_device_id_returns_system_error()
    {
        var harness = ConfiguredHarness().WithEndpoint();

        Assert.Equal(ExitCode.SystemError, harness.Run("--device-id", "missing"));
        Assert.Contains("No active render endpoint matches", harness.ErrorText);
    }

    private static AppHarness ConfiguredHarness()
    {
        var harness = new AppHarness();
        harness.ControlPanelFormats.Items.AddRange([
            new(0, "first raw", 2, 32000, 16, 16, ControlPanelParseStatus.Parsed, null),
            new(1, "second raw", 2, 48000, 16, 16, ControlPanelParseStatus.Parsed, null),
        ]);
        harness.ControlPanelFormats.SpeakerConfigurations.AddRange([
            new(0, "立體聲", 2),
            new(1, "四聲道", 4),
            new(2, "5.1 環場音效", 6),
        ]);
        return harness;
    }
}
