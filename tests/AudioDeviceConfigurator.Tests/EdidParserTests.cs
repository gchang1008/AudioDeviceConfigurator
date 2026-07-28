using AudioDeviceConfigurator.Edid;
using AudioDeviceConfigurator.Tests.Fixtures;

namespace AudioDeviceConfigurator.Tests;

public class EdidParserTests
{
    [Fact]
    public void Parses_identity_fields_from_a_valid_base_block()
    {
        var edid = new EdidBuilder()
            .WithManufacturer("DEL")
            .WithProductCode(0x4321)
            .WithSerialNumber(0xAABBCCDD)
            .WithMonitorName("U2723QE")
            .Build();

        var parsed = EdidParser.Parse(edid);

        Assert.Equal("DEL", parsed.ManufacturerId);
        Assert.Equal(0x4321, parsed.ProductCode);
        Assert.Equal(0xAABBCCDDu, parsed.SerialNumber);
        Assert.Equal("U2723QE", parsed.MonitorName);
        Assert.Equal(1, parsed.EdidVersion);
        Assert.Equal(4, parsed.EdidRevision);
        Assert.Equal(2024, parsed.ManufactureYear);
        Assert.Empty(parsed.AudioDescriptors);
    }

    [Fact]
    public void Parses_an_lpcm_short_audio_descriptor_from_a_cta_block()
    {
        var edid = new EdidBuilder()
            .WithCtaAudioBlock(SadSpec.Lpcm(8, [48000, 96000], [16, 24]))
            .Build();

        var parsed = EdidParser.Parse(edid);

        var sad = Assert.Single(parsed.AudioDescriptors);
        Assert.Equal(AudioFormatCode.Lpcm, sad.FormatCode);
        Assert.Equal(8, sad.MaxChannels);
        Assert.Equal([48000, 96000], sad.SampleRates);
        Assert.Equal([16, 24], sad.BitDepths);
        Assert.Equal("ext1/adb0/sad0", sad.SourceReference);
    }

    [Fact]
    public void Parses_every_supported_sample_rate_and_bit_depth_bit()
    {
        var edid = new EdidBuilder()
            .WithCtaAudioBlock(SadSpec.Lpcm(8, [32000, 44100, 48000, 88200, 96000, 176400, 192000], [16, 20, 24]))
            .Build();

        var sad = Assert.Single(EdidParser.Parse(edid).AudioDescriptors);

        Assert.Equal([32000, 44100, 48000, 88200, 96000, 176400, 192000], sad.SampleRates);
        Assert.Equal([16, 20, 24], sad.BitDepths);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(8)]
    public void Parses_declared_maximum_channel_count(int maxChannels)
    {
        var edid = new EdidBuilder()
            .WithCtaAudioBlock(SadSpec.Lpcm(maxChannels, [48000], [16]))
            .Build();

        Assert.Equal(maxChannels, EdidParser.Parse(edid).AudioDescriptors[0].MaxChannels);
    }

    [Fact]
    public void Retains_non_lpcm_descriptors_separately()
    {
        var edid = new EdidBuilder()
            .WithCtaAudioBlock(
                SadSpec.Lpcm(2, [48000], [16]),
                SadSpec.NonLpcm(formatCode: 2, maxChannels: 6, rates: [48000], maxBitRateKbps: 640))
            .Build();

        var parsed = EdidParser.Parse(edid);

        Assert.Equal(2, parsed.AudioDescriptors.Count);
        Assert.Single(parsed.LpcmDescriptors);
        var nonLpcm = Assert.Single(parsed.NonLpcmDescriptors);
        Assert.Equal(AudioFormatCode.Ac3, nonLpcm.FormatCode);
        Assert.Equal(640, nonLpcm.MaxBitRateKbps);
        Assert.Empty(nonLpcm.BitDepths);
    }

    [Fact]
    public void Parses_descriptors_across_multiple_cta_blocks_with_traceable_sources()
    {
        var edid = new EdidBuilder()
            .WithCtaAudioBlock(SadSpec.Lpcm(2, [48000], [16]))
            .WithCtaAudioBlock(SadSpec.Lpcm(8, [96000], [24]))
            .Build();

        var parsed = EdidParser.Parse(edid);

        Assert.Equal(2, parsed.ExtensionCount);
        Assert.Equal(["ext1/adb0/sad0", "ext2/adb0/sad0"],
            parsed.AudioDescriptors.Select(d => d.SourceReference));
    }

    [Fact]
    public void Parses_two_audio_data_blocks_within_one_cta_block()
    {
        var edid = new EdidBuilder()
            .WithCtaTwoAudioBlocks(
                [SadSpec.Lpcm(2, [48000], [16])],
                [SadSpec.Lpcm(8, [192000], [24])])
            .Build();

        var parsed = EdidParser.Parse(edid);

        Assert.Equal(["ext1/adb0/sad0", "ext1/adb1/sad0"],
            parsed.AudioDescriptors.Select(d => d.SourceReference));
    }

