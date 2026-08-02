using System.Diagnostics;
using System.Text;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Domain;

namespace AudioDeviceConfigurator.Windows;

/// <summary>
/// Reads the endpoint's Advanced Default Format choices through the visible Sound Control Panel.
/// The dialog is read-only: no selection is changed and the process started here is always closed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ControlPanelFormatProvider : IControlPanelFormatProvider
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupGrace = TimeSpan.FromSeconds(2);
    private readonly IWin32ControlApi _win32;
    internal static class WorkerTimeoutPolicy
    {
        public static bool CanKill(bool ready, bool startupExpired, bool operationExpired, bool cleanupExpired) =>
            ready ? operationExpired && cleanupExpired : startupExpired;
    }

    private sealed class WorkerState
    {
        private readonly object _gate = new();
        public string Stage { get; private set; } = "launch";
        public IntPtr SoundHandle { get; private set; }
        public string SoundOwnership { get; private set; } = "Unknown";
        public IntPtr PropertiesHandle { get; private set; }
        public string PropertiesOwnership { get; private set; } = "Unknown";
        public DateTimeOffset? LastHeartbeat { get; private set; }
        public string Diagnostic { get; private set; } = string.Empty;
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly StringBuilder Error = new();
        public void Update(string stage, IntPtr sound, string soundOwnership, IntPtr properties, string propertiesOwnership, DateTimeOffset? heartbeat) { lock (_gate) { Stage = stage; if (sound != IntPtr.Zero) SoundHandle = sound; SoundOwnership = soundOwnership; if (properties != IntPtr.Zero) PropertiesHandle = properties; PropertiesOwnership = propertiesOwnership; LastHeartbeat = heartbeat; if (stage == "worker-ready") Ready.TrySetResult(); } }
        public void SetDiagnostic(string value) { lock (_gate) Diagnostic = value; }
        public (string Stage, IntPtr Sound, string SoundOwnership, IntPtr Properties, string PropertiesOwnership, string Diagnostic) Snapshot() { lock (_gate) return (Stage, SoundHandle, SoundOwnership, PropertiesHandle, PropertiesOwnership, Diagnostic); }
    }

    private static readonly Regex Numbers = new(@"(?<!\d)(\d+)(?!\d)", RegexOptions.Compiled);

    public ControlPanelFormatProvider(IWin32ControlApi? win32 = null)
    {
        _win32 = win32 ?? new Win32ControlApi();
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr extraData);

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr extraData);

    private const uint WmClose = 0x0010;

    private static List<AutomationElement> WalkDirectChildren(AutomationElement root, long deadline)
    {
        var walker = TreeWalker.RawViewWalker;
        var result = new List<AutomationElement>();
        var pending = new Stack<AutomationElement>();
        pending.Push(root);
        while (pending.Count > 0 && Stopwatch.GetTimestamp() < deadline && result.Count < 512)
        {
            var parent = pending.Pop();
            AutomationElement? child;
            try { child = walker.GetFirstChild(parent); }
            catch (ElementNotAvailableException) { continue; }
            while (child is not null && Stopwatch.GetTimestamp() < deadline && result.Count < 512)
            {
                result.Add(child);
                pending.Push(child);
                try { child = walker.GetNextSibling(child); }
                catch (ElementNotAvailableException) { child = null; }
            }
        }

        return result;
    }

    private static HashSet<IntPtr> TopLevelWindows()
    {
        var handles = new HashSet<IntPtr>();
        EnumWindows((handle, _) =>
        {
            handles.Add(handle);
            return true;
        }, IntPtr.Zero);
        return handles;
    }

    public ControlPanelFormatResult ReadDefaultFormats(EndpointInfo endpoint, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(endpoint));
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Unable to locate worker process.");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (processPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(processPath);
            startInfo.FileName = "dotnet";
        }
        startInfo.ArgumentList.Add("--control-panel-worker");
        startInfo.ArgumentList.Add(payload);

        using var worker = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start Control Panel worker process.");
        var state = new WorkerState();
        var output = worker.StandardOutput.ReadToEndAsync();
        var error = ReadWorkerErrorAsync(worker.StandardError, state);
        var startupDeadline = Stopwatch.GetTimestamp() + (long)(StartupTimeout.TotalSeconds * Stopwatch.Frequency);
        while (!state.Ready.Task.IsCompleted && !worker.HasExited && Stopwatch.GetTimestamp() < startupDeadline) Thread.Sleep(10);
        if (!state.Ready.Task.IsCompleted && WorkerTimeoutPolicy.CanKill(false, startupExpired: true, operationExpired: false, cleanupExpired: false))
        {
            var snapshot = state.Snapshot();
            CleanupCreatedWindows(_win32, snapshot.Sound, snapshot.SoundOwnership, snapshot.Properties, snapshot.PropertiesOwnership, TimeSpan.FromMilliseconds(500));
            try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); worker.WaitForExit(1000); } catch { }
            if (worker.HasExited)
            {
                var startupError = error.GetAwaiter().GetResult().Trim();
                throw new InvalidOperationException($"Control Panel worker exited during startup at stage '{snapshot.Stage}': {startupError}");
            }
            throw new TimeoutException($"Timed out starting Control Panel worker at stage '{snapshot.Stage}'.");
        }
        var workerCompletionTimeout = timeout + CleanupGrace + TimeSpan.FromSeconds(1);
        var operationExpired = !worker.WaitForExit((int)Math.Clamp(workerCompletionTimeout.TotalMilliseconds, 1, int.MaxValue));
        var cleanupExpired = operationExpired && !worker.WaitForExit((int)CleanupGrace.TotalMilliseconds);
        if (WorkerTimeoutPolicy.CanKill(true, startupExpired: false, operationExpired, cleanupExpired))
        {
            var snapshot = state.Snapshot();
            CleanupCreatedWindows(_win32, snapshot.Sound, snapshot.SoundOwnership, snapshot.Properties, snapshot.PropertiesOwnership, TimeSpan.FromMilliseconds(500));
            try { worker.Kill(entireProcessTree: true); worker.WaitForExit(1000); } catch { }
            throw new TimeoutException($"Timed out reading Control Panel formats at heartbeat stage '{snapshot.Stage}' (operation stage '{snapshot.Stage}'); cleanup grace expired. Diagnostic: {snapshot.Diagnostic}");
        }

        var json = output.GetAwaiter().GetResult();
        var errorText = error.GetAwaiter().GetResult();
        if (worker.ExitCode != 0)
        {
            throw new InvalidOperationException($"Control Panel worker failed: {errorText.Trim()}");
        }
        return JsonSerializer.Deserialize<ControlPanelFormatResult>(json)
            ?? throw new InvalidOperationException("Control Panel worker returned no result.");
    }

    private static async Task<string> ReadWorkerErrorAsync(StreamReader reader, WorkerState state)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("HEARTBEAT ", StringComparison.Ordinal))
            {
                var values = line[10..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
                static IntPtr Handle(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var value) && long.TryParse(value, out var h) ? new IntPtr(h) : IntPtr.Zero;
                static DateTimeOffset? Timestamp(IReadOnlyDictionary<string, string> values) => values.TryGetValue("timestamp", out var value) && DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : null;
                state.Update(values.GetValueOrDefault("stage", "unknown"), Handle(values, "sound"), values.GetValueOrDefault("soundOwnership", "Unknown"), Handle(values, "properties"), values.GetValueOrDefault("propertiesOwnership", "Unknown"), Timestamp(values));
            }
            else if (line.StartsWith("DIAG ", StringComparison.Ordinal))
            {
                state.SetDiagnostic(line[5..]);
                lock (state.Error) state.Error.AppendLine(line);
            }
            else lock (state.Error) state.Error.AppendLine(line);
        }
        return state.Error.ToString();
    }

    internal static bool CleanupCreatedWindows(IWin32ControlApi api, IntPtr sound, string soundOwnership, IntPtr properties, string propertiesOwnership, TimeSpan timeout)
    {
        var success = true;
        foreach (var (handle, ownership) in new[] { (properties, propertiesOwnership), (sound, soundOwnership) })
        {
            if (ownership != "Created" || handle == IntPtr.Zero) continue;
            api.TrySendMessageTimeout(handle, WmClose, IntPtr.Zero, IntPtr.Zero, timeout, out _);
            var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            while (api.IsWindow(handle) && Stopwatch.GetTimestamp() < deadline) Thread.Sleep(25);
            success &= !api.IsWindow(handle);
        }
        return success;
    }

    public static int RunWorker(string encodedEndpoint)
    {
        var endpoint = JsonSerializer.Deserialize<EndpointInfo>(Convert.FromBase64String(encodedEndpoint))
            ?? throw new InvalidOperationException("Invalid Control Panel worker endpoint.");
        var provider = new ControlPanelFormatProvider();
        EmitWorkerHeartbeat("worker-ready", IntPtr.Zero, "Unknown", IntPtr.Zero, "Unknown");
        var result = provider.ReadDefaultFormatsInProcess(endpoint, DefaultTimeout);
        Console.Write(JsonSerializer.Serialize(result));
        return 0;
    }

    private ControlPanelFormatResult ReadDefaultFormatsInProcess(EndpointInfo endpoint, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var startedAt = DateTimeOffset.Now;
        var started = Stopwatch.GetTimestamp();
        Exception? failure = null;
        ControlPanelFormatResult? result = null;
        var operationStage = "launch";
        var cleanupStage = "not-started";
        using var finished = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                result = ReadOnSta(endpoint, timeout, started, startedAt,
                    value => operationStage = value,
                    value => cleanupStage = value);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                finished.Set();
            }
        })
        {
            IsBackground = true,
            Name = "ControlPanelFormatProvider",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!finished.Wait(timeout) && !finished.Wait(CleanupGrace))
        {
            throw new TimeoutException($"Timed out reading Control Panel formats at operation stage '{operationStage}'; cleanup stage '{cleanupStage}' after {timeout.TotalSeconds:0.#} seconds; cleanup grace expired.");
        }

        if (failure is not null)
        {
            throw new InvalidOperationException($"[stage={operationStage}] {failure.Message}; cleanup={cleanupStage}.", failure);
        }

        return result ?? throw new InvalidOperationException("The Control Panel read returned no result.");
    }

    private static void EmitWorkerHeartbeat(string stage, IntPtr sound, string soundOwnership, IntPtr properties, string propertiesOwnership)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        Console.Error.WriteLine($"HEARTBEAT stage={stage} timestamp={timestamp} sound={sound.ToInt64()} soundOwnership={soundOwnership} properties={properties.ToInt64()} propertiesOwnership={propertiesOwnership}");
    }

    private static void EmitWindowDiagnostic(IReadOnlyList<Win32WindowInfo> windows)
    {
        var visible = windows.Where(window => window.IsVisible).Take(30)
            .Select(window => $"HWND={window.Handle.ToInt64()},class={window.ClassName},title={window.Title}");
        Console.Error.WriteLine($"DIAG sound-scan windows={windows.Count} visible={string.Join(" || ", visible)}");
    }

    private List<IntPtr> FindExactExistingProperties(EndpointInfo endpoint)
    {
        var description = NormalizeUiName(endpoint.DeviceDescription);
        return _win32.EnumerateTopLevelWindows()
            .Where(window => window.IsVisible
                && string.Equals(window.ClassName, "#32770", StringComparison.Ordinal)
                && NormalizeUiName(window.Title).StartsWith(description, StringComparison.OrdinalIgnoreCase)
                && _win32.EnumerateChildWindows(window.Handle).Any(child => string.Equals(_win32.GetWindowClassName(child), "SysTabControl32", StringComparison.OrdinalIgnoreCase)))
            .Select(window => window.Handle)
            .ToList();
    }

    private ControlPanelFormatResult ReadExistingProperties(EndpointInfo endpoint, IntPtr dialog, TimeSpan timeout, DateTimeOffset startedAt)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        EmitWorkerHeartbeat("properties-dialog", IntPtr.Zero, "Unknown", dialog, "Reused");
        if (!_win32.TryReadFormatItemsAcrossTabs(dialog, Remaining(deadline), out var read, out var observed))
            throw new InvalidOperationException($"[format-tab] Existing target Properties had no valid format tab; observed={observed}");
        EmitWorkerHeartbeat($"advanced-tab-{read.TabIndex}", IntPtr.Zero, "Unknown", dialog, "Reused");
        return new ControlPanelFormatResult(read.Items.Select((text, index) => ParseFormatItem(index, text)).ToList(),
            Array.Empty<ControlPanelSpeakerConfigurationItem>(), null,
            new ControlPanelFormatSnapshot(startedAt, DateTimeOffset.Now, "Win32 existing Properties", false, true, null));
    }

    private ControlPanelFormatResult ReadOnSta(
        EndpointInfo endpoint,
        TimeSpan timeout,
        long started,
        DateTimeOffset startedAt,
        Action<string> setOperationStage,
        Action<string> setCleanupStage)
    {
        var beforeWindows = TopLevelWindows();
        var existingProperties = FindExactExistingProperties(endpoint);
        if (existingProperties.Count > 1)
        {
            throw new InvalidOperationException($"[properties-dialog] Multiple existing target Properties dialogs: {string.Join(",", existingProperties)}");
        }
        if (existingProperties.Count == 1)
        {
            return ReadExistingProperties(endpoint, existingProperties[0], timeout, startedAt);
        }

        setOperationStage("launch");
        var startInfo = new ProcessStartInfo
        {
            FileName = "control.exe",
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add("mmsys.cpl,,0");
        using var controlPanel = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to open the Sound Control Panel.");

        var cleanupAttempted = false;
        var cleanupSucceeded = false;
        var windowOwnership = "Unknown";
        var propertyOwnership = "Unknown";
        IntPtr propertyHandle = IntPtr.Zero;
        IntPtr soundHandle = IntPtr.Zero;
        string? cleanupFailure = null;
        IReadOnlyList<ControlPanelFormatItem>? items = null;
        IReadOnlyList<ControlPanelSpeakerConfigurationItem> speakerConfigurations = Array.Empty<ControlPanelSpeakerConfigurationItem>();
        try
        {
            var deadline = started + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            EmitWorkerHeartbeat("launch", soundHandle, windowOwnership, propertyHandle, propertyOwnership);
            setOperationStage("sound");
            EmitWorkerHeartbeat("sound", soundHandle, windowOwnership, propertyHandle, propertyOwnership);
            {
            AutomationElement? window = null;
            AutomationElement? endpointElement = null;
            while (window is null && Stopwatch.GetTimestamp() < deadline)
            {
                (window, endpointElement, windowOwnership) = FindSoundWindow(beforeWindows, endpoint, deadline,
                    value => { setOperationStage(value); EmitWorkerHeartbeat(value, soundHandle, windowOwnership, propertyHandle, propertyOwnership); });
                Thread.Sleep(50);
            }

            soundHandle = window is null ? IntPtr.Zero : new IntPtr(window.Current.NativeWindowHandle);
            EmitWorkerHeartbeat("sound", soundHandle, windowOwnership, propertyHandle, propertyOwnership);
            setOperationStage("endpoint");
            if (window is null || endpointElement is null)
            {
                throw new InvalidOperationException("No Sound window exposed a unique active endpoint list item.");
            }

            if (endpointElement.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern))
            {
                ((SelectionItemPattern)selectionPattern).Select();
            }

            endpointElement.SetFocus();
            setOperationStage("speaker-button");
            speakerConfigurations = TryReadSpeakerConfigurations(window, soundHandle, deadline);
            setOperationStage("properties-button");
            EmitWorkerHeartbeat("properties-button", soundHandle, windowOwnership, propertyHandle, propertyOwnership);
            var beforePropertiesWindows = TopLevelWindows();
            var propertiesButton = _win32.EnumerateChildWindows(soundHandle)
                .FirstOrDefault(handle => string.Equals(_win32.GetWindowClassName(handle), "Button", StringComparison.OrdinalIgnoreCase)
                    && IsPropertiesButtonTitle(_win32.GetWindowText(handle)));
            if (propertiesButton == IntPtr.Zero)
            {
                throw new InvalidOperationException("[sound-properties-button] Properties button was not found by Win32.");
            }
            if (!_win32.PostMessage(propertiesButton, 0x00F5, IntPtr.Zero, IntPtr.Zero))
            {
                throw new InvalidOperationException("[sound-properties-button] BM_CLICK failed.");
            }

            Thread.Sleep(200);
            setOperationStage("properties-dialog");
            var propertyResult = FindPropertiesDialog(endpoint, beforePropertiesWindows, deadline);
            propertyHandle = propertyResult.Handle;
            propertyOwnership = propertyResult.Ownership;
            var observedWindows = propertyResult.Observed;
            EmitWorkerHeartbeat("properties-dialog", soundHandle, windowOwnership, propertyHandle, propertyOwnership);
            if (propertyHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException($"[properties-dialog] Audio Properties dialog was not found; observed windows: {observedWindows}");
            }
            setOperationStage("advanced-tab");
            EmitWorkerHeartbeat("advanced-tab", soundHandle, windowOwnership, propertyHandle, propertyOwnership);
            if (!_win32.TryReadFormatItemsAcrossTabs(propertyHandle, Remaining(deadline), out var read, out var tabObserved))
            {
                throw new InvalidOperationException($"[format-tab] No valid format tab; observed tabs: {tabObserved}");
            }
            setOperationStage($"format-tab-{read.TabIndex}");
            items = read.Items.Select((text, index) => ParseFormatItem(index, text)).ToList();
            }
        }
        finally
        {
            setCleanupStage("cleanup");
            cleanupAttempted = windowOwnership == "Created" || propertyOwnership == "Created";
            if (cleanupAttempted)
            {
                cleanupSucceeded = CleanupCreatedWindows(_win32, soundHandle, windowOwnership, propertyHandle, propertyOwnership, TimeSpan.FromMilliseconds(500));
                try
                {
                    if (!controlPanel.HasExited) { controlPanel.Kill(entireProcessTree: true); controlPanel.WaitForExit(500); }
                    cleanupSucceeded &= controlPanel.HasExited;
                }
                catch (Exception ex) { cleanupFailure = ex.Message; cleanupSucceeded = false; }
            }
            else cleanupSucceeded = true;
        }

        if (cleanupFailure is not null && items is not null)
        {
            throw new InvalidOperationException($"[stage=cleanup] Control Panel cleanup failed: {cleanupFailure}; cleanup=Failed.");
        }

        return new ControlPanelFormatResult(
            items ?? throw new InvalidOperationException("The Control Panel returned no format items."),
            speakerConfigurations,
            speakerConfigurations.Count == 0 ? null : speakerConfigurations.Max(item => item.Channels),
            new ControlPanelFormatSnapshot(
                startedAt,
                DateTimeOffset.Now,
                $"mmsys.cpl UI Automation; WindowOwnership={windowOwnership}; PropertiesOwnership={propertyOwnership}",
                cleanupAttempted,
                cleanupSucceeded,
                cleanupFailure));
    }

    private (AutomationElement? Window, AutomationElement? Endpoint, string Ownership) FindSoundWindow(
        IReadOnlySet<IntPtr> beforeWindows,
        EndpointInfo endpoint,
        long deadline,
        Action<string> setStage)
    {
        var names = new[] { endpoint.DeviceDescription, endpoint.FriendlyName }
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        while (Stopwatch.GetTimestamp() < deadline)
        {
            setStage("sound-windows");
            var windows = _win32.EnumerateTopLevelWindows();
            EmitWindowDiagnostic(windows);
            var candidates = windows
                .Where(window => string.Equals(window.ClassName, "#32770", StringComparison.Ordinal)
                    && (IsSoundWindowTitle(window.Title) || NormalizeUiName(window.Title).Length <= 12))
                .OrderBy(window => beforeWindows.Contains(window.Handle) ? 1 : 0)
                .ToList();
            foreach (var candidate in candidates)
            {
                var handle = candidate.Handle;
                setStage("sound-uia");
                AutomationElement window;
                try { window = AutomationElement.FromHandle(handle); }
                catch (ElementNotAvailableException) { continue; }

                setStage("sound-endpoint");
                var matches = FindEndpointMatches(window, names, deadline);
                setStage("sound-identity");
                if (ShouldAcceptSoundWindow(matches.Count, IsSoundMain(window, deadline)))
                {
                    var ownership = beforeWindows.Contains(handle) ? "Reused" : "Created";
                    EmitWorkerHeartbeat("sound", handle, ownership, IntPtr.Zero, "Unknown");
                    return (window, matches[0], ownership);
                }
            }

            Thread.Sleep(50);
        }

        return (null, null, "Unknown");
    }

    private IReadOnlyList<ControlPanelSpeakerConfigurationItem> TryReadSpeakerConfigurations(
        AutomationElement soundWindow,
        IntPtr soundHandle,
        long deadline)
    {
        var speakerDeadline = Math.Min(
            deadline,
            Stopwatch.GetTimestamp() + (long)(StartupTimeout.TotalSeconds * Stopwatch.Frequency));
        var buttons = _win32.EnumerateChildWindows(soundHandle)
            .Where(handle => string.Equals(_win32.GetWindowClassName(handle), "Button", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var configureButton = buttons.FirstOrDefault(handle =>
        {
            var title = _win32.GetWindowText(handle);
            return IsConfigureButtonTitle(title)
                || (title.EndsWith("(&C)", StringComparison.OrdinalIgnoreCase)
                    && !title.Contains("預設", StringComparison.OrdinalIgnoreCase));
        });
        if (configureButton == IntPtr.Zero)
        {
            return Array.Empty<ControlPanelSpeakerConfigurationItem>();
        }

        var beforeWindows = TopLevelWindows();
        Exception? invokeFailure = null;
        var invokeThread = new Thread(() =>
        {
            try
            {
                var button = AutomationElement.FromHandle(configureButton);
                if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                {
                    throw new InvalidOperationException("Configure button did not expose InvokePattern.");
                }

                ((InvokePattern)pattern).Invoke();
            }
            catch (Exception ex)
            {
                invokeFailure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "SpeakerSetupInvoker",
        };
        invokeThread.SetApartmentState(ApartmentState.STA);
        invokeThread.Start();

        Thread.Sleep(200);
        IntPtr setupHandle = IntPtr.Zero;
        IReadOnlyList<ControlPanelSpeakerConfigurationItem> configurations = Array.Empty<ControlPanelSpeakerConfigurationItem>();
        var setupObserved = string.Empty;
        var soundObserved = string.Empty;
        while (Stopwatch.GetTimestamp() < speakerDeadline)
        {
            setupHandle = _win32.EnumerateTopLevelWindows()
                .Where(window => window.IsVisible
                    && !beforeWindows.Contains(window.Handle)
                    && string.Equals(window.ClassName, "NativeHWNDHost", StringComparison.Ordinal))
                .Select(window => window.Handle)
                .FirstOrDefault();
            if (setupHandle != IntPtr.Zero)
            {
                TryReadSpeakerConfigurations(setupHandle, speakerDeadline, out configurations, out setupObserved);
            }
            if (configurations.Count == 0)
            {
                TryReadSpeakerConfigurations(soundHandle, speakerDeadline, out configurations, out soundObserved);
            }
            if (configurations.Count > 0 && setupHandle != IntPtr.Zero)
            {
                break;
            }

            Thread.Sleep(50);
        }

        if (configurations.Count == 0 || setupHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"[speaker-setup] Speaker Setup did not expose a readable configuration list. "
                + $"Setup HWND={setupHandle.ToInt64()}; setup observed={setupObserved}; sound observed={soundObserved}; "
                + $"invoke failure={invokeFailure?.Message}");
        }

        if (!CloseWindow(setupHandle, TimeSpan.FromMilliseconds(500)))
        {
            throw new InvalidOperationException("[speaker-setup] Speaker Setup window did not close.");
        }
        if (!invokeThread.Join(1000))
        {
            throw new InvalidOperationException("[speaker-setup] Configure invocation did not return after closing Speaker Setup.");
        }
        if (invokeFailure is not null)
        {
            throw new InvalidOperationException($"[speaker-setup] Configure invocation failed: {invokeFailure.Message}", invokeFailure);
        }

        return configurations;
    }

    private static bool TryReadSpeakerConfigurations(
        IntPtr dialogHandle,
        long deadline,
        out IReadOnlyList<ControlPanelSpeakerConfigurationItem> items,
        out string observed)
    {
        items = Array.Empty<ControlPanelSpeakerConfigurationItem>();
        observed = string.Empty;
        AutomationElement dialog;
        try { dialog = AutomationElement.FromHandle(dialogHandle); }
        catch (ElementNotAvailableException) { return false; }

        var elements = WalkDirectChildren(dialog, deadline);
        observed = string.Join(" | ", elements.Select(element =>
            $"{element.Current.ControlType.ProgrammaticName}:'{NormalizeUiName(element.Current.Name)}'"));
        var configurations = elements
            .Where(element => element.Current.ControlType == ControlType.ListItem
                && !string.IsNullOrWhiteSpace(element.Current.Name))
            .Select(element => NormalizeUiName(element.Current.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((name, index) => CreateSpeakerConfiguration(index, name))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();

        if (configurations.Count == 0)
        {
            return false;
        }

        items = configurations;
        return true;
    }

    private static ControlPanelSpeakerConfigurationItem? CreateSpeakerConfiguration(int index, string displayText)
    {
        var channels = ParseSpeakerChannels(displayText);
        return channels is null ? null : new ControlPanelSpeakerConfigurationItem(index, displayText, channels.Value);
    }

    internal static int? ParseSpeakerChannels(string text)
    {
        var normalized = NormalizeUiName(text);
        if (normalized.Contains("7.1", StringComparison.OrdinalIgnoreCase))
        {
            return 8;
        }

        if (normalized.Contains("5.1", StringComparison.OrdinalIgnoreCase))
        {
            return 6;
        }

        if (normalized.Contains("Quadraphonic", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("四聲道", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Surround", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("環場", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (normalized.Contains("Stereo", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("立體聲", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return null;
    }

    private bool CloseWindow(IntPtr handle, TimeSpan timeout)
    {
        if (handle == IntPtr.Zero)
        {
            return true;
        }

        _win32.TrySendMessageTimeout(handle, WmClose, IntPtr.Zero, IntPtr.Zero, timeout, out _);
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (_win32.IsWindow(handle) && Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(25);
        }

        return !_win32.IsWindow(handle);
    }

    private static bool IsConfigureButtonTitle(string title)
    {
        var normalized = NormalizeUiName(title);
        return normalized.Contains("Configure", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("配置", StringComparison.OrdinalIgnoreCase)
            || (normalized.StartsWith("設定", StringComparison.OrdinalIgnoreCase)
                && !normalized.Contains("預設", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPropertiesButtonTitle(string title)
    {
        var normalized = NormalizeUiName(title);
        return normalized.Contains("Properties", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("屬性", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("內容", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSoundWindowTitle(string title)
    {
        var normalized = NormalizeUiName(title);
        return normalized.Contains("Sound", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("聲音", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("音效", StringComparison.OrdinalIgnoreCase);
    }

    private static List<AutomationElement> FindEndpointMatches(AutomationElement window, IReadOnlyList<string> names, long deadline) =>
        WalkDirectChildren(window, deadline)
            .Where(item => item.Current.ControlType == ControlType.ListItem
                && names.Any(name => string.Equals(
                    item.Current.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();

    internal static bool ShouldAcceptSoundWindow(int endpointMatchCount, bool isSoundMain) =>
        endpointMatchCount > 0 && isSoundMain;

    private static bool IsSoundMain(AutomationElement window, long deadline)
    {
        if (!string.Equals(window.Current.ClassName, "#32770", StringComparison.Ordinal))
        {
            return false;
        }

        var tabs = WalkDirectChildren(window, deadline)
            .Where(tab => tab.Current.ControlType == ControlType.TabItem)
            .Select(tab => NormalizeUiName(tab.Current.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return tabs.Any(name => name is "Playback" or "播放" or "播放裝置")
            && tabs.Any(name => name is "Recording" or "錄製" or "錄音");
    }

    private static (AutomationElement? Element, string Observed) FindPropertiesButton(AutomationElement window, long deadline)
    {
        var observed = new List<string>();
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var buttons = WalkDirectChildren(window, deadline)
                .Where(button => button.Current.ControlType == ControlType.Button)
                .ToList();
            observed.Clear();
            foreach (var button in buttons)
            {
                var invoke = button.TryGetCurrentPattern(InvokePattern.Pattern, out _);
                observed.Add($"Name='{button.Current.Name}',AutomationId='{button.Current.AutomationId}',Enabled={button.Current.IsEnabled},Invoke={invoke}");
            }

            var properties = buttons
                .Where(button => button.Current.IsEnabled
                    && button.TryGetCurrentPattern(InvokePattern.Pattern, out _))
                .FirstOrDefault(button => IsPropertiesName(button.Current.Name));
            if (properties is not null)
            {
                return (properties, string.Join("; ", observed));
            }

            Thread.Sleep(50);
        }

        return (null, string.Join("; ", observed));
    }

    private static bool IsPropertiesName(string name)
    {
        var normalized = NormalizeUiName(name);
        return normalized.StartsWith("properties", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("內容", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("屬性", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsExistingAudioPropertyTitleMatch(string title, EndpointInfo endpoint)
    {
        var normalizedTitle = NormalizeUiName(title);
        return new[] { endpoint.DeviceDescription, endpoint.FriendlyName }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Any(name => normalizedTitle.StartsWith(NormalizeUiName(name), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeUiName(string value) =>
        value.Trim()
            .Replace("&", "", StringComparison.Ordinal)
            .Replace("（", "(", StringComparison.Ordinal)
            .Replace("）", ")", StringComparison.Ordinal)
            .Trim();

    private (IntPtr Handle, string Ownership, string Observed) FindPropertiesDialog(
        EndpointInfo endpoint,
        IReadOnlySet<IntPtr> beforeWindows,
        long deadline)
    {
        var observed = new List<string>();
        while (Stopwatch.GetTimestamp() < deadline)
        {
            observed.Clear();
            var windows = _win32.EnumerateTopLevelWindows();
            var candidates = windows
                .Where(window => string.Equals(window.ClassName, "#32770", StringComparison.Ordinal))
                .ToList();
            var delta = candidates.Where(window => !beforeWindows.Contains(window.Handle)).ToList();
            var matchingDelta = delta.Where(window => IsExistingAudioPropertyTitleMatch(window.Title, endpoint)).ToList();
            var matchingExisting = candidates.Where(window => beforeWindows.Contains(window.Handle)
                && IsExistingAudioPropertyTitleMatch(window.Title, endpoint)).ToList();
            Console.Error.WriteLine($"DIAG properties-scan candidates={candidates.Count} delta={delta.Count} matchingDelta={matchingDelta.Count} matchingExisting={matchingExisting.Count} titles={string.Join(" || ", candidates.Take(30).Select(window => $"HWND={window.Handle.ToInt64()},title={window.Title}"))}");
            foreach (var window in candidates)
            {
                observed.Add($"HWND={window.Handle},Class={window.ClassName},Title='{window.Title}',Ownership={(beforeWindows.Contains(window.Handle) ? "Reused" : "Created")}");
            }

            if (matchingDelta.Count > 1 || (matchingDelta.Count == 0 && matchingExisting.Count > 1))
            {
                return (IntPtr.Zero, "Unknown", $"Multiple matching dialogs: {string.Join("; ", observed)}");
            }

            var selected = matchingDelta.Count == 1
                ? matchingDelta
                : matchingExisting.Count == 1 ? matchingExisting : new List<Win32WindowInfo>();
            if (selected.Count == 1)
            {
                var match = selected[0];
                var ownership = beforeWindows.Contains(match.Handle) ? "Reused" : "Created";
                EmitWorkerHeartbeat("properties-dialog", IntPtr.Zero, "Unknown", match.Handle, ownership);
                return (match.Handle, ownership, string.Join("; ", observed));
            }
            else if (selected.Count > 1)
            {
                return (IntPtr.Zero, "Unknown", $"Multiple matching dialogs: {string.Join("; ", observed)}");
            }

            Thread.Sleep(50);
        }

        return (IntPtr.Zero, "Unknown", string.Join("; ", observed));
    }

    private static (AutomationElement? Element, string Observed) FindAdvancedTab(AutomationElement dialog, long deadline)
    {
        var observed = new List<string>();
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var tabs = WalkDirectChildren(dialog, deadline)
                .Where(tab => tab.Current.ControlType == ControlType.TabItem)
                .ToList();
            observed = tabs.Select(tab => $"Name='{tab.Current.Name}',AutomationId='{tab.Current.AutomationId}'").ToList();
            var advanced = tabs.FirstOrDefault(tab =>
                string.Equals(tab.Current.AutomationId, "Advanced", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tab.Current.Name, "進階", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tab.Current.Name, "Advanced", StringComparison.OrdinalIgnoreCase));
            if (advanced is not null)
            {
                return (advanced, string.Join("; ", observed));
            }

            Thread.Sleep(50);
        }

        return (null, string.Join("; ", observed));
    }

    private static AutomationElement? FindEnabledFormatCombo(AutomationElement dialog, long deadline)
    {
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var combos = WalkDirectChildren(dialog, deadline)
                .Where(combo => combo.Current.ControlType == ControlType.ComboBox
                    && combo.Current.IsEnabled)
                .ToList();
            var combo = combos.FirstOrDefault(candidate =>
                candidate.Current.Name.Contains("Default", StringComparison.OrdinalIgnoreCase)
                || candidate.Current.Name.Contains("預設", StringComparison.OrdinalIgnoreCase)
                || candidate.Current.AutomationId.Contains("Default", StringComparison.OrdinalIgnoreCase));
            combo ??= combos.FirstOrDefault();
            if (combo is not null)
            {
                return combo;
            }

            Thread.Sleep(50);
        }

        return null;
    }

    private static AutomationElement? FindEndpoint(AutomationElement window, string friendlyName, long deadline)
    {
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var items = WalkDirectChildren(window, deadline)
                .Where(e => e.Current.ControlType == ControlType.ListItem
                    && e.Current.IsEnabled && !string.IsNullOrWhiteSpace(e.Current.Name))
                .ToList();
            var matches = items.Where(e => string.Equals(
                e.Current.Name.Trim(), friendlyName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (matches.Count > 1)
            {
                throw new InvalidOperationException($"Endpoint friendly name '{friendlyName}' matched multiple Control Panel items.");
            }

            Thread.Sleep(50);
        }

        return null;
    }

    private static AutomationElement? FindAdvancedButton(AutomationElement window, long deadline)
    {
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var buttons = WalkDirectChildren(window, deadline)
                .Where(button => button.Current.ControlType == ControlType.Button);
            var advanced = buttons.Cast<AutomationElement>().FirstOrDefault(e =>
                string.Equals(e.Current.Name, "Advanced", StringComparison.OrdinalIgnoreCase));
            if (advanced is not null)
            {
                return advanced;
            }

            Thread.Sleep(50);
        }

        return null;
    }

    private static AutomationElement? FindFormatCombo(AutomationElement window, long deadline)
    {
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var combos = WalkDirectChildren(window, deadline)
                .Where(combo => combo.Current.ControlType == ControlType.ComboBox);
            var combo = combos.Cast<AutomationElement>().FirstOrDefault(e =>
                e.Current.IsEnabled && WalkDirectChildren(e, deadline)
                    .Any(child => child.Current.ControlType == ControlType.ListItem));
            if (combo is not null)
            {
                return combo;
            }

            Thread.Sleep(50);
        }

        return null;
    }

    private IReadOnlyList<ControlPanelFormatItem> ReadItems(AutomationElement combo, long deadline)
    {
        var handle = new IntPtr(combo.Current.NativeWindowHandle);
        if (handle != IntPtr.Zero && TryReadWin32Combo(handle, deadline, out var win32Items))
        {
            return win32Items.Select((text, index) => ParseFormatItem(index, text)).ToList();
        }

        var items = WalkDirectChildren(combo, deadline)
            .Where(item => item.Current.ControlType == ControlType.ListItem)
            .ToList();

        return items
            .Select((item, index) => ParseFormatItem(index, item.Current.Name))
            .ToList();
    }

    private IntPtr FindWin32FormatCombo(IntPtr dialog, long deadline)
    {
        const uint CbGetCount = 0x0146;
        foreach (var handle in _win32.EnumerateChildWindows(dialog))
        {
            if (Stopwatch.GetTimestamp() >= deadline
                || !_win32.IsWindowVisible(handle)
                || !string.Equals(_win32.GetWindowClassName(handle), "ComboBox", StringComparison.OrdinalIgnoreCase)) continue;
            if (_win32.TrySendMessageTimeout(handle, CbGetCount, IntPtr.Zero, IntPtr.Zero, Remaining(deadline), out var count)
                && count.ToInt64() > 0 && count.ToInt64() <= 512) return handle;
        }
        return IntPtr.Zero;
    }

    private bool TryReadWin32Combo(IntPtr combo, long deadline, out IReadOnlyList<string> items)
    {
        const uint CbGetCount = 0x0146;
        const uint CbGetLbTextLen = 0x0149;
        const uint CbGetLbText = 0x0148;
        items = Array.Empty<string>();
        var remaining = Remaining(deadline);
        if (!_win32.TrySendMessageTimeout(combo, CbGetCount, IntPtr.Zero, IntPtr.Zero, remaining, out var countValue)
            || countValue.ToInt64() < 0 || countValue.ToInt64() > 512)
        {
            return false;
        }

        var result = new List<string>((int)countValue.ToInt64());
        for (var index = 0; index < countValue.ToInt64(); index++)
        {
            if (Stopwatch.GetTimestamp() >= deadline
                || !_win32.TrySendMessageTimeout(combo, CbGetLbTextLen, (IntPtr)index, IntPtr.Zero,
                    Remaining(deadline), out var lengthValue)) return false;
            var length = lengthValue.ToInt64();
            if (length < 0 || length > 4096) return false;
            var unicode = _win32.IsWindowUnicode(combo);
            var charWidth = unicode ? 2 : 1;
            var memory = Marshal.AllocHGlobal((int)(length + 1) * charWidth);
            try
            {
                if (!_win32.TrySendMessageTimeout(combo, CbGetLbText, (IntPtr)index, memory,
                    Remaining(deadline), out _)) return false;
                var decoded = DecodeComboText(memory, unicode, (int)length);
                if (unicode && !decoded.Contains("Hz", StringComparison.OrdinalIgnoreCase))
                {
                    var ansi = DecodeComboText(memory, false, (int)length);
                    if (ansi.Contains("Hz", StringComparison.OrdinalIgnoreCase)) decoded = ansi;
                }
                result.Add(decoded);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        items = result;
        return true;
    }

    internal static string DecodeComboText(IntPtr buffer, bool unicode) =>
        unicode ? Marshal.PtrToStringUni(buffer) ?? string.Empty : Marshal.PtrToStringAnsi(buffer) ?? string.Empty;

    private static string DecodeComboText(IntPtr buffer, bool unicode, int length) =>
        unicode ? Marshal.PtrToStringUni(buffer, length) ?? string.Empty : Marshal.PtrToStringAnsi(buffer, length) ?? string.Empty;

    private bool TrySelectAdvancedTab(AutomationElement dialog, long deadline, out string observed)
    {
        var handle = new IntPtr(dialog.Current.NativeWindowHandle);
        if (handle == IntPtr.Zero)
        {
            observed = "dialog has no native window handle";
            return false;
        }

        return _win32.TrySelectAdvancedTab(handle, Remaining(deadline), out observed);
    }

    private static TimeSpan Remaining(long deadline)
    {
        var ticks = deadline - Stopwatch.GetTimestamp();
        return TimeSpan.FromSeconds(Math.Max(0.001, ticks / (double)Stopwatch.Frequency));
    }

    internal static ControlPanelFormatItem ParseFormatItem(int index, string text)
    {
        var numbers = Numbers.Matches(text)
            .Select(m => int.TryParse(m.Value, out var value) ? (int?)value : null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        var bits = numbers.FirstOrDefault(n => n is 16 or 20 or 24 or 32);
        var rate = numbers.FirstOrDefault(n => n is >= 8000 and <= 384000 && n != bits);
        if (bits == 0 || rate == 0)
        {
            return new ControlPanelFormatItem(index, text, null, null, null, null,
                ControlPanelParseStatus.Unparsed, "Could not identify sample rate and effective bits.");
        }

        var channels = numbers.FirstOrDefault(n => (n is 1 or 2 or 4 or 6 or 8) && n != bits);
        var container = bits > 16 ? 32 : bits;
        return new ControlPanelFormatItem(
            index, text, channels == 0 ? null : channels, rate, bits, container,
            ControlPanelParseStatus.Parsed, null);
    }
}
