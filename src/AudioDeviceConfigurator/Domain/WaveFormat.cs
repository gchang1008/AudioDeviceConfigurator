namespace AudioDeviceConfigurator.Domain;

/// <summary>
/// A PCM wave format expressed with both container and effective (valid) bit depth.
/// Effective bit depth is what the user sees in Control Panel; container depth is diagnostic.
/// </summary>
public sealed record WaveFormat(
    int Channels,
    int SampleRate,
    int ContainerBits,
    int ValidBits,
    uint ChannelMask,
    bool Extensible)
{
    public int BlockAlign => Channels * (ContainerBits / 8);

    public int AverageBytesPerSecond => SampleRate * BlockAlign;

    public override string ToString() =>
        $"{Channels} ch, {ValidBits} bit, {SampleRate} Hz";
}

/// <summary>A default-format structure as saved by SVCL, with everything needed for diagnostics.</summary>
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

    /// <summary>Effective bit depth: valid bits for WAVEFORMATEXTENSIBLE, container bits otherwise.</summary>
    public int EffectiveBits => FormatTag == WaveFormatExtensible && ValidBits > 0 ? ValidBits : ContainerBits;

    public override string ToString() =>
        $"{Channels} ch, {EffectiveBits} bit, {SampleRate} Hz";
}
