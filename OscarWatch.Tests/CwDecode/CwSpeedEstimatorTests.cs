using OscarWatch.Core.CwDecode;
using Xunit;

namespace OscarWatch.Tests.CwDecode;

public sealed class CwSpeedEstimatorTests
{
    private static readonly CwModelMetadata Metadata = CwModelMetadata.DeepCw();

    private static double? EstimateFor(float[] audio)
    {
        var spectrogram = CwSpectrogram.Compute(audio, Metadata, out var frames);
        return CwSpeedEstimator.Estimate(
            spectrogram, frames, Metadata.SpectrogramFrequencyBins,
            (double)Metadata.HopLength / Metadata.SampleRate);
    }

    [Theory]
    [InlineData(12, 0.0)]
    [InlineData(18, 0.05)]
    [InlineData(22, 0.0)]
    [InlineData(28, 0.05)]
    [InlineData(30, 0.1)]
    public void ReadsTheSendingSpeed(int wpm, double noiseRms)
    {
        var audio = CwTestAudio.Morse("CQ CQ DE MM9SQL MM9SQL IO85 K", wpm, 750, Metadata.SampleRate,
            noiseRms: noiseRms);

        var estimate = EstimateFor(audio);

        Assert.NotNull(estimate);
        Assert.InRange(estimate!.Value, wpm * 0.9, wpm * 1.1);
    }

    [Fact]
    public void NoiseHasNoSpeed()
    {
        var random = new Random(3);
        var audio = new float[Metadata.SampleRate * 12];
        for (var i = 0; i < audio.Length; i++)
            audio[i] = (float)(0.05 * (random.NextDouble() * 2 - 1));

        Assert.Null(EstimateFor(audio));
    }

    [Fact]
    public void SteadyCarrierHasNoSpeed()
    {
        var audio = CwTestAudio.Tone(800, Metadata.SampleRate, Metadata.SampleRate * 12);

        Assert.Null(EstimateFor(audio));
    }
}
