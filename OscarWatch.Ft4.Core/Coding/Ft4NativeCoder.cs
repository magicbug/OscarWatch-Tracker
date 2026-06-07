using System.Runtime.InteropServices;
using OscarWatch.Ft4.Core.Native;

namespace OscarWatch.Ft4.Core.Coding;

public sealed class Ft4NativeCoder : IFt4Coder
{
    public Ft4NativeCoder()
    {
        if (!NativeFt4Loader.EnsureLoaded())
            throw new InvalidOperationException(NativeFt4Loader.LoadError ?? "ft4_coder is not available on this platform.");
    }

    public bool IsNative => true;

    public void Encode(byte[] message, float txAudioFrequency, float[] audioSamples)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(audioSamples);

        if (message.Length != Ft4Constants.EncodeMessageLength)
            throw new ArgumentException($"FT4 message must be {Ft4Constants.EncodeMessageLength} bytes.", nameof(message));
        if (audioSamples.Length != Ft4Constants.EncodeSampleCount)
            throw new ArgumentException($"Encode buffer must be {Ft4Constants.EncodeSampleCount} samples.", nameof(audioSamples));

        Ft4NativeInterop.EncodeFt4(message, ref txAudioFrequency, audioSamples);
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
        if (myCall.Length != Ft4Constants.MaxCallLength)
            throw new ArgumentException($"My call must be {Ft4Constants.MaxCallLength} bytes.", nameof(myCall));
        if (theirCall.Length != Ft4Constants.MaxCallLength)
            throw new ArgumentException($"Their call must be {Ft4Constants.MaxCallLength} bytes.", nameof(theirCall));

        Ft4NativeInterop.DecodedMessageCallbackDelegate callback = messagePtr =>
        {
            var message = Marshal.PtrToStringAnsi(messagePtr);
            if (!string.IsNullOrEmpty(message))
                onMessageDecoded(message);
        };

        Ft4NativeInterop.DecodeFt4(
            audioSamples,
            ref qsoProgress,
            ref rxAudioFrequencyHz,
            ref cutoffFrequencyHz,
            myCall,
            theirCall,
            callback);
    }
}
