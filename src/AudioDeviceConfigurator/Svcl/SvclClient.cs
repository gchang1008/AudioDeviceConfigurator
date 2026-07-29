using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Domain;

namespace AudioDeviceConfigurator.Svcl;

public sealed record SvclCommandLog(
    string Command,
    IReadOnlyList<string> Arguments,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? Failure);

public sealed class SvclException(string message) : Exception(message);

/// <summary>
/// Wraps svcl.exe. A zero process exit code proves nothing: SVCL has been observed printing
/// "No items found" and exiting 0, so every operation is verified by reading data back.
/// </summary>
public sealed class SvclClient(
    IProcessRunner processRunner,
    IFileSystem fileSystem,
    string svclPath)
{
    public const string MinimumVersion = "1.28";
    private const string NoItemsFound = "No items found";

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);

    private readonly List<SvclCommandLog> _log = [];

    public IReadOnlyList<SvclCommandLog> CommandLog => _log;

    public string ExecutablePath => svclPath;

    public string? DetectedVersion { get; private set; }

    /// <summary>Verifies svcl.exe exists and is at least the minimum required version.</summary>
    public void VerifyInstallation()
    {
        if (!fileSystem.FileExists(svclPath))
        {
            throw new SvclException($"svcl.exe was not found at {svclPath}. The SVCL {MinimumVersion} or newer package must be deployed next to this application.");
        }

        var version = fileSystem.GetFileVersion(svclPath);
        DetectedVersion = version;
        if (version is null)
        {
            throw new SvclException($"Unable to read the product version of {svclPath}. SVCL {MinimumVersion} or newer is required.");
        }

        if (CompareVersions(version, MinimumVersion) < 0)
        {
            throw new SvclException($"svcl.exe version {version} is older than the required {MinimumVersion}.");
        }
    }

    /// <summary>Reads the current default format of the endpoint via /SaveDeviceFormat.</summary>
    public SavedFormat SaveDeviceFormat(string deviceId)
    {
        var tempPath = fileSystem.GetTempFilePath(".dat");
        try
        {
            fileSystem.DeleteFile(tempPath);
            var args = new[] { "/SaveDeviceFormat", deviceId, tempPath };
            var result = processRunner.Run(svclPath, args, ProcessTimeout);

            var failure = DetectFailure(result);
            if (failure is null && !fileSystem.FileExists(tempPath))
            {
                failure = "SVCL did not write the saved format file.";
            }

            byte[]? data = null;
            if (failure is null)
            {
                data = fileSystem.ReadAllBytes(tempPath);
                if (data.Length < 16)
                {
                    failure = $"The saved device format is incomplete ({data.Length} bytes).";
                }
            }

            Log("/SaveDeviceFormat", args, result, failure);

            if (failure is not null)
            {
                throw new SvclException($"/SaveDeviceFormat failed for '{deviceId}': {failure}");
            }

            return ParseSavedFormat(data!);
        }
        finally
        {
            fileSystem.DeleteFile(tempPath);
        }
    }

    /// <summary>Applies a default format via /SetDefaultFormat. Readback is the caller's responsibility.</summary>
    public void SetDefaultFormat(string deviceId, int effectiveBits, int sampleRate, int channels)
    {
        var args = new[]
        {
            "/SetDefaultFormat",
            deviceId,
            effectiveBits.ToString(),
            sampleRate.ToString(),
            channels.ToString(),
        };

        var result = processRunner.Run(svclPath, args, ProcessTimeout);
        var failure = DetectFailure(result);
        Log("/SetDefaultFormat", args, result, failure);

        if (failure is not null)
        {
            throw new SvclException($"/SetDefaultFormat failed for '{deviceId}': {failure}");
        }
    }

    /// <summary>Parses a WAVEFORMATEX / WAVEFORMATEXTENSIBLE structure as written by SVCL.</summary>
    public static SavedFormat ParseSavedFormat(byte[] data)
    {
        if (data.Length < 16)
        {
            throw new SvclException($"The saved device format is incomplete ({data.Length} bytes).");
        }

        var formatTag = BitConverter.ToUInt16(data, 0);
        var channels = BitConverter.ToUInt16(data, 2);
        var sampleRate = BitConverter.ToUInt32(data, 4);
        var containerBits = BitConverter.ToUInt16(data, 14);
        int validBits = containerBits;
        uint channelMask = 0;

        if (formatTag == SavedFormat.WaveFormatExtensible)
        {
            if (data.Length < 24)
            {
                throw new SvclException(
                    $"The saved WAVEFORMATEXTENSIBLE data is incomplete ({data.Length} bytes).");
            }

            validBits = BitConverter.ToUInt16(data, 18);
            channelMask = BitConverter.ToUInt32(data, 20);
        }

        if (channels == 0 || sampleRate == 0 || containerBits == 0)
        {
            throw new SvclException(
                $"The saved device format is malformed (channels={channels}, rate={sampleRate}, bits={containerBits}).");
        }

        return new SavedFormat(
            FormatTag: formatTag,
            Channels: channels,
            SampleRate: (int)sampleRate,
            ContainerBits: containerBits,
            ValidBits: validBits,
            ChannelMask: channelMask,
            RawBytes: data);
    }

    /// <summary>Compares dotted numeric versions; missing components count as zero.</summary>
    public static int CompareVersions(string left, string right)
    {
        var a = ParseParts(left);
        var b = ParseParts(right);
        var length = Math.Max(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;

        static int[] ParseParts(string value) =>
            value.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
    }

    private static string? DetectFailure(ProcessResult result)
    {
        if (result.ExitCode != 0)
        {
            return $"SVCL exited with code {result.ExitCode}.";
        }

        var combined = result.StandardOutput + result.StandardError;
        if (combined.Contains(NoItemsFound, StringComparison.OrdinalIgnoreCase))
        {
            return $"SVCL reported '{NoItemsFound}'.";
        }

        return null;
    }

    private void Log(string command, IReadOnlyList<string> args, ProcessResult result, string? failure) =>
        _log.Add(new SvclCommandLog(
            command,
            args,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            failure));
}
