namespace OscarWatch.Core.Ft4;

/// <summary>
/// Finds a late FT4 burst in a slot recording so it can be shifted into the
/// native decoder's early time window. ft8_lib only searches about the first
/// 0.9 s; a full-duplex echo often starts later and is still obvious on the waterfall.
/// </summary>
public static class Ft4EchoAligner
{
    /// <summary>Latest burst start the stock FT4 candidate search will still see.</summary>
    public const double NativeWindowSeconds = 0.90;

    /// <summary>Lead-in left in front of the tone after alignment.</summary>
    public const double AlignedLeadSeconds = 0.35;

    public static bool TryFindToneOnset(
        ReadOnlySpan<float> samples,
        int sampleRate,
        double centreHz,
        out int onsetSample)
    {
        onsetSample = 0;
        if (sampleRate < 8000
            || samples.Length < sampleRate
            || !double.IsFinite(centreHz))
        {
            return false;
        }

        centreHz = Math.Clamp(centreHz, 200, 3000);
        var bestHz = centreHz;
        var bestPeak = 0.0;
        // The echo can sit well off the TX marker. A ±80 Hz search missed a copy
        // that was obvious on the waterfall, so calibration never ran.
        for (var offset = -200.0; offset <= 200.0; offset += 20.0)
        {
            var hz = centreHz + offset;
            if (hz < 150 || hz > 3400)
                continue;
            var peak = PeakWindowPower(samples, sampleRate, hz);
            if (peak > bestPeak)
            {
                bestPeak = peak;
                bestHz = hz;
            }
        }

        if (bestPeak <= 0)
            return false;

        return TryOnsetAt(samples, sampleRate, bestHz, bestPeak, out onsetSample);
    }

    /// <summary>
    /// Strongest steady tone within ±<paramref name="halfWidthHz"/> of <paramref name="centreHz"/>,
    /// measured over the FT4 burst (not the quiet ends of the slot).
    /// Rejects a peak stuck on the edge of the window, which is usually another station.
    /// </summary>
    public static bool TryMeasurePeakHz(
        ReadOnlySpan<float> samples,
        int sampleRate,
        double centreHz,
        out double peakHz,
        double halfWidthHz = 220)
    {
        peakHz = 0;
        if (sampleRate < 8000
            || samples.Length < sampleRate
            || !double.IsFinite(centreHz)
            || !double.IsFinite(halfWidthHz)
            || halfWidthHz < 20)
        {
            return false;
        }

        var start = (int)(0.8 * sampleRate);
        var end = Math.Min(samples.Length, (int)(5.2 * sampleRate));
        if (end - start < sampleRate)
        {
            start = 0;
            end = samples.Length;
        }

        var burst = samples.Slice(start, end - start);
        var lo = Math.Max(150, centreHz - halfWidthHz);
        var hi = Math.Min(3400, centreHz + halfWidthHz);
        if (hi - lo < 40)
            return false;

        var bestHz = lo;
        var best = -1.0;
        var powers = new List<double>();
        for (var hz = lo; hz <= hi; hz += 10)
        {
            var power = TonePower(burst, sampleRate, hz);
            powers.Add(power);
            if (power > best)
            {
                best = power;
                bestHz = hz;
            }
        }

        var refineLo = Math.Max(lo, bestHz - 10);
        var refineHi = Math.Min(hi, bestHz + 10);
        for (var hz = refineLo; hz <= refineHi; hz += 1)
        {
            var power = TonePower(burst, sampleRate, hz);
            if (power > best)
            {
                best = power;
                bestHz = hz;
            }
        }

        powers.Sort();
        var median = powers[powers.Count / 2];
        if (median <= 0 || best < median * 8.0)
            return false;

        if (bestHz <= lo + 12 || bestHz >= hi - 12)
            return false;

        peakHz = bestHz;
        return true;
    }

    private static double TonePower(ReadOnlySpan<float> samples, int sampleRate, double hz)
    {
        var omega = 2.0 * Math.PI * hz / sampleRate;
        double iSum = 0;
        double qSum = 0;
        for (var n = 0; n < samples.Length; n++)
        {
            var s = samples[n];
            iSum += s * Math.Cos(omega * n);
            qSum += s * Math.Sin(omega * n);
        }

        var nSamp = (double)samples.Length;
        return (iSum * iSum + qSum * qSum) / (nSamp * nSamp);
    }

