using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Cli;
using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Svcl;

namespace AudioDeviceConfigurator.Application;

/// <summary>Everything the application needs from the outside world.</summary>
public sealed record AppEnvironment(
    IAudioEndpointProvider Endpoints,
    IFileSystem FileSystem,
    IClock Clock,
    IConsole Console,
    IControlPanelFormatProvider ControlPanelFormats,
    ISvclClient Svcl);

/// <summary>Drives the interactive CLI on top of the shared device configuration service.</summary>
public sealed class ValidationRunner(AppEnvironment env, CancellationToken cancellationToken = default)
{
    private ControlPanelFormatResult? _controlPanelFormats;

    public ExitCode Run(CliOptions options)
    {
        if (options.Error is not null)
        {
            env.Console.WriteError(options.Error);
            env.Console.WriteLine();
            env.Console.WriteLine(CliOptions.HelpText);
            return ExitCode.SystemError;
        }

        if (options.ShowHelp)
        {
            env.Console.WriteLine(CliOptions.HelpText);
            return ExitCode.Pass;
        }

        if (options.ListDevices)
        {
            return RunList();
        }

        var status = ExitCode.Pass;
        try
        {
            status = Execute(options);
        }
        catch (OperationCanceledException)
        {
            env.Console.WriteLine();
            env.Console.WriteLine("Cancelled by user (Ctrl+C).");
            status = ExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            env.Console.WriteError($"ERROR: {ex.Message}");
            status = ExitCode.SystemError;
        }

        if (_controlPanelFormats is not null)
        {
            PrintControlPanelOptions(_controlPanelFormats);
        }

        env.Console.WriteLine();
        env.Console.WriteLine($"Overall status: {DescribeStatus(status)}");
        env.Console.WriteLine($"Exit code {(int)status}: {DescribeExitCode(status)}");
        return status;
    }

    private ExitCode RunList()
    {
        try
        {
            env.Console.WriteLine("Active render endpoints:");
            foreach (var endpoint in env.Endpoints.GetActiveRenderEndpoints())
            {
                var marker = endpoint.IsDefault ? " (default)" : "";
                env.Console.WriteLine($"  {endpoint.FriendlyName}{marker}");
                env.Console.WriteLine($"    Endpoint ID : {endpoint.EndpointId}");
                env.Console.WriteLine($"    Description : {endpoint.DeviceDescription}");
            }

            return ExitCode.Pass;
        }
        catch (Exception ex)
        {
            env.Console.WriteError($"ERROR: {ex.Message}");
            return ExitCode.SystemError;
        }
    }

    private ExitCode Execute(CliOptions options)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var endpoints = env.Endpoints.GetActiveRenderEndpoints();
        var endpoint = ResolveEndpoint(endpoints, options.DeviceId);

        env.Console.WriteLine($"Endpoint : {endpoint.FriendlyName}");
        env.Console.WriteLine($"           {endpoint.EndpointId}");
        env.Console.WriteLine();

        var service = new DeviceConfigurationService(env.Endpoints, env.ControlPanelFormats, env.Svcl);
        var catalog = service.GetOptionsAsync(endpoint, cancellationToken).GetAwaiter().GetResult();
        if (catalog is EndpointOptionsResult.NotApplicable)
        {
            return ExitCode.NotApplicable;
        }

        var options_available = ((EndpointOptionsResult.Available)catalog).Options;
        _controlPanelFormats = env.ControlPanelFormats.ReadDefaultFormats(
            endpoint, TimeSpan.FromSeconds(30));

        var channels = options_available.Channels;
        var formats = options_available.Formats;

        var selectedChannels = Select("speaker channel count", channels, value => $"{value} channels");
        var selectedFormat = Select("audio format", formats, item => item.DisplayText);

        env.Console.WriteLine();
        env.Console.WriteLine("Selected settings:");
        env.Console.WriteLine($"  Speaker channels : {selectedChannels}");
        env.Console.WriteLine($"  Audio format     : {selectedFormat.DisplayText}");
        env.Console.WriteLine("Apply these settings? [Y/n]");
        var confirmation = env.Console.ReadLine()?.Trim();
        if (confirmation is null
            || (confirmation.Length > 0
                && !string.Equals(confirmation, "Y", StringComparison.OrdinalIgnoreCase)))
        {
            throw new OperationCanceledException();
        }

