using System.Collections.ObjectModel;
using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Application;
using AudioDeviceConfigurator.Audio;

namespace AudioDeviceConfigurator.Gui;

/// <summary>
/// Pure logic for the WPF main window. Tested without spinning up WPF so the binding
/// contracts, locking rules, and status transitions stay covered.
/// </summary>
public sealed class MainViewModel
{
    private readonly DeviceConfigurationService _service;
    private readonly IAudioPlaybackService _playback;
    private readonly Func<CancellationTokenSource> _createCancellation;
    private readonly Func<EndpointInfo, WaveSource>? _resolveWaveSource;
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _applyCancellation;

    public MainViewModel(
        DeviceConfigurationService service,
        IAudioPlaybackService playback,
        Func<CancellationTokenSource>? createCancellation = null,
        Func<EndpointInfo, WaveSource>? resolveWaveSource = null,
        Action<Action>? dispatcher = null)
    {
        _service = service;
        _playback = playback;
        _createCancellation = createCancellation ?? (() => new CancellationTokenSource());
        _resolveWaveSource = resolveWaveSource;
        _dispatch = dispatcher ?? (action => action());
        _playback.PlaybackFailed += OnPlaybackFailed;
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
        && !IsBusy
        && !_playback.IsPlaying;

    public bool CanPlay => SelectedEndpoint is not null
        && !IsBusy
        && !_playback.IsPlaying
        && HasVerifiedSwitch;

    public bool CanStop => _playback.IsPlaying;

    private bool HasVerifiedSwitch { get; set; }

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
        // Stop any existing playback stream before swapping the endpoint's format.
        _playback.Stop();
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

        if (result.Status == SwitchStatus.Pass && _resolveWaveSource is not null && SelectedEndpoint is not null)
        {
            try
            {
                var source = _resolveWaveSource(SelectedEndpoint);
                _playback.Start(SelectedEndpoint, source);
                _dispatch(() =>
                {
                    HasVerifiedSwitch = true;
                    StatusMessage = "Switch verified; playing test audio.";
                });
            }
            catch (Exception ex)
            {
                _dispatch(() => StatusMessage = $"Switch verified, but playback failed: {ex.Message}");
            }
        }
        else if (result.Status == SwitchStatus.Pass)
        {
            _dispatch(() => HasVerifiedSwitch = true);
        }

        return result;
    }

    public void StopPlayback()
    {
        _playback.Stop();
    }

    private void OnPlaybackFailed(Exception ex)
    {
        _dispatch(() => StatusMessage = $"Playback error: {ex.Message}");
    }

    public void NotePlaybackStopped()
    {
        _dispatch(() => { /* binding re-evaluates CanPlay/CanStop on next refresh */ });
    }

    public void NotePlaybackStarted()
    {
        _dispatch(() => { /* playback state is owned by the service */ });
    }

    private void PopulateOptions(EndpointOptionsResult result)
    {
        EndpointOptions.Clear();
        Channels.Clear();
        Formats.Clear();
        SelectedEndpointOptions = null;
        SelectedChannelIndex = null;
        SelectedFormatIndex = null;
        HasVerifiedSwitch = false;

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
                break;
            case SwitchStatus.FormatMismatch:
                StatusMessage = result.RollbackVerified
                    ? "Switch failed; original settings restored."
                    : "Switch failed; rollback incomplete.";
                break;
            case SwitchStatus.Cancelled:
                StatusMessage = "Cancelled.";
                break;
            case SwitchStatus.NotApplicable:
                StatusMessage = "Endpoint has no selectable options.";
                break;
            case SwitchStatus.SystemError:
                StatusMessage = result.Message ?? "Unknown error.";
                break;
        }
    }

    private void SetBusy(bool value)
    {
        _dispatch(() => IsBusy = value);
    }
}