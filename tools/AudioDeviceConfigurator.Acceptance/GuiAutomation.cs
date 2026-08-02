using System.Diagnostics;
using System.Windows.Automation;

namespace AudioDeviceConfigurator.Acceptance;

internal sealed record GuiOption(string Name, string? HelpText);

internal sealed class GuiAutomation : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(35);
    private readonly Process _process;
    private readonly AutomationElement _window;

    private GuiAutomation(Process process, AutomationElement window)
    {
        _process = process;
        _window = window;
    }

    public static GuiAutomation Launch(string executablePath)
    {
        var process = Process.Start(new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
        }) ?? throw new InvalidOperationException("The GUI process did not start.");

        try
        {
            var window = WaitUntil(() =>
            {
                process.Refresh();
                if (process.HasExited || process.MainWindowHandle == IntPtr.Zero)
                {
                    return null;
                }

                var candidate = AutomationElement.FromHandle(process.MainWindowHandle);
                return candidate.Current.AutomationId == "AudioDeviceConfigurator.MainWindow"
                    ? candidate
                    : null;
            }, DefaultTimeout, "The AudioDeviceConfigurator main window did not appear.");

            return new GuiAutomation(process, window);
        }
        catch
        {
            CloseProcess(process);
            throw;
        }
    }

    public IReadOnlyList<GuiOption> GetEndpointOptions()
    {
        var combo = Find("EndpointCombo");
        Expand(combo);
        try
        {
            return GetListItems(combo)
                .Select(item => new GuiOption(item.Current.Name, item.Current.HelpText))
                .ToArray();
        }
        finally
        {
            Collapse(combo);
        }
    }

    public void SelectEndpoint(string endpointId)
    {
        var combo = Find("EndpointCombo");
        Expand(combo);
        try
        {
            var item = WaitUntil(
                () => GetListItems(combo).FirstOrDefault(candidate =>
                    string.Equals(candidate.Current.HelpText, endpointId, StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(10),
                $"Endpoint '{endpointId}' was not exposed by the GUI.");
            Select(item);
        }
        finally
        {
            Collapse(combo);
        }
    }

    public IReadOnlyList<string> WaitForChannels()
    {
        try
        {
            return WaitUntil(() => ReadComboNames("ChannelCombo"), DefaultTimeout,
                "Speaker-channel options were not loaded.", values => values.Count > 0);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message} Last GUI status: {Status}", ex);
        }
    }

    public IReadOnlyList<string> WaitForFormats()
    {
        try
        {
            return WaitUntil(() => ReadComboNames("FormatCombo"), DefaultTimeout,
                "Default-format options were not loaded.", values => values.Count > 0);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message} Last GUI status: {Status}", ex);
        }
    }

    public void SelectChannel(int channels) => SelectByName("ChannelCombo", channels.ToString());

    public void SelectFormat(string formatText) => SelectByName("FormatCombo", formatText);

    public void Invoke(string automationId)
    {
        var element = Find(automationId);
        if (!element.Current.IsEnabled)
        {
            throw new InvalidOperationException($"'{automationId}' is disabled.");
        }
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    public bool IsEnabled(string automationId) => Find(automationId).Current.IsEnabled;

    public string Status => Find("StatusText").Current.Name;

    public string WaitForStatus(Func<string, bool> predicate, string failureMessage)
    {
        var history = new List<string>();
        var last = string.Empty;
        var deadline = DateTimeOffset.UtcNow + DefaultTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = Status;
            if (!string.Equals(current, last, StringComparison.Ordinal))
            {
                history.Add($"{DateTimeOffset.Now:HH:mm:ss.fff} {current} "
                    + $"[Apply={IsEnabled("ApplyButton")}, Play={IsEnabled("PlayButton")}, Stop={IsEnabled("StopButton")}]");
                last = current;
            }
            if (predicate(current))
            {
                foreach (var item in history)
                {
                    Console.WriteLine($"GUI: {item}");
                }
                return current;
            }
            Thread.Sleep(100);
        }

        throw new TimeoutException($"{failureMessage} Status history: {string.Join(" -> ", history)}");
    }

    public void Close()
    {
        if (_process.HasExited)
        {
            return;
        }

        if (_window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
        {
            ((WindowPattern)pattern).Close();
        }
        else
        {
            _process.CloseMainWindow();
        }

        if (!_process.WaitForExit(5000))
        {
            _process.Kill(true);
            _process.WaitForExit(5000);
        }
    }

    public void Dispose()
    {
        Close();
        _process.Dispose();
    }

    private IReadOnlyList<string> ReadComboNames(string automationId)
    {
        var combo = Find(automationId);
        Expand(combo);
        try
        {
            return GetListItems(combo).Select(item => item.Current.Name).ToArray();
        }
        finally
        {
            Collapse(combo);
        }
    }

    private void SelectByName(string automationId, string name)
    {
        var combo = Find(automationId);
        Expand(combo);
        try
        {
            var items = GetListItems(combo);
            var item = items.FirstOrDefault(candidate =>
                string.Equals(candidate.Current.Name, name, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"'{name}' was not found in {automationId}.");
            Select(item);
        }
        finally
        {
            Collapse(combo);
        }
    }

    private AutomationElement Find(string automationId) =>
        WaitUntil(
            () => _window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, automationId)),
            TimeSpan.FromSeconds(10),
            $"Automation element '{automationId}' was not found.");

    private static IReadOnlyList<AutomationElement> GetListItems(AutomationElement combo) =>
        combo.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>()
            .ToArray();

    private static void Expand(AutomationElement combo)
    {
        if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern))
        {
            ((ExpandCollapsePattern)pattern).Expand();
        }
    }

    private static void Collapse(AutomationElement combo)
    {
        if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern))
        {
            ((ExpandCollapsePattern)pattern).Collapse();
        }
    }

    private static void Select(AutomationElement item) =>
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

    private static T WaitUntil<T>(
        Func<T?> read,
        TimeSpan timeout,
        string failureMessage,
        Func<T, bool>? ready = null) where T : class
    {
        ready ??= value => value is not null;
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var value = read();
                if (value is not null && ready(value))
                {
                    return value;
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
            {
                lastError = ex;
            }
            Thread.Sleep(100);
        }

        throw new TimeoutException(failureMessage, lastError);
    }

    private static string WaitUntil(
        Func<string> read,
        TimeSpan timeout,
        string failureMessage,
        Func<string, bool> ready)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var value = read();
            if (ready(value))
            {
                return value;
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException($"{failureMessage} Last status: {read()}");
    }

    private static IReadOnlyList<string> WaitUntil(
        Func<IReadOnlyList<string>> read,
        TimeSpan timeout,
        string failureMessage,
        Func<IReadOnlyList<string>, bool> ready)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var value = read();
            if (ready(value))
            {
                return value;
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException(failureMessage);
    }

    private static void CloseProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(3000))
                {
                    process.Kill(true);
                }
            }
        }
        finally
        {
            process.Dispose();
        }
    }
}
