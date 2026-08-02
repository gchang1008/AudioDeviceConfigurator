using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Application;
using AudioDeviceConfigurator.Audio;

namespace AudioDeviceConfigurator.Gui;

/// <summary>
/// Pure logic for the WPF main window. Tested without spinning up WPF so the binding
/// contracts, locking rules, and status transitions stay covered.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
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
        _playback.PropertyChanged += (_, _) => RefreshPlaybackState();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<EndpointInfo> Endpoints { get; } = new();

    public ObservableCollection<EndpointOptions> EndpointOptions { get; } = new();

    public ObservableCollection<int> Channels { get; } = new();

    public ObservableCollection<ControlPanelFormatItem> Formats { get; } = new();

    public EndpointInfo? SelectedEndpoint { get; private set; }

    public EndpointOptions? SelectedEndpointOptions { get; private set; }

    public int? SelectedChannelIndex { get; private set; }

    public int? SelectedFormatIndex { get; private set; }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanApply));
                OnPropertyChanged(nameof(CanPlay));
                OnPropertyChanged(nameof(CanStop));
            }
        }
    }

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

    private bool HasVerifiedSwitch
    {
        get => _hasVerifiedSwitch;
        set
        {
            if (Set(ref _hasVerifiedSwitch, value))
            {
                OnPropertyChanged(nameof(CanApply));
                OnPropertyChanged(nameof(CanPlay));
            }
        }
    }

    private bool _hasVerifiedSwitch;
    private string _statusMessage = "Loading endpoints...";
    private bool _isBusy;

    public async Task LoadEndpointsAsync(CancellationToken cancellationToken)
    {
        var endpoints = await _service.ListEndpointsAsync(cancellationToken);
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
        OnPropertyChanged(nameof(SelectedEndpoint));
        OnPropertyChanged(nameof(CanApply));
        SelectedEndpointOptions = null;
        SelectedChannelIndex = null;
        SelectedFormatIndex = null;
        OnPropertyChanged(nameof(SelectedChannelIndex));
        OnPropertyChanged(nameof(SelectedFormatIndex));
        SetBusy(true);
        try
        {
            var result = await _service.GetOptionsAsync(endpoint, cancellationToken);
            _dispatch(() => PopulateOptions(result));
        }
        finally
        {
            SetBusy(false);
        }
    }

    public void SelectChannelIndex(int index)
    {
        if (SelectedChannelIndex == index)
        {
            return;
        }
        SelectedChannelIndex = Channels.Count > index ? index : null;
        OnPropertyChanged(nameof(SelectedChannelIndex));
        OnPropertyChanged(nameof(CanApply));
    }

    public void SelectFormatIndex(int index)
    {
        if (SelectedFormatIndex == index)
        {
            return;
        }
        SelectedFormatIndex = Formats.Count > index ? index : null;
        OnPropertyChanged(nameof(SelectedFormatIndex));
        OnPropertyChanged(nameof(CanApply));
    }

    public async Task<SwitchResult> ApplyAsync(CancellationToken cancellationToken)
    {
        if (SelectedEndpoint is null || SelectedEndpointOptions is null
            || SelectedChannelIndex is null || SelectedFormatIndex is null)
        {
            return new SwitchResult(SwitchStatus.SystemError, "Select endpoint, channel, and format first.", true);
        }

        var endpoint = SelectedEndpoint;
        var options = SelectedEndpointOptions;
        var channels = options.Channels;
        var formats = options.Formats;
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
            result = await _service.ApplyAsync(
                endpoint,
                options,
                channel,
                formatIndex,
                linked.Token);
        }
        finally
        {
            SetBusy(false);
        }

        _dispatch(() => ApplyStatusMessage(result));

        if (result.Status == SwitchStatus.Pass && _resolveWaveSource is not null)
        {
            try
            {
                var source = _resolveWaveSource(endpoint);
                _playback.Start(endpoint, source);
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

    public void Play()
    {
        if (!CanPlay || SelectedEndpoint is null || _resolveWaveSource is null)
        {
            return;
        }

        try
        {
            var source = _resolveWaveSource(SelectedEndpoint);
            _playback.Start(SelectedEndpoint, source);
            StatusMessage = "Playing test audio.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Playback failed: {ex.Message}";
        }
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
        OnPropertyChanged(nameof(SelectedEndpointOptions));
        OnPropertyChanged(nameof(SelectedChannelIndex));
        OnPropertyChanged(nameof(SelectedFormatIndex));

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

    private void SetBusy(bool value) => IsBusy = value;

    private void RefreshPlaybackState()
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanStop));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}