using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Svcl;

namespace AudioDeviceConfigurator.Tests.Fakes;

public sealed class FakeSvclClient : ISvclClient
{
    private readonly Queue<Func<SavedFormat>> _savedFormats = new();

    public List<string> Operations { get; } = [];
    public IReadOnlyList<SvclCommandLog> CommandLog { get; } = [];
    public string ExecutablePath { get; } = @"C:\App\svcl.exe";
    public string? DetectedVersion { get; private set; }
    public Exception? VerifyFailure { get; set; }
    public Exception? SetSpeakersFailure { get; set; }
    public Exception? SetFormatFailure { get; set; }
    public Func<int, Exception?>? SetSpeakersFailureForCall { get; set; }
    public Func<int, Exception?>? SetFormatFailureForCall { get; set; }
    public Action? AfterSetSpeakers { get; set; }
    public Action? AfterSetFormat { get; set; }

    public FakeSvclClient EnqueueSavedFormat(SavedFormat format)
    {
        _savedFormats.Enqueue(() => format);
        return this;
    }

    public FakeSvclClient EnqueueSaveFailure(Exception error)
    {
        _savedFormats.Enqueue(() => throw error);
        return this;
    }

    public void VerifyInstallation()
    {
        Operations.Add("VerifyInstallation");
        if (VerifyFailure is not null)
        {
            throw VerifyFailure;
        }

        DetectedVersion = "1.28";
    }

    public SavedFormat SaveDeviceFormat(string deviceId)
    {
        Operations.Add($"Save:{deviceId}");
        if (_savedFormats.Count == 0)
        {
            throw new InvalidOperationException("No fake saved format was queued.");
        }

        return _savedFormats.Dequeue()();
    }

    public void SetSpeakersConfig(string deviceId, int channels) =>
        SetSpeakersConfig(deviceId, SvclClient.GetSpeakerMask(channels));

    public void SetSpeakersConfig(string deviceId, uint channelMask)
    {
        Operations.Add($"SetSpeakers:{deviceId}:{channelMask:x}");
        var call = Operations.Count(operation => operation.StartsWith("SetSpeakers:", StringComparison.Ordinal));
        var failure = SetSpeakersFailureForCall?.Invoke(call) ?? SetSpeakersFailure;
        if (failure is not null)
        {
            throw failure;
        }

        AfterSetSpeakers?.Invoke();
    }

    public void SetDefaultFormat(string deviceId, int effectiveBits, int sampleRate, int channels)
    {
        Operations.Add($"SetFormat:{deviceId}:{channels}:{effectiveBits}:{sampleRate}");
        var call = Operations.Count(operation => operation.StartsWith("SetFormat:", StringComparison.Ordinal));
        var failure = SetFormatFailureForCall?.Invoke(call) ?? SetFormatFailure;
        if (failure is not null)
        {
            throw failure;
        }

        AfterSetFormat?.Invoke();
    }
}
