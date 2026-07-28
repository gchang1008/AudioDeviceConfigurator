using AudioDeviceConfigurator.Candidates;
using AudioDeviceConfigurator.Edid;
using AudioDeviceConfigurator.Tests.Fixtures;

namespace AudioDeviceConfigurator.Tests;

public class CandidateGeneratorTests
{
    private static IReadOnlyList<CandidateFormat> GenerateFrom(params SadSpec[] sads)
    {
        var edid = new EdidBuilder().WithCtaAudioBlock(sads).Build();
        return CandidateGenerator.Generate(EdidParser.Parse(edid));
    }

    [Fact]
    public void Generates_the_cross_product_of_declared_channels_rates_and_depths()
    {
        var candidates = GenerateFrom(SadSpec.Lpcm(8, [48000, 96000], [16, 24]));

        Assert.Equal(12, candidates.Count); // 3 channel counts x 2 rates x 2 depths
    }

    [Fact]
    public void Includes_channel_counts_up_to_the_declared_maximum_of_eight()
    {
        var candidates = GenerateFrom(SadSpec.Lpcm(8, [48000], [16]));

        Assert.Equal([2, 6, 8], candidates.Select(c => c.Channels));
    }

    [Fact]
    public void Includes_only_two_and_six_channels_when_the_maximum_is_six()
    {
        var candidates = GenerateFrom(SadSpec.Lpcm(6, [48000], [16]));

        Assert.Equal([2, 6], candidates.Select(c => c.Channels));
    }

    [Fact]
    public void Includes_only_two_channels_when_the_maximum_is_two()
    {
        var candidates = GenerateFrom(SadSpec.Lpcm(2, [48000], [16]));

        Assert.Equal([2], candidates.Select(c => c.Channels));
    }

    [Fact]
    public void Produces_no_candidates_when_the_maximum_is_below_two()
    {
        Assert.Empty(GenerateFrom(SadSpec.Lpcm(1, [48000], [16])));
    }

    [Fact]
    public void Includes_seven_channel_maximum_only_up_to_six_channels()
    {
        var candidates = GenerateFrom(SadSpec.Lpcm(7, [48000], [16]));

        Assert.Equal([2, 6], candidates.Select(c => c.Channels));
    }

    [Fact]
    public void Represents_sixteen_bit_with_a_sixteen_bit_container()
    {
        var candidate = Assert.Single(GenerateFrom(SadSpec.Lpcm(2, [48000], [16])));

        Assert.Equal(16, candidate.ContainerBits);
        Assert.Equal(16, candidate.EffectiveBits);
    }

    [Fact]
    public void Represents_twenty_bit_with_a_twenty_four_bit_container_and_twenty_valid_bits()
    {
        var candidate = Assert.Single(GenerateFrom(SadSpec.Lpcm(2, [48000], [20])));

        Assert.Equal(24, candidate.ContainerBits);
        Assert.Equal(20, candidate.EffectiveBits);
    }

    [Fact]
    public void Represents_twenty_four_bit_with_a_thirty_two_bit_container_and_twenty_four_valid_bits()
    {
        var candidate = Assert.Single(GenerateFrom(SadSpec.Lpcm(2, [48000], [24])));

        Assert.Equal(32, candidate.ContainerBits);
        Assert.Equal(24, candidate.EffectiveBits);
    }

    [Fact]
    public void Does_not_infer_twenty_bit_when_edid_does_not_advertise_it()
    {
        var candidates = GenerateFrom(SadSpec.Lpcm(2, [48000], [16, 24]));

        Assert.DoesNotContain(candidates, c => c.EffectiveBits == 20);
    }

    [Fact]
    public void Includes_only_the_sample_rates_edid_advertises()
    {
        var candidates = GenerateFrom(SadSpec.Lpcm(2, [44100, 192000], [16]));

        Assert.Equal([44100, 192000], candidates.Select(c => c.SampleRate));
    }

    [Fact]
    public void Supports_all_seven_defined_sample_rates()
    {
        var candidates = GenerateFrom(
            SadSpec.Lpcm(2, [32000, 44100, 48000, 88200, 96000, 176400, 192000], [16]));

        Assert.Equal([32000, 44100, 48000, 88200, 96000, 176400, 192000],
            candidates.Select(c => c.SampleRate));
    }

