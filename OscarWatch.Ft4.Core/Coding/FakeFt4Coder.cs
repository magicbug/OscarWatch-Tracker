using OscarWatch.Ft4.Core.Native;

namespace OscarWatch.Ft4.Core.Coding;

/// <summary>Test double when the native codec is unavailable.</summary>
public sealed class FakeFt4Coder : IFt4Coder
{
    public bool IsNative => false;

    public void Encode(byte[] message, float txAudioFrequency, float[] audioSamples)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(audioSamples);

        if (message.Length != Ft4Constants.EncodeMessageLength)
            throw new ArgumentException($"FT4 message must be {Ft4Constants.EncodeMessageLength} bytes.", nameof(message));
        if (audioSamples.Length != Ft4Constants.EncodeSampleCount)
            throw new ArgumentException($"Encode buffer must be {Ft4Constants.EncodeSampleCount} samples.", nameof(audioSamples));

        var toneHz = txAudioFrequency > 0 ? txAudioFrequency : Ft4Constants.DefaultAudioFrequencyHz;
        var phase = 0.0;
        var phaseStep = 2.0 * Math.PI * toneHz / Ft4Constants.SamplingRateHz;

        for (var i = 0; i < audioSamples.Length; i++)
        {
            audioSamples[i] = (float)Math.Sin(phase) * 0.25f;
            phase += phaseStep;
        }
    }

    public void Decode(
        float[] audioSamples,
        ref Ft4QsoStage qsoProgress,
        ref int rxAudioFrequencyHz,
        ref int cutoffFrequencyHz,
        byte[] myCall,
        byte[] theirCall,
        Action<string> onMessageDecoded)
    {
        ArgumentNullException.ThrowIfNull(audioSamples);
        ArgumentNullException.ThrowIfNull(myCall);
        ArgumentNullException.ThrowIfNull(theirCall);
        ArgumentNullException.ThrowIfNull(onMessageDecoded);

        if (audioSamples.Length != Ft4Constants.DecodeSampleCount)
            throw new ArgumentException($"Decode buffer must be {Ft4Constants.DecodeSampleCount} samples.", nameof(audioSamples));

        qsoProgress = Ft4QsoStage.Calling;
        rxAudioFrequencyHz = Ft4Constants.DefaultAudioFrequencyHz;
        cutoffFrequencyHz = 4000;
        onMessageDecoded("CQ FAKECALL FN03");
    }
}
