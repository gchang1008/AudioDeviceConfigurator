using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioDeviceConfigurator.Abstractions;

namespace AudioDeviceConfigurator.Reporting;

public sealed class ReportWriteException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed record ReportPaths(string JsonPath, string CsvPath);

/// <summary>
/// Writes the timestamped JSON and CSV pair under a Reports directory beside the executable.
/// Failing to write either report is a system error, so evidence is never silently missing.
/// </summary>
public sealed class ReportWriter(IFileSystem fileSystem, string reportsDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ReportsDirectory => reportsDirectory;

    public ReportPaths Write(RunReport report, string machineName, string monitorName, DateTimeOffset localTime)
    {
        try
        {
            fileSystem.CreateDirectory(reportsDirectory);
        }
        catch (Exception ex)
        {
            throw new ReportWriteException($"Unable to create the Reports directory '{reportsDirectory}': {ex.Message}", ex);
        }

        var baseName = BuildBaseName(machineName, monitorName, localTime);
        var paths = ResolveUniquePaths(baseName);

        try
        {
            fileSystem.WriteAllText(paths.JsonPath, JsonSerializer.Serialize(report, JsonOptions));
        }
        catch (Exception ex)
        {
            throw new ReportWriteException($"Unable to write the JSON report '{paths.JsonPath}': {ex.Message}", ex);
        }

        try
        {
            fileSystem.WriteAllText(paths.CsvPath, BuildCsv(report));
        }
        catch (Exception ex)
        {
            throw new ReportWriteException($"Unable to write the CSV report '{paths.CsvPath}': {ex.Message}", ex);
        }

        return paths;
    }

    private ReportPaths ResolveUniquePaths(string baseName)
    {
        var json = Path.Combine(reportsDirectory, baseName + ".json");
        var csv = Path.Combine(reportsDirectory, baseName + ".csv");
        if (!fileSystem.FileExists(json) && !fileSystem.FileExists(csv))
        {
            return new ReportPaths(json, csv);
        }

        for (var sequence = 2; sequence < 1000; sequence++)
        {
            var candidate = $"{baseName}_{sequence}";
            json = Path.Combine(reportsDirectory, candidate + ".json");
            csv = Path.Combine(reportsDirectory, candidate + ".csv");
            if (!fileSystem.FileExists(json) && !fileSystem.FileExists(csv))
            {
                return new ReportPaths(json, csv);
            }
        }

        throw new ReportWriteException($"Unable to find an unused report filename for '{baseName}'.");
    }

    public static string BuildBaseName(string machineName, string monitorName, DateTimeOffset localTime) =>
        $"{Sanitize(machineName)}_{Sanitize(monitorName)}_{localTime.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}";

    /// <summary>Replaces anything outside [A-Za-z0-9._-] so filenames stay portable and recognizable.</summary>
    public static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        }

        var result = builder.ToString().Trim('_');
        if (result.Length > 40)
        {
            result = result[..40];
        }

        return result.Length == 0 ? "Unknown" : result;
    }

    private static readonly string[] CsvHeaders =
    [
        "RunId", "StartedAtLocal", "CompletedAtLocal", "OverallStatus", "ExitCode",
        "MachineName", "OsDescription", "OsVersion", "Architecture", "ApplicationVersion",
        "EndpointId", "EndpointName", "EndpointDescription", "EndpointIsDefault",
        "AudioDriverName", "AudioDriverVersion",
        "MonitorId", "MonitorFriendlyName", "MonitorManufacturer", "MonitorProductCode",
        "MonitorName", "AdapterName", "GpuDriverVersion",
        "OriginalChannels", "OriginalSampleRate", "OriginalEffectiveBits",
        "CandidateIndex", "Channels", "SampleRate", "EffectiveBits", "ContainerBits", "ChannelMask",
        "SourceSads", "WasapiResult", "WasapiHResult", "ApplyResult",
        "ReadbackChannels", "ReadbackSampleRate", "ReadbackEffectiveBits", "ReadbackContainerBits",
        "ApplyElapsedMs", "CandidateStatus", "FailureDetail",
        "RestoreAttempted", "RestoreSucceeded", "RestoreFailureDetail", "Errors",
    ];

    public static string BuildCsv(RunReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", CsvHeaders));

        if (report.Candidates.Count == 0)
        {
            builder.AppendLine(string.Join(",", BuildRow(report, null)));
            return builder.ToString();
        }

        foreach (var candidate in report.Candidates)
        {
            builder.AppendLine(string.Join(",", BuildRow(report, candidate)));
        }

        return builder.ToString();
    }

    private static IEnumerable<string> BuildRow(RunReport r, CandidateReport? c)
    {
        var s = r.System;
        var e = r.Endpoint;
        var m = r.Monitor;
        var o = r.OriginalFormat;

        string[] values =
        [
            r.RunId, r.StartedAtLocal, r.CompletedAtLocal, r.OverallStatus, r.ExitCode.ToString(),
            s.MachineName, s.OsDescription, s.OsVersion, s.Architecture, s.ApplicationVersion,
            e?.EndpointId ?? "", e?.FriendlyName ?? "", e?.DeviceDescription ?? "", e?.IsDefault.ToString() ?? "",
            e?.DriverName ?? "", e?.DriverVersion ?? "",
            m?.MonitorId ?? "", m?.FriendlyName ?? "", m?.ManufacturerId ?? "", m?.ProductCode.ToString() ?? "",
            m?.MonitorName ?? "", m?.AdapterName ?? "", m?.GpuDriverVersion ?? "",
            o?.Channels.ToString() ?? "", o?.SampleRate.ToString() ?? "", o?.EffectiveBits.ToString() ?? "",
            c?.Index.ToString() ?? "", c?.Channels.ToString() ?? "", c?.SampleRate.ToString() ?? "",
            c?.EffectiveBits.ToString() ?? "", c?.ContainerBits.ToString() ?? "",
            c is null ? "" : $"0x{c.ChannelMask:X}",
            c is null ? "" : string.Join(" ", c.SourceSadReferences),
            c?.WasapiResult.ToString() ?? "", c?.WasapiHResult ?? "", c?.ApplyResult.ToString() ?? "",
            c?.ReadbackChannels?.ToString() ?? "", c?.ReadbackSampleRate?.ToString() ?? "",
            c?.ReadbackEffectiveBits?.ToString() ?? "", c?.ReadbackContainerBits?.ToString() ?? "",
            c?.ApplyElapsedMilliseconds?.ToString("F0", CultureInfo.InvariantCulture) ?? "",
            c?.Status.ToString() ?? "", c?.FailureDetail ?? "",
            r.Restore.Attempted.ToString(), r.Restore.Succeeded.ToString(), r.Restore.FailureDetail ?? "",
            string.Join(" | ", r.Errors),
        ];

        return values.Select(Escape);
    }

    private static string Escape(string value)
    {
        var normalized = value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        if (normalized.Contains(',') || normalized.Contains('"'))
        {
            return '"' + normalized.Replace("\"", "\"\"") + '"';
        }

        return normalized;
    }
}
