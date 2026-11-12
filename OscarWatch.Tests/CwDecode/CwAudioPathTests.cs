using OscarWatch.Core.CwDecode;
using Xunit;

namespace OscarWatch.Tests.CwDecode;

public sealed class CwAudioPathTests
{
    [Fact]
    public void ShifterMovesNoteDownByOffset()
    {
        const int rate = 3200;
        var samples = CwTestAudio.Tone(800, rate, rate).ToList();
        var shifter = new CwFrequencyShifter(rate) { ShiftHz = -100 };

        shifter.Process(samples);

        var at700 = CwTestAudio.PowerAt(samples, rate, 700, 200, 2400);
        var at800 = CwTestAudio.PowerAt(samples, rate, 800, 200, 2400);
        var at900 = CwTestAudio.PowerAt(samples, rate, 900, 200, 2400);
        Assert.True(at700 > 100 * at800, $"700 Hz {at700}, 800 Hz {at800}");
        Assert.True(at700 > 100 * at900, $"700 Hz {at700}, 900 Hz {at900}");
    }

    [Fact]
    public void ShifterAtZeroPassesNoteThrough()
    {
        const int rate = 3200;
        var samples = CwTestAudio.Tone(700, rate, rate).ToList();
        var shifter = new CwFrequencyShifter(rate);

        shifter.Process(samples);

        Assert.InRange(CwTestAudio.PowerAt(samples, rate, 700, 200, 2400), 0.05, 0.07);
    }

    [Fact]
    public void ResamplerKeepsNoteAndRejectsAliases()
    {
        var resampler = new CwResampler(8000, 3200, 1400, 127);
        var output = new List<float>();
        resampler.Process(CwTestAudio.Tone(700, 8000, 8000), output);

        Assert.InRange(output.Count, 3150, 3250);
        var kept = CwTestAudio.PowerAt(output, 3200, 700, 200, 2400);

        var aliased = new List<float>();
        new CwResampler(8000, 3200, 1400, 127).Process(CwTestAudio.Tone(2500, 8000, 8000), aliased);
        var folded = CwTestAudio.PowerAt(aliased, 3200, 700, 200, 2400);

        Assert.True(kept > 0.04, $"kept {kept}");
        Assert.True(folded < kept / 1000, $"2500 Hz folded to 700 Hz at {folded}");
    }

    [Fact]
    public void ToneFinderReportsNote()
    {
        const double binHz = 8000.0 / 2048;
        var power = new float[1025];
        for (var k = 0; k < power.Length; k++)
            power[k] = 1f;
        var bin = (int)Math.Round(723 / binHz);
        power[bin] = 1000f;
        power[bin + 1] = 300f;

        var tone = CwToneFinder.Find(power, binHz);

        Assert.NotNull(tone);
        Assert.InRange(tone!.Value, 720, 730);
        Assert.True(CwToneFinder.IsInModelBand(tone.Value, CwModelMetadata.DeepCw()));
    }

    [Fact]
    public void ToneFinderIgnoresFlatNoise()
    {
        var power = new float[1025];
        var random = new Random(3);
        for (var k = 0; k < power.Length; k++)
            power[k] = 1f + (float)random.NextDouble();

        Assert.Null(CwToneFinder.Find(power, 8000.0 / 2048));
    }
}
