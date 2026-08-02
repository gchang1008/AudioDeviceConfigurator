namespace AudioDeviceConfigurator.Domain;

/// <summary>A default-format structure saved by SVCL.</summary>
public sealed record SavedFormat(
    int FormatTag,
    int Channels,
    int SampleRate,
    int ContainerBits,
    int ValidBits,
    uint ChannelMask,
    byte[] RawBytes)
{
    public const int WaveFormatExtensible = 0xFFFE;

    public int EffectiveBits =>
        FormatTag == WaveFormatExtensible && ValidBits > 0 ? ValidBits : ContainerBits;

    public override string ToString() =>
        $"{Channels} ch, {EffectiveBits} bit, {SampleRate} Hz";
}
