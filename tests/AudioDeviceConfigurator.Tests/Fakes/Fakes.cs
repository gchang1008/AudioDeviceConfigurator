using System.Text;
using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Domain;

namespace AudioDeviceConfigurator.Tests.Fakes;

public sealed class FakeDisplayProvider : IDisplayProvider
{
    public List<DisplayInfo> Displays { get; } = [];
    public Exception? ThrowOnGet { get; set; }

    public IReadOnlyList<DisplayInfo> GetActiveDisplays() =>
        ThrowOnGet is not null ? throw ThrowOnGet : Displays;
}

public sealed class FakeEndpointProvider : IAudioEndpointProvider
{
    public List<EndpointInfo> Endpoints { get; } = [];

    public IReadOnlyList<EndpointInfo> GetActiveRenderEndpoints() => Endpoints;

    public EndpointInfo? GetDefaultRenderEndpoint() => Endpoints.FirstOrDefault(e => e.IsDefault);
}

public sealed class FakeWasapiProbe : IWasapiFormatProbe
{
    private readonly Dictionary<string, int> _overrides = new();

    public int DefaultHResult { get; set; } = FormatSupportResult.SOk;

    public List<WaveFormat> Queries { get; } = [];

    public static string Key(int channels, int rate, int validBits, bool extensible = true) =>
        $"{channels}/{rate}/{validBits}/{extensible}";

    public FakeWasapiProbe Set(int channels, int rate, int validBits, int hresult, bool? extensible = null)
    {
        if (extensible is null)
        {
            _overrides[Key(channels, rate, validBits, true)] = hresult;
            _overrides[Key(channels, rate, validBits, false)] = hresult;
        }
        else
        {
            _overrides[Key(channels, rate, validBits, extensible.Value)] = hresult;
        }

        return this;
    }

    public FormatSupportResult IsExclusiveFormatSupported(string endpointId, WaveFormat format)
    {
        Queries.Add(format);
        var key = Key(format.Channels, format.SampleRate, format.ValidBits, format.Extensible);
        return new FormatSupportResult(_overrides.TryGetValue(key, out var hr) ? hr : DefaultHResult);
    }
}

public sealed class FakeClock : IClock
{
    private TimeSpan _elapsed = TimeSpan.Zero;

    public DateTimeOffset LocalNow { get; set; } =
        new(2026, 7, 28, 14, 30, 0, TimeSpan.FromHours(8));

    public DateTimeOffset UtcNow => LocalNow.ToUniversalTime();

    public TimeSpan Elapsed => _elapsed;

    public List<TimeSpan> Sleeps { get; } = [];

    public void Sleep(TimeSpan duration)
    {
        Sleeps.Add(duration);
        _elapsed += duration;
        LocalNow = LocalNow.Add(duration);
    }

    public void Advance(TimeSpan duration)
    {
        _elapsed += duration;
        LocalNow = LocalNow.Add(duration);
    }
}

public sealed class FakeConsole : IConsole
{
    private readonly Queue<string?> _inputs = new();

    public StringBuilder Output { get; } = new();
    public StringBuilder Errors { get; } = new();
    public List<string> Lines { get; } = [];

    public FakeConsole EnqueueInput(params string?[] values)
    {
        foreach (var value in values)
        {
            _inputs.Enqueue(value);
        }

        return this;
    }

    public void WriteLine(string text = "")
    {
        Output.AppendLine(text);
        Lines.Add(text);
    }

    public void WriteError(string text)
    {
        Errors.AppendLine(text);
        Lines.Add(text);
    }

    public string? ReadLine() => _inputs.Count > 0 ? _inputs.Dequeue() : null;

    public string Text => Output.ToString();

    public string ErrorText => Errors.ToString();
}

public sealed class FakeSystemInfoProvider : ISystemInfoProvider
{
    public SystemInfo Info { get; set; } = new(
        MachineName: "TEST-PC",
        OsDescription: "Microsoft Windows 11 Pro",
        OsVersion: "10.0.26100",
        Architecture: "X64",
        UserName: "tester",
        ApplicationVersion: "1.0.0");

    public SystemInfo GetSystemInfo() => Info;
}

/// <summary>In-memory filesystem so report content and SVCL temp files are fully observable.</summary>
public sealed class FakeFileSystem : IFileSystem
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> FileVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> CreatedDirectories { get; } = [];
    public Func<string, Exception?>? WriteFailure { get; set; }
    public Func<string, Exception?>? CreateDirectoryFailure { get; set; }

    private int _tempCounter;

    public bool FileExists(string path) => Files.ContainsKey(path);

    public byte[] ReadAllBytes(string path) =>
        Files.TryGetValue(path, out var data) ? data : throw new FileNotFoundException(path);

    public void WriteAllText(string path, string contents)
    {
        if (WriteFailure?.Invoke(path) is { } ex)
        {
            throw ex;
        }

        Files[path] = Encoding.UTF8.GetBytes(contents);
    }

    public void WriteAllBytes(string path, byte[] data) => Files[path] = data;

    public void DeleteFile(string path) => Files.Remove(path);

    public void CreateDirectory(string path)
    {
        if (CreateDirectoryFailure?.Invoke(path) is { } ex)
        {
            throw ex;
        }

        Directories.Add(path);
        CreatedDirectories.Add(path);
    }

    public string? GetFileVersion(string path) =>
        FileVersions.TryGetValue(path, out var version) ? version : null;

    public string GetTempFilePath(string suffix) => $@"C:\Temp\svcl-format-{++_tempCounter}{suffix}";

    public string ReadText(string path) => Encoding.UTF8.GetString(ReadAllBytes(path));
}

public sealed class FakeDriverMetadataProvider : IDriverMetadataProvider
{
    public DriverMetadata NextResult { get; set; } = new(
        GpuName: null, GpuDriverVersion: null, GpuDriverProvider: null, AudioHdmi: []);

    public DriverMetadata GetDriverMetadata() => NextResult;
}
