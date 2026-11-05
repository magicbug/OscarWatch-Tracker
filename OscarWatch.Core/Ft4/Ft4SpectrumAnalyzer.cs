namespace OscarWatch.Core.Ft4;

/// <summary>Real FFT magnitude spectrum for the FT4 waterfall (USB audio passband).</summary>
public static class Ft4SpectrumAnalyzer
{
    public const double DefaultMinHz = 200;
    public const double DefaultMaxHz = 3000;

    /// <summary>
    /// Half-width of the WSJT-X-style FT4 filter brackets around the selected tone (Hz).
    /// FT4 uses 4 tones at 20.833 Hz spacing (≈62.5 Hz span); brackets are drawn a little wider (~90 Hz total).
    /// </summary>
    public const double Ft4FilterHalfWidthHz = 45;

    private const int MaxFftSize = 4096;

    /// <summary>Most half-overlapping FFTs averaged into one row when more audio than one FFT is supplied.</summary>
    public const int MaxAveragedSegments = 4;

    /// <summary>Frequency segments whose floors make up the Flatten baseline.</summary>
    public const int FlattenSegments = 10;

    /// <summary>
    /// Low percentile per Flatten segment. Kept low so one or two FT4 signals inside a
    /// segment do not lift its floor and dim themselves.
    /// </summary>
    public const float FlattenPercentile = 0.10f;

    /// <summary>
    /// Compute power spectrum bins for <paramref name="minHz"/>..<paramref name="maxHz"/>
    /// from mono PCM at <paramref name="sampleRate"/>. Returns false if too few samples.
    /// When more than one FFT of audio is supplied, up to <see cref="MaxAveragedSegments"/>
    /// half-overlapping FFTs from the newest audio are averaged, which steadies the noise
    /// so weak traces stand out.
    /// </summary>
    public static bool TryComputePassband(
        ReadOnlySpan<float> samples,
        int sampleRate,
        Span<float> outputBins,
        double minHz = DefaultMinHz,
        double maxHz = DefaultMaxHz)
    {
        if (sampleRate < 8000 || outputBins.Length == 0 || samples.Length < 64)
            return false;

        var fftSize = HighestPowerOfTwoNotExceeding(Math.Min(samples.Length, MaxFftSize));
        if (fftSize < 64)
            return false;

        var hop = fftSize / 2;
        var segments = Math.Min(MaxAveragedSegments, 1 + (samples.Length - fftSize) / hop);
        var maxBin = fftSize / 2;
        var power = new double[maxBin + 1];
        var window = new double[fftSize];
        for (var i = 0; i < fftSize; i++)
            window[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (fftSize - 1));

        var re = new double[fftSize];
        var im = new double[fftSize];
        // Normalise by FFT length so display dB is roughly independent of window size.
        var scale = 1.0 / ((double)fftSize * fftSize * segments);
        for (var s = 0; s < segments; s++)
        {
            var start = samples.Length - fftSize - s * hop;
            for (var i = 0; i < fftSize; i++)
            {
                re[i] = samples[start + i] * window[i];
                im[i] = 0;
            }

            FftInPlace(re, im);
            for (var b = 0; b <= maxBin; b++)
                power[b] += (re[b] * re[b] + im[b] * im[b]) * scale;
        }

        var binHz = (double)sampleRate / fftSize;
        for (var c = 0; c < outputBins.Length; c++)
        {
            var hz = minHz + (maxHz - minHz) * c / Math.Max(1, outputBins.Length - 1);
            var bin = hz / binHz;
            var i0 = (int)Math.Floor(bin);
            var i1 = i0 + 1;
            if (i0 < 1)
                i0 = 1;
            if (i1 > maxBin)
                i1 = maxBin;
            if (i0 > maxBin)
            {
                outputBins[c] = 0;
                continue;
            }

            var mag0 = PowerDb(power[i0]);
            var mag1 = i1 == i0 ? mag0 : PowerDb(power[i1]);
            var frac = bin - Math.Floor(bin);
            outputBins[c] = (float)(mag0 * (1 - frac) + mag1 * frac);
        }

        return true;
    }

    /// <summary>
    /// Estimate the noise floor as a low percentile of the magnitude bins (not the minimum,
    /// which is too optimistic and blows out the waterfall colour map).
    /// </summary>
    public static float EstimateNoiseFloorDb(ReadOnlySpan<float> bins, float percentile = 0.20f)
    {
        if (bins.Length == 0)
            return -100f;

        var copy = bins.ToArray();
        Array.Sort(copy);
        var idx = (int)Math.Clamp(percentile * (copy.Length - 1), 0, copy.Length - 1);
        return copy[idx];
    }

    /// <summary>Share of the gap to the latest row's floor that the waterfall floor moves each row.</summary>
    public const double NoiseFloorFollowRate = 0.35;

