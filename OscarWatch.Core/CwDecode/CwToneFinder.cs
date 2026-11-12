namespace OscarWatch.Core.CwDecode;

/// <summary>Finds the CW note in an averaged power spectrum, for the tuning readout.</summary>
public static class CwToneFinder
{
    /// <summary>A peak this far above the median of the search range counts as a note.</summary>
    public const double MinPeakAboveMedianDb = 10.0;

    /// <param name="power">Linear power per bin, bin 0 at 0 Hz.</param>
    /// <returns>The note in hertz, or null when nothing stands out.</returns>
    public static double? Find(ReadOnlySpan<float> power, double binHz, double minHz = 250, double maxHz = 1600)
    {
        var lo = Math.Max(1, (int)Math.Ceiling(minHz / binHz));
        var hi = Math.Min(power.Length - 2, (int)Math.Floor(maxHz / binHz));
        if (hi - lo < 4)
            return null;

        var values = new float[hi - lo + 1];
        var peak = lo;
        for (var k = lo; k <= hi; k++)
        {
            values[k - lo] = power[k];
            if (power[k] > power[peak])
                peak = k;
        }

        Array.Sort(values);
        var median = values[values.Length / 2];
        if (median <= 0 || power[peak] <= 0 || 10 * Math.Log10(power[peak] / median) < MinPeakAboveMedianDb)
            return null;

        // Parabolic interpolation on log power puts the estimate between bins.
        var a = Math.Log(Math.Max(power[peak - 1], 1e-20f));
        var b = Math.Log(power[peak]);
        var c = Math.Log(Math.Max(power[peak + 1], 1e-20f));
        var denominator = a - 2 * b + c;
        var delta = denominator == 0 ? 0 : 0.5 * (a - c) / denominator;
        return (peak + Math.Clamp(delta, -0.5, 0.5)) * binHz;
    }

    /// <summary>True when <paramref name="toneHz"/> sits inside the band the model reads.</summary>
    public static bool IsInModelBand(double toneHz, CwModelMetadata metadata) =>
        toneHz >= metadata.SpectrogramMinFreqHz && toneHz <= metadata.SpectrogramMaxFreqHz;
}
