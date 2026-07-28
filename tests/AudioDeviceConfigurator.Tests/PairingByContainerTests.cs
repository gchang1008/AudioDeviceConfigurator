using AudioDeviceConfigurator.Domain;

namespace AudioDeviceConfigurator.Tests;

/// <summary>
/// Pairing must use the endpoint's own container, not merely the number of active displays.
/// A display count of one is not evidence that the display belongs to the selected endpoint.
/// </summary>
public class PairingByContainerTests
{
    private const string HdmiContainer = "{3EEC0E47-18D4-5403-803C-2D7DBFC7C5CD}";
    private const string OtherContainer = "{8CC9B6BC-C46B-57C7-9704-BB561FD18B65}";

    [Fact]
    public void Pairs_the_endpoint_with_the_display_sharing_its_container()
    {
        var h = new AppHarness()
            .WithEndpoint(name: "VX229 (NVIDIA)", containerId: HdmiContainer)
            .WithLpcmDisplay(maxChannels: 2, depths: [16],
                monitorId: "mon-other", name: "VG27AQL1A", containerId: OtherContainer)
            .WithLpcmDisplay(maxChannels: 8, depths: [16],
                monitorId: "mon-vx229", name: "VX229", containerId: HdmiContainer);

        var exit = h.Run();

        Assert.Equal(ExitCode.Pass, exit);
        Assert.Equal("mon-vx229", h.JsonReport.GetProperty("Monitor").GetProperty("MonitorId").GetString());
        Assert.DoesNotContain("Select the monitor", h.ConsoleText);
    }

    [Fact]
    public void Does_not_silently_test_the_only_display_when_it_is_not_the_endpoints_own()
    {
        // A USB headset endpoint with exactly one active display attached to a different container.
        var h = new AppHarness()
            .WithEndpoint(name: "Speakers (USB Headset)", containerId: OtherContainer)
            .WithLpcmDisplay(maxChannels: 2, depths: [16],
                monitorId: "mon-vx229", name: "VX229", containerId: HdmiContainer);
        h.Console.EnqueueInput("C");

        var exit = h.Run();

        Assert.Equal(ExitCode.Cancelled, exit);
        Assert.Contains("Select the monitor", h.ConsoleText);
        Assert.Empty(h.Svcl.SetCommands);
    }

    [Fact]
    public void Prompts_when_the_endpoint_container_matches_more_than_one_display()
    {
        var h = new AppHarness()
            .WithEndpoint(containerId: HdmiContainer)
            .WithLpcmDisplay(maxChannels: 2, depths: [16],
                monitorId: "mon-a", name: "Monitor A", containerId: HdmiContainer)
            .WithLpcmDisplay(maxChannels: 8, depths: [16],
                monitorId: "mon-b", name: "Monitor B", containerId: HdmiContainer);
        h.Console.EnqueueInput("2");

        var exit = h.Run();

        Assert.Equal(ExitCode.Pass, exit);
        Assert.Contains("Select the monitor", h.ConsoleText);
        Assert.Equal("mon-b", h.JsonReport.GetProperty("Monitor").GetProperty("MonitorId").GetString());
    }

    [Fact]
    public void Falls_back_to_a_prompt_when_the_endpoint_has_no_container()
    {
        var h = new AppHarness()
            .WithEndpoint(containerId: null)
            .WithLpcmDisplay(maxChannels: 2, depths: [16],
                monitorId: "mon-a", name: "Monitor A", containerId: HdmiContainer);
        h.Console.EnqueueInput("1");

        var exit = h.Run();

        Assert.Equal(ExitCode.Pass, exit);
        Assert.Contains("Select the monitor", h.ConsoleText);
    }

    [Fact]
    public void Treats_the_null_container_sentinel_as_no_container()
    {
        // Windows reports {00000000-0000-0000-FFFF-FFFFFFFFFFFF} for devices with no real container.
        var h = new AppHarness()
            .WithEndpoint(containerId: "{00000000-0000-0000-FFFF-FFFFFFFFFFFF}")
            .WithLpcmDisplay(maxChannels: 2, depths: [16],
                monitorId: "mon-a", name: "Monitor A", containerId: "{00000000-0000-0000-FFFF-FFFFFFFFFFFF}");
        h.Console.EnqueueInput("1");

        var exit = h.Run();

        // The sentinel must never be treated as a match, even when both sides carry it.
        Assert.Equal(ExitCode.Pass, exit);
        Assert.Contains("Select the monitor", h.ConsoleText);
    }

    [Fact]
    public void Compares_containers_case_insensitively()
    {
        var h = new AppHarness()
            .WithEndpoint(containerId: HdmiContainer.ToLowerInvariant())
            .WithLpcmDisplay(maxChannels: 2, depths: [16],
                monitorId: "mon-a", name: "Monitor A", containerId: HdmiContainer.ToUpperInvariant())
            .WithLpcmDisplay(maxChannels: 2, depths: [16],
                monitorId: "mon-b", name: "Monitor B", containerId: OtherContainer);

        var exit = h.Run();

        Assert.Equal(ExitCode.Pass, exit);
        Assert.Equal("mon-a", h.JsonReport.GetProperty("Monitor").GetProperty("MonitorId").GetString());
        Assert.DoesNotContain("Select the monitor", h.ConsoleText);
    }

    [Fact]
    public void An_explicit_monitor_id_still_overrides_container_pairing()
    {
        var h = new AppHarness()
            .WithEndpoint(containerId: HdmiContainer)
            .WithLpcmDisplay(maxChannels: 2, depths: [16],
                monitorId: "mon-own", name: "Own", containerId: HdmiContainer)
            .WithLpcmDisplay(maxChannels: 8, depths: [16],
                monitorId: "mon-forced", name: "Forced", containerId: OtherContainer);

        var exit = h.Run("--monitor-id", "mon-forced");

        Assert.Equal(ExitCode.Pass, exit);
        Assert.Equal("mon-forced", h.JsonReport.GetProperty("Monitor").GetProperty("MonitorId").GetString());
        Assert.DoesNotContain("Select the monitor", h.ConsoleText);
    }

    [Fact]
    public void Names_the_paired_display_and_its_container_in_the_report()
    {
        var h = new AppHarness()
            .WithEndpoint(containerId: HdmiContainer)
            .WithLpcmDisplay(maxChannels: 2, depths: [16],
                monitorId: "mon-a", name: "Monitor A", containerId: HdmiContainer);

        h.Run();

        var monitor = h.JsonReport.GetProperty("Monitor");
        Assert.Equal(HdmiContainer, monitor.GetProperty("ContainerId").GetString());
        Assert.Equal("Container", monitor.GetProperty("PairingMethod").GetString());
    }

    [Fact]
    public void Records_an_interactive_pairing_as_such()
    {
        var h = new AppHarness()
            .WithEndpoint(containerId: null)
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: "mon-a", name: "A", containerId: null);
        h.Console.EnqueueInput("1");

        h.Run();

        Assert.Equal("Interactive", h.JsonReport.GetProperty("Monitor").GetProperty("PairingMethod").GetString());
    }

    [Fact]
    public void Records_an_explicitly_supplied_monitor_as_such()
    {
        var h = new AppHarness()
            .WithEndpoint(containerId: HdmiContainer)
            .WithLpcmDisplay(maxChannels: 2, depths: [16], monitorId: "mon-a", name: "A", containerId: HdmiContainer);

        h.Run("--monitor-id", "mon-a");

        Assert.Equal("Explicit", h.JsonReport.GetProperty("Monitor").GetProperty("PairingMethod").GetString());
    }
}