    [Fact]
    public void Retains_duplicate_sad_declarations_as_distinct_sources()
    {
        var edid = new EdidBuilder()
            .WithCtaAudioBlock(
                SadSpec.Lpcm(2, [48000], [16]),
                SadSpec.Lpcm(2, [48000], [16]))
            .Build();

        var parsed = EdidParser.Parse(edid);

        Assert.Equal(2, parsed.AudioDescriptors.Count);
        Assert.Equal(["ext1/adb0/sad0", "ext1/adb0/sad1"],
            parsed.AudioDescriptors.Select(d => d.SourceReference));
    }

    [Fact]
    public void Checksums_but_ignores_non_cta_extension_blocks()
    {
        var edid = new EdidBuilder()
            .WithUnknownExtensionBlock()
            .WithCtaAudioBlock(SadSpec.Lpcm(6, [48000], [16, 20]))
            .Build();

        var parsed = EdidParser.Parse(edid);

        var sad = Assert.Single(parsed.AudioDescriptors);
        Assert.Equal("ext2/adb0/sad0", sad.SourceReference);
    }

    [Fact]
    public void Preserves_raw_bytes_for_the_declared_block_count()
    {
        var edid = new EdidBuilder()
            .WithCtaAudioBlock(SadSpec.Lpcm(2, [48000], [16]))
            .Build();

        var parsed = EdidParser.Parse(edid);

        Assert.Equal(256, parsed.RawBytes.Length);
        Assert.Equal(edid, parsed.RawBytes);
    }

    [Fact]
    public void Rejects_an_invalid_header()
    {
        var edid = new EdidBuilder().WithCorruptHeader().Build();

        var ex = Assert.Throws<EdidValidationException>(() => EdidParser.Parse(edid));
        Assert.Contains("header", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_a_base_block_shorter_than_128_bytes()
    {
        var edid = new EdidBuilder().TruncatedTo(64).Build();

        var ex = Assert.Throws<EdidValidationException>(() => EdidParser.Parse(edid));
        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_an_extension_count_that_exceeds_the_supplied_bytes()
    {
        var edid = new EdidBuilder()
            .WithCtaAudioBlock(SadSpec.Lpcm(2, [48000], [16]))
            .WithDeclaredExtensionCount(3)
            .Build();

        var ex = Assert.Throws<EdidValidationException>(() => EdidParser.Parse(edid));
        Assert.Contains("extension", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_a_truncated_extension_block()
    {
        var edid = new EdidBuilder()
            .WithCtaAudioBlock(SadSpec.Lpcm(2, [48000], [16]))
            .TruncatedTo(200)
            .Build();

        Assert.Throws<EdidValidationException>(() => EdidParser.Parse(edid));
    }

    [Fact]
    public void Rejects_a_base_block_checksum_failure()
    {
        var edid = new EdidBuilder().WithCorruptChecksum(0).Build();

        var ex = Assert.Throws<EdidValidationException>(() => EdidParser.Parse(edid));
        Assert.Contains("block 0 checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Rejects_a_checksum_failure_in_any_extension_block_position(int blockIndex)
    {
        var edid = new EdidBuilder()
            .WithCtaAudioBlock(SadSpec.Lpcm(2, [48000], [16]))
            .WithCtaAudioBlock(SadSpec.Lpcm(8, [96000], [24]))
            .WithCorruptChecksum(blockIndex)
            .Build();

        var ex = Assert.Throws<EdidValidationException>(() => EdidParser.Parse(edid));
        Assert.Contains($"block {blockIndex} checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_an_audio_data_block_whose_length_is_not_a_multiple_of_three()
    {
        var block = new byte[128];
        block[0] = 0x02;
        block[1] = 0x03;
        block[2] = 4 + 5;
        block[4] = (1 << 5) | 4; // Audio Data Block declaring 4 payload bytes
        var edid = new EdidBuilder().WithRawExtensionBlock(block).Build();

        var ex = Assert.Throws<EdidValidationException>(() => EdidParser.Parse(edid));
        Assert.Contains("multiple of 3", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_a_data_block_that_overruns_the_collection()
    {
        var block = new byte[128];
        block[0] = 0x02;
        block[1] = 0x03;
        block[2] = 8; // collection ends at offset 8
        block[4] = (1 << 5) | 9; // but the block claims 9 payload bytes
        var edid = new EdidBuilder().WithRawExtensionBlock(block).Build();

        var ex = Assert.Throws<EdidValidationException>(() => EdidParser.Parse(edid));
        Assert.Contains("overruns", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accepts_a_cta_block_with_no_data_block_collection()
    {
        var block = new byte[128];
        block[0] = 0x02;
        block[1] = 0x03;
        block[2] = 0; // no DTDs and no data blocks
        var edid = new EdidBuilder().WithRawExtensionBlock(block).Build();

        Assert.Empty(EdidParser.Parse(edid).AudioDescriptors);
    }
}
