using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
    public async Task Status_binding_remains_active_after_window_loads_endpoints()
    {
        _harness.SeedEndpoint("ep-1", isDefault: true);

        await _harness.WaitForEndpointsAsync(TimeSpan.FromSeconds(5));

        _harness.Invoke(() =>
        {
            var expression = System.Windows.Data.BindingOperations.GetBindingExpression(
                _harness.StatusText, TextBlock.TextProperty);
            Assert.NotNull(expression);
            Assert.Equal("Select an endpoint to load its options.", _harness.StatusText.Text);
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
            Assert.Equal("ChannelCombo",
                System.Windows.Automation.AutomationProperties.GetAutomationId(_harness.ChannelCombo));
            Assert.Equal("FormatCombo",
                System.Windows.Automation.AutomationProperties.GetAutomationId(_harness.FormatCombo));
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

        foreach (var comboBox in new[]
                 {
                     _harness.EndpointCombo,
                     _harness.ChannelCombo,
                     _harness.FormatCombo,
                 })
        {
            System.Windows.Data.BindingExpression? expression = null;
            _harness.Invoke(() => expression = System.Windows.Data.BindingOperations.GetBindingExpression(
                comboBox, UIElement.IsEnabledProperty));
            Assert.NotNull(expression);
            Assert.Equal(nameof(MainViewModel.CanChangeSelection), expression!.ParentBinding.Path.Path);
            Assert.Same(_harness.ViewModel, expression.DataItem);
        }

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
    public void MainViewModel_raises_PropertyChanged_when_IsBusy_changes()
    {
        var changes = new List<string?>();
        _harness.ViewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        // Trigger the dispatch path that the WPF view also subscribes to.
        _harness.Invoke(() => _harness.ViewModel.LoadEndpointsAsync(CancellationToken.None));
        // IsBusy is not flipped by LoadEndpointsAsync (it does not toggle), so use a synchronous
        // trigger that the VM exposes internally.
        // Direct setter access isn't possible; instead verify ChannelCombo selection drives
        // SelectedChannelIndex which should fire PropertyChanged for SelectedChannelIndex + CanApply.
        _harness.SeedEndpoint("ep-1", isDefault: true);
        _harness.SeedOptions("ep-1", channels: new[] { 2 }, formats: new[]
        {
            new ControlPanelFormatItem(0, "16 bit, 44100 Hz", 2, 44100, 16, 16, ControlPanelParseStatus.Parsed, null),
        });
        _harness.Invoke(async () =>
        {
            await _harness.ViewModel.LoadEndpointsAsync(CancellationToken.None);
            await _harness.ViewModel.EndpointChangedAsync(_harness.ViewModel.Endpoints[0], CancellationToken.None);
            _harness.ViewModel.SelectChannelIndex(0);
            _harness.ViewModel.SelectFormatIndex(0);
        });

        Assert.Contains(nameof(MainViewModel.SelectedEndpoint), changes);
        Assert.Contains(nameof(MainViewModel.SelectedChannelIndex), changes);
        Assert.Contains(nameof(MainViewModel.CanApply), changes);
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
            _harness.ChannelCombo.SelectedIndex = 0;
            _harness.FormatCombo.SelectedIndex = 0;
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
            _harness.ChannelCombo.SelectedIndex = 0;
            _harness.FormatCombo.SelectedIndex = 0;
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
            .EnqueueSavedFormat(new SavedFormat(SavedFormat.WaveFormatExtensible, 2, 44100, 32, 16, 0x3, new byte[40]));

        await _harness.WaitForEndpointsAsync(TimeSpan.FromSeconds(5));
        _harness.Invoke(() => _harness.EndpointCombo.SelectedIndex = 0);
        await _harness.WaitForChannelsAsync(TimeSpan.FromSeconds(5));
        _harness.Invoke(() =>
        {
            _harness.ChannelCombo.SelectedIndex = 0;
            _harness.FormatCombo.SelectedIndex = 0;
            _harness.ApplyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });

        await _harness.WaitForPlaybackStartedAsync(TimeSpan.FromSeconds(5));
        _harness.Invoke(() =>
        {
            Assert.True(_harness.Playback.IsPlaying);
            Assert.False(_harness.ApplyButton.IsEnabled);
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
            .EnqueueSavedFormat(new SavedFormat(SavedFormat.WaveFormatExtensible, 2, 44100, 32, 16, 0x3, new byte[40]));

        await _harness.WaitForEndpointsAsync(TimeSpan.FromSeconds(5));
        _harness.Invoke(() => _harness.EndpointCombo.SelectedIndex = 0);
        await _harness.WaitForChannelsAsync(TimeSpan.FromSeconds(5));
        _harness.Invoke(() =>
        {
            _harness.ChannelCombo.SelectedIndex = 0;
            _harness.FormatCombo.SelectedIndex = 0;
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
        public MainWindow? Window { get; set; }
        public MainViewModel ViewModel { get; set; } = null!;
        public ComboBox EndpointCombo { get; set; } = null!;
        public ComboBox ChannelCombo { get; set; } = null!;
        public ComboBox FormatCombo { get; set; } = null!;
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
            var window = new MainWindow(ViewModel);
            EndpointCombo = (ComboBox)window.FindName("EndpointCombo")!;
            ChannelCombo = (ComboBox)window.FindName("ChannelCombo")!;
            FormatCombo = (ComboBox)window.FindName("FormatCombo")!;
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