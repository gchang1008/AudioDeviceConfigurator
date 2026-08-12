using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Application;
using AudioDeviceConfigurator.Audio;
using AudioDeviceConfigurator.Domain;

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
    private readonly HashSet<(int SampleRate, int BitDepth)> _formatPairs = new();
    private CancellationTokenSource? _applyCancellation;
    private string? _requestedPlaybackEndpointId;
    private EndpointInfo? _defaultEndpoint;
    private bool _followDefaultEndpoint;
    private bool _defaultPlaybackRequested;

    private static readonly int[] CommonChannels = [2, 4, 6, 8];
    private static readonly int[] CommonSampleRates =
        [32000, 44100, 48000, 88200, 96000, 176400, 192000];
    private static readonly int[] CommonBitDepths = [16, 20, 24, 32];
    private const int MinSampleRate = 32000;
    private const int MaxSampleRate = 192000;

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
        SeedCommonSwitchOptions();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<EndpointInfo> Endpoints { get; } = new();

    public ObservableCollection<EndpointOptions> EndpointOptions { get; } = new();

    public ObservableCollection<int> Channels { get; } = new();

    public ObservableCollection<ControlPanelFormatItem> Formats { get; } = new();

    public ObservableCollection<NumericSwitchOption> ChannelOptions { get; } = new();

    public ObservableCollection<NumericSwitchOption> SampleRateOptions { get; } = new();

    public ObservableCollection<NumericSwitchOption> BitDepthOptions { get; } = new();

    public EndpointInfo? SelectedEndpoint { get; private set; }

    public EndpointOptions? SelectedEndpointOptions { get; private set; }

    public int? SelectedChannelIndex { get; private set; }

    public int? SelectedFormatIndex { get; private set; }

    public int? SelectedChannel { get; private set; }

    public int? SelectedSampleRate { get; private set; }

    public int? SelectedBitDepth { get; private set; }

    public int? ActiveChannel
    {
        get => _activeChannel;
        private set => _activeChannel = value;
    }

    public int? ActiveSampleRate
    {
        get => _activeSampleRate;
        private set => _activeSampleRate = value;
    }

    public int? ActiveBitDepth
    {
        get => _activeBitDepth;
        private set => _activeBitDepth = value;
    }

    public string ActiveChannelDisplay => ActiveChannel is int ch ? $"Channel: {ch} ch" : "Channel: —";
    public string ActiveSampleRateDisplay => ActiveSampleRate is int rate ? $"Sample Rate: {rate:N0} Hz" : "Sample Rate: —";
    public string ActiveBitDepthDisplay => ActiveBitDepth is int bits ? $"Bit Depth: {bits}-bit" : "Bit Depth: —";

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
                OnPropertyChanged(nameof(CanChangeSelection));
            }
        }
    }

    public bool CanApply => SelectedEndpoint is not null
        && SelectedEndpointOptions is not null
        && SelectedChannel is not null
        && SelectedSampleRate is not null
        && SelectedBitDepth is not null
        && _formatPairs.Contains((SelectedSampleRate.Value, SelectedBitDepth.Value))
        && !IsBusy;

    public bool CanPlay => !IsBusy
        && !_playback.IsPlaying
        && (SelectedEndpoint is not null || _defaultEndpoint is not null);

    public bool CanStop => _playback.IsPlaying;

    public bool CanChangeSelection => !IsBusy;

    private int? _activeChannel;
    private int? _activeSampleRate;
    private int? _activeBitDepth;
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
            SetDefaultEndpoint(endpoints.FirstOrDefault(endpoint => endpoint.IsDefault));
            StatusMessage = endpoints.Count == 0
                ? "No active render endpoints found."
                : "Select an endpoint to load its options.";
        });
    }

    public async Task RefreshEndpointsAsync(
        IReadOnlyCollection<AudioEndpointChange> changes,
        CancellationToken cancellationToken)
    {
        var selectedId = SelectedEndpoint?.EndpointId;
        var endpoints = await _service.ListEndpointsAsync(cancellationToken);
        var refreshedSelection = selectedId is null
            ? null
            : endpoints.FirstOrDefault(endpoint =>
                string.Equals(endpoint.EndpointId, selectedId, StringComparison.OrdinalIgnoreCase));
        var selectedEndpointChanged = selectedId is not null
            && (refreshedSelection is null || changes.Any(change =>
                (change.Kind is AudioEndpointChangeKind.Added
                    or AudioEndpointChangeKind.Removed
                    or AudioEndpointChangeKind.StateChanged)
                && string.Equals(change.EndpointId, selectedId, StringComparison.OrdinalIgnoreCase)));

        _dispatch(() =>
        {
            Endpoints.Clear();
            foreach (var endpoint in endpoints)
            {
                Endpoints.Add(endpoint);
            }
            SetDefaultEndpoint(endpoints.FirstOrDefault(endpoint => endpoint.IsDefault));

            if (selectedId is null)
            {
                StatusMessage = endpoints.Count == 0
                    ? "No active render endpoints found."
                    : "Select an endpoint to load its options.";
                return;
            }

            if (!selectedEndpointChanged)
            {
                SelectedEndpoint = refreshedSelection;
                OnPropertyChanged(nameof(SelectedEndpoint));
            }
        });

        if (selectedId is null)
        {
            if (_followDefaultEndpoint)
            {
                await RefreshDefaultEndpointAsync(cancellationToken);
            }
            return;
        }

        if (!selectedEndpointChanged)
        {
            return;
        }

        var continuePlayback = selectedId is not null
            && string.Equals(
                _requestedPlaybackEndpointId,
                selectedId,
                StringComparison.OrdinalIgnoreCase);
        _playback.Stop();
        if (continuePlayback)
        {
            _requestedPlaybackEndpointId = null;
        }
        if (refreshedSelection is null)
        {
            _followDefaultEndpoint = true;
            _defaultPlaybackRequested = continuePlayback;
            _dispatch(() => ClearEndpointState(clearEndpoint: true));
            await RefreshDefaultEndpointAsync(cancellationToken);
            return;
        }

        await EndpointChangedAsync(refreshedSelection, cancellationToken);
        await LoadActiveSettingsAsync(refreshedSelection, cancellationToken);
    }

    private async Task RefreshDefaultEndpointAsync(CancellationToken cancellationToken)
    {
        var defaultEndpoint = _defaultEndpoint;
        if (defaultEndpoint is null)
        {
            _playback.Stop();
            _requestedPlaybackEndpointId = null;
            _dispatch(() =>
            {
                SetActiveSettings(null, null, null);
                StatusMessage = "Selected endpoint disconnected; no default playback endpoint is available.";
            });
            return;
        }

        string status;
        if (_defaultPlaybackRequested && _resolveWaveSource is not null)
        {
            if (!_playback.IsPlaying
                || !string.Equals(
                    _requestedPlaybackEndpointId,
                    defaultEndpoint.EndpointId,
                    StringComparison.OrdinalIgnoreCase))
            {
                _requestedPlaybackEndpointId = null;
                try
                {
                    var source = _resolveWaveSource(defaultEndpoint);
                    _playback.Start(defaultEndpoint, source);
                    _requestedPlaybackEndpointId = defaultEndpoint.EndpointId;
                    status = $"Selected endpoint disconnected; playing on default endpoint '{defaultEndpoint.FriendlyName}'.";
                }
                catch (Exception ex)
                {
                    status = $"Selected endpoint disconnected; default playback failed: {ex.Message}";
                }
            }
            else
            {
                status = $"Selected endpoint disconnected; playing on default endpoint '{defaultEndpoint.FriendlyName}'.";
            }
        }
        else
        {
            status = "Selected endpoint disconnected; showing the default endpoint's active format.";
        }

        await LoadActiveSettingsAsync(defaultEndpoint, cancellationToken);
        _dispatch(() => StatusMessage = status);
    }

    public void ReportEndpointRefreshFailure(string message)
    {
        StatusMessage = $"Failed to refresh endpoints: {message}";
    }

    public async Task LoadActiveSettingsAsync(EndpointInfo endpoint, CancellationToken cancellationToken)
    {
        var format = await Task.Run(
            () => _service.ReadCurrentFormat(endpoint.EndpointId),
            cancellationToken).ConfigureAwait(false);
        _dispatch(() =>
        {
            if (format is null)
            {
                SetActiveSettings(null, null, null);
            }
            else
            {
                SetActiveSettings(
                    format.Channels,
                    format.SampleRate,
                    format.EffectiveBits);
                SelectCurrentEndpointOptions(endpoint, format);
            }
        });
    }

    public async Task EndpointChangedAsync(EndpointInfo endpoint, CancellationToken cancellationToken)
    {
        _followDefaultEndpoint = false;
        _defaultPlaybackRequested = false;
        SelectedEndpoint = endpoint;
        OnPropertyChanged(nameof(SelectedEndpoint));
        OnPropertyChanged(nameof(CanApply));
        SelectedEndpointOptions = null;
        SelectedChannelIndex = null;
        SelectedFormatIndex = null;
        SetActiveSettings(null, null, null);
        ClearSwitchSelection();
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

    public void SelectChannel(int value)
    {
        var option = ChannelOptions.SingleOrDefault(item => item.Value == value && item.IsEnabled);
        if (option is null)
        {
            return;
        }

        SelectedChannel = value;
        SetSelected(ChannelOptions, value);
        OnPropertyChanged(nameof(SelectedChannel));
        OnPropertyChanged(nameof(CanApply));
    }

    public void SelectSampleRate(int value)
    {
        var option = SampleRateOptions.SingleOrDefault(item => item.Value == value && item.IsEnabled);
        if (option is null)
        {
            return;
        }

        SelectedSampleRate = value;
        SetSelected(SampleRateOptions, value);
        if (SelectedBitDepth is int bitDepth && !_formatPairs.Contains((value, bitDepth)))
        {
            SelectedBitDepth = null;
            SetSelected(BitDepthOptions, null);
            OnPropertyChanged(nameof(SelectedBitDepth));
        }
        RefreshFormatOptionState();
        OnPropertyChanged(nameof(SelectedSampleRate));
        OnPropertyChanged(nameof(CanApply));
    }

    public void SelectBitDepth(int value)
    {
        var option = BitDepthOptions.SingleOrDefault(item => item.Value == value && item.IsEnabled);
        if (option is null)
        {
            return;
        }

        SelectedBitDepth = value;
        SetSelected(BitDepthOptions, value);
        if (SelectedSampleRate is int sampleRate && !_formatPairs.Contains((sampleRate, value)))
        {
            SelectedSampleRate = null;
            SetSelected(SampleRateOptions, null);
            OnPropertyChanged(nameof(SelectedSampleRate));
        }
        RefreshFormatOptionState();
        OnPropertyChanged(nameof(SelectedBitDepth));
        OnPropertyChanged(nameof(CanApply));
    }

    public void SelectChannelIndex(int index)
    {
        if (SelectedChannelIndex == index)
        {
            return;
        }
        SelectedChannelIndex = Channels.Count > index ? index : null;
        if (SelectedChannelIndex is int selectedIndex)
        {
            SelectChannel(Channels[selectedIndex]);
        }
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
        if (SelectedFormatIndex is int selectedIndex)
        {
            var format = Formats[selectedIndex];
            SelectSampleRate(format.SampleRate!.Value);
            SelectBitDepth(format.EffectiveBits!.Value);
        }
        OnPropertyChanged(nameof(SelectedFormatIndex));
        OnPropertyChanged(nameof(CanApply));
    }

    public async Task<SwitchResult> ApplyAsync(CancellationToken cancellationToken)
    {
        if (SelectedEndpoint is null || SelectedEndpointOptions is null
            || SelectedChannel is null || SelectedSampleRate is null || SelectedBitDepth is null)
        {
            return new SwitchResult(SwitchStatus.SystemError, "Select endpoint, channel, sample rate, and bit depth first.", true);
        }

        var endpoint = SelectedEndpoint;
        var options = SelectedEndpointOptions;
        var channel = SelectedChannel.Value;
        var formatIndex = FindFormatIndex(options, SelectedSampleRate.Value, SelectedBitDepth.Value);
        if (formatIndex < 0)
        {
            return new SwitchResult(SwitchStatus.SystemError, "The selected sample rate and bit depth are not available.", true);
        }

        _applyCancellation?.Dispose();
        _applyCancellation = _createCancellation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _applyCancellation.Token);
        // Stop any existing playback stream before swapping the endpoint's format.
        _defaultPlaybackRequested = false;
        _requestedPlaybackEndpointId = null;
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

        if (result.Status == SwitchStatus.Pass)
        {
            _dispatch(() =>
            {
                SetActiveSettings(channel, SelectedSampleRate!.Value, SelectedBitDepth!.Value);
            });
        }

        if (result.Status == SwitchStatus.Pass && _resolveWaveSource is not null)
        {
            try
            {
                var source = _resolveWaveSource(endpoint);
                _playback.Start(endpoint, source);
                _requestedPlaybackEndpointId = endpoint.EndpointId;
                _dispatch(() => StatusMessage = "Switch verified; playing test audio.");
            }
            catch (Exception ex)
            {
                _dispatch(() => StatusMessage = $"Switch verified, but playback failed: {ex.Message}");
            }
        }

        return result;
    }

    public void Play()
    {
        if (!CanPlay || _resolveWaveSource is null)
        {
            return;
        }

        var endpoint = SelectedEndpoint ?? _defaultEndpoint;
        if (endpoint is null)
        {
            return;
        }

        try
        {
            var source = _resolveWaveSource(endpoint);
            _playback.Start(endpoint, source);
            _requestedPlaybackEndpointId = endpoint.EndpointId;
            if (SelectedEndpoint is null)
            {
                _followDefaultEndpoint = true;
                _defaultPlaybackRequested = true;
                StatusMessage = $"Playing test audio on default endpoint '{endpoint.FriendlyName}'.";
            }
            else
            {
                _defaultPlaybackRequested = false;
                StatusMessage = "Playing test audio.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Playback failed: {ex.Message}";
        }
    }

    public void StopPlayback()
    {
        _defaultPlaybackRequested = false;
        _requestedPlaybackEndpointId = null;
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
                BuildSwitchOptions(available.Options);
                StatusMessage = Channels.Count == 0 || Formats.Count == 0
                    ? "Endpoint has no selectable options."
                    : "Select channel and format, then Apply.";
                break;
            case EndpointOptionsResult.NotApplicable:
                StatusMessage = "Endpoint has no selectable options.";
                break;
        }
    }

    private void BuildSwitchOptions(EndpointOptions options)
    {
        ChannelOptions.Clear();
        SampleRateOptions.Clear();
        BitDepthOptions.Clear();
        _formatPairs.Clear();

        foreach (var format in options.Formats)
        {
            _formatPairs.Add((format.SampleRate!.Value, format.EffectiveBits!.Value));
        }

        foreach (var value in CommonChannels)
        {
            ChannelOptions.Add(new NumericSwitchOption(value, $"{value} ch", $"ChannelOption-{value}")
            {
                IsEnabled = options.Channels.Contains(value),
            });
        }

        foreach (var value in CommonSampleRates
                     .Concat(_formatPairs.Select(pair => pair.SampleRate))
                     .Distinct()
                     .Where(value => value >= MinSampleRate && value <= MaxSampleRate)
                     .OrderBy(value => value))
        {
            SampleRateOptions.Add(new NumericSwitchOption(value, $"{value:N0} Hz", $"SampleRateOption-{value}"));
        }

        foreach (var value in CommonBitDepths)
        {
            BitDepthOptions.Add(new NumericSwitchOption(value, $"{value}-bit", $"BitDepthOption-{value}"));
        }

        RefreshFormatOptionState();
    }

    private void SeedCommonSwitchOptions()
    {
        ChannelOptions.Clear();
        SampleRateOptions.Clear();
        BitDepthOptions.Clear();

        foreach (var value in CommonChannels)
        {
            ChannelOptions.Add(new NumericSwitchOption(value, $"{value} ch", $"ChannelOption-{value}"));
        }

        foreach (var value in CommonSampleRates)
        {
            SampleRateOptions.Add(new NumericSwitchOption(value, $"{value:N0} Hz", $"SampleRateOption-{value}"));
        }

        foreach (var value in CommonBitDepths)
        {
            BitDepthOptions.Add(new NumericSwitchOption(value, $"{value}-bit", $"BitDepthOption-{value}"));
        }
    }

    private void RefreshFormatOptionState()
    {
        foreach (var option in SampleRateOptions)
        {
            option.IsEnabled = _formatPairs.Any(pair => pair.SampleRate == option.Value);
        }
        foreach (var option in BitDepthOptions)
        {
            option.IsEnabled = SelectedSampleRate is int sampleRate
                ? _formatPairs.Contains((sampleRate, option.Value))
                : _formatPairs.Any(pair => pair.BitDepth == option.Value);
        }
    }

    private void SetDefaultEndpoint(EndpointInfo? endpoint)
    {
        _defaultEndpoint = endpoint;
        OnPropertyChanged(nameof(CanPlay));
    }

    private void ClearEndpointState(bool clearEndpoint)
    {
        if (clearEndpoint)
        {
            SelectedEndpoint = null;
            OnPropertyChanged(nameof(SelectedEndpoint));
        }

        SelectedEndpointOptions = null;
        SelectedChannelIndex = null;
        SelectedFormatIndex = null;
        EndpointOptions.Clear();
        Channels.Clear();
        Formats.Clear();
        _formatPairs.Clear();
        SetActiveSettings(null, null, null);
        ClearSwitchSelection();
        SeedCommonSwitchOptions();
        OnPropertyChanged(nameof(SelectedEndpointOptions));
        OnPropertyChanged(nameof(SelectedChannelIndex));
        OnPropertyChanged(nameof(SelectedFormatIndex));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanPlay));
    }

    private void SelectCurrentEndpointOptions(EndpointInfo endpoint, SavedFormat format)
    {
        if (!string.Equals(
                SelectedEndpoint?.EndpointId,
                endpoint.EndpointId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ClearSwitchSelection();
        RefreshFormatOptionState();

        if (ChannelOptions.Any(option =>
                option.Value == format.Channels && option.IsEnabled))
        {
            SelectChannel(format.Channels);
        }

        if (_formatPairs.Contains((format.SampleRate, format.EffectiveBits)))
        {
            SelectSampleRate(format.SampleRate);
            SelectBitDepth(format.EffectiveBits);
        }
    }

    private void ClearSwitchSelection()
    {
        SelectedChannel = null;
        SelectedSampleRate = null;
        SelectedBitDepth = null;
        SetSelected(ChannelOptions, null);
        SetSelected(SampleRateOptions, null);
        SetSelected(BitDepthOptions, null);
        OnPropertyChanged(nameof(SelectedChannel));
        OnPropertyChanged(nameof(SelectedSampleRate));
        OnPropertyChanged(nameof(SelectedBitDepth));
        OnPropertyChanged(nameof(CanApply));
    }

    private static void SetSelected(IEnumerable<NumericSwitchOption> options, int? value)
    {
        foreach (var option in options)
        {
            option.IsSelected = option.Value == value;
        }
    }

    private static int FindFormatIndex(EndpointOptions options, int sampleRate, int bitDepth)
    {
        for (var index = 0; index < options.Formats.Count; index++)
        {
            var format = options.Formats[index];
            if (format.SampleRate == sampleRate && format.EffectiveBits == bitDepth)
            {
                return index;
            }
        }
        return -1;
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

    private void SetActiveSettings(int? channel, int? sampleRate, int? bitDepth)
    {
        if (Set(ref _activeChannel, channel))
        {
            OnPropertyChanged(nameof(ActiveChannel));
            OnPropertyChanged(nameof(ActiveChannelDisplay));
        }
        if (Set(ref _activeSampleRate, sampleRate))
        {
            OnPropertyChanged(nameof(ActiveSampleRate));
            OnPropertyChanged(nameof(ActiveSampleRateDisplay));
        }
        if (Set(ref _activeBitDepth, bitDepth))
        {
            OnPropertyChanged(nameof(ActiveBitDepth));
            OnPropertyChanged(nameof(ActiveBitDepthDisplay));
        }
    }

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