using System.Diagnostics;
using AudioDeviceConfigurator.Windows;

namespace AudioDeviceConfigurator.Tests;

public sealed class Win32ControlApiTests
{
    [Fact]
    public void Invalid_handle_fails_without_blocking()
    {
        var api = new Win32ControlApi();
        var started = Stopwatch.GetTimestamp();
        var ok = api.TrySendMessageTimeout(IntPtr.Zero, 0x0146, IntPtr.Zero, IntPtr.Zero,
            TimeSpan.FromMilliseconds(25), out _);

        Assert.False(ok);
        Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Advanced_tab_selection_returns_within_timeout_for_invalid_parent()
    {
        var started = Stopwatch.GetTimestamp();
        var ok = new Win32ControlApi().TrySelectAdvancedTab(IntPtr.Zero, TimeSpan.FromMilliseconds(25), out var observed);

        Assert.False(ok);
        Assert.Empty(observed);
        Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Child_enumeration_returns_without_blocking_for_invalid_parent()
    {
        var started = Stopwatch.GetTimestamp();
        _ = new Win32ControlApi().EnumerateChildWindows(IntPtr.Zero);
        Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }
}
