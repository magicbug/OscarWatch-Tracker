using OscarWatch.Ft4.Core.Native;

namespace OscarWatch.Ft4.Core.Coding;

public interface IFt4Coder
{
    bool IsNative { get; }

    void Encode(byte[] message, float txAudioFrequency, float[] audioSamples);

    void Decode(
        float[] audioSamples,
        ref Ft4QsoStage qsoProgress,
        ref int rxAudioFrequencyHz,
        ref int cutoffFrequencyHz,
        byte[] myCall,
        byte[] theirCall,
        Action<string> onMessageDecoded);
}
