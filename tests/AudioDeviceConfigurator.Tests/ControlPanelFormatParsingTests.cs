using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Windows;

namespace AudioDeviceConfigurator.Tests;

public sealed class ControlPanelFormatParsingTests
{
    [Theory]
    [InlineData("16 位元，44100 Hz (CD 音質)", 16, 44100, 16)]
    [InlineData("24 位元，48000 Hz (錄音室品質)", 24, 48000, 32)]
    [InlineData("32 位元，192000 Hz (錄音室品質)", 32, 192000, 32)]
    [InlineData("2 channel, 16 bit, 48000 Hz", 16, 48000, 16)]
    public void Parses_default_format_without_requiring_channel_text(
        string text,
        int bits,
        int rate,
        int container)
    {
        var item = ControlPanelFormatProvider.ParseFormatItem(0, text);

        Assert.Equal(ControlPanelParseStatus.Parsed, item.ParseStatus);
        Assert.Equal(bits, item.EffectiveBits);
        Assert.Equal(rate, item.SampleRate);
        Assert.Equal(container, item.ContainerBits);
    }
}
