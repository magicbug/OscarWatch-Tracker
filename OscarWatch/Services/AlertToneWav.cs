using System.Buffers.Binary;

namespace OscarWatch.Services;

/// <summary>Builds short mono PCM tones as WAV blobs (the same tones on every platform).</summary>
internal static class AlertToneWav
{
    public const int SampleRate = 44100;
    public const int Channels = 1;
    public const int BitsPerSample = 16;

    /// <summary>Two rising blips (~0.4 s), the scheduled-pass alert.</summary>
    public static byte[] Create() => Wrap(CreatePcm16());

    /// <summary>PCM16 samples for <see cref="Create"/>, for PortAudio playback.</summary>
    public static short[] CreatePcm16()
    {
        var samples = new short[SampleRate * 2 / 5];
        WriteTone(samples, offset: 0, durationSamples: SampleRate / 10, frequencyHz: 880);
        WriteTone(samples, offset: SampleRate / 8, durationSamples: SampleRate / 8, frequencyHz: 1174.66);
        return samples;
    }

    /// <summary>
    /// Three even notes stepping down (~0.5 s). A station calling on FT4,
    /// distinct from the rising pass alert.
    /// </summary>
    public static byte[] CreateCall() => Wrap(CreateCallPcm16());

    /// <summary>PCM16 samples for <see cref="CreateCall"/>, for PortAudio playback.</summary>
    public static short[] CreateCallPcm16()
    {
        var note = SampleRate / 8;
        var gap = SampleRate / 20;
        var samples = new short[note * 3 + gap * 2];
        WriteTone(samples, 0, note, 659.25);
        WriteTone(samples, note + gap, note, 523.25);
        WriteTone(samples, (note + gap) * 2, note, 392);
        return samples;
    }

    private static byte[] Wrap(short[] samples)
    {
        var dataBytes = samples.Length * sizeof(short);
        var file = new byte[44 + dataBytes];
        WriteHeader(file, dataBytes);
        Buffer.BlockCopy(samples, 0, file, 44, dataBytes);
        return file;
    }

    private static void WriteTone(short[] samples, int offset, int durationSamples, double frequencyHz)
    {
        var end = Math.Min(samples.Length, offset + durationSamples);
        for (var i = offset; i < end; i++)
        {
            var t = (i - offset) / (double)SampleRate;
            var envelope = AttackReleaseEnvelope(i - offset, durationSamples);
            var sample = Math.Sin(2 * Math.PI * frequencyHz * t) * envelope * 0.35;
            samples[i] = (short)Math.Clamp((int)(sample * short.MaxValue), short.MinValue, short.MaxValue);
        }
    }

    private static double AttackReleaseEnvelope(int index, int durationSamples)
    {
        var attack = Math.Min(durationSamples / 8, SampleRate / 100);
        var release = Math.Min(durationSamples / 3, SampleRate / 20);
        if (index < attack)
            return attack == 0 ? 1 : index / (double)attack;
        if (index > durationSamples - release)
            return release == 0 ? 0 : (durationSamples - index) / (double)release;
        return 1;
    }

    private static void WriteHeader(Span<byte> file, int dataBytes)
    {
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteInt32LittleEndian(file[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(file[8..]);
        "fmt "u8.CopyTo(file[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(file[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(file[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(file[22..], Channels);
        BinaryPrimitives.WriteInt32LittleEndian(file[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(file[28..], SampleRate * Channels * BitsPerSample / 8);
        BinaryPrimitives.WriteInt16LittleEndian(file[32..], (short)(Channels * BitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(file[34..], BitsPerSample);
        "data"u8.CopyTo(file[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(file[40..], dataBytes);
    }
}
