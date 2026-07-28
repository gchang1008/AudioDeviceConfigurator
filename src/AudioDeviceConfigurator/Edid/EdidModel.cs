namespace AudioDeviceConfigurator.Edid;

/// <summary>CTA-861 audio format codes (Table 37).</summary>
public enum AudioFormatCode
{
    Reserved0 = 0,
    Lpcm = 1,
    Ac3 = 2,
    Mpeg1 = 3,
    Mp3 = 4,
    Mpeg2 = 5,
    Aac = 6,
    Dts = 7,
    Atrac = 8,
    Dsd = 9,
    EnhancedAc3 = 10,
    DtsHd = 11,
    Mlp = 12,
    Dst = 13,
    WmaPro = 14,
    Extended = 15,
}

/// <summary>A parsed CTA-861 Short Audio Descriptor (3 bytes).</summary>
public sealed record ShortAudioDescriptor(
    int ExtensionBlockIndex,
    int DataBlockIndex,
    int DescriptorIndex,
    AudioFormatCode FormatCode,
    int MaxChannels,
    IReadOnlyList<int> SampleRates,
    IReadOnlyList<int> BitDepths,
    int MaxBitRateKbps,
    byte[] RawBytes)
{
    public bool IsLpcm => FormatCode == AudioFormatCode.Lpcm;

    /// <summary>Stable identifier used to trace a candidate format back to its EDID source.</summary>
    public string SourceReference =>
        $"ext{ExtensionBlockIndex}/adb{DataBlockIndex}/sad{DescriptorIndex}";
}

/// <summary>Everything the parser extracted from a monitor's EDID blocks.</summary>
public sealed record ParsedEdid(
    string ManufacturerId,
    int ProductCode,
    uint SerialNumber,
    int ManufactureWeek,
    int ManufactureYear,
    string? MonitorName,
    int EdidVersion,
    int EdidRevision,
    int ExtensionCount,
    IReadOnlyList<ShortAudioDescriptor> AudioDescriptors,
    byte[] RawBytes)
{
    public IReadOnlyList<ShortAudioDescriptor> LpcmDescriptors =>
        AudioDescriptors.Where(d => d.IsLpcm).ToList();

    public IReadOnlyList<ShortAudioDescriptor> NonLpcmDescriptors =>
        AudioDescriptors.Where(d => !d.IsLpcm).ToList();
}

public sealed class EdidValidationException(string message) : Exception(message);
