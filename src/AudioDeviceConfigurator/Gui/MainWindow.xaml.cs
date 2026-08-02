using System.Windows;
using System.Windows.Controls;
using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Application;
using AudioDeviceConfigurator.Svcl;
using AudioDeviceConfigurator.Windows;

namespace AudioDeviceConfigurator.Gui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        EndpointCombo.ItemsSource = _viewModel.Endpoints;
        ChannelCombo.ItemsSource = _viewModel.Channels;
        FormatCombo.ItemsSource = _viewModel.Formats;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.LoadEndpointsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to load endpoints: {ex.Message}";
        }
    }

    private async void EndpointCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EndpointCombo.SelectedItem is not EndpointInfo endpoint)
        {
            return;
        }

        try
        {
            await _viewModel.EndpointChangedAsync(endpoint, CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to load options: {ex.Message}";
        }
    }

    private void ChannelCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelCombo.SelectedIndex < 0)
        {
            return;
        }
        _viewModel.SelectChannelIndex(ChannelCombo.SelectedIndex);
    }

    private void FormatCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FormatCombo.SelectedIndex < 0)
        {
            return;
        }
        _viewModel.SelectFormatIndex(FormatCombo.SelectedIndex);
    }

    private async void ApplyButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.ApplyAsync(CancellationToken.None);
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
        _viewModel.StopPlayback();
        base.OnClosed(e);
    }
}