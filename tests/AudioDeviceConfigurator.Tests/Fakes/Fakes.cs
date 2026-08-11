using System.Text;
using AudioDeviceConfigurator.Abstractions;

namespace AudioDeviceConfigurator.Tests.Fakes;

public sealed class FakeEndpointProvider : IAudioEndpointProvider
{
    public List<EndpointInfo> Endpoints { get; } = [];
    public int ReadCalls { get; private set; }

    public IReadOnlyList<EndpointInfo> GetActiveRenderEndpoints()
    {
        ReadCalls++;
        return Endpoints.ToArray();
    }

    public EndpointInfo? GetDefaultRenderEndpoint() => Endpoints.FirstOrDefault(e => e.IsDefault);
}

public sealed class FakeEndpointChangeMonitor : IAudioEndpointChangeMonitor
{
    private EventHandler<AudioEndpointChange>? _changed;

    public int SubscriberCount { get; private set; }
    public int DisposeCalls { get; private set; }

    public event EventHandler<AudioEndpointChange>? Changed
    {
        add
        {
            _changed += value;
            SubscriberCount++;
        }
        remove
        {
            _changed -= value;
            SubscriberCount--;
        }
    }

    public void Raise(AudioEndpointChange change) => _changed?.Invoke(this, change);

    public void Dispose() => DisposeCalls++;
}

public sealed class FakeControlPanelFormatProvider : IControlPanelFormatProvider
{
    public List<ControlPanelFormatItem> Items { get; } = [];
    public List<ControlPanelSpeakerConfigurationItem> SpeakerConfigurations { get; } = [];
    public List<string> EndpointIds { get; } = [];
    public EndpointInfo? LastEndpoint { get; private set; }
    public TimeSpan? Timeout { get; private set; }
    public Func<EndpointInfo, TimeSpan, ControlPanelFormatResult>? Provider { get; set; }

    public ControlPanelFormatResult ReadDefaultFormats(EndpointInfo endpoint, TimeSpan timeout)
    {
        EndpointIds.Add(endpoint.EndpointId);
        LastEndpoint = endpoint;
        Timeout = timeout;
        if (Provider is not null)
        {
            return Provider(endpoint, timeout);
        }

        var maxSupportedChannels = SpeakerConfigurations.Count == 0
            ? (int?)null
            : SpeakerConfigurations.Max(item => item.Channels);
        return new ControlPanelFormatResult(Items, SpeakerConfigurations, maxSupportedChannels,
            new ControlPanelFormatSnapshot(DateTimeOffset.MinValue, DateTimeOffset.MinValue, "fake", true, true, null));
    }
}

public sealed class FakeClock : IClock
{
    public List<TimeSpan> Sleeps { get; } = [];

    public void Sleep(TimeSpan duration) => Sleeps.Add(duration);
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

public sealed class FakeProcessRunner(
    Func<IReadOnlyList<string>, FakeFileSystem, ProcessResult> handler,
    FakeFileSystem fileSystem) : IProcessRunner
{
    public List<IReadOnlyList<string>> Invocations { get; } = [];

    public ProcessResult Run(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        Invocations.Add(arguments.ToArray());
        return handler(arguments, fileSystem);
    }
}

/// <summary>In-memory filesystem for SVCL executable and temporary format files.</summary>
public sealed class FakeFileSystem : IFileSystem
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> FileVersions { get; } = new(StringComparer.OrdinalIgnoreCase);

    private int _tempCounter;

    public bool FileExists(string path) => Files.ContainsKey(path);

    public byte[] ReadAllBytes(string path) =>
        Files.TryGetValue(path, out var data) ? data : throw new FileNotFoundException(path);

    public void WriteAllBytes(string path, byte[] data) => Files[path] = data;

    public void DeleteFile(string path) => Files.Remove(path);

    public string? GetFileVersion(string path) =>
        FileVersions.TryGetValue(path, out var version) ? version : null;

    public string GetTempFilePath(string suffix) => $@"C:\Temp\svcl-format-{++_tempCounter}{suffix}";
}
