using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Application;
using AudioDeviceConfigurator.Svcl;
using AudioDeviceConfigurator.Windows;

namespace AudioDeviceConfigurator.Gui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IAudioEndpointChangeMonitor? _endpointChanges;
    private readonly DispatcherTimer _endpointRefreshTimer;
    private readonly List<AudioEndpointChange> _pendingEndpointChanges = [];
    private readonly CancellationTokenSource _windowCts = new();
    private bool _endpointRefreshInProgress;
    private bool _suppressEndpointSelectionChanged;
    private bool _closed;

    public MainWindow(MainViewModel viewModel)
        : this(viewModel, null)
    {
    }

    public MainWindow(
        MainViewModel viewModel,
        IAudioEndpointChangeMonitor? endpointChanges)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _endpointChanges = endpointChanges;
        _endpointRefreshTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(350),
            DispatcherPriority.Background,
            EndpointRefreshTimer_OnTick,
            Dispatcher);
        _endpointRefreshTimer.Stop();
        if (_endpointChanges is not null)
        {
            _endpointChanges.Changed += EndpointChanges_OnChanged;
        }
        EndpointCombo.ItemsSource = _viewModel.Endpoints;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // OnLoaded itself can't be cancelled mid-flight without aborting the
            // startup sequence; cancellation is observed in selection-changed
            // and apply paths instead. The window token is checked after the
            // initial endpoint load to avoid touching UI after close.
            await _viewModel.LoadEndpointsAsync(CancellationToken.None);
            if (_closed)
            {
                return;
            }

            var defaultEndpoint = _viewModel.Endpoints.FirstOrDefault(item => item.IsDefault)
                ?? _viewModel.Endpoints.FirstOrDefault();
            if (defaultEndpoint is not null)
            {
                EndpointCombo.SelectedItem = defaultEndpoint;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to load endpoints: {ex.Message}";
        }
    }

    private void EndpointChanges_OnChanged(object? sender, AudioEndpointChange change)
    {
        if (_closed || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_closed)
                {
                    return;
                }
                _pendingEndpointChanges.Add(change);
                _endpointRefreshTimer.Stop();
                _endpointRefreshTimer.Start();
            });
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async void EndpointRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        _endpointRefreshTimer.Stop();
        if (_closed || _pendingEndpointChanges.Count == 0)
        {
            return;
        }
        if (_endpointRefreshInProgress || _viewModel.IsBusy)
        {
            _endpointRefreshTimer.Start();
            return;
        }

        var changes = _pendingEndpointChanges.ToArray();
        _pendingEndpointChanges.Clear();
        _endpointRefreshInProgress = true;
        _suppressEndpointSelectionChanged = true;
        try
        {
            await _viewModel.RefreshEndpointsAsync(changes, _windowCts.Token);
            if (_closed)
            {
                return;
            }

            EndpointCombo.SelectedItem = _viewModel.SelectedEndpoint;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _viewModel.ReportEndpointRefreshFailure(ex.Message);
        }
        finally
        {
            _suppressEndpointSelectionChanged = false;
            _endpointRefreshInProgress = false;
            if (!_closed && _pendingEndpointChanges.Count > 0)
            {
                _endpointRefreshTimer.Start();
            }
        }
    }

    private async void EndpointCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_closed || _suppressEndpointSelectionChanged)
        {
            return;
        }
        if (EndpointCombo.SelectedItem is not EndpointInfo endpoint)
        {
            return;
        }

        try
        {
            await _viewModel.EndpointChangedAsync(endpoint, _windowCts.Token);
            if (_closed)
            {
                return;
            }

            await _viewModel.LoadActiveSettingsAsync(endpoint, _windowCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to load options: {ex.Message}";
        }
    }

    private void ChannelOption_OnChecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NumericSwitchOption option })
        {
            _viewModel.SelectChannel(option.Value);
        }
    }

    private void SampleRateOption_OnChecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NumericSwitchOption option })
        {
            _viewModel.SelectSampleRate(option.Value);
        }
    }

    private void BitDepthOption_OnChecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NumericSwitchOption option })
        {
            _viewModel.SelectBitDepth(option.Value);
        }
    }

    private async void ApplyButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.ApplyAsync(_windowCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Apply failed: {ex.Message}";
        }
    }

    private void PlayButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.Play();
    }

    private void StopButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.StopPlayback();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _windowCts.Cancel();
        _endpointRefreshTimer.Stop();
        _pendingEndpointChanges.Clear();
        if (_endpointChanges is not null)
        {
            _endpointChanges.Changed -= EndpointChanges_OnChanged;
        }
        _viewModel.StopPlayback();
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}