namespace AudioDeviceConfigurator.Tests.Fixtures;

/// <summary>Builds complete binary EDID images so parser behaviour can be exercised realistically.</summary>
public sealed class EdidBuilder
{
    private readonly List<byte[]> _ctaBlocks = [];
    private string _manufacturerId = "ABC";
    private int _productCode = 0x1234;
    private uint _serialNumber = 0x11223344;
    private string? _monitorName = "TEST MONITOR";
    private bool _corruptHeader;
    private int? _declaredExtensionCount;
    private readonly HashSet<int> _corruptChecksumBlocks = [];
    private int? _truncateTo;

    public EdidBuilder WithManufacturer(string id)
    {
        _manufacturerId = id;
        return this;
    }

    public EdidBuilder WithProductCode(int code)
    {
        _productCode = code;
        return this;
    }

    public EdidBuilder WithSerialNumber(uint serial)
    {
        _serialNumber = serial;
        return this;
    }

    public EdidBuilder WithMonitorName(string? name)
    {
        _monitorName = name;
        return this;
    }

    public EdidBuilder WithCorruptHeader()
    {
        _corruptHeader = true;
        return this;
    }

    public EdidBuilder WithDeclaredExtensionCount(int count)
    {
        _declaredExtensionCount = count;
        return this;
    }

    public EdidBuilder WithCorruptChecksum(int blockIndex)
    {
        _corruptChecksumBlocks.Add(blockIndex);
        return this;
    }

    public EdidBuilder TruncatedTo(int bytes)
    {
        _truncateTo = bytes;
        return this;
    }

    /// <summary>Adds a CTA-861 extension block containing one Audio Data Block built from the given SADs.</summary>
    public EdidBuilder WithCtaAudioBlock(params SadSpec[] sads)
    {
        var block = new byte[128];
        block[0] = 0x02; // CTA extension tag
        block[1] = 0x03; // revision 3
        block[3] = 0x00; // no underscan/audio/ycc flags needed

        var payload = new List<byte>();
        if (sads.Length > 0)
        {
            var length = sads.Length * 3;
            payload.Add((byte)((1 << 5) | length)); // tag 1 = Audio Data Block
            foreach (var sad in sads)
            {
                payload.AddRange(sad.ToBytes());
            }
        }

        payload.CopyTo(block, 4);
        block[2] = (byte)(4 + payload.Count); // DTD start = end of data block collection
        _ctaBlocks.Add(block);
        return this;
    }

    /// <summary>Adds a CTA extension block with two separate Audio Data Blocks.</summary>
    public EdidBuilder WithCtaTwoAudioBlocks(SadSpec[] first, SadSpec[] second)
    {
        var block = new byte[128];
        block[0] = 0x02;
        block[1] = 0x03;

        var payload = new List<byte>();
        foreach (var group in new[] { first, second })
        {
            payload.Add((byte)((1 << 5) | (group.Length * 3)));
            foreach (var sad in group)
            {
                payload.AddRange(sad.ToBytes());
            }
        }

        payload.CopyTo(block, 4);
        block[2] = (byte)(4 + payload.Count);
        _ctaBlocks.Add(block);
        return this;
    }

    /// <summary>Adds a non-CTA extension block, which the parser must checksum but otherwise ignore.</summary>
    public EdidBuilder WithUnknownExtensionBlock(byte tag = 0x70)
    {
        var block = new byte[128];
        block[0] = tag;
        _ctaBlocks.Add(block);
        return this;
    }

    /// <summary>Adds a raw pre-built extension block, used for malformed-collection scenarios.</summary>
    public EdidBuilder WithRawExtensionBlock(byte[] block)
    {
        if (block.Length != 128)
        {
            throw new ArgumentException("Extension block must be exactly 128 bytes.", nameof(block));
        }

        _ctaBlocks.Add((byte[])block.Clone());
        return this;
    }

