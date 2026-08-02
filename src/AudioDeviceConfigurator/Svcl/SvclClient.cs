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

public interface ISvclClient
{
    IReadOnlyList<SvclCommandLog> CommandLog { get; }
    string ExecutablePath { get; }
    string? DetectedVersion { get; }
    void VerifyInstallation();
    SavedFormat SaveDeviceFormat(string deviceId);
    void SetSpeakersConfig(string deviceId, int channels);
    void SetSpeakersConfig(string deviceId, uint channelMask);
    void SetDefaultFormat(string deviceId, int effectiveBits, int sampleRate, int channels);
}

/// <summary>Wraps SVCL commands and validates their observable results.</summary>
public sealed class SvclClient(
    IProcessRunner processRunner,
    IFileSystem fileSystem,
    string svclPath) : ISvclClient
{
    public const string MinimumVersion = "1.28";
    private const string NoItemsFound = "No items found";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);
    private readonly List<SvclCommandLog> _log = [];

    public IReadOnlyList<SvclCommandLog> CommandLog => _log;
    public string ExecutablePath => svclPath;
    public string? DetectedVersion { get; private set; }

    public void VerifyInstallation()
    {
        if (!fileSystem.FileExists(svclPath))
        {
            throw new SvclException(
                $"svcl.exe was not found at {svclPath}. SVCL {MinimumVersion} or newer is required.");
        }

        var version = fileSystem.GetFileVersion(svclPath);
        DetectedVersion = version;
        if (version is null)
        {
            throw new SvclException($"Unable to read the file version of {svclPath}.");
        }

        if (CompareVersions(NormalizeNirsoftVersion(version), MinimumVersion) < 0)
        {
            throw new SvclException(
                $"svcl.exe version {version} is older than the required {MinimumVersion}.");
        }
    }

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

    public void SetSpeakersConfig(string deviceId, int channels) =>
        SetSpeakersConfig(deviceId, GetSpeakerMask(channels));

    public void SetSpeakersConfig(string deviceId, uint channelMask)
    {
        if (channelMask == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelMask));
        }

        var mask = $"0x{channelMask:x}";
        var args = new[] { "/SetSpeakersConfig", deviceId, mask, mask, mask };
        ExecuteSet("/SetSpeakersConfig", deviceId, args);
    }

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
        ExecuteSet("/SetDefaultFormat", deviceId, args);
    }

    public static uint GetSpeakerMask(int channels) => channels switch
    {
        2 => 0x3,
        4 => 0x33,
        6 => 0x3f,
        8 => 0x63f,
        _ => throw new ArgumentOutOfRangeException(
            nameof(channels), channels, "Only 2, 4, 6, and 8 channels are supported."),
    };

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
        var validBits = (int)containerBits;
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
            formatTag, channels, (int)sampleRate, containerBits, validBits, channelMask, data);
    }

    public static string NormalizeNirsoftVersion(string fileVersion)
    {
        var parts = fileVersion.Split('.');
        if (parts.Length < 3
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || !int.TryParse(parts[2], out var build))
        {
            return fileVersion;
        }

        return minor >= 10 ? $"{major}.{minor}" : $"{major}.{minor}{build}";
    }

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
            value.Split('.').Select(part => int.TryParse(part, out var n) ? n : 0).ToArray();
    }

    private void ExecuteSet(string command, string deviceId, IReadOnlyList<string> args)
    {
        var result = processRunner.Run(svclPath, args, ProcessTimeout);
        var failure = DetectFailure(result);
        Log(command, args, result, failure);
        if (failure is not null)
        {
            throw new SvclException($"{command} failed for '{deviceId}': {failure}");
        }
    }

    private static string? DetectFailure(ProcessResult result)
    {
        if (result.ExitCode != 0)
        {
            return $"SVCL exited with code {result.ExitCode}.";
        }

        return (result.StandardOutput + result.StandardError)
            .Contains(NoItemsFound, StringComparison.OrdinalIgnoreCase)
            ? $"SVCL reported '{NoItemsFound}'."
            : null;
    }

    private void Log(
        string command,
        IReadOnlyList<string> args,
        ProcessResult result,
        string? failure) =>
        _log.Add(new SvclCommandLog(
            command, args, result.ExitCode, result.StandardOutput, result.StandardError, failure));
}
