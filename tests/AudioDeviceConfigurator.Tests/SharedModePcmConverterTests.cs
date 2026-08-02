using AudioDeviceConfigurator.Audio;

namespace AudioDeviceConfigurator.Tests;

public sealed class SharedModePcmConverterTests
{
    [Fact]
    public void Converts_eight_channel_24_bit_pcm_to_stereo_32_bit_float_mix()
    {
        var pcm = new byte[8 * 3];
        WriteInt24(pcm, 0, 0x400000);
        WriteInt24(pcm, 3, -0x400000);
        var source = new WaveSource(new WaveFormat(48000, 8, 24, 24), pcm);
        var target = new WaveFormat(48000, 2, 32, 8);

        var converted = SharedModePcmConverter.Convert(source, target, targetIsFloat: true);

        Assert.Equal(target, converted.Format);
        Assert.Equal(8, converted.PcmData.Length);
        Assert.Equal(0.5f, BitConverter.ToSingle(converted.PcmData, 0), 3);
        Assert.Equal(-0.5f, BitConverter.ToSingle(converted.PcmData, 4), 3);
    }

    [Fact]
    public void Resamples_pcm_to_the_endpoint_mix_rate()
    {
        var pcm = new byte[2 * 2 * 2];
        BitConverter.GetBytes((short)0).CopyTo(pcm, 0);
        BitConverter.GetBytes((short)0).CopyTo(pcm, 2);
        BitConverter.GetBytes(short.MaxValue).CopyTo(pcm, 4);
        BitConverter.GetBytes(short.MaxValue).CopyTo(pcm, 6);
        var source = new WaveSource(new WaveFormat(24000, 2, 16, 4), pcm);
        var target = new WaveFormat(48000, 2, 16, 4);

        var converted = SharedModePcmConverter.Convert(source, target, targetIsFloat: false);

        Assert.Equal(16, converted.PcmData.Length);
        Assert.InRange(BitConverter.ToInt16(converted.PcmData, 4), 16000, 17000);
    }

    private static void WriteInt24(byte[] target, int offset, int value)
    {
        target[offset] = (byte)value;
        target[offset + 1] = (byte)(value >> 8);
        target[offset + 2] = (byte)(value >> 16);
    }
}
