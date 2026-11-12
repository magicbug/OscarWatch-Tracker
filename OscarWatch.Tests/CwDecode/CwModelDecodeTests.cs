using OscarWatch.Core.CwDecode;
using OscarWatch.Cw;
using Xunit;
using Xunit.Abstractions;

namespace OscarWatch.Tests.CwDecode;

/// <summary>Runs synthesised Morse through the real DeepCW model and the streaming decoder.</summary>
public sealed class CwModelDecodeTests(ITestOutputHelper output)
{
    private static CwOnnxModel LoadModel()
    {
        var dir = CwTestAudio.FindModelDirectory();
        Assert.NotNull(dir);
        return CwOnnxModel.Load(dir);
    }

    [Fact]
    public void ShippedMetadataMatchesBuiltInDefaults()
    {
        using var model = LoadModel();
        var builtIn = CwModelMetadata.DeepCw();

        Assert.Equal(builtIn.SampleRate, model.Metadata.SampleRate);
        Assert.Equal(builtIn.FftLength, model.Metadata.FftLength);
        Assert.Equal(builtIn.HopLength, model.Metadata.HopLength);
        Assert.Equal(builtIn.OutputTimeStride, model.Metadata.OutputTimeStride);
        Assert.Equal(builtIn.SpectrogramFrequencyBins, model.Metadata.SpectrogramFrequencyBins);
        Assert.Equal(builtIn.Chars, model.Metadata.Chars);
        Assert.Equal(builtIn.BlankIndex, model.Metadata.BlankIndex);
    }

    [Theory]
    [InlineData(20, 700.0, 0.0)]
    [InlineData(25, 600.0, 0.05)]
    [InlineData(16, 900.0, 0.1)]
    public void StreamingDecodeReadsCallAndLocator(int wpm, double toneHz, double noiseRms)
    {
        using var model = LoadModel();
        var decoder = new CwStreamDecoder(model.Metadata, model);
        var audio = CwTestAudio.Morse("CQ CQ DE MM9SQL MM9SQL IO85 K", wpm, toneHz, model.Metadata.SampleRate,
            noiseRms: noiseRms);

        var committedDuringStream = "";
        const int chunk = 320;
        for (var i = 0; i < audio.Length; i += chunk)
        {
            decoder.Append(audio.AsSpan(i, Math.Min(chunk, audio.Length - i)));
            if (decoder.Step() is { } update)
                committedDuringStream += update.CommittedText;
        }

        decoder.Finish();
        output.WriteLine($"Streamed: {committedDuringStream}");
        output.WriteLine($"Transcript: {decoder.Transcript}");

        Assert.Contains("MM9SQL", decoder.Transcript);
        Assert.Contains("IO85", decoder.Transcript);
        Assert.False(string.IsNullOrWhiteSpace(committedDuringStream), "Nothing was committed before Finish.");

        var tokens = CwCopySpotter.Tokenize(decoder.Transcript);
        Assert.Contains(tokens, t => t is { Kind: CwTokenKind.Callsign, Value: "MM9SQL" });
        Assert.Contains(tokens, t => t is { Kind: CwTokenKind.Locator, Value: "IO85" });
    }

    /// <summary>
    /// The live path: soundcard rate to 8 kHz (waterfall), to the model rate, then the rig control
    /// note offset removed. A note at 1300 Hz, outside the model band, is brought back to 1100 Hz.
    /// </summary>
    [Fact]
    public void SoundcardRateAudioDecodesThroughTheReceiveChain()
    {
        using var model = LoadModel();
        var decoder = new CwStreamDecoder(model.Metadata, model);
        var audio = CwTestAudio.Morse("CQ DE G4ABC IO91 K", 22, 1300, 48000, noiseRms: 0.02);
        var toWaterfall = new OscarWatch.Core.Sstv.SstvResampler(48000, 8000);
        var toModel = new CwResampler(8000, model.Metadata.SampleRate, 1400, 127);
        var shifter = new CwFrequencyShifter(model.Metadata.SampleRate) { ShiftHz = -200 };
        var at8k = new List<float>();
        var atModel = new List<float>();

        for (var i = 0; i < audio.Length; i += 1024)
        {
            at8k.Clear();
            atModel.Clear();
            toWaterfall.Process(audio.AsSpan(i, Math.Min(1024, audio.Length - i)), at8k);
            toModel.Process(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(at8k), atModel);
            shifter.Process(atModel);
            decoder.Append(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(atModel));
            decoder.Step();
        }

        decoder.Finish();
        output.WriteLine($"Receive chain: {decoder.Transcript}");

        Assert.Contains("G4ABC", decoder.Transcript);
        Assert.Contains("IO91", decoder.Transcript);
    }

    [Fact]
    public void NoiseAloneDecodesLittle()
    {
        using var model = LoadModel();
        var decoder = new CwStreamDecoder(model.Metadata, model);
        var random = new Random(7);
        var audio = new float[model.Metadata.SampleRate * 15];
        for (var i = 0; i < audio.Length; i++)
            audio[i] = (float)(0.05 * (random.NextDouble() * 2 - 1));

        decoder.Append(audio);
        decoder.Step();
        decoder.Finish();
        output.WriteLine($"Noise transcript: '{decoder.Transcript}'");

        Assert.True(decoder.Transcript.Replace(" ", "").Length <= 4, decoder.Transcript);
    }

    [Fact]
    public void LongStreamKeepsDecoding()
    {
        using var model = LoadModel();
        var decoder = new CwStreamDecoder(model.Metadata, model);
        var text = string.Join(' ', Enumerable.Repeat("CQ DE G4ABC", 8));
        var audio = CwTestAudio.Morse(text, 22, 750, model.Metadata.SampleRate);

        const int chunk = 640;
        for (var i = 0; i < audio.Length; i += chunk)
        {
            decoder.Append(audio.AsSpan(i, Math.Min(chunk, audio.Length - i)));
            decoder.Step();
        }

        decoder.Finish();
        output.WriteLine($"{audio.Length / (double)model.Metadata.SampleRate:F1} s: {decoder.Transcript}");

        Assert.True(decoder.Transcript.Split("G4ABC").Length - 1 >= 6, decoder.Transcript);
    }
}