    /// <summary>
    /// Drop the quiet lead so <paramref name="onsetSample"/> sits at
    /// <see cref="AlignedLeadSeconds"/>. Keeps enough audio for a full FT4 burst.
    /// </summary>
    public static float[] AlignToNativeWindow(
        float[] samples,
        int sampleRate,
        int onsetSample,
        out double trimmedSeconds)
    {
        trimmedSeconds = 0;
        if (sampleRate < 8000 || samples.Length == 0)
            return samples;

        var lead = (int)(AlignedLeadSeconds * sampleRate);
        var minRemain = (int)(5.3 * sampleRate);
        var start = onsetSample - lead;
        if (start < 0)
            start = 0;
        if (samples.Length - start < minRemain)
            start = Math.Max(0, samples.Length - minRemain);

        trimmedSeconds = start / (double)sampleRate;
        if (start == 0)
            return samples;

        var aligned = new float[samples.Length - start];
        Array.Copy(samples, start, aligned, 0, aligned.Length);
        return aligned;
    }

    /// <summary>
    /// Candidate buffers that put a late full-duplex echo into the native decoder window.
    /// Prefers a measured onset at the waterfall peak; otherwise tries a few fixed shifts.
    /// </summary>
    public static IEnumerable<(float[] Samples, double ShiftSeconds)> EnumerateEchoAlignments(
        float[] samples,
        int sampleRate,
        double centreHz)
    {
        if (samples.Length < (int)(5.3 * sampleRate) || sampleRate < 8000)
            yield break;

        var searchHz = centreHz;
        var havePeak = TryMeasurePeakHz(samples, sampleRate, centreHz, out var peakHz);
        if (havePeak)
            searchHz = peakHz;

        if (TryFindToneOnset(samples, sampleRate, searchHz, out var onset)
            && onset > (int)(AlignedLeadSeconds * sampleRate))
        {
            var aligned = AlignToNativeWindow(samples, sampleRate, onset, out var shiftSec);
            if (shiftSec >= 0.15 && aligned.Length >= (int)(5.3 * sampleRate))
            {
                yield return (aligned, shiftSec);
                yield break;
            }
        }

        // Tone is visible on the waterfall but the onset edge is muddy (common on a
        // strong satellite echo). Try a few plausible starts so DecodeFt4 still sees it.
        if (!havePeak)
            yield break;

        foreach (var assumeOnsetSec in new[] { 0.9, 1.2, 1.5, 1.8, 2.2, 2.6 })
        {
            var assumed = (int)(assumeOnsetSec * sampleRate);
            if (assumed >= samples.Length)
                break;

            var aligned = AlignToNativeWindow(samples, sampleRate, assumed, out var shiftSec);
            if (shiftSec < 0.15 || aligned.Length < (int)(5.3 * sampleRate))
                continue;

            yield return (aligned, shiftSec);
        }
    }

    private static bool TryOnsetAt(
        ReadOnlySpan<float> samples,
        int sampleRate,
        double hz,
        double peak,
        out int onsetSample)
    {
        onsetSample = 0;
        var window = Math.Max(64, sampleRate / 25);
        var hop = Math.Max(32, window / 2);
        if (samples.Length < window * 4)
            return false;

        var powers = WindowPowers(samples, sampleRate, hz, window, hop);
        if (powers.Count < 4)
            return false;

        var sorted = powers.ToArray();
        Array.Sort(sorted);
        var noise = sorted[sorted.Length / 5];
        var threshold = Math.Max(noise * 8.0, peak * 0.2);
        if (peak < noise * 6.0)
            return false;

        for (var i = 0; i + 2 < powers.Count; i++)
        {
            if (powers[i] < threshold || powers[i + 1] < threshold || powers[i + 2] < threshold)
                continue;
            onsetSample = i * hop;
            return true;
        }

        return false;
    }

    private static double PeakWindowPower(ReadOnlySpan<float> samples, int sampleRate, double hz)
    {
        var window = Math.Max(64, sampleRate / 25);
        var hop = Math.Max(32, window / 2);
        var powers = WindowPowers(samples, sampleRate, hz, window, hop);
        var peak = 0.0;
        foreach (var p in powers)
        {
            if (p > peak)
                peak = p;
        }

        return peak;
    }

    private static List<double> WindowPowers(
        ReadOnlySpan<float> samples,
        int sampleRate,
        double hz,
        int window,
        int hop)
    {
        var omega = 2.0 * Math.PI * hz / sampleRate;
        var powers = new List<double>(samples.Length / hop);
        for (var start = 0; start + window <= samples.Length; start += hop)
        {
            double iSum = 0;
            double qSum = 0;
            for (var n = 0; n < window; n++)
            {
                var s = samples[start + n];
                iSum += s * Math.Cos(omega * n);
                qSum += s * Math.Sin(omega * n);
            }

            powers.Add((iSum * iSum + qSum * qSum) / (window * (double)window));
        }

        return powers;
    }
}
