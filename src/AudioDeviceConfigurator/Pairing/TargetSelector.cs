using AudioDeviceConfigurator.Abstractions;

namespace AudioDeviceConfigurator.Pairing;

public sealed class TargetSelectionException(string message) : Exception(message);

public sealed class SelectionCancelledException(string message) : Exception(message);

public sealed record SelectedTarget(EndpointInfo Endpoint, DisplayInfo Display, bool WasInteractive);

/// <summary>
/// Chooses the endpoint and monitor to test. Automatic when unambiguous, interactive when not,
/// and never silent about picking the wrong target.
/// </summary>
public sealed class TargetSelector(IConsole console)
{
    public SelectedTarget Select(
        IReadOnlyList<EndpointInfo> endpoints,
        IReadOnlyList<DisplayInfo> displays,
        string? requestedEndpointId,
        string? requestedMonitorId)
    {
        if (endpoints.Count == 0)
        {
            throw new TargetSelectionException("No active render endpoints were found.");
        }

        if (displays.Count == 0)
        {
            throw new TargetSelectionException("No active displays were found.");
        }

        var endpoint = ResolveEndpoint(endpoints, requestedEndpointId);
        var display = ResolveDisplay(displays, requestedMonitorId, endpoint, out var interactive);
        return new SelectedTarget(endpoint, display, interactive);
    }

    private static EndpointInfo ResolveEndpoint(IReadOnlyList<EndpointInfo> endpoints, string? requestedId)
    {
        if (requestedId is not null)
        {
            return endpoints.FirstOrDefault(e =>
                       string.Equals(e.EndpointId, requestedId, StringComparison.OrdinalIgnoreCase))
                   ?? throw new TargetSelectionException(
                       $"No active render endpoint matches the ID '{requestedId}'.");
        }

        return endpoints.FirstOrDefault(e => e.IsDefault)
               ?? throw new TargetSelectionException(
                   "No default active render endpoint was found. Specify one with --device-id.");
    }

    private DisplayInfo ResolveDisplay(
        IReadOnlyList<DisplayInfo> displays,
        string? requestedId,
        EndpointInfo endpoint,
        out bool interactive)
    {
        interactive = false;

        if (requestedId is not null)
        {
            return displays.FirstOrDefault(d =>
                       string.Equals(
                           StripWin32Prefix(d.MonitorId),
                           StripWin32Prefix(requestedId),
                           StringComparison.OrdinalIgnoreCase))
                   ?? throw new TargetSelectionException(
                       $"No active display matches the monitor ID '{requestedId}'.");
        }

        if (displays.Count == 1)
        {
            return displays[0];
        }

        interactive = true;
        return PromptForDisplay(displays, endpoint);
    }

    /// <summary>
    /// Every active monitor ID carries the same Win32 namespace prefix, which shells mangle when
    /// an ID is copied from --list into --monitor-id. Ignoring it on both sides of the comparison
    /// stays injective, so two distinct monitors can never collapse onto one match.
    /// </summary>
    private static string StripWin32Prefix(string id) =>
        id.StartsWith(@"\\?\", StringComparison.Ordinal) ? id[4..] : id;

    private DisplayInfo PromptForDisplay(IReadOnlyList<DisplayInfo> displays, EndpointInfo endpoint)
    {
        console.WriteLine();
        console.WriteLine($"Endpoint '{endpoint.FriendlyName}' could not be paired with a single active display.");
        console.WriteLine("Select the monitor connected to this endpoint:");
        for (var i = 0; i < displays.Count; i++)
        {
            console.WriteLine($"  [{i + 1}] {displays[i].FriendlyName}  ({displays[i].MonitorId})");
        }

        console.WriteLine("  [C] Cancel");

        while (true)
        {
            console.WriteLine();
            console.WriteLine($"Enter 1-{displays.Count}, or C to cancel:");
            var input = console.ReadLine();

            if (input is null || input.Trim().Equals("C", StringComparison.OrdinalIgnoreCase))
            {
                throw new SelectionCancelledException("Monitor selection was cancelled by the user.");
            }

            if (int.TryParse(input.Trim(), out var choice) && choice >= 1 && choice <= displays.Count)
            {
                return displays[choice - 1];
            }

            console.WriteLine("Invalid selection.");
        }
    }
}