    /// <summary>
    /// Move the waterfall noise floor towards the floor measured on the newest row.
    /// It has to settle within a few rows: radio AGC drops the whole passband when a strong
    /// signal arrives, and a slow floor then paints the weaker signals black for whole slots.
    /// </summary>
    public static double FollowNoiseFloor(double currentDb, double rowFloorDb) =>
        currentDb + (rowFloorDb - currentDb) * NoiseFloorFollowRate;

    /// <summary>Floor of each of <paramref name="floors"/>.Length equal frequency segments of <paramref name="bins"/>.</summary>
    public static void MeasureSegmentFloorsDb(
        ReadOnlySpan<float> bins,
        Span<double> floors,
        float percentile = FlattenPercentile)
    {
        var count = floors.Length;
        for (var k = 0; k < count; k++)
        {
            var from = k * bins.Length / count;
            var to = Math.Max(from + 1, (k + 1) * bins.Length / count);
            floors[k] = EstimateNoiseFloorDb(bins[from..Math.Min(to, bins.Length)], percentile);
        }
    }

    /// <summary>
    /// Flatten baseline per column: straight lines between segment centres, level beyond
    /// the outer centres. Removes the slope of the radio's SSB filter, as WSJT-X Flatten does.
    /// </summary>
    public static void InterpolateBaselineDb(ReadOnlySpan<double> floors, Span<float> baseline)
    {
        if (floors.Length == 0 || baseline.Length == 0)
            return;

        var width = (double)baseline.Length / floors.Length;
        for (var x = 0; x < baseline.Length; x++)
        {
            var pos = (x + 0.5) / width - 0.5;
            var k0 = (int)Math.Floor(pos);
            if (k0 < 0)
            {
                baseline[x] = (float)floors[0];
                continue;
            }

            if (k0 >= floors.Length - 1)
            {
                baseline[x] = (float)floors[^1];
                continue;
            }

            var frac = pos - k0;
            baseline[x] = (float)(floors[k0] * (1 - frac) + floors[k0 + 1] * frac);
        }
    }

    public static double PixelToHz(double x, double width, double minHz, double maxHz)
    {
        if (width <= 1)
            return minHz;
        var t = Math.Clamp(x / width, 0, 1);
        return minHz + t * (maxHz - minHz);
    }

    public static double HzToPixel(double hz, double width, double minHz, double maxHz)
    {
        if (width <= 1 || maxHz <= minHz)
            return 0;
        var t = (hz - minHz) / (maxHz - minHz);
        return Math.Clamp(t, 0, 1) * width;
    }

    /// <summary>
    /// Centre to use when drawing an FT4 filter bracket so both legs stay inside the plot.
    /// A centre outside the passband used to pin both legs on the same border pixel, which
    /// the control then clipped, so the bracket vanished off the waterfall.
    /// </summary>
    public static double VisibleBracketCentreHz(
        double centreHz,
        double halfWidthHz,
        double minHz,
        double maxHz)
    {
        if (!double.IsFinite(minHz) || !double.IsFinite(maxHz) || maxHz <= minHz)
            return 1500;

        var mid = (minHz + maxHz) / 2.0;
        if (!double.IsFinite(centreHz))
            return mid;

        var half = double.IsFinite(halfWidthHz) ? Math.Max(0, halfWidthHz) : 0;
        var lo = minHz + half;
        var hi = maxHz - half;
        if (hi <= lo)
            return mid;

        return Math.Clamp(centreHz, lo, hi);
    }

    private static float PowerDb(double p) =>
        p < 1e-20 ? -100f : (float)(10.0 * Math.Log10(p));

    private static int HighestPowerOfTwoNotExceeding(int n)
    {
        var p = 1;
        while ((p << 1) <= n)
            p <<= 1;
        return p;
    }

    /// <summary>In-place radix-2 Cooley–Tukey FFT.</summary>
    private static void FftInPlace(double[] re, double[] im)
    {
        var n = re.Length;
        var j = 0;
        for (var i = 1; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2.0 * Math.PI / len;
            var wlenRe = Math.Cos(ang);
            var wlenIm = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                var wRe = 1.0;
                var wIm = 0.0;
                var half = len >> 1;
                for (var k = 0; k < half; k++)
                {
                    var uRe = re[i + k];
                    var uIm = im[i + k];
                    var vRe = re[i + k + half] * wRe - im[i + k + half] * wIm;
                    var vIm = re[i + k + half] * wIm + im[i + k + half] * wRe;
                    re[i + k] = uRe + vRe;
                    im[i + k] = uIm + vIm;
                    re[i + k + half] = uRe - vRe;
                    im[i + k + half] = uIm - vIm;
                    var nextWRe = wRe * wlenRe - wIm * wlenIm;
                    wIm = wRe * wlenIm + wIm * wlenRe;
                    wRe = nextWRe;
                }
            }
        }
    }
}