    [Fact]
    public void Uses_standard_channel_masks()
    {
        var candidates = GenerateFrom(SadSpec.Lpcm(8, [48000], [16]));

        Assert.Equal(0x3u, candidates.Single(c => c.Channels == 2).Format.ChannelMask);
        Assert.Equal(0x3Fu, candidates.Single(c => c.Channels == 6).Format.ChannelMask);
        Assert.Equal(0x63Fu, candidates.Single(c => c.Channels == 8).Format.ChannelMask);
    }

    [Fact]
    public void Deduplicates_identical_declarations_while_preserving_every_source()
    {
        var candidates = GenerateFrom(
            SadSpec.Lpcm(2, [48000], [16]),
            SadSpec.Lpcm(2, [48000], [16]));

        var candidate = Assert.Single(candidates);
        Assert.Equal(["ext1/adb0/sad0", "ext1/adb0/sad1"], candidate.SourceSadReferences);
    }

    [Fact]
    public void Merges_overlapping_declarations_from_different_sads()
    {
        var candidates = GenerateFrom(
            SadSpec.Lpcm(2, [48000], [16, 24]),
            SadSpec.Lpcm(8, [48000], [16]));

        var stereo16 = candidates.Single(c => c is { Channels: 2, SampleRate: 48000, EffectiveBits: 16 });
        Assert.Equal(["ext1/adb0/sad0", "ext1/adb0/sad1"], stereo16.SourceSadReferences);

        var stereo24 = candidates.Single(c => c is { Channels: 2, SampleRate: 48000, EffectiveBits: 24 });
        Assert.Equal(["ext1/adb0/sad0"], stereo24.SourceSadReferences);

        var eight16 = candidates.Single(c => c is { Channels: 8, EffectiveBits: 16 });
        Assert.Equal(["ext1/adb0/sad1"], eight16.SourceSadReferences);
    }

    [Fact]
    public void Ignores_non_lpcm_descriptors()
    {
        var candidates = GenerateFrom(
            SadSpec.NonLpcm(formatCode: 2, maxChannels: 8, rates: [48000]),
            SadSpec.NonLpcm(formatCode: 7, maxChannels: 8, rates: [48000]));

        Assert.Empty(candidates);
    }

    [Fact]
    public void Orders_candidates_by_channels_then_rate_then_bit_depth()
    {
        var candidates = GenerateFrom(SadSpec.Lpcm(6, [96000, 48000], [24, 16]));

        var ordered = candidates
            .Select(c => (c.Channels, c.SampleRate, c.EffectiveBits))
            .ToArray();

        Assert.Equal(
        [
            (2, 48000, 16), (2, 48000, 24),
            (2, 96000, 16), (2, 96000, 24),
            (6, 48000, 16), (6, 48000, 24),
            (6, 96000, 16), (6, 96000, 24),
        ], ordered);
    }

    [Fact]
    public void Orders_twenty_bit_between_sixteen_and_twenty_four()
    {
        var candidates = GenerateFrom(SadSpec.Lpcm(2, [48000], [16, 20, 24]));

        Assert.Equal([16, 20, 24], candidates.Select(c => c.EffectiveBits));
    }

    [Fact]
    public void Derives_candidates_from_sads_spread_across_multiple_cta_blocks()
    {
        var edid = new EdidBuilder()
            .WithCtaAudioBlock(SadSpec.Lpcm(2, [48000], [16]))
            .WithCtaAudioBlock(SadSpec.Lpcm(8, [48000], [16]))
            .Build();

        var candidates = CandidateGenerator.Generate(EdidParser.Parse(edid));

        Assert.Equal([2, 6, 8], candidates.Select(c => c.Channels));
        Assert.Equal(["ext1/adb0/sad0", "ext2/adb0/sad0"],
            candidates.Single(c => c.Channels == 2).SourceSadReferences);
    }

    [Fact]
    public void Computes_block_align_from_the_container_bit_depth()
    {
        var candidate = Assert.Single(GenerateFrom(SadSpec.Lpcm(2, [48000], [24])));

        Assert.Equal(8, candidate.Format.BlockAlign);           // 2 ch x 4 bytes
        Assert.Equal(384000, candidate.Format.AverageBytesPerSecond);
    }
}
