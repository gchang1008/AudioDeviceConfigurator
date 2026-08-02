using System.Buffers.Binary;

namespace AudioDeviceConfigurator.Audio;

internal static class SharedModePcmConverter
{
    public static WaveSource Convert(WaveSource source, WaveFormat target, bool targetIsFloat)
    {
        if (source.Format.Channels == 0 || source.Format.BlockAlign == 0
            || source.PcmData.Length < source.Format.BlockAlign)
        {
            throw new InvalidDataException("The WAV contains no complete PCM frames.");
        }
        if (target.Channels == 0 || target.BlockAlign == 0)
        {
            throw new InvalidDataException("The endpoint mix format is invalid.");
        }
        if (source.Format.BitsPerSample is not (16 or 24 or 32))
        {
            throw new InvalidDataException(
                $"WAV PCM depth {source.Format.BitsPerSample} is not supported for Shared Mode conversion.");
        }
        if (targetIsFloat && target.BitsPerSample != 32)
        {
            throw new InvalidDataException("Only 32-bit float endpoint mix formats are supported.");
        }
        if (!targetIsFloat && target.BitsPerSample is not (16 or 24 or 32))
        {
            throw new InvalidDataException(
                $"Endpoint PCM depth {target.BitsPerSample} is not supported.");
        }

        var sourceFrames = source.PcmData.Length / source.Format.BlockAlign;
        var targetFrames = Math.Max(1,
            (int)Math.Ceiling(sourceFrames * (double)target.SampleRate / source.Format.SampleRate));
        var output = new byte[checked(targetFrames * target.BlockAlign)];
        var sourceFrame = new float[source.Format.Channels];
        var nextFrame = new float[source.Format.Channels];
        var targetFrame = new float[target.Channels];

        for (var frame = 0; frame < targetFrames; frame++)
        {
            var sourcePosition = frame * (double)source.Format.SampleRate / target.SampleRate;
            var first = Math.Min((int)sourcePosition, sourceFrames - 1);
            var second = Math.Min(first + 1, sourceFrames - 1);
            var fraction = (float)(sourcePosition - first);
            ReadFrame(source, first, sourceFrame);
            ReadFrame(source, second, nextFrame);
            for (var channel = 0; channel < sourceFrame.Length; channel++)
            {
                sourceFrame[channel] += (nextFrame[channel] - sourceFrame[channel]) * fraction;
            }

            MapChannels(sourceFrame, targetFrame);
            WriteFrame(output, frame * target.BlockAlign, targetFrame, target, targetIsFloat);
        }

        return new WaveSource(target, output);
    }

    private static void MapChannels(float[] source, float[] target)
    {
        Array.Clear(target);
        if (target.Length == 1)
        {
            target[0] = source.Length == 1 ? source[0] : (source[0] + source[1]) * 0.5f;
            return;
        }

        if (source.Length == 1)
        {
            target[0] = source[0];
            target[1] = source[0];
            return;
        }

        var copied = Math.Min(source.Length, target.Length);
        Array.Copy(source, target, copied);
    }

    private static void ReadFrame(WaveSource source, int frame, float[] samples)
    {
        var bytesPerSample = source.Format.BitsPerSample / 8;
        var offset = frame * source.Format.BlockAlign;
        for (var channel = 0; channel < samples.Length; channel++)
        {
            var sampleOffset = offset + channel * bytesPerSample;
            samples[channel] = source.Format.BitsPerSample switch
            {
                16 => BinaryPrimitives.ReadInt16LittleEndian(
                    source.PcmData.AsSpan(sampleOffset, 2)) / 32768f,
                24 => ReadInt24(source.PcmData, sampleOffset) / 8388608f,
                32 => BinaryPrimitives.ReadInt32LittleEndian(
                    source.PcmData.AsSpan(sampleOffset, 4)) / 2147483648f,
                _ => 0,
            };
        }
    }

    private static int ReadInt24(byte[] data, int offset)
    {
        var value = data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16;
        return (value & 0x800000) == 0 ? value : value | unchecked((int)0xff000000);
    }

    private static void WriteFrame(
        byte[] output,
        int offset,
        float[] samples,
        WaveFormat format,
        bool isFloat)
    {
        var bytesPerSample = format.BitsPerSample / 8;
        for (var channel = 0; channel < samples.Length; channel++)
        {
            var sampleOffset = offset + channel * bytesPerSample;
            var sample = Math.Clamp(samples[channel], -1f, 1f);
            if (isFloat)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    output.AsSpan(sampleOffset, 4), BitConverter.SingleToInt32Bits(sample));
                continue;
            }

            switch (format.BitsPerSample)
            {
                case 16:
                    BinaryPrimitives.WriteInt16LittleEndian(
                        output.AsSpan(sampleOffset, 2),
                        (short)Math.Clamp(Math.Round(sample * 32767f), short.MinValue, short.MaxValue));
                    break;
                case 24:
                    var value24 = (int)Math.Clamp(Math.Round(sample * 8388607f), -8388608, 8388607);
                    output[sampleOffset] = (byte)value24;
                    output[sampleOffset + 1] = (byte)(value24 >> 8);
                    output[sampleOffset + 2] = (byte)(value24 >> 16);
                    break;
                case 32:
                    BinaryPrimitives.WriteInt32LittleEndian(
                        output.AsSpan(sampleOffset, 4),
                        (int)Math.Clamp(Math.Round(sample * 2147483647d), int.MinValue, int.MaxValue));
                    break;
            }
        }
    }
}
