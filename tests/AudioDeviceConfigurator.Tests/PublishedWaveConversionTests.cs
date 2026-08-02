using AudioDeviceConfigurator.Audio;

namespace AudioDeviceConfigurator.Tests;

public sealed class PublishedWaveConversionTests
{
    [Fact]
    public void Published_test_audio_converts_to_non_silent_stereo_mix()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "test_audio.wav"));
        var source = WaveSource.Parse(File.ReadAllBytes(path));
        Assert.Equal(new WaveFormat(48000, 8, 24, 24), source.Format);
        Assert.Contains(source.PcmData.Take(48000 * 3 * 24), value => value != 0);
        Assert.Contains(Enumerable.Range(0, 48000 * 3)
            .Select(frame => source.PcmData[frame * 24]
                | source.PcmData[frame * 24 + 1] << 8
                | source.PcmData[frame * 24 + 2] << 16), value => value != 0);

        var converted = SharedModePcmConverter.Convert(
            source,
            new WaveFormat(48000, 2, 32, 8),
            targetIsFloat: false);

        var peak = 0;
        for (var offset = 0; offset + 4 <= Math.Min(converted.PcmData.Length, 48000 * 3 * 8); offset += 4)
        {
            peak = Math.Max(peak, Math.Abs(BitConverter.ToInt32(converted.PcmData, offset)));
        }
        Assert.True(peak > 1_000_000, $"Converted PCM peak was {peak}.");
    }
}
