using System.Text;

namespace OscarWatch.Core.Sstv;

/// <summary>Reads PCM WAV (8, 16, 24 or 32-bit integer, or 32-bit float) as mono float samples.</summary>
public static class SstvWavReader
{
    public static (float[] Samples, int SampleRate) Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static (float[] Samples, int SampleRate) Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (ReadTag(reader) != "RIFF")
            throw new InvalidDataException("Not a RIFF file.");
        reader.ReadUInt32();
        if (ReadTag(reader) != "WAVE")
            throw new InvalidDataException("Not a WAVE file.");

        int format = 0, channels = 0, rate = 0, bits = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var tag = ReadTag(reader);
            var size = reader.ReadUInt32();
            var next = stream.Position + size + (size & 1);
            if (tag == "fmt ")
            {
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (format == 0xFFFE && size >= 40)
                {
                    reader.ReadUInt16();
                    reader.ReadUInt16();
                    reader.ReadUInt32();
                    format = reader.ReadUInt16();
                }
            }
            else if (tag == "data")
            {
                if (channels <= 0 || rate <= 0)
                    throw new InvalidDataException("WAV data chunk before format chunk.");

                // Some writers leave the size at zero or too large while recording.
                var available = stream.Length - stream.Position;
                var length = size == 0 || size > available ? available : size;
                return (ReadSamples(reader, length, format, channels, bits), rate);
            }

            if (next > stream.Length)
                break;
            stream.Position = next;
        }

        throw new InvalidDataException("WAV file has no data chunk.");
    }

    private static float[] ReadSamples(BinaryReader reader, long length, int format, int channels, int bits)
    {
        var bytesPerSample = bits / 8;
        if (bytesPerSample <= 0 || (format != 1 && format != 3) || (format == 3 && bits != 32))
            throw new InvalidDataException($"Unsupported WAV format {format} with {bits} bits.");

        var frames = length / (bytesPerSample * channels);
        var output = new float[frames];
        var frame = new byte[bytesPerSample * channels];
        for (long i = 0; i < frames; i++)
        {
            if (reader.Read(frame, 0, frame.Length) < frame.Length)
            {
                Array.Resize(ref output, (int)i);
                break;
            }

            var sum = 0.0;
            for (var c = 0; c < channels; c++)
                sum += Decode(frame.AsSpan(c * bytesPerSample, bytesPerSample), format, bits);
            output[i] = (float)(sum / channels);
        }

        return output;
    }

    private static double Decode(ReadOnlySpan<byte> b, int format, int bits)
    {
        if (format == 3)
            return BitConverter.ToSingle(b);

        return bits switch
        {
            8 => (b[0] - 128) / 128.0,
            16 => BitConverter.ToInt16(b) / 32768.0,
            24 => ((b[2] << 24) | (b[1] << 16) | (b[0] << 8)) / 2147483648.0,
            32 => BitConverter.ToInt32(b) / 2147483648.0,
            _ => throw new InvalidDataException($"Unsupported WAV bit depth {bits}."),
        };
    }

    private static string ReadTag(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
}
