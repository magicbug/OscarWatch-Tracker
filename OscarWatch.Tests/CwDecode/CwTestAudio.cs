using OscarWatch.Core.Cw;
using OscarWatch.Cw;

namespace OscarWatch.Tests.CwDecode;

internal static class CwTestAudio
{
    /// <summary>Keyed sine with 5 ms raised-cosine edges, plus optional Gaussian noise.</summary>
    public static float[] Morse(string text, int wpm, double toneHz, int sampleRate,
        double leadSeconds = 1.0, double tailSeconds = 1.5, double noiseRms = 0.0, int seed = 1)
    {
        var dotSamples = MorseCode.DotMs(wpm) / 1000.0 * sampleRate;
        var keyEvents = MorseCode.BuildKeyEvents(CwMessage.FromText(text));
        var keyed = new List<bool>();
        foreach (var e in keyEvents)
        {
            var n = (int)Math.Round(e.Units * dotSamples);
            for (var i = 0; i < n; i++)
                keyed.Add(e.KeyDown);
        }

        var lead = (int)(leadSeconds * sampleRate);
        var total = lead + keyed.Count + (int)(tailSeconds * sampleRate);
        var audio = new float[total];
        var edge = Math.Max(1, (int)(0.005 * sampleRate));
        var envelope = 0.0;
        var random = new Random(seed);
        for (var i = 0; i < total; i++)
        {
            var k = i - lead;
            var down = k >= 0 && k < keyed.Count && keyed[k];
            envelope = Math.Clamp(envelope + (down ? 1.0 : -1.0) / edge, 0.0, 1.0);
            var shaped = 0.5 - 0.5 * Math.Cos(Math.PI * envelope);
            var sample = 0.5 * shaped * Math.Sin(2 * Math.PI * toneHz * i / sampleRate);
            if (noiseRms > 0)
                sample += noiseRms * Gaussian(random);
            audio[i] = (float)sample;
        }

        return audio;
    }

    public static float[] Tone(double hz, int sampleRate, int count, double amplitude = 0.5)
    {
        var audio = new float[count];
        for (var i = 0; i < count; i++)
            audio[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / sampleRate));
        return audio;
    }

    /// <summary>Power at <paramref name="hz"/> by single-bin DFT over the given range.</summary>
    public static double PowerAt(IReadOnlyList<float> audio, int sampleRate, double hz, int start, int count)
    {
        double re = 0, im = 0;
        for (var i = start; i < start + count; i++)
        {
            var phase = 2 * Math.PI * hz * i / sampleRate;
            re += audio[i] * Math.Cos(phase);
            im += audio[i] * Math.Sin(phase);
        }

        return (re * re + im * im) / ((double)count * count);
    }

    public static string? FindModelDirectory()
    {
        var beside = CwOnnxModel.DefaultDirectory;
        if (File.Exists(Path.Combine(beside, CwOnnxModel.ModelFileName)))
            return beside;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "native", "deepcw");
            if (File.Exists(Path.Combine(candidate, CwOnnxModel.ModelFileName)))
                return candidate;
        }

        return null;
    }

    private static double Gaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