        var formatIndex = options_available.IndexOfFormat(selectedFormat);
        var outcome = service.ApplyAsync(endpoint, selectedChannels, formatIndex, cancellationToken).GetAwaiter().GetResult();
        switch (outcome.Status)
        {
            case SwitchStatus.Pass:
                env.Console.WriteLine();
                env.Console.WriteLine("Switch completed and verified. The selected settings remain active.");
                return ExitCode.Pass;
            case SwitchStatus.FormatMismatch:
                if (outcome.RollbackVerified)
                {
                    env.Console.WriteLine("Original settings were restored and verified.");
                }
                return ExitCode.FormatMismatch;
            case SwitchStatus.Cancelled:
                if (outcome.RollbackVerified)
                {
                    env.Console.WriteLine("Original settings were restored and verified.");
                }
                return ExitCode.Cancelled;
            case SwitchStatus.NotApplicable:
                return ExitCode.NotApplicable;
            case SwitchStatus.SystemError:
                env.Console.WriteError($"ERROR: {outcome.Message}");
                return ExitCode.SystemError;
            default:
                return ExitCode.SystemError;
        }
    }

    private ExitCode FailWithMessage(string message)
    {
        env.Console.WriteError($"ERROR: {message}");
        return ExitCode.SystemError;
    }

    private T Select<T>(string label, IReadOnlyList<T> options, Func<T, string> display)
    {
        env.Console.WriteLine();
        env.Console.WriteLine($"Available {label} options:");
        for (var i = 0; i < options.Count; i++)
        {
            env.Console.WriteLine($"  [{i + 1}] {display(options[i])}");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            env.Console.WriteLine($"Select 1-{options.Count}, or C to cancel:");
            var input = env.Console.ReadLine()?.Trim();
            if (input is null || string.Equals(input, "C", StringComparison.OrdinalIgnoreCase))
            {
                throw new OperationCanceledException();
            }

            if (int.TryParse(input, out var index) && index >= 1 && index <= options.Count)
            {
                return options[index - 1];
            }

            env.Console.WriteLine("Invalid selection.");
        }
    }

    internal static EndpointInfo ResolveEndpoint(IReadOnlyList<EndpointInfo> endpoints, string? requestedId)
    {
        if (endpoints.Count == 0)
        {
            throw new InvalidOperationException("No active render endpoints were found.");
        }

        if (requestedId is not null)
        {
            return endpoints.FirstOrDefault(endpoint => string.Equals(
                       endpoint.EndpointId, requestedId, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException(
                       $"No active render endpoint matches the ID '{requestedId}'.");
        }

        return endpoints.FirstOrDefault(endpoint => endpoint.IsDefault)
               ?? throw new InvalidOperationException(
                   "No default active render endpoint was found. Specify one with --device-id.");
    }

    private void PrintControlPanelOptions(ControlPanelFormatResult controlPanel)
    {
        env.Console.WriteLine();
        env.Console.WriteLine("Control Panel options:");
        env.Console.WriteLine("  Source=mmsys.cpl");
        if (controlPanel.SpeakerConfigurations.Count > 0)
        {
            var channels = string.Join(", ", controlPanel.SpeakerConfigurations
                .Select(item => item.Channels)
                .Distinct()
                .OrderBy(value => value));
            var configurations = string.Join(", ", controlPanel.SpeakerConfigurations
                .Select(item => $"{item.DisplayText} ({item.Channels})"));
            env.Console.WriteLine($"  Supported speaker channels={channels}");
            env.Console.WriteLine($"  Speaker configurations={configurations}");
            env.Console.WriteLine($"  Max supported channels={controlPanel.MaxSupportedChannels}");
        }

        foreach (var item in controlPanel.Items)
        {
            env.Console.WriteLine($"  {item.DisplayText}");
        }

        env.Console.WriteLine($"  Cleanup={(controlPanel.Snapshot.CleanupSucceeded ? "Succeeded" : "Failed")}");
        env.Console.WriteLine();
    }

    private static string DescribeStatus(ExitCode status) => status switch
    {
        ExitCode.Pass => "PASS",
        ExitCode.FormatMismatch => "FAIL",
        ExitCode.SystemError => "ERROR",
        ExitCode.Cancelled => "CANCELLED",
        ExitCode.NotApplicable => "N/A",
        _ => "UNKNOWN",
    };

    private static string DescribeExitCode(ExitCode status) => status switch
    {
        ExitCode.Pass => "the selected settings were applied and read back successfully",
        ExitCode.FormatMismatch => "apply or readback failed, but the original settings were restored and verified",
        ExitCode.SystemError => "discovery, SVCL, or rollback failed",
        ExitCode.Cancelled => "cancelled before settings were changed",
        ExitCode.NotApplicable => "no selectable Control Panel speaker channels or formats were found",
        _ => "unknown",
    };
}