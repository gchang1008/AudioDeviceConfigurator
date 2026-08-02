using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Svcl;

namespace AudioDeviceConfigurator.Application;

/// <summary>Decoupled options for a single endpoint, ready for UI binding.</summary>
public sealed record EndpointOptions(
    IReadOnlyList<int> Channels,
    IReadOnlyList<ControlPanelFormatItem> Formats)
{
    public int IndexOfFormat(ControlPanelFormatItem item)
    {
        for (var i = 0; i < Formats.Count; i++)
        {
            if (ReferenceEquals(Formats[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>Outcome returned by <see cref="DeviceConfigurationService.GetOptionsAsync"/>.</summary>
public abstract record EndpointOptionsResult
{
    private EndpointOptionsResult() { }

    public sealed record Available(EndpointOptions Options) : EndpointOptionsResult;

    public sealed record NotApplicable : EndpointOptionsResult;

    public static readonly EndpointOptionsResult NotApplicableInstance = new NotApplicable();
}

/// <summary>Status enum for a configuration switch.</summary>
public enum SwitchStatus
{
    Pass,
    FormatMismatch,
    NotApplicable,
    Cancelled,
    SystemError,
}

/// <summary>Result of an attempted configuration switch.</summary>
public sealed record SwitchResult(
    SwitchStatus Status,
    string? Message,
    bool RollbackVerified);

/// <summary>
/// Non-interactive service shared by the CLI and the GUI. Encapsulates endpoint resolution,
/// Control Panel enumeration, and the SVCL save/apply/verify/rollback transaction.
/// </summary>
public sealed class DeviceConfigurationService
{
    private readonly IAudioEndpointProvider _endpoints;
    private readonly IControlPanelFormatProvider _controlPanel;
    private readonly ISvclClient _svcl;
    private static readonly TimeSpan ControlPanelTimeout = TimeSpan.FromSeconds(30);

    public DeviceConfigurationService(
        IAudioEndpointProvider endpoints,
        IControlPanelFormatProvider controlPanel,
        ISvclClient svcl)
    {
        _endpoints = endpoints;
        _controlPanel = controlPanel;
        _svcl = svcl;
    }

    /// <summary>Returns the active render endpoints exposed by Windows Core Audio.</summary>
    public Task<IReadOnlyList<EndpointInfo>> ListEndpointsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_endpoints.GetActiveRenderEndpoints());

    /// <summary>Reads the Control Panel catalog for the endpoint and exposes selectable channels and formats.</summary>
    public Task<EndpointOptionsResult> GetOptionsAsync(EndpointInfo endpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _controlPanel.ReadDefaultFormats(endpoint, ControlPanelTimeout);

        var channels = result.SpeakerConfigurations
            .Select(item => item.Channels)
            .Where(value => value is 2 or 4 or 6 or 8)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        var formats = result.Items
            .Where(item => item.ParseStatus == ControlPanelParseStatus.Parsed
                && item.EffectiveBits.HasValue
                && item.SampleRate.HasValue)
            .ToArray();

        if (channels.Length == 0 || formats.Length == 0)
        {
            return Task.FromResult<EndpointOptionsResult>(EndpointOptionsResult.NotApplicableInstance);
        }

        return Task.FromResult<EndpointOptionsResult>(
            new EndpointOptionsResult.Available(new EndpointOptions(channels, formats)));
    }

    /// <summary>Runs the SVCL save/apply/verify/rollback transaction for the given endpoint.</summary>
    public async Task<SwitchResult> ApplyAsync(
        EndpointInfo endpoint,
        int channels,
        int formatIndex,
        CancellationToken cancellationToken)
    {
        var optionsResult = await GetOptionsAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (optionsResult is EndpointOptionsResult.NotApplicable)
        {
            return new SwitchResult(SwitchStatus.NotApplicable, null, RollbackVerified: true);
        }

        var options = ((EndpointOptionsResult.Available)optionsResult).Options;
        if (formatIndex < 0 || formatIndex >= options.Formats.Count
            || Array.IndexOf(options.Channels.ToArray(), channels) < 0)
        {
            return new SwitchResult(
                SwitchStatus.SystemError,
                "The selected settings are not available for this endpoint.",
                RollbackVerified: true);
        }

        var format = options.Formats[formatIndex];

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _svcl.VerifyInstallation();
        }
        catch (Exception ex)
        {
            return new SwitchResult(SwitchStatus.SystemError, ex.Message, RollbackVerified: true);
        }

        SavedFormat before;
        try
        {
            before = _svcl.SaveDeviceFormat(endpoint.EndpointId);
        }
        catch (Exception ex)
        {
            return new SwitchResult(SwitchStatus.SystemError, ex.Message, RollbackVerified: true);
        }

        if (before.ChannelMask == 0)
        {
            return new SwitchResult(
                SwitchStatus.SystemError,
                "The original speaker channel mask is unavailable; no settings were changed.",
                RollbackVerified: true);
        }

        var settersStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            settersStarted = true;
            _svcl.SetSpeakersConfig(endpoint.EndpointId, channels);
            cancellationToken.ThrowIfCancellationRequested();
            _svcl.SetDefaultFormat(
                endpoint.EndpointId,
                format.EffectiveBits!.Value,
                format.SampleRate!.Value,
                channels);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            var after = _svcl.SaveDeviceFormat(endpoint.EndpointId);

            if (!Matches(after, channels, format.EffectiveBits.Value, format.SampleRate.Value,
                    SvclClient.GetSpeakerMask(channels)))
            {
                return await RollBackAsync(endpoint.EndpointId, before, SwitchStatus.FormatMismatch)
                    .ConfigureAwait(false);
            }

            return new SwitchResult(SwitchStatus.Pass, null, RollbackVerified: true);
        }
        catch (OperationCanceledException)
        {
            if (!settersStarted)
            {
                return new SwitchResult(SwitchStatus.Cancelled, null, RollbackVerified: true);
            }

            return await RollBackAsync(endpoint.EndpointId, before, SwitchStatus.Cancelled)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await RollBackAsync(endpoint.EndpointId, before, SwitchStatus.SystemError, ex.Message)
                .ConfigureAwait(false);
        }
    }

    private async Task<SwitchResult> RollBackAsync(
        string deviceId,
        SavedFormat before,
        SwitchStatus failureStatus,
        string? failureMessage = null)
    {
        try
        {
            _svcl.SetSpeakersConfig(deviceId, before.ChannelMask);
            _svcl.SetDefaultFormat(deviceId, before.EffectiveBits, before.SampleRate, before.Channels);
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            var restored = _svcl.SaveDeviceFormat(deviceId);
            if (!Matches(restored, before.Channels, before.EffectiveBits, before.SampleRate, before.ChannelMask))
            {
                return new SwitchResult(
                    SwitchStatus.SystemError,
                    "Rollback failed; the original settings may not have been completely restored.",
                    RollbackVerified: false);
            }

            return new SwitchResult(failureStatus, failureMessage, RollbackVerified: true);
        }
        catch (Exception ex)
        {
            return new SwitchResult(
                SwitchStatus.SystemError,
                $"Rollback failed; the original settings may not have been completely restored: {ex.Message}",
                RollbackVerified: false);
        }
    }

    private static bool Matches(
        SavedFormat format,
        int channels,
        int effectiveBits,
        int sampleRate,
        uint channelMask) =>
        format.Channels == channels
        && format.EffectiveBits == effectiveBits
        && format.SampleRate == sampleRate
        && format.ChannelMask == channelMask;
}