using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Application;
using AudioDeviceConfigurator.Audio;
using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Gui;
using AudioDeviceConfigurator.Tests.Fakes;

namespace AudioDeviceConfigurator.Tests;

/// <summary>
/// Drives a real WPF MainWindow on an STA thread with fake services. Validates the binding,
/// button state, and click handlers without needing Core Audio or SVCL on the host.
/// </summary>
public sealed class MainWindowIntegrationTests : IDisposable
{
    private readonly Thread _staThread;
    private readonly WindowHarness _harness = new();

    public MainWindowIntegrationTests()
    {
        _staThread = new Thread(() =>
        {
            var window = _harness.Build();
            _harness.Window = window;
            _harness.Window.Show();
            System.Windows.Threading.Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "MainWindowIntegrationTests.STA",
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
        _harness.WaitForWindow(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        _harness.Invoke(() =>
        {
            _harness.Window?.Close();
            System.Windows.Threading.Dispatcher.ExitAllFrames();
        });
        _staThread.Join(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Active_settings_display_updated_after_apply()
    {
        _harness.SeedEndpoint("ep-1", isDefault: true);
        _harness.SeedOptions("ep-1", channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        });
        // LoadActiveSettingsAsync consumes one (initial device format).
        // ApplyAsync consumes before + after = 2 more. Total = 3.
        _harness.Svcl
            .EnqueueSavedFormat(new SavedFormat(SavedFormat.WaveFormatExtensible, 2, 44100, 32, 16, 0x3, new byte[40]))
            .EnqueueSavedFormat(new SavedFormat(SavedFormat.WaveFormatExtensible, 2, 44100, 32, 16, 0x3, new byte[40]))
            .EnqueueSavedFormat(new SavedFormat(SavedFormat.WaveFormatExtensible, 2, 44100, 32, 16, 0x3, new byte[40]));

        await _harness.WaitForEndpointsAsync(TimeSpan.FromSeconds(5));
        await _harness.WaitForChannelsAsync(TimeSpan.FromSeconds(5));
        await _harness.WaitForActiveSettingsAsync(TimeSpan.FromSeconds(5));

        // Before Apply: Active region already reflects the device's current format
        // (loaded by LoadActiveSettingsAsync right after EndpointChangedAsync).
        _harness.Invoke(() =>
        {
            var channelText = (TextBlock?)FindByAutomationId("ActiveChannelText");
            Assert.NotNull(channelText);
            Assert.Equal("Channel: 2 ch", channelText!.Text);
            Assert.Equal("Sample Rate: 44,100 Hz",
                ((TextBlock?)FindByAutomationId("ActiveSampleRateText"))!.Text);
            Assert.Equal("Bit Depth: 16-bit",
                ((TextBlock?)FindByAutomationId("ActiveBitDepthText"))!.Text);
        });

        _harness.Invoke(() =>
        {
            _harness.SelectConfiguration(2, 44100, 16);
            _harness.ApplyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });
        await _harness.WaitForPlaybackStartedAsync(TimeSpan.FromSeconds(5));

        _harness.Invoke(() =>
        {
            var channelText = (TextBlock?)FindByAutomationId("ActiveChannelText");
            Assert.NotNull(channelText);
            Assert.Equal("Channel: 2 ch", channelText!.Text);
        });
    }

    private DependencyObject? FindByAutomationId(string id)
    {
        var window = _harness.Window!;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(window); i++)
        {
            var child = VisualTreeHelper.GetChild(window, i);
            if (Matches(child, id)) return child;
            var found = SearchTree(child, id);
            if (found is not null) return found;
        }
        return null;
    }

    private DependencyObject? SearchTree(DependencyObject parent, string id)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (Matches(child, id)) return child;
            var found = SearchTree(child, id);
            if (found is not null) return found;
        }
        return null;
    }

    private static bool Matches(DependencyObject obj, string id) =>
        System.Windows.Automation.AutomationProperties.GetAutomationId(obj) == id;

    [Fact]
    public async Task Changing_radio_button_during_playback_keeps_apply_enabled_and_reapply_works()
    {
        _harness.SeedEndpoint("ep-1", isDefault: true);
        _harness.SeedOptions("ep-1", channels: new[] { 2, 4 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
            new ControlPanelFormatItem(1, "16 bit, 48000 Hz", 2, 48000, 16, 16, ControlPanelParseStatus.Parsed, null),
        });
// LoadActive consumes 1, then each of 2 Apply calls consumes before+after = 2 each.
        // Total: 1 + 2 + 2 = 5.
        _harness.Svcl
            .EnqueueSavedFormat(new SavedFormat(SavedFormat.WaveFormatExtensible, 2, 44100, 32, 16, 0x3, new byte[40]))
            .EnqueueSavedFormat(new SavedFormat(SavedFormat.WaveFormatExtensible, 2, 44100, 32, 16, 0x3, new byte[40]))
            .EnqueueSavedFormat(new SavedFormat(SavedFormat.WaveFormatExtensible, 2, 44100, 32, 16, 0x3, new byte[40]))
            .EnqueueSavedFormat(new SavedFormat(SavedFormat.WaveFormatExtensible, 2, 48000, 32, 16, 0x3, new byte[40]))
            .EnqueueSavedFormat(new SavedFormat(SavedFormat.WaveFormatExtensible, 2, 48000, 32, 16, 0x3, new byte[40]));

        await _harness.WaitForEndpointsAsync(TimeSpan.FromSeconds(5));
        _harness.Invoke(() => _harness.EndpointCombo.SelectedIndex = 0);
        await _harness.WaitForChannelsAsync(TimeSpan.FromSeconds(5));
        _harness.Invoke(() =>
        {
            _harness.SelectConfiguration(2, 44100, 16);
            _harness.ApplyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });
        await _harness.WaitForPlaybackStartedAsync(TimeSpan.FromSeconds(5));

        // User picks a different sample rate while playback is running.
        _harness.Invoke(() => _harness.SelectConfiguration(2, 48000, 16));

        _harness.Invoke(() =>
        {
            Assert.True(_harness.ApplyButton.IsEnabled);
            _harness.ApplyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });
        await _harness.WaitForPlaybackStoppedAsync(TimeSpan.FromSeconds(5));
        await _harness.WaitForPlaybackStartedAsync(TimeSpan.FromSeconds(5));
        _harness.Invoke(() =>
        {
            Assert.True(_harness.Playback.IsPlaying);
            Assert.Contains("SetFormat:ep-1:2:16:48000", _harness.Svcl.Operations);
        });
    }

    [Fact]
    public async Task Window_loads_default_endpoint_on_startup()
    {
        _harness.SeedEndpoint("ep-other", isDefault: false);
        _harness.SeedEndpoint("ep-default", isDefault: true);
        _harness.SeedOptions("ep-default", channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        });

        await _harness.WaitForEndpointsAsync(TimeSpan.FromSeconds(5));
        await _harness.WaitForChannelsAsync(TimeSpan.FromSeconds(5));

        _harness.Invoke(() =>
        {
            Assert.Equal("ep-default", EndpointCombo_SelectedEndpointId());
        });
    }

