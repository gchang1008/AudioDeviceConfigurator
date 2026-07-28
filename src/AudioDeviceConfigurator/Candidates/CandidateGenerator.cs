using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Edid;

namespace AudioDeviceConfigurator.Candidates;

/// <summary>An EDID-derived format to be validated, with traceability back to its source SADs.</summary>
public sealed record CandidateFormat(
    WaveFormat Format,
    IReadOnlyList<string> SourceSadReferences)
{
    public int Channels => Format.Channels;
    public int SampleRate => Format.SampleRate;
    public int EffectiveBits => Format.ValidBits;
    public int ContainerBits => Format.ContainerBits;

    public override string ToString() => Format.ToString();
}

/// <summary>
/// Derives the candidate matrix from parsed EDID. Only EDID-declared depths and rates are used,
/// and only the 2/6/8 channel counts that fit within each SAD's declared maximum.
/// </summary>
public static class CandidateGenerator
{
    /// <summary>Channel counts in scope for the first version.</summary>
    public static readonly int[] TargetChannelCounts = [2, 6, 8];

    // Standard Windows channel masks: stereo, 5.1, 7.1.
    private const uint MaskStereo = 0x3;
    private const uint Mask51 = 0x3F;
    private const uint Mask71 = 0x63F;

    public static uint ChannelMaskFor(int channels) => channels switch
    {
        2 => MaskStereo,
        6 => Mask51,
        8 => Mask71,
        _ => throw new ArgumentOutOfRangeException(nameof(channels), channels, "Only 2, 6 and 8 channels are in scope."),
    };

    /// <summary>Maps an EDID effective bit depth to the selected Windows container representation.</summary>
    public static (int Container, int Valid) RepresentationFor(int effectiveBits) => effectiveBits switch
    {
        16 => (16, 16),
        20 => (24, 20),
        24 => (32, 24),
        _ => throw new ArgumentOutOfRangeException(nameof(effectiveBits), effectiveBits, "Only 16, 20 and 24 bit LPCM is in scope."),
    };

    public static IReadOnlyList<CandidateFormat> Generate(ParsedEdid edid)
    {
        ArgumentNullException.ThrowIfNull(edid);

        // Deduplicate on the observable format while accumulating every contributing SAD.
        var accumulated = new Dictionary<(int Channels, int SampleRate, int Bits), List<string>>();

        foreach (var sad in edid.LpcmDescriptors)
        {
            foreach (var channels in TargetChannelCounts)
            {
                if (channels > sad.MaxChannels)
                {
                    continue;
                }

                foreach (var rate in sad.SampleRates)
                {
                    foreach (var bits in sad.BitDepths)
                    {
                        var key = (channels, rate, bits);
                        if (!accumulated.TryGetValue(key, out var sources))
                        {
                            sources = [];
                            accumulated[key] = sources;
                        }

                        if (!sources.Contains(sad.SourceReference))
                        {
                            sources.Add(sad.SourceReference);
                        }
                    }
                }
            }
        }

        return accumulated
            .OrderBy(e => e.Key.Channels)
            .ThenBy(e => e.Key.SampleRate)
            .ThenBy(e => e.Key.Bits)
            .Select(e =>
            {
                var (container, valid) = RepresentationFor(e.Key.Bits);
                var format = new WaveFormat(
                    Channels: e.Key.Channels,
                    SampleRate: e.Key.SampleRate,
                    ContainerBits: container,
                    ValidBits: valid,
                    ChannelMask: ChannelMaskFor(e.Key.Channels),
                    Extensible: true);
                return new CandidateFormat(format, e.Value);
            })
            .ToList();
    }
}
