using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Windows;

namespace AudioDeviceConfigurator.Tests;

public sealed class ControlPanelWindowCleanupTests
{
    [Fact]
    public void Timeout_closes_created_properties_then_sound_but_leaves_reused_windows()
    {
        var api = new FakeWin32();
        var result = ControlPanelFormatProvider.CleanupCreatedWindows(api, (IntPtr)33, "Reused", (IntPtr)22, "Created", TimeSpan.FromMilliseconds(10));
        Assert.True(result);
        Assert.Equal(new[] { (IntPtr)22 }, api.Messages);
        Assert.DoesNotContain((IntPtr)33, api.Messages);
    }

    private sealed class FakeWin32 : IWin32ControlApi
    {
        public List<IntPtr> Messages { get; } = new();
        public bool IsWindowUnicode(IntPtr handle) => true;
        public bool IsWindow(IntPtr handle) => false;
        public bool TrySendMessageTimeout(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam, TimeSpan timeout, out IntPtr result) { Messages.Add(handle); result = IntPtr.Zero; return true; }
        public bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam) => true;
        public bool IsWindowVisible(IntPtr handle) => true;
        public string GetWindowClassName(IntPtr handle) => string.Empty;
        public string GetWindowText(IntPtr handle) => string.Empty;
        public IReadOnlyList<IntPtr> EnumerateChildWindows(IntPtr parent) => Array.Empty<IntPtr>();
        public IReadOnlyList<Win32WindowInfo> EnumerateTopLevelWindows() => Array.Empty<Win32WindowInfo>();
        public bool TrySelectAdvancedTab(IntPtr dialog, TimeSpan timeout, out string observed) { observed = string.Empty; return false; }
        public bool TryReadComboBoxItems(IntPtr handle, TimeSpan timeout, out IReadOnlyList<string> items, out string diagnostic) { items = Array.Empty<string>(); diagnostic = string.Empty; return false; }        public bool TryReadFormatItemsAcrossTabs(IntPtr dialog, TimeSpan timeout, out Win32FormatReadResult result, out string observed) { result = new(-1, -1, Array.Empty<string>()); observed = string.Empty; return false; }
    }
}
