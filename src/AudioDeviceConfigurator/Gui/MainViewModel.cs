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
    private readonly HashSet<(int SampleRate, int BitDepth)> _formatPairs = new();
    private CancellationTokenSource? _applyCancellation;

    private static readonly int[] CommonChannels = [2, 4, 6, 8];
    private static readonly int[] CommonSampleRates =
        [8000, 11025, 16000, 22050, 32000, 44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000];
    private static readonly int[] CommonBitDepths = [16, 20, 24, 32];

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
        && !IsBusy
        && !_playback.IsPlaying;

    public bool CanPlay => SelectedEndpoint is not null
        && !IsBusy
        && !_playback.IsPlaying
        && HasVerifiedSwitch;

    public bool CanStop => _playback.IsPlaying;

    public bool CanChangeSelection => !IsBusy;

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
            _dispatch(() => HasVerifiedSwitch = true);
        }

        if (result.Status == SwitchStatus.Pass && _resolveWaveSource is not null)
        {
            try
            {
                var source = _resolveWaveSource(endpoint);
                _playback.Start(endpoint, source);
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