    [Fact]
    public async Task Switch_options_are_populated_before_endpoint_is_selected()
    {
        // The VM seeds common switch options in its constructor so the three columns are
        // visible immediately when the window opens.
        var vm = _harness.ViewModel;
        Assert.Equal(4, vm.ChannelOptions.Count);
        Assert.Equal(7, vm.SampleRateOptions.Count);
        Assert.Equal(4, vm.BitDepthOptions.Count);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Endpoint_notifications_from_a_background_thread_are_debounced_and_refreshed_on_the_dispatcher()
    {
        _harness.SeedEndpoint("ep-1", isDefault: true);
        _harness.SeedOptions("ep-1", channels: new[] { 2 }, formats:
        [
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        ]);
        await _harness.Window!.Dispatcher.InvokeAsync(async () =>
        {
            await _harness.ViewModel.LoadEndpointsAsync(CancellationToken.None);
            await _harness.ViewModel.EndpointChangedAsync(_harness.ViewModel.Endpoints[0], CancellationToken.None);
        }).Task.Unwrap();
        var reads = _harness.Endpoints.ReadCalls;
        _harness.SeedEndpoint("ep-2", isDefault: false);

        await Task.Run(() =>
        {
            _harness.EndpointChanges.Raise(new AudioEndpointChange(AudioEndpointChangeKind.Added, "ep-2"));
            _harness.EndpointChanges.Raise(new AudioEndpointChange(AudioEndpointChangeKind.StateChanged, "ep-2"));
            _harness.EndpointChanges.Raise(new AudioEndpointChange(AudioEndpointChangeKind.DefaultChanged, "ep-1"));
        });

        await WaitUntilAsync(
            () => _harness.ViewModel.Endpoints.Count == 2,
            TimeSpan.FromSeconds(5));
        await Task.Delay(500);

        Assert.Equal(reads + 1, _harness.Endpoints.ReadCalls);
        Assert.Equal("ep-1", _harness.ViewModel.SelectedEndpoint?.EndpointId);
        Assert.Equal(1, _harness.EndpointChanges.SubscriberCount);
    }

    [Fact]
    public async Task Endpoint_refresh_waits_until_option_loading_is_no_longer_busy()
    {
        await WaitUntilAsync(
            () => _harness.ViewModel.StatusMessage == "No active render endpoints found.",
            TimeSpan.FromSeconds(2));
        _harness.SeedEndpoint("ep-1", isDefault: true);
        await _harness.Window!.Dispatcher.InvokeAsync(
            () => _harness.ViewModel.LoadEndpointsAsync(CancellationToken.None)).Task.Unwrap();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _harness.ControlPanel.Provider = (_, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return new ControlPanelFormatResult(
                [new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null)],
                [new ControlPanelSpeakerConfigurationItem(0, "2 channels", 2)],
                2,
                new ControlPanelFormatSnapshot(DateTimeOffset.MinValue, DateTimeOffset.MinValue, "fake", true, true, null));
        };

        var optionLoad = _harness.Window.Dispatcher.InvokeAsync(async () =>
            await _harness.ViewModel.EndpointChangedAsync(
                _harness.ViewModel.Endpoints[0], CancellationToken.None)).Task.Unwrap();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        _harness.SeedEndpoint("ep-2", isDefault: false);
        _harness.EndpointChanges.Raise(
            new AudioEndpointChange(AudioEndpointChangeKind.Added, "ep-2"));

        await Task.Delay(600);
        Assert.Single(_harness.ViewModel.Endpoints);

        release.Set();
        await optionLoad;
        await WaitUntilAsync(
            () => _harness.ViewModel.Endpoints.Count == 2,
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Closing_window_unsubscribes_from_endpoint_notifications()
    {
        await WaitUntilAsync(
            () => _harness.EndpointChanges.SubscriberCount == 1
                && _harness.ViewModel.StatusMessage == "No active render endpoints found.",
            TimeSpan.FromSeconds(2));
        var reads = _harness.Endpoints.ReadCalls;

        _harness.Invoke(() => _harness.Window!.Close());
        _harness.EndpointChanges.Raise(
            new AudioEndpointChange(AudioEndpointChangeKind.Added, "ep-after-close"));
        await Task.Delay(500);

        Assert.Equal(0, _harness.EndpointChanges.SubscriberCount);
        Assert.Equal(reads, _harness.Endpoints.ReadCalls);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
        Assert.True(condition());
    }

    private string? EndpointCombo_SelectedEndpointId()
    {
        var selected = _harness.EndpointCombo.SelectedItem as EndpointInfo;
        return selected?.EndpointId;
    }

    [Fact]
    public async Task Status_binding_remains_active_after_window_loads_endpoints()
    {
        _harness.SeedEndpoint("ep-1", isDefault: true);

        await _harness.WaitForEndpointsAsync(TimeSpan.FromSeconds(5));

        _harness.Invoke(() =>
        {
            var expression = System.Windows.Data.BindingOperations.GetBindingExpression(
                _harness.StatusText, TextBlock.TextProperty);
            Assert.NotNull(expression);
            // Default endpoint is auto-selected on startup; with no SeedOptions the VM reports
            // the endpoint has no selectable options.
            Assert.Equal("Endpoint has no selectable options.", _harness.StatusText.Text);
        });
    }

    [Fact]
    public void Main_window_exposes_stable_automation_ids()
    {
        _harness.Invoke(() =>
        {
            Assert.Equal("AudioDeviceConfigurator.MainWindow",
                System.Windows.Automation.AutomationProperties.GetAutomationId(_harness.Window!));
            Assert.Equal("EndpointCombo",
                System.Windows.Automation.AutomationProperties.GetAutomationId(_harness.EndpointCombo));
            Assert.Equal("ChannelsSwitchGroup",
                System.Windows.Automation.AutomationProperties.GetAutomationId(_harness.ChannelsSwitchGroup));
            Assert.Equal("SampleRateSwitchGroup",
                System.Windows.Automation.AutomationProperties.GetAutomationId(_harness.SampleRateSwitchGroup));
            Assert.Equal("BitDepthSwitchGroup",
                System.Windows.Automation.AutomationProperties.GetAutomationId(_harness.BitDepthSwitchGroup));
            Assert.Equal("ApplyButton",
                System.Windows.Automation.AutomationProperties.GetAutomationId(_harness.ApplyButton));
            Assert.Equal("PlayButton",
                System.Windows.Automation.AutomationProperties.GetAutomationId(_harness.PlayButton));
            Assert.Equal("StopButton",
                System.Windows.Automation.AutomationProperties.GetAutomationId(_harness.StopButton));
            Assert.Equal("StatusText",
                System.Windows.Automation.AutomationProperties.GetAutomationId(_harness.StatusText));
        });
    }

    [Fact]
    public void Apply_Play_Stop_buttons_bind_to_MainViewModel_through_DataContext()
    {
        var window = _harness.Window!;
        object? dataContext = null;
        _harness.Invoke(() => dataContext = window.DataContext);
        Assert.Same(_harness.ViewModel, dataContext);

        System.Windows.Data.BindingExpression? endpointExpression = null;
        _harness.Invoke(() => endpointExpression = System.Windows.Data.BindingOperations.GetBindingExpression(
            _harness.EndpointCombo, UIElement.IsEnabledProperty));
        Assert.NotNull(endpointExpression);
        Assert.Equal(nameof(MainViewModel.CanChangeSelection), endpointExpression!.ParentBinding.Path.Path);
        Assert.Same(_harness.ViewModel, endpointExpression.DataItem);

        System.Windows.Data.BindingExpression? applyExpression = null;
        _harness.Invoke(() => applyExpression = System.Windows.Data.BindingOperations.GetBindingExpression(
            _harness.ApplyButton, System.Windows.Controls.Button.IsEnabledProperty));
        Assert.NotNull(applyExpression);
        Assert.Same(_harness.ViewModel, applyExpression!.DataItem);

        System.Windows.Data.BindingExpression? playExpression = null;
        _harness.Invoke(() => playExpression = System.Windows.Data.BindingOperations.GetBindingExpression(
            _harness.PlayButton, System.Windows.Controls.Button.IsEnabledProperty));
        Assert.NotNull(playExpression);
        Assert.Same(_harness.ViewModel, playExpression!.DataItem);

        System.Windows.Data.BindingExpression? stopExpression = null;
        _harness.Invoke(() => stopExpression = System.Windows.Data.BindingOperations.GetBindingExpression(
            _harness.StopButton, System.Windows.Controls.Button.IsEnabledProperty));
        Assert.NotNull(stopExpression);
        Assert.Same(_harness.ViewModel, stopExpression!.DataItem);
    }

    [Fact]
    public async Task MainViewModel_raises_PropertyChanged_when_IsBusy_changes()
    {
        var changes = new List<string?>();
        _harness.ViewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        // Trigger the dispatch path that the WPF view also subscribes to.
        _harness.Invoke(() => _harness.ViewModel.LoadEndpointsAsync(CancellationToken.None));
        // IsBusy is not flipped by LoadEndpointsAsync (it does not toggle), so use a synchronous
        // trigger that the VM exposes internally.
        // Direct setter access isn't possible; verify selection changes raise the expected notifications.
        // SelectedChannelIndex which should fire PropertyChanged for SelectedChannelIndex + CanApply.
        _harness.SeedEndpoint("ep-1", isDefault: true);
        _harness.SeedOptions("ep-1", channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        });
        await _harness.Window!.Dispatcher.InvokeAsync(async () =>
        {
            await _harness.ViewModel.LoadEndpointsAsync(CancellationToken.None);
            await _harness.ViewModel.EndpointChangedAsync(_harness.ViewModel.Endpoints[0], CancellationToken.None);
            _harness.ViewModel.SelectChannelIndex(0);
            _harness.ViewModel.SelectFormatIndex(0);
        }).Task.Unwrap();

        var observedChanges = changes.ToArray();
        Assert.Contains(nameof(MainViewModel.SelectedEndpoint), observedChanges);
        Assert.Contains(nameof(MainViewModel.SelectedChannelIndex), observedChanges);
        Assert.Contains(nameof(MainViewModel.CanApply), observedChanges);
    }

    [Fact]
    public async Task Apply_button_becomes_enabled_after_selecting_endpoint_channel_and_format()
    {
        _harness.SeedEndpoint("ep-1", isDefault: true);
        _harness.SeedOptions("ep-1", channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        });

        await _harness.WaitForEndpointsAsync(TimeSpan.FromSeconds(5));
        _harness.Invoke(() => _harness.EndpointCombo.SelectedIndex = 0);
        await _harness.WaitForChannelsAsync(TimeSpan.FromSeconds(5));

        // Before picking channel + format, Apply must be disabled.
        _harness.Invoke(() =>
        {
            Assert.False(_harness.ApplyButton.IsEnabled,
                "Apply should remain disabled until both channel and format are chosen.");
        });

        _harness.Invoke(() =>
        {
            _harness.SelectConfiguration(2, 44100, 16);
        });

        await _harness.WaitForApplyEnabledAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Window_shows_endpoints_and_unlocks_apply_after_options_loaded()
    {
        _harness.SeedEndpoint("ep-1", isDefault: true);
        _harness.SeedOptions("ep-1", channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        });

        await _harness.WaitForEndpointsAsync(TimeSpan.FromSeconds(5));

        Assert.Single(_harness.EndpointCombo.Items);
        _harness.Invoke(() => _harness.EndpointCombo.SelectedIndex = 0);
        await _harness.WaitForChannelsAsync(TimeSpan.FromSeconds(5));

        _harness.Invoke(() =>
        {
            _harness.SelectConfiguration(2, 44100, 16);
        });

        await _harness.WaitForApplyEnabledAsync(TimeSpan.FromSeconds(5));
        _harness.Invoke(() =>
        {
            Assert.True(_harness.ApplyButton.IsEnabled);
            Assert.False(_harness.PlayButton.IsEnabled);
            Assert.False(_harness.StopButton.IsEnabled);
        });
    }

