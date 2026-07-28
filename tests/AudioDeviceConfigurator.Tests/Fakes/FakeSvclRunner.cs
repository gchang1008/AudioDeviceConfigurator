using AudioDeviceConfigurator.Abstractions;

namespace AudioDeviceConfigurator.Tests.Fakes;

public sealed record SvclInvocation(string Command, IReadOnlyList<string> Arguments)
{
    public override string ToString() => $"{Command} {string.Join(" ", Arguments.Skip(1))}";
}

/// <summary>
/// Simulates svcl.exe: it holds a current default format, applies /SetDefaultFormat and writes
/// /SaveDeviceFormat structures, and can be told to misbehave the way the real tool does.
/// </summary>
public sealed class FakeSvclRunner(FakeFileSystem fileSystem, FakeClock clock) : IProcessRunner
{
    public List<SvclInvocation> Invocations { get; } = [];

    /// <summary>The current device default format as (channels, rate, effective bits).</summary>
    public (int Channels, int SampleRate, int EffectiveBits) CurrentFormat { get; set; } = (2, 48000, 16);

    /// <summary>When set, /SetDefaultFormat stores this instead of the requested format.</summary>
    public Func<(int Channels, int SampleRate, int EffectiveBits), (int Channels, int SampleRate, int EffectiveBits)>? SubstituteOnSet { get; set; }

    /// <summary>Number of /SaveDeviceFormat polls that return the stale format before the new one appears.</summary>
    public int DelayedReadbackPolls { get; set; }

    public int SetExitCode { get; set; }
    public int SaveExitCode { get; set; }
    public string SetStdout { get; set; } = "";
    public string SaveStdout { get; set; } = "";
    public bool SaveWritesNoFile { get; set; }
    public byte[]? SaveRawOverride { get; set; }
    public bool WriteExtensible { get; set; } = true;
    public int ContainerBitsOverride { get; set; }
    public Func<int, bool>? FailSetOnInvocation { get; set; }
    public Action? OnSet { get; set; }

    private (int Channels, int SampleRate, int EffectiveBits)? _pending;
    private int _pendingPollsLeft;
    private int _setCount;

    public ProcessResult Run(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var command = arguments[0];
        Invocations.Add(new SvclInvocation(command, arguments));
        clock.Advance(TimeSpan.FromMilliseconds(10));

        return command switch
        {
            "/SetDefaultFormat" => RunSet(arguments),
            "/SaveDeviceFormat" => RunSave(arguments),
            _ => new ProcessResult(0, "", ""),
        };
    }

    private ProcessResult RunSet(IReadOnlyList<string> arguments)
    {
        _setCount++;
        OnSet?.Invoke();

        if (FailSetOnInvocation?.Invoke(_setCount) == true)
        {
            return new ProcessResult(1, "", "SVCL failed.");
        }

        if (SetExitCode != 0)
        {
            return new ProcessResult(SetExitCode, SetStdout, "");
        }

        if (SetStdout.Contains("No items found", StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessResult(0, SetStdout, "");
        }

        var requested = (
            Channels: int.Parse(arguments[4]),
            SampleRate: int.Parse(arguments[3]),
            EffectiveBits: int.Parse(arguments[2]));

        var applied = SubstituteOnSet?.Invoke(requested) ?? requested;

        if (DelayedReadbackPolls > 0)
        {
            _pending = applied;
            _pendingPollsLeft = DelayedReadbackPolls;
        }
        else
        {
            CurrentFormat = applied;
        }

        return new ProcessResult(0, SetStdout, "");
    }

    private ProcessResult RunSave(IReadOnlyList<string> arguments)
    {
        if (SaveExitCode != 0)
        {
            return new ProcessResult(SaveExitCode, SaveStdout, "");
        }

        if (SaveStdout.Contains("No items found", StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessResult(0, SaveStdout, "");
        }

        if (SaveWritesNoFile)
        {
            return new ProcessResult(0, SaveStdout, "");
        }

        if (_pending is { } pending)
        {
            if (_pendingPollsLeft <= 0)
            {
                CurrentFormat = pending;
                _pending = null;
            }
            else
            {
                _pendingPollsLeft--;
            }
        }

        var path = arguments[2];
        fileSystem.WriteAllBytes(path, SaveRawOverride ?? BuildFormatBytes(CurrentFormat));
        return new ProcessResult(0, SaveStdout, "");
    }

    public byte[] BuildFormatBytes((int Channels, int SampleRate, int EffectiveBits) format)
    {
        var container = ContainerBitsOverride > 0
            ? ContainerBitsOverride
            : format.EffectiveBits switch
            {
                16 => 16,
                20 => 24,
                24 => 32,
                _ => format.EffectiveBits,
            };

        return WriteExtensible
            ? BuildExtensible(format.Channels, format.SampleRate, container, format.EffectiveBits, ChannelMask(format.Channels))
            : BuildWaveFormatEx(format.Channels, format.SampleRate, container);
    }

    public static uint ChannelMask(int channels) => channels switch
    {
        2 => 0x3,
        6 => 0x3F,
        8 => 0x63F,
        _ => 0,
    };

    public static byte[] BuildWaveFormatEx(int channels, int sampleRate, int containerBits)
    {
        var blockAlign = channels * (containerBits / 8);
        var data = new byte[18];
        BitConverter.GetBytes((ushort)1).CopyTo(data, 0);              // WAVE_FORMAT_PCM
        BitConverter.GetBytes((ushort)channels).CopyTo(data, 2);
        BitConverter.GetBytes((uint)sampleRate).CopyTo(data, 4);
        BitConverter.GetBytes((uint)(sampleRate * blockAlign)).CopyTo(data, 8);
        BitConverter.GetBytes((ushort)blockAlign).CopyTo(data, 12);
        BitConverter.GetBytes((ushort)containerBits).CopyTo(data, 14);
        BitConverter.GetBytes((ushort)0).CopyTo(data, 16);             // cbSize
        return data;
    }

    public static byte[] BuildExtensible(int channels, int sampleRate, int containerBits, int validBits, uint channelMask)
    {
        var blockAlign = channels * (containerBits / 8);
        var data = new byte[40];
        BitConverter.GetBytes((ushort)0xFFFE).CopyTo(data, 0);         // WAVE_FORMAT_EXTENSIBLE
        BitConverter.GetBytes((ushort)channels).CopyTo(data, 2);
        BitConverter.GetBytes((uint)sampleRate).CopyTo(data, 4);
        BitConverter.GetBytes((uint)(sampleRate * blockAlign)).CopyTo(data, 8);
        BitConverter.GetBytes((ushort)blockAlign).CopyTo(data, 12);
        BitConverter.GetBytes((ushort)containerBits).CopyTo(data, 14);
        BitConverter.GetBytes((ushort)22).CopyTo(data, 16);            // cbSize
        BitConverter.GetBytes((ushort)validBits).CopyTo(data, 18);
        BitConverter.GetBytes(channelMask).CopyTo(data, 20);
        return data;
    }

    public IEnumerable<SvclInvocation> SetCommands =>
        Invocations.Where(i => i.Command == "/SetDefaultFormat");

    public IEnumerable<(int Bits, int Rate, int Channels)> AppliedFormats =>
        SetCommands.Select(i => (int.Parse(i.Arguments[2]), int.Parse(i.Arguments[3]), int.Parse(i.Arguments[4])));
}
