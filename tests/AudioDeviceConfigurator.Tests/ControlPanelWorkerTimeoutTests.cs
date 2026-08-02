using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Windows;

namespace AudioDeviceConfigurator.Tests;

public sealed class ControlPanelWorkerTimeoutTests
{
    [Fact]
    public void Parent_timeout_terminates_worker_without_leaving_background_thread()
    {
        var endpoint = new EndpointInfo("id", "name", "description", null, null, true);
        var provider = new ControlPanelFormatProvider();
        var ex = Assert.ThrowsAny<Exception>(() => provider.ReadDefaultFormats(endpoint, TimeSpan.FromMilliseconds(1)));
        Assert.Contains("worker", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, false, true, true, true)]
    [InlineData(false, true, false, false, true)]
    public void Parent_kill_policy_requires_the_correct_timeout_phase(bool ready, bool startupExpired, bool operationExpired, bool cleanupExpired, bool expected)
    {
        Assert.Equal(expected, ControlPanelFormatProvider.WorkerTimeoutPolicy.CanKill(ready, startupExpired, operationExpired, cleanupExpired));
    }
}
