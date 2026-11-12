using OscarWatch.Core.CwDecode;
using Xunit;

namespace OscarWatch.Tests.CwDecode;

public sealed class CwSpectrogramTests
{
    private static readonly CwModelMetadata Model = CwModelMetadata.DeepCw();

    [Fact]
    public void BandCoversFourHundredToTwelveHundredHertzInclusive()
    {
        var (start, stop) = CwSpectrogram.FrequencyBinRange(Model);

        Assert.Equal(32, start);
        Assert.Equal(97, stop);
        Assert.Equal(Model.SpectrogramFrequencyBins, stop - start);
    }

    [Fact]
    public void FrameCountMatchesCentredFraming()
    {
        Assert.Equal(1, CwSpectrogram.FrameCount(256, Model) - 256 / 48);
        Assert.Equal(1 + 3200 / 48, CwSpectrogram.FrameCount(3200, Model));
    }

    [Theory]
    [InlineData(600.0)]
    [InlineData(800.0)]
    [InlineData(1000.0)]
    public void ToneLandsInItsBin(double hz)
    {
        var audio = CwTestAudio.Tone(hz, Model.SampleRate, Model.SampleRate);
        var spectrogram = CwSpectrogram.Compute(audio, Model, out var frames);
        var bins = Model.SpectrogramFrequencyBins;

        var row = frames / 2 * bins;
        var peak = 0;
        for (var b = 1; b < bins; b++)
        {
            if (spectrogram[row + b] > spectrogram[row + peak])
                peak = b;
        }

        var (start, _) = CwSpectrogram.FrequencyBinRange(Model);
        var binHz = (double)Model.SampleRate / Model.FftLength;
        Assert.Equal(hz, (start + peak) * binHz, 1);
    }

    [Fact]
    public void NormalisationPutsNoiseNearOneAndKeepsToneWellAbove()
    {
        var audio = CwTestAudio.Morse("TEST TEST", 20, 800, Model.SampleRate, noiseRms: 0.02);
        var spectrogram = CwSpectrogram.Compute(audio, Model, out _);

        var sorted = (float[])spectrogram.Clone();
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];
        var peak = sorted[^1];

        Assert.InRange(median, 0.3f, 1.2f);
        Assert.True(peak > median + 2.0f, $"peak {peak} median {median}");
    }

    [Fact]
    public void SilenceStaysFinite()
    {
        var spectrogram = CwSpectrogram.Compute(new float[3200], Model, out _);

        Assert.All(spectrogram, v => Assert.True(float.IsFinite(v)));
    }
}
