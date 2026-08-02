using AudioDeviceConfigurator.Audio;

namespace AudioDeviceConfigurator.Tests;

public sealed class WaveSourceTests
{
    [Fact]
    public void Parse_decodes_valid_RIFF_WAVE_with_data_chunk()
    {
        var bytes = BuildWav(channels: 2, sampleRate: 48000, bitsPerSample: 16, pcmBytes: 1024);

        var source = WaveSource.Parse(bytes);

        Assert.Equal(48000, source.Format.SampleRate);
        Assert.Equal(2, source.Format.Channels);
        Assert.Equal(16, source.Format.BitsPerSample);
        Assert.Equal(4, source.Format.BlockAlign);
        Assert.Equal(1024, source.PcmData.Length);
    }

    [Fact]
    public void Parse_rejects_missing_RIFF_marker()
    {
        var bytes = new byte[64];
        Assert.Throws<InvalidDataException>(() => WaveSource.Parse(bytes));
    }

    [Fact]
    public void Parse_throws_for_unsupported_format_tag()
    {
        var bytes = BuildWav(formatTag: 0x0006, channels: 2, sampleRate: 48000, bitsPerSample: 16, pcmBytes: 64);
        Assert.Throws<InvalidDataException>(() => WaveSource.Parse(bytes));
    }

    [Fact]
    public void Parse_throws_when_data_chunk_missing()
    {
        var bytes = BuildWav(channels: 2, sampleRate: 48000, bitsPerSample: 16, pcmBytes: 0, includeData: false);
        Assert.Throws<InvalidDataException>(() => WaveSource.Parse(bytes));
    }

    [Fact]
    public void Parse_real_test_audio_wav_in_repo()
    {
        // The repo's test_audio.wav is 8-channel, 48 kHz PCM. Run only when the file is present
        // so the test does not break other contributor environments.
        var path = Path.Combine(AppContext.BaseDirectory, "test_audio.wav");
        if (!File.Exists(path))
        {
            return;
        }

        var source = WaveSource.Parse(File.ReadAllBytes(path));

        Assert.Equal(8, source.Format.Channels);
        Assert.Equal(48000, source.Format.SampleRate);
        Assert.NotEmpty(source.PcmData);
    }

    private static byte[] BuildWav(
        ushort formatTag = 0x0001,
        ushort channels = 2,
        uint sampleRate = 48000,
        ushort bitsPerSample = 16,
        int pcmBytes = 0,
        bool includeData = true)
    {
        var blockAlign = (ushort)(channels * bitsPerSample / 8);
        var avgBytesPerSec = sampleRate * blockAlign;
        var fmtSize = 16u;
        var riffSize = (uint)(4 + 8 + fmtSize + (includeData ? 8 + pcmBytes : 0));

        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        writer.Write((uint)0x46464952); // "RIFF"
        writer.Write(riffSize);
        writer.Write((uint)0x45564157); // "WAVE"
        writer.Write((uint)0x20746D66); // "fmt "
        writer.Write(fmtSize);
        writer.Write(formatTag);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(avgBytesPerSec);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        if (includeData)
        {
            writer.Write((uint)0x61746164); // "data"
            writer.Write((uint)pcmBytes);
            writer.Write(new byte[pcmBytes]);
        }
        writer.Flush();
        return stream.ToArray();
    }
}