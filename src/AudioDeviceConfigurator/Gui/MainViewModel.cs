using System.Collections.ObjectModel;
using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Application;

namespace AudioDeviceConfigurator.Gui;

/// <summary>
/// Pure logic for the WPF main window. Tested without spinning up WPF so the binding
/// contracts, locking rules, and status transitions stay covered.
/// </summary>
public sealed class MainViewModel
{
    private readonly DeviceConfigurationService _service;
    private readonly Func<CancellationTokenSource> _createCancellation;
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _applyCancellation;

    public MainViewModel(
        DeviceConfigurationService service,
        Func<CancellationTokenSource>? createCancellation = null,
        Action<Action>? dispatcher = null)
    {
        _service = service;
        _createCancellation = createCancellation ?? (() => new CancellationTokenSource());
        _dispatch = dispatcher ?? (action => action());
    }

    public ObservableCollection<EndpointInfo> Endpoints { get; } = new();

    public ObservableCollection<EndpointOptions> EndpointOptions { get; } = new();

    public ObservableCollection<int> Channels { get; } = new();

    public ObservableCollection<ControlPanelFormatItem> Formats { get; } = new();

    public EndpointInfo? SelectedEndpoint { get; private set; }

    public EndpointOptions? SelectedEndpointOptions { get; private set; }

    public int? SelectedChannelIndex { get; private set; }

    public int? SelectedFormatIndex { get; private set; }

    public string StatusMessage { get; private set; } = "Loading endpoints...";

    public bool IsBusy { get; private set; }

    public bool CanApply => SelectedEndpoint is not null
        && SelectedChannelIndex is not null
        && SelectedFormatIndex is not null
        && !IsBusy;

    public bool CanPlay { get; private set; }

    public bool CanStop { get; private set; }

    public async Task LoadEndpointsAsync(CancellationToken cancellationToken)
    {
        var endpoints = await _service.ListEndpointsAsync(cancellationToken).ConfigureAwait(false);
        _dispatch(() =>
        {
            Endpoints.Clear();
            foreach (var endpoint in endpoints)
            {
                Endpoints.Add(endpoint);
            }
            StatusMessage = endpoints.Count == 0
                ? "No active render endpoints found."
                : "Select an endpoint to load its options.";
        });
    }

    public async Task EndpointChangedAsync(EndpointInfo endpoint, CancellationToken cancellationToken)
    {
        SelectedEndpoint = endpoint;
        SelectedEndpointOptions = null;
        SelectedChannelIndex = null;
        SelectedFormatIndex = null;
        SetBusy(true);
        try
        {
            var result = await _service.GetOptionsAsync(endpoint, cancellationToken).ConfigureAwait(false);
            _dispatch(() => PopulateOptions(result));
        }
        finally
        {
            SetBusy(false);
        }
    }

    public void SelectChannelIndex(int index) => SelectedChannelIndex = Channels.Count > index ? index : null;

    public void SelectFormatIndex(int index) => SelectedFormatIndex = Formats.Count > index ? index : null;

    public async Task<SwitchResult> ApplyAsync(CancellationToken cancellationToken)
    {
        if (SelectedEndpoint is null || SelectedEndpointOptions is null
            || SelectedChannelIndex is null || SelectedFormatIndex is null)
        {
            return new SwitchResult(SwitchStatus.SystemError, "Select endpoint, channel, and format first.", true);
        }

        var channels = SelectedEndpointOptions.Channels;
        var formats = SelectedEndpointOptions.Formats;
        var channel = channels[SelectedChannelIndex.Value];
        var formatIndex = SelectedFormatIndex.Value;

        _applyCancellation?.Dispose();
        _applyCancellation = _createCancellation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _applyCancellation.Token);
        SetBusy(true);
        SwitchResult result;
        try
        {
            result = await _service.ApplyAsync(SelectedEndpoint, channel, formatIndex, linked.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            SetBusy(false);
        }

        _dispatch(() => ApplyStatusMessage(result));
        return result;
    }

    public void NotePlaybackStopped()
    {
        _dispatch(() =>
        {
            CanPlay = SelectedEndpoint is not null && !IsBusy;
            CanStop = false;
        });
    }

    public void NotePlaybackStarted()
    {
        _dispatch(() =>
        {
            CanPlay = false;
            CanStop = true;
        });
    }

    private void PopulateOptions(EndpointOptionsResult result)
    {
        EndpointOptions.Clear();
        Channels.Clear();
        Formats.Clear();
        SelectedEndpointOptions = null;
        SelectedChannelIndex = null;
        SelectedFormatIndex = null;
        CanPlay = false;
        CanStop = false;

        switch (result)
        {
            case EndpointOptionsResult.Available available:
                SelectedEndpointOptions = available.Options;
                EndpointOptions.Add(available.Options);
                foreach (var channel in available.Options.Channels)
                {
                    Channels.Add(channel);
                }
                foreach (var format in available.Options.Formats)
                {
                    Formats.Add(format);
                }
                StatusMessage = Channels.Count == 0 || Formats.Count == 0
                    ? "Endpoint has no selectable options."
                    : "Select channel and format, then Apply.";
                break;
            case EndpointOptionsResult.NotApplicable:
                StatusMessage = "Endpoint has no selectable options.";
                break;
        }
    }

    private void ApplyStatusMessage(SwitchResult result)
    {
        switch (result.Status)
        {
            case SwitchStatus.Pass:
                StatusMessage = "Switch completed and verified.";
                CanPlay = !IsBusy;
                CanStop = false;
                break;
            case SwitchStatus.FormatMismatch:
                StatusMessage = result.RollbackVerified
                    ? "Switch failed; original settings restored."
                    : "Switch failed; rollback incomplete.";
                CanPlay = false;
                CanStop = false;
                break;
            case SwitchStatus.Cancelled:
                StatusMessage = "Cancelled.";
                CanPlay = false;
                CanStop = false;
                break;
            case SwitchStatus.NotApplicable:
                StatusMessage = "Endpoint has no selectable options.";
                CanPlay = false;
                CanStop = false;
                break;
            case SwitchStatus.SystemError:
                StatusMessage = result.Message ?? "Unknown error.";
                CanPlay = false;
                CanStop = false;
                break;
        }
    }

    private void SetBusy(bool value)
    {
        _dispatch(() => IsBusy = value);
    }
}