    public byte[] Build()
    {
        var baseBlock = new byte[128];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(baseBlock, 0);
        if (_corruptHeader)
        {
            baseBlock[1] = 0x00;
        }

        var mfg = ((_manufacturerId[0] - 'A' + 1) << 10)
                  | ((_manufacturerId[1] - 'A' + 1) << 5)
                  | (_manufacturerId[2] - 'A' + 1);
        baseBlock[8] = (byte)(mfg >> 8);
        baseBlock[9] = (byte)(mfg & 0xFF);
        baseBlock[10] = (byte)(_productCode & 0xFF);
        baseBlock[11] = (byte)(_productCode >> 8);
        baseBlock[12] = (byte)(_serialNumber & 0xFF);
        baseBlock[13] = (byte)((_serialNumber >> 8) & 0xFF);
        baseBlock[14] = (byte)((_serialNumber >> 16) & 0xFF);
        baseBlock[15] = (byte)((_serialNumber >> 24) & 0xFF);
        baseBlock[16] = 10; // week
        baseBlock[17] = 34; // 2024
        baseBlock[18] = 1;  // version
        baseBlock[19] = 4;  // revision

        if (_monitorName is not null)
        {
            baseBlock[54 + 3] = 0xFC;
            var chars = _monitorName.PadRight(13, '\n')[..13];
            for (var i = 0; i < 13; i++)
            {
                baseBlock[54 + 5 + i] = (byte)chars[i];
            }
        }

        baseBlock[126] = (byte)(_declaredExtensionCount ?? _ctaBlocks.Count);

        var result = new List<byte>();
        result.AddRange(Checksum(baseBlock, _corruptChecksumBlocks.Contains(0)));
        for (var i = 0; i < _ctaBlocks.Count; i++)
        {
            result.AddRange(Checksum(_ctaBlocks[i], _corruptChecksumBlocks.Contains(i + 1)));
        }

        var bytes = result.ToArray();
        return _truncateTo is { } n && n < bytes.Length ? bytes[..n] : bytes;
    }

    private static byte[] Checksum(byte[] block, bool corrupt)
    {
        var copy = (byte[])block.Clone();
        var sum = 0;
        for (var i = 0; i < 127; i++)
        {
            sum += copy[i];
        }

        copy[127] = (byte)((256 - (sum % 256)) % 256);
        if (corrupt)
        {
            copy[127] ^= 0xFF;
        }

        return copy;
    }
}

/// <summary>Declarative description of one Short Audio Descriptor.</summary>
public sealed record SadSpec(
    int FormatCode,
    int MaxChannels,
    int[] SampleRates,
    int[] BitDepths = null!,
    int MaxBitRateKbps = 0)
{
    private static readonly int[] RateOrder = [32000, 44100, 48000, 88200, 96000, 176400, 192000];
    private static readonly int[] DepthOrder = [16, 20, 24];

    public static SadSpec Lpcm(int maxChannels, int[] rates, int[] depths) =>
        new(1, maxChannels, rates, depths);

    public static SadSpec NonLpcm(int formatCode, int maxChannels, int[] rates, int maxBitRateKbps = 640) =>
        new(formatCode, maxChannels, rates, [], maxBitRateKbps);

    public byte[] ToBytes()
    {
        var b0 = (byte)(((FormatCode & 0x0F) << 3) | ((MaxChannels - 1) & 0x07));

        byte b1 = 0;
        foreach (var rate in SampleRates)
        {
            var idx = Array.IndexOf(RateOrder, rate);
            if (idx < 0)
            {
                throw new ArgumentException($"Unsupported sample rate {rate}.");
            }

            b1 |= (byte)(1 << idx);
        }

        byte b2;
        if (FormatCode == 1)
        {
            b2 = 0;
            foreach (var depth in BitDepths ?? [])
            {
                var idx = Array.IndexOf(DepthOrder, depth);
                if (idx < 0)
                {
                    throw new ArgumentException($"Unsupported bit depth {depth}.");
                }

                b2 |= (byte)(1 << idx);
            }
        }
        else
        {
            b2 = (byte)(MaxBitRateKbps / 8);
        }

        return [b0, b1, b2];
    }
}
