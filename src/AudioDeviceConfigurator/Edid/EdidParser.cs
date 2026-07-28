namespace AudioDeviceConfigurator.Edid;

/// <summary>
/// Strict EDID 1.x + CTA-861 parser. Every declared block must be complete and checksum-clean;
/// anything else throws so the tool can never fall back to assumed capabilities.
/// </summary>
public static class EdidParser
{
    public const int BlockSize = 128;

    private static readonly byte[] Header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    private static readonly int[] SampleRateBits =
        [32000, 44100, 48000, 88200, 96000, 176400, 192000];

    private static readonly int[] LpcmBitDepthBits = [16, 20, 24];

    public static ParsedEdid Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length < BlockSize)
        {
            throw new EdidValidationException(
                $"EDID is truncated: expected at least {BlockSize} bytes but got {data.Length}.");
        }

        for (var i = 0; i < Header.Length; i++)
        {
            if (data[i] != Header[i])
            {
                throw new EdidValidationException("EDID header signature is invalid.");
            }
        }

        ValidateChecksum(data, 0);

        var extensionCount = data[126];
        var requiredLength = BlockSize * (extensionCount + 1);
        if (data.Length < requiredLength)
        {
            throw new EdidValidationException(
                $"EDID declares {extensionCount} extension block(s) requiring {requiredLength} bytes but only {data.Length} bytes are present.");
        }

        var descriptors = new List<ShortAudioDescriptor>();
        for (var ext = 1; ext <= extensionCount; ext++)
        {
            var offset = ext * BlockSize;
            ValidateChecksum(data, offset);
            if (data[offset] == 0x02)
            {
                ParseCtaBlock(data, offset, ext, descriptors);
            }
        }

        return new ParsedEdid(
            ManufacturerId: ReadManufacturerId(data),
            ProductCode: data[10] | (data[11] << 8),
            SerialNumber: (uint)(data[12] | (data[13] << 8) | (data[14] << 16) | (data[15] << 24)),
            ManufactureWeek: data[16],
            ManufactureYear: data[17] + 1990,
            MonitorName: ReadMonitorName(data),
            EdidVersion: data[18],
            EdidRevision: data[19],
            ExtensionCount: extensionCount,
            AudioDescriptors: descriptors,
            RawBytes: data[..requiredLength]);
    }

    private static void ValidateChecksum(byte[] data, int offset)
    {
        var sum = 0;
        for (var i = 0; i < BlockSize; i++)
        {
            sum += data[offset + i];
        }

        if ((byte)sum != 0)
        {
            var block = offset / BlockSize;
            throw new EdidValidationException(
                $"EDID block {block} checksum is invalid (sum mod 256 = {(byte)sum}).");
        }
    }

    private static void ParseCtaBlock(byte[] data, int offset, int extIndex, List<ShortAudioDescriptor> sink)
    {
        var revision = data[offset + 1];
        if (revision < 3)
        {
            // CTA revision 1/2 blocks carry no Data Block Collection.
            return;
        }

        var dtdOffset = data[offset + 2];
        if (dtdOffset == 0)
        {
            // No detailed timing descriptors and no data block collection.
            return;
        }

        if (dtdOffset < 4 || dtdOffset > BlockSize)
        {
            throw new EdidValidationException(
                $"CTA block {extIndex} declares an out-of-range data block collection end offset ({dtdOffset}).");
        }

        var cursor = offset + 4;
        var end = offset + dtdOffset;
        var dataBlockIndex = 0;

        while (cursor < end)
        {
            var header = data[cursor];
            var tag = header >> 5;
            var length = header & 0x1F;

            if (cursor + 1 + length > end)
            {
                throw new EdidValidationException(
                    $"CTA block {extIndex} data block {dataBlockIndex} overruns the data block collection.");
            }

            if (tag == 1)
            {
                if (length % 3 != 0)
                {
                    throw new EdidValidationException(
                        $"CTA block {extIndex} Audio Data Block {dataBlockIndex} length {length} is not a multiple of 3.");
                }

                for (var d = 0; d < length / 3; d++)
                {
                    var p = cursor + 1 + (d * 3);
                    sink.Add(ParseSad(data, p, extIndex, dataBlockIndex, d));
                }
            }

            cursor += 1 + length;
            dataBlockIndex++;
        }
    }

    private static ShortAudioDescriptor ParseSad(byte[] data, int p, int extIndex, int dataBlockIndex, int descriptorIndex)
    {
        var b0 = data[p];
        var b1 = data[p + 1];
        var b2 = data[p + 2];

        var formatCode = (AudioFormatCode)((b0 >> 3) & 0x0F);
        var maxChannels = (b0 & 0x07) + 1;

        var rates = new List<int>();
        for (var i = 0; i < SampleRateBits.Length; i++)
        {
            if ((b1 & (1 << i)) != 0)
            {
                rates.Add(SampleRateBits[i]);
            }
        }

        var depths = new List<int>();
        var maxBitRate = 0;
        if (formatCode == AudioFormatCode.Lpcm)
        {
            for (var i = 0; i < LpcmBitDepthBits.Length; i++)
            {
                if ((b2 & (1 << i)) != 0)
                {
                    depths.Add(LpcmBitDepthBits[i]);
                }
            }
        }
        else
        {
            maxBitRate = b2 * 8;
        }

        return new ShortAudioDescriptor(
            ExtensionBlockIndex: extIndex,
            DataBlockIndex: dataBlockIndex,
            DescriptorIndex: descriptorIndex,
            FormatCode: formatCode,
            MaxChannels: maxChannels,
            SampleRates: rates,
            BitDepths: depths,
            MaxBitRateKbps: maxBitRate,
            RawBytes: [b0, b1, b2]);
    }

    private static string ReadManufacturerId(byte[] data)
    {
        var value = (data[8] << 8) | data[9];
        Span<char> chars =
        [
            (char)('A' + ((value >> 10) & 0x1F) - 1),
            (char)('A' + ((value >> 5) & 0x1F) - 1),
            (char)('A' + (value & 0x1F) - 1),
        ];
        return new string(chars);
    }

    private static string? ReadMonitorName(byte[] data)
    {
        for (var i = 54; i <= 108; i += 18)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 0 || data[i + 3] != 0xFC)
            {
                continue;
            }

            var text = new char[13];
            var count = 0;
            for (var j = 0; j < 13; j++)
            {
                var c = data[i + 5 + j];
                if (c == 0x0A)
                {
                    break;
                }

                text[count++] = (char)c;
            }

            var name = new string(text, 0, count).Trim();
            return name.Length == 0 ? null : name;
        }

        return null;
    }
}