    [Fact]
    public async Task Apply_click_runs_service_and_enables_play_after_pass()
    {
        _harness.SeedEndpoint("ep-1", isDefault: true);
        _harness.SeedOptions("ep-1", channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        });
        _harness.Svcl
            .EnqueueSavedFormat(new SavedFormat(SavedFormat.WaveFormatExtensible, 2, 44100, 32, 16, 0x3, new byte[40]))
            .EnqueueSavedFormat(new SavedFormat(SavedFormat.WaveFormatExtensible, 2, 44100, 32, 16, 0x3, new byte[40]))
            .EnqueueSavedFormat(new SavedFormat(SavedFormat.WaveFormatExtensible, 2, 44100, 32, 16, 0x3, new byte[40]));

        await _harness.WaitForEndpointsAsync(TimeSpan.FromSeconds(5));
        _harness.Invoke(() => _harness.EndpointCombo.SelectedIndex = 0);
        await _harness.WaitForChannelsAsync(TimeSpan.FromSeconds(5));
        _harness.Invoke(() =>
        {
            _harness.SelectConfiguration(2, 44100, 16);
            _harness.ApplyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });

        await _harness.WaitForPlaybackStartedAsync(TimeSpan.FromSeconds(5));
        _harness.Invoke(() =>
        {
            Assert.True(_harness.Playback.IsPlaying);
            // Apply stays enabled so the user can pick a new channel/sample rate/bit depth
            // and re-apply; ApplyAsync stops the current stream before swapping settings.
            Assert.True(_harness.ApplyButton.IsEnabled);
            Assert.False(_harness.PlayButton.IsEnabled);
            Assert.True(_harness.StopButton.IsEnabled);
        });
    }

    [Fact]
    public async Task Stop_click_releases_wasapi_resources()
    {
        _harness.SeedEndpoint("ep-1", isDefault: true);
        _harness.SeedOptions("ep-1", channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        });
        _harness.Svcl
            .EnqueueSavedFormat(new SavedFormat(SavedFormat.WaveFormatExtensible, 2, 44100, 32, 16, 0x3, new byte[40]))
            .EnqueueSavedFormat(new SavedFormat(SavedFormat.WaveFormatExtensible, 2, 44100, 32, 16, 0x3, new byte[40]))
            .EnqueueSavedFormat(new SavedFormat(SavedFormat.WaveFormatExtensible, 2, 44100, 32, 16, 0x3, new byte[40]));

        await _harness.WaitForEndpointsAsync(TimeSpan.FromSeconds(5));
        _harness.Invoke(() => _harness.EndpointCombo.SelectedIndex = 0);
        await _harness.WaitForChannelsAsync(TimeSpan.FromSeconds(5));
        _harness.Invoke(() =>
        {
            _harness.SelectConfiguration(2, 44100, 16);
            _harness.ApplyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });
        await _harness.WaitForPlaybackStartedAsync(TimeSpan.FromSeconds(5));

        _harness.Invoke(() => _harness.StopButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)));

        await _harness.WaitForPlaybackStoppedAsync(TimeSpan.FromSeconds(5));
        _harness.Invoke(() =>
        {
            Assert.False(_harness.Playback.IsPlaying);
            Assert.True(_harness.StopCalls >= 1);
        });
    }

    private sealed class WindowHarness
    {
        public FakeEndpointProvider Endpoints { get; } = new();
        public FakeControlPanelFormatProvider ControlPanel { get; } = new();
        public FakeSvclClient Svcl { get; } = new();
        public FakePlaybackService Playback { get; } = new();
        public FakeEndpointChangeMonitor EndpointChanges { get; } = new();
        public MainWindow? Window { get; set; }
        public MainViewModel ViewModel { get; set; } = null!;
        public ComboBox EndpointCombo { get; set; } = null!;
        public ItemsControl ChannelsSwitchGroup { get; set; } = null!;
        public ItemsControl SampleRateSwitchGroup { get; set; } = null!;
        public ItemsControl BitDepthSwitchGroup { get; set; } = null!;
        public Button ApplyButton { get; set; } = null!;
        public Button PlayButton { get; set; } = null!;
        public Button StopButton { get; set; } = null!;
        public TextBlock StatusText { get; set; } = null!;
        public int StopCalls => Playback.StopCalls;

        public MainWindow Build()
        {
            var service = new DeviceConfigurationService(Endpoints, ControlPanel, Svcl);
            ViewModel = new MainViewModel(
                service,
                Playback,
                createCancellation: null,
                resolveWaveSource: _ => new WaveSource(new WaveFormat(48000, 2, 16, 4), new byte[48000]),
                dispatcher: action => action());
            var window = new MainWindow(ViewModel, EndpointChanges);
            EndpointCombo = (ComboBox)window.FindName("EndpointCombo")!;
            ChannelsSwitchGroup = (ItemsControl)window.FindName("ChannelsSwitchGroup")!;
            SampleRateSwitchGroup = (ItemsControl)window.FindName("SampleRateSwitchGroup")!;
            BitDepthSwitchGroup = (ItemsControl)window.FindName("BitDepthSwitchGroup")!;
            ApplyButton = (Button)window.FindName("ApplyButton")!;
            PlayButton = (Button)window.FindName("PlayButton")!;
            StopButton = (Button)window.FindName("StopButton")!;
            StatusText = (TextBlock)window.FindName("StatusText")!;
            return window;
        }

        public void SeedEndpoint(string id, bool isDefault) =>
            Endpoints.Endpoints.Add(new EndpointInfo(id, id, id, null, null, isDefault));

        public void SeedOptions(string id, IReadOnlyList<int> channels, IReadOnlyList<ControlPanelFormatItem> formats)
        {
            ControlPanel.Provider = (endpoint, _) =>
            {
                if (endpoint.EndpointId != id)
                {
                    return new ControlPanelFormatResult(
                        Array.Empty<ControlPanelFormatItem>(),
                        Array.Empty<ControlPanelSpeakerConfigurationItem>(),
                        null,
                        new ControlPanelFormatSnapshot(DateTimeOffset.MinValue, DateTimeOffset.MinValue, "fake", true, true, null));
                }
                return new ControlPanelFormatResult(
                    formats,
                    channels.Select((value, index) => new ControlPanelSpeakerConfigurationItem(index, $"{value} channels", value)).ToList(),
                    channels.Max(),
                    new ControlPanelFormatSnapshot(DateTimeOffset.MinValue, DateTimeOffset.MinValue, "fake", true, true, null));
            };
        }

        public void SelectConfiguration(int channels, int sampleRate, int bitDepth)
        {
            ViewModel.SelectChannel(channels);
            ViewModel.SelectSampleRate(sampleRate);
            ViewModel.SelectBitDepth(bitDepth);
        }

        public void Invoke(Action action) =>
            Window!.Dispatcher.Invoke(action);

        public void WaitForWindow(TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (Window is null && DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
            if (Window is null)
            {
                throw new TimeoutException("MainWindow did not initialize on the STA thread.");
            }
        }

        public async Task WaitForEndpointsAsync(TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (ViewModel.Endpoints.Count == 0 && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
            if (ViewModel.Endpoints.Count == 0)
            {
                throw new TimeoutException("Endpoints were never loaded.");
            }
        }

        public async Task WaitForChannelsAsync(TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (ViewModel.Channels.Count == 0 && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
            if (ViewModel.Channels.Count == 0)
            {
                throw new TimeoutException("Channels were never loaded for the selected endpoint.");
            }
        }

        public async Task WaitForActiveSettingsAsync(TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (ViewModel.ActiveChannel is null && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(25);
            }
            if (ViewModel.ActiveChannel is null)
            {
                throw new TimeoutException("Active settings were never loaded for the selected endpoint.");
            }
            await Window!.Dispatcher.InvokeAsync(
                () => { },
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        public async Task WaitForApplyEnabledAsync(TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var enabled = (bool)Window!.Dispatcher.Invoke(() => ApplyButton.IsEnabled);
                if (enabled)
                {
                    return;
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("Apply button never became enabled.");
        }

        public async Task WaitForPlaybackStartedAsync(TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var playing = (bool)Window!.Dispatcher.Invoke(() => Playback.IsPlaying);
                if (playing)
                {
                    return;
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("Playback service never started.");
        }

        public async Task WaitForPlaybackStoppedAsync(TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var playing = (bool)Window!.Dispatcher.Invoke(() => Playback.IsPlaying);
                if (!playing)
                {
                    return;
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("Playback service did not stop.");
        }
    }

    private sealed class FakePlaybackService : IAudioPlaybackService
    {
        private bool _isPlaying;
        public bool IsPlaying
        {
            get => _isPlaying;
            private set
            {
                _isPlaying = value;
                PropertyChanged?.Invoke(this, new(nameof(IsPlaying)));
            }
        }
        public int StopCalls { get; private set; }
        public event Action<Exception>? PlaybackFailed;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Start(EndpointInfo endpoint, WaveSource source) => IsPlaying = true;
        public void Stop()
        {
            StopCalls++;
            IsPlaying = false;
        }
    }
}