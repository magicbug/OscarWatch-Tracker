using OscarWatch.Core.CwDecode;
using Xunit;

namespace OscarWatch.Tests.CwDecode;

public sealed class CwTunerTests
{
    private const int Rate = 8000;

    private static List<float> Run(CwTuner tuner, float[] audio)
    {
        var samples = new List<float>(audio);
        tuner.Process(samples);
        return samples;
    }

    [Fact]
    public void NoteAtTheOffsetLandsOnTheBandCentre()
    {
        var tuner = new CwTuner(Rate, CwModelMetadata.DeepCw()) { OffsetHz = 800 };
        var output = Run(tuner, CwTestAudio.Tone(1600, Rate, Rate));

        var atCentre = CwTestAudio.PowerAt(output, Rate, 800, 2000, 4000);
        var atOriginal = CwTestAudio.PowerAt(output, Rate, 1600, 2000, 4000);

        Assert.True(atCentre > 0.02, $"centre {atCentre}");
        Assert.True(atOriginal < atCentre * 1e-3, $"original {atOriginal}");
    }

    [Fact]
    public void AudioBelowTheShiftDoesNotFoldIntoTheBand()
    {
        var tuner = new CwTuner(Rate, CwModelMetadata.DeepCw()) { OffsetHz = 800 };
        var output = Run(tuner, CwTestAudio.Tone(300, Rate, Rate));

        // Shifted down by 800 Hz, an unfiltered 300 Hz tone would mirror to 500 Hz.
        var folded = CwTestAudio.PowerAt(output, Rate, 500, 2000, 4000);

        Assert.True(folded < 0.0625 * 1e-3, $"folded {folded}");
    }

    [Fact]
    public void ZeroOffsetPassesTheBandUnchanged()
    {
        var tuner = new CwTuner(Rate, CwModelMetadata.DeepCw());
        var output = Run(tuner, CwTestAudio.Tone(700, Rate, Rate));

        var power = CwTestAudio.PowerAt(output, Rate, 700, 2000, 4000);

        Assert.InRange(power, 0.0625 * 0.8, 0.0625 * 1.2);
    }

    [Fact]
    public void OffsetIsClampedToTheWaterfall()
    {
        Assert.Equal(CwTuner.MaxOffsetHz, CwTuner.ClampOffset(5000));
        Assert.Equal(CwTuner.MinOffsetHz, CwTuner.ClampOffset(-5000));
        Assert.Equal(0, CwTuner.ClampOffset(double.NaN));
    }
}
