using System.Runtime.InteropServices;

namespace AudioDeviceConfigurator.Audio;

/// <summary>Decoded WAV payload and the mix format WASAPI expects for playback.</summary>
public sealed record WaveSource(WaveFormat Format, byte[] PcmData)
{
    public static WaveSource Parse(byte[] wavBytes)
    {
        if (wavBytes.Length < 44
            || ReadUInt32(wavBytes, 0) != 0x46464952u
            || ReadUInt32(wavBytes, 8) != 0x45564157u)
        {
            throw new InvalidDataException("WAV file is missing RIFF/WAVE markers.");
        }

        var position = 12; // skip RIFF(4) + size(4) + WAVE(4)
        ushort formatTag = 0;
        ushort channels = 0;
        uint sampleRate = 0;
        ushort blockAlign = 0;
        ushort bitsPerSample = 0;
        uint dataOffset = 0;
        uint dataLength = 0;
        while (position + 8 <= wavBytes.Length)
        {
            var chunkId = ReadUInt32(wavBytes, position);
            var chunkSize = ReadUInt32(wavBytes, position + 4);
            if (chunkId == 0x20746D66u) // "fmt "
            {
                formatTag = ReadUInt16(wavBytes, position + 8);
                channels = ReadUInt16(wavBytes, position + 10);
                sampleRate = ReadUInt32(wavBytes, position + 12);
                blockAlign = ReadUInt16(wavBytes, position + 20);
                bitsPerSample = ReadUInt16(wavBytes, position + 22);
            }
            else if (chunkId == 0x61746164u) // "data"
            {
                dataOffset = (uint)position + 8;
                dataLength = chunkSize;
                break;
            }

            position += 8 + (int)chunkSize + ((int)chunkSize & 1);
        }

        if (dataOffset == 0 || formatTag == 0 || channels == 0 || sampleRate == 0 || bitsPerSample == 0)
        {
            throw new InvalidDataException("WAV file is missing fmt or data chunk.");
        }

        if (formatTag != 0x0001 && formatTag != 0xFFFE)
        {
            throw new InvalidDataException($"WAV format tag 0x{formatTag:X4} is not supported.");
        }

        var pcm = new byte[dataLength];
        Buffer.BlockCopy(wavBytes, (int)dataOffset, pcm, 0, (int)dataLength);

        var format = new WaveFormat(
            SampleRate: (int)sampleRate,
            Channels: channels,
            BitsPerSample: bitsPerSample,
            BlockAlign: blockAlign == 0 ? (ushort)(channels * bitsPerSample / 8) : blockAlign);
        return new WaveSource(format, pcm);
    }

    private static uint ReadUInt32(byte[] buffer, int offset) =>
        BitConverter.ToUInt32(buffer, offset);

    private static ushort ReadUInt16(byte[] buffer, int offset) =>
        BitConverter.ToUInt16(buffer, offset);
}

public sealed record WaveFormat(int SampleRate, ushort Channels, ushort BitsPerSample, ushort BlockAlign)
{
    public int BytesPerFrame => Channels * BitsPerSample / 8;
}