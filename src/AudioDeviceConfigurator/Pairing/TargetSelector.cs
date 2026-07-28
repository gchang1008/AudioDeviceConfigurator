using AudioDeviceConfigurator.Abstractions;

namespace AudioDeviceConfigurator.Pairing;

public sealed class TargetSelectionException(string message) : Exception(message);

public sealed class SelectionCancelledException(string message) : Exception(message);

public sealed record SelectedTarget(
    EndpointInfo Endpoint,
    DisplayInfo Display,
    PairingMethod PairingMethod)
{
    public bool WasInteractive => PairingMethod == PairingMethod.Interactive;
}

/// <summary>How the tested display was chosen, recorded so a result can be judged in context.</summary>
public enum PairingMethod
{
    /// <summary>The tester supplied --monitor-id.</summary>
    Explicit,

    /// <summary>Exactly one active display shares the endpoint's device container.</summary>
    Container,

    /// <summary>The tester chose from a list because pairing was ambiguous.</summary>
    Interactive,
}

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
        var display = ResolveDisplay(displays, requestedMonitorId, endpoint, out var method);
        return new SelectedTarget(endpoint, display, method);
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
        out PairingMethod method)
    {
        if (requestedId is not null)
        {
            method = PairingMethod.Explicit;
            return displays.FirstOrDefault(d =>
                       string.Equals(
                           StripWin32Prefix(d.MonitorId),
                           StripWin32Prefix(requestedId),
                           StringComparison.OrdinalIgnoreCase))
                   ?? throw new TargetSelectionException(
                       $"No active display matches the monitor ID '{requestedId}'.");
        }

        // Pair on the device container the endpoint and its monitor share. A display count of one
        // is NOT evidence of ownership: a USB headset endpoint with one attached monitor would
        // otherwise be silently tested against an EDID that has nothing to do with it.
        var owned = MatchByContainer(displays, endpoint);
        if (owned.Count == 1)
        {
            method = PairingMethod.Container;
            return owned[0];
        }

        method = PairingMethod.Interactive;
        return PromptForDisplay(owned.Count > 1 ? owned : displays, endpoint);
    }

    /// <summary>
    /// Displays sharing the endpoint's container. The all-zero/all-F GUID is Windows' "no
    /// container" sentinel and must never be treated as a match, even on both sides.
    /// </summary>
    private static IReadOnlyList<DisplayInfo> MatchByContainer(
        IReadOnlyList<DisplayInfo> displays,
        EndpointInfo endpoint)
    {
        if (!HasRealContainer(endpoint.ContainerId))
        {
            return [];
        }

        return displays
            .Where(d => HasRealContainer(d.ContainerId)
                        && string.Equals(
                            NormalizeContainer(d.ContainerId!),
                            NormalizeContainer(endpoint.ContainerId!),
                            StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private const string NullContainerSentinel = "00000000-0000-0000-FFFF-FFFFFFFFFFFF";

    private static bool HasRealContainer(string? containerId) =>
        !string.IsNullOrWhiteSpace(containerId)
        && !NormalizeContainer(containerId).Equals(NullContainerSentinel, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeContainer(string containerId) =>
        containerId.Trim().Trim('{', '}');

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
