namespace OscarWatch.Core.CwDecode;

/// <summary>
/// Estimates sending speed from a model spectrogram. Mark lengths are smeared by the FFT window,
/// so it works from the spacing between mark starts instead: within and between letters those
/// fall on even multiples of a dot (2, 4, 6 and 8 dots). Above about 30 wpm the model's FFT
/// window blurs the gaps between dots together, so the reading is not reliable there.
/// </summary>
public static class CwSpeedEstimator
{
    public const double MinWpm = 5;
    public const double MaxWpm = 50;
    private const double MinSeconds = 2.0;
    /// <summary>Fewer than this and twice the true dot can fit as well, halving the reading.</summary>
    private const int MinIntervals = 10;
    /// <summary>The note must stand this far above the noise (log1p units) to time it.</summary>
    private const double MinContrast = 0.8;
    /// <summary>Mean misfit, as a fraction of two dots, a candidate dot length may have.</summary>
    private const double MaxMeanMisfit = 0.17;
    private const int MaxDotMultiple = 8;

    /// <param name="spectrogram">Row-major [frame, bin], as fed to the model.</param>
    /// <param name="frameSeconds">Time between spectrogram frames.</param>
    /// <returns>Words per minute (PARIS), or null when no steady keying is heard.</returns>
    public static double? Estimate(ReadOnlySpan<float> spectrogram, int frames, int bins, double frameSeconds)
    {
        if (frames * frameSeconds < MinSeconds || bins < 3 || spectrogram.Length < frames * bins)
            return null;

        var envelope = Envelope(spectrogram, frames, bins);
        var sorted = (float[])envelope.Clone();
        Array.Sort(sorted);
        var low = sorted[(int)(0.3 * (frames - 1))];
        var high = sorted[(int)(0.97 * (frames - 1))];
        if (high - low < MinContrast)
            return null;

        var onsets = Onsets(envelope, low + 0.4f * (high - low), low + 0.6f * (high - low));
        if (onsets.Count < MinIntervals + 1)
            return null;

        var intervals = new double[onsets.Count - 1];
        for (var i = 1; i < onsets.Count; i++)
            intervals[i - 1] = (onsets[i] - onsets[i - 1]) * frameSeconds;

        // Every half of a good dot length also fits, so take the longest one that fits well.
        var longestDot = 1.2 / MinWpm;
        var shortestDot = 1.2 / MaxWpm;
        for (var dot = longestDot; dot >= shortestDot; dot -= 0.0005)
        {
            if (Misfit(intervals, dot) is { } misfit && misfit <= MaxMeanMisfit)
                return Math.Clamp(1.2 / Refine(intervals, dot), MinWpm, MaxWpm);
        }

        return null;
    }

    /// <summary>Strongest note's level per frame: the loudest bin on average, with its neighbours.</summary>
    private static float[] Envelope(ReadOnlySpan<float> spectrogram, int frames, int bins)
    {
        var mean = new double[bins];
        for (var t = 0; t < frames; t++)
        for (var b = 0; b < bins; b++)
            mean[b] += spectrogram[t * bins + b];

        var peak = 0;
        for (var b = 1; b < bins; b++)
        {
            if (mean[b] > mean[peak])
                peak = b;
        }

        var lo = Math.Max(0, peak - 1);
        var hi = Math.Min(bins - 1, peak + 1);
        var envelope = new float[frames];
        for (var t = 0; t < frames; t++)
        {
            var level = 0f;
            for (var b = lo; b <= hi; b++)
                level = Math.Max(level, spectrogram[t * bins + b]);
            envelope[t] = level;
        }

        return envelope;
    }

    /// <summary>Mark starts in fractional frames, with hysteresis so noise does not split a mark.</summary>
    private static List<double> Onsets(float[] envelope, float off, float on)
    {
        var onsets = new List<double>();
        var keyed = envelope[0] >= on;
        for (var t = 1; t < envelope.Length; t++)
        {
            if (!keyed && envelope[t] >= on)
            {
                keyed = true;
                var mid = (on + off) / 2;
                var start = t;
                while (start > 0 && envelope[start - 1] >= mid)
                    start--;
                var before = start > 0 ? envelope[start - 1] : envelope[start];
                var rise = envelope[start] - before;
                var fraction = rise > 0 ? (mid - before) / rise : 0;
                onsets.Add(start - 1 + Math.Clamp(fraction, 0, 1));
            }
            else if (keyed && envelope[t] < off)
            {
                keyed = false;
            }
        }

        return onsets;
    }

    /// <summary>Mean distance of each interval from its nearest even multiple of a dot, in units of two dots.</summary>
    private static double? Misfit(double[] intervals, double dot)
    {
        var total = 0.0;
        var counted = 0;
        foreach (var interval in intervals)
        {
            var units = interval / (2 * dot);
            if (units > MaxDotMultiple / 2.0 + 0.5)
                continue;

            var nearest = Math.Max(1, Math.Round(units));
            total += Math.Min(0.5, Math.Abs(units - nearest));
            counted++;
        }

        return counted >= MinIntervals ? total / counted : null;
    }

    /// <summary>Least-squares dot length for the intervals that fit <paramref name="dot"/>.</summary>
    private static double Refine(double[] intervals, double dot)
    {
        double sumIn = 0, sumUnits = 0;
        foreach (var interval in intervals)
        {
            var units = interval / (2 * dot);
            if (units > MaxDotMultiple / 2.0 + 0.5)
                continue;

            var nearest = Math.Max(1, Math.Round(units));
            if (Math.Abs(units - nearest) > 0.25)
                continue;

            sumIn += interval * nearest;
            sumUnits += nearest * nearest;
        }

        return sumUnits > 0 ? sumIn / (2 * sumUnits) : dot;
    }
}
