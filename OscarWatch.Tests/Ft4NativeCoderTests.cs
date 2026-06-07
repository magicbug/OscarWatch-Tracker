using OscarWatch.Ft4.Core.Coding;
using OscarWatch.Ft4.Core.Native;

namespace OscarWatch.Tests;

public class Ft4NativeCoderTests
{
    [Fact]
    public void FakeFt4Coder_encode_fills_expected_sample_count()
    {
        var coder = new FakeFt4Coder();
        var message = Ft4MessageBuffer.FormatMessage("CQ TEST FN03");
        var audio = new float[Ft4Constants.EncodeSampleCount];

        coder.Encode(message, Ft4Constants.DefaultAudioFrequencyHz, audio);

        Assert.Contains(audio, sample => Math.Abs(sample) > 0.01f);
    }

    [Fact]
    public void FakeFt4Coder_decode_invokes_callback()
    {
        var coder = new FakeFt4Coder();
        var audio = new float[Ft4Constants.DecodeSampleCount];
        var stage = Ft4QsoStage.Signoff;
        var rxTone = 0;
        var cutoff = 0;
        string? decoded = null;

        coder.Decode(
            audio,
            ref stage,
            ref rxTone,
            ref cutoff,
            Ft4MessageBuffer.FormatCall("MYCALL"),
            Ft4MessageBuffer.FormatCall("THEIRCALL"),
            message => decoded = message);

        Assert.Equal("CQ FAKECALL FN03", decoded);
        Assert.Equal(Ft4QsoStage.Calling, stage);
        Assert.Equal(Ft4Constants.DefaultAudioFrequencyHz, rxTone);
        Assert.Equal(4000, cutoff);
    }

    [Fact]
    public void Native_encode_produces_nonzero_waveform_peak()
    {
        if (!NativeFt4Loader.IsAvailable)
            return;

        var coder = Ft4CoderFactory.CreateNativeOrThrow();
        var message = Ft4MessageBuffer.FormatMessage("CQ TESTCALL FN03");
        var audio = new float[Ft4Constants.EncodeSampleCount];

        coder.Encode(message, Ft4Constants.DefaultAudioFrequencyHz, audio);

        var peak = audio.Max(Math.Abs);
        Assert.True(peak > 0.01f, $"Expected encoded waveform peak > 0.01, got {peak}");
    }

    [Fact]
    public void Native_encode_decode_roundtrip_finds_cq()
    {
        if (!NativeFt4Loader.IsAvailable)
            return;

        var coder = Ft4CoderFactory.CreateNativeOrThrow();
        var message = Ft4MessageBuffer.FormatMessage("CQ TESTCALL FN03");
        var encoded = new float[Ft4Constants.EncodeSampleCount];
        var decodeWindow = new float[Ft4Constants.DecodeSampleCount];

        coder.Encode(message, Ft4Constants.DefaultAudioFrequencyHz, encoded);
        Array.Copy(encoded, decodeWindow, encoded.Length);

        var stage = Ft4QsoStage.Calling;
        var rx = Ft4Constants.DefaultAudioFrequencyHz;
        var cutoff = 4000;
        string? decoded = null;

        coder.Decode(
            decodeWindow,
            ref stage,
            ref rx,
            ref cutoff,
            Ft4MessageBuffer.FormatCall("TESTCALL"),
            Ft4MessageBuffer.FormatCall("          "),
            line => decoded = line);

        Assert.False(string.IsNullOrWhiteSpace(decoded));
        Assert.Contains("CQ", decoded!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TESTCALL", decoded!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ft4MessageBuffer_formats_space_padded_message()
    {
        var message = Ft4MessageBuffer.FormatMessage("CQ TEST");

        Assert.Equal(Ft4Constants.EncodeMessageLength, message.Length);
        Assert.Equal((byte)'C', message[0]);
        Assert.Equal((byte)'T', message[6]);
        for (var i = 7; i < message.Length; i++)
            Assert.Equal((byte)' ', message[i]);
    }
}
