using AudioDeviceConfigurator.Windows;

namespace AudioDeviceConfigurator.Tests;

public sealed class ControlPanelSpeakerConfigurationTests
{
    [Theory]
    [InlineData("Stereo", 2)]
    [InlineData("立體聲", 2)]
    [InlineData("Quadraphonic", 4)]
    [InlineData("四聲道", 4)]
    [InlineData("Surround", 4)]
    [InlineData("環場", 4)]
    [InlineData("5.1 Surround", 6)]
    [InlineData("5.1 環場音效", 6)]
    [InlineData("7.1 Surround", 8)]
    [InlineData("7.1 環場音效", 8)]
    public void Parses_supported_speaker_configuration_channel_counts(string text, int expected)
    {
        Assert.Equal(expected, ControlPanelFormatProvider.ParseSpeakerChannels(text));
    }

    [Fact]
    public void Unknown_speaker_configuration_returns_null()
    {
        Assert.Null(ControlPanelFormatProvider.ParseSpeakerChannels("Mono"));
    }
}
