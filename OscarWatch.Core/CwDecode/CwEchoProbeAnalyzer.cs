using System.Runtime.InteropServices;

namespace OscarWatch.Core.CwDecode;

/// <summary>One spectral peak in the probe waterfall window considered as a potential own-call echo.</summary>
public sealed record CwProbeCandidatePeak(
    double FrequencyHz,
    double SnrDb,
    double RiseDb);

/// <summary>Outcome of scanning a captured CW probe window for the operator's callsign echo.</summary>
public enum CwEchoProbeOutcome
{
    Matched,
    NoEcho,
    Ambiguous
}

/// <summary>Result of isolating and verifying candidate CW peaks against the operator's callsign.</summary>
public sealed record CwEchoProbeResult(
    CwEchoProbeOutcome Outcome,
    double? EchoHz = null,
    float MeanConfidence = 0f,
    double SnrDb = 0,
    IReadOnlyList<double>? AmbiguousFrequenciesHz = null,
    string? DecodedPreview = null);

/// <summary>
/// Pure DSP + DeepCW echo finder and disambiguator operating on a captured 8 kHz audio window
/// and its feed-forward-corrected waterfall rows.
/// </summary>
public static class CwEchoProbeAnalyzer
{
    public const int SampleRate = 8000;
    public const double SearchMinHz = 150.0;
    public const double SearchMaxHz = 1900.0;
    public const double MinPeakAboveMedianDb = 8.0;
    public const double PeakMinSeparationHz = 40.0;
    public const int MaxCandidates = 5;
    /// <summary>Half-width of the narrow Blackman band-pass used to isolate one candidate tone.</summary>
    public const double IsolationHalfBandwidthHz = 110.0;
    public const float MinCharConfidence = 0.55f;
    public const float MinMeanConfidence = 0.75f;
    public const double AmbiguityToleranceHz = 25.0;

    private const int BandPassTaps = 383;
    private const double ResampleCutoffHz = 1400.0;
    private const int ResampleTaps = 127;

    /// <summary>
    /// Find prominent CW spectral peaks across 150–1900 Hz, preferring peaks whose probe-window
    /// linear power rose over the pre-TX baseline spectrum.
    /// </summary>
    public static IReadOnlyList<CwProbeCandidatePeak> FindCandidatePeaks(
        IReadOnlyList<float[]> probeRows,
        double binHz,
        IReadOnlyList<float[]>? baselineRows = null,
        double? preferredBandCentreHz = null)
    {
        var avgPower = ComputeAverageLinearPower(probeRows);
        if (avgPower is null || binHz <= 0)
            return [];

        var baselinePower = baselineRows is { Count: > 0 }
            ? ComputeAverageLinearPower(baselineRows)
            : null;

        return FindCandidatePeaksFromPower(avgPower, baselinePower, binHz, preferredBandCentreHz);
    }

    /// <summary>
    /// Discover candidate peaks in <paramref name="probeRows"/>, isolate and decode each peak
    /// through DeepCW, and accept only a single unambiguous high-confidence exact match on
    /// <paramref name="expectedCallsign"/>.
    /// </summary>
    public static CwEchoProbeResult Analyze(
        ReadOnlySpan<float> samplesAt8k,
        IReadOnlyList<float[]> probeRows,
        double binHz,
        string expectedCallsign,
        CwModelMetadata metadata,
        ICwModelRunner runner,
        IReadOnlyList<float[]>? baselineRows = null,
        double? preferredBandCentreHz = null)
    {
        var normalizedTarget = NormalizeWord(expectedCallsign);
        if (normalizedTarget.Length == 0 || samplesAt8k.Length == 0 || probeRows.Count == 0 || binHz <= 0)
            return new CwEchoProbeResult(CwEchoProbeOutcome.NoEcho);

        var avgPower = ComputeAverageLinearPower(probeRows);
        if (avgPower is null)
            return new CwEchoProbeResult(CwEchoProbeOutcome.NoEcho);

        var baselinePower = baselineRows is { Count: > 0 }
            ? ComputeAverageLinearPower(baselineRows)
            : null;

        var candidates = FindCandidatePeaksFromPower(avgPower, baselinePower, binHz, preferredBandCentreHz);
        if (candidates.Count == 0)
            return new CwEchoProbeResult(CwEchoProbeOutcome.NoEcho);

        var modelCentreHz = (metadata.SpectrogramMinFreqHz + metadata.SpectrogramMaxFreqHz) / 2.0;
        var matches = new List<CandidateMatch>();

        foreach (var candidate in candidates)
        {
            var isolated = IsolateToModelRate(samplesAt8k, candidate.FrequencyHz, modelCentreHz, metadata);
            if (isolated.Count < metadata.FftLength)
                continue;

            // Measure the actual dominant tone inside the isolated 3200 Hz audio that the model will see.
            // If filter-skirt bleed-through from a stronger nearby peak dominates this slice, its shifted
            // frequency reveals the true source tone (avoiding false ambiguity).
            var dominantShiftedHz = FindDominantShiftedFrequencyHz(CollectionsMarshal.AsSpan(isolated), metadata);
            var actualToneHz = dominantShiftedHz is { } shifted
                ? candidate.FrequencyHz + (shifted - modelCentreHz)
                : candidate.FrequencyHz;

            // If this slice is actually dominated by a tone already covered by another candidate, skip duplicate inference.
            if (dominantShiftedHz is not null
                && Math.Abs(actualToneHz - candidate.FrequencyHz) > 35.0
                && candidates.Any(other =>
                    !ReferenceEquals(other, candidate)
                    && Math.Abs(other.FrequencyHz - actualToneHz) <= 35.0))
            {
                continue;
            }

            var decoder = new CwStreamDecoder(metadata, runner);
            decoder.Append(CollectionsMarshal.AsSpan(isolated));
            var decoded = decoder.FinishWithConfidence();
            if (!TryMatchExactCallsign(decoded, normalizedTarget, out var meanConfidence))
                continue;

            var resolvedHz = ResolveDominantFrequencyInBand(avgPower, binHz, actualToneHz, halfBandwidthHz: 35.0)
                ?? actualToneHz;

            matches.Add(new CandidateMatch(
                resolvedHz,
                meanConfidence,
                candidate.SnrDb,
                decoded.CommittedText.Trim()));
        }

        if (matches.Count == 0)
            return new CwEchoProbeResult(CwEchoProbeOutcome.NoEcho);

        var clusters = ClusterMatches(matches);
        if (clusters.Count >= 2)
        {
            var frequencies = clusters
                .Select(c => Math.Round(c.FrequencyHz, 1))
                .OrderBy(f => f)
                .ToArray();
            return new CwEchoProbeResult(
                CwEchoProbeOutcome.Ambiguous,
                AmbiguousFrequenciesHz: frequencies);
        }

        var winner = clusters[0];
        return new CwEchoProbeResult(
            CwEchoProbeOutcome.Matched,
            EchoHz: winner.FrequencyHz,
            MeanConfidence: winner.MeanConfidence,
            SnrDb: winner.SnrDb,
            DecodedPreview: winner.DecodedPreview);
    }

    private static IReadOnlyList<CwProbeCandidatePeak> FindCandidatePeaksFromPower(
        double[] avgPower,
        double[]? baselinePower,
        double binHz,
        double? preferredBandCentreHz)
    {
        var lo = Math.Max(1, (int)Math.Ceiling(SearchMinHz / binHz));
        var hi = Math.Min(avgPower.Length - 2, (int)Math.Floor(SearchMaxHz / binHz));
        if (hi - lo < 4)
            return [];

        var sorted = new double[hi - lo + 1];
        for (var k = lo; k <= hi; k++)
            sorted[k - lo] = avgPower[k];
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];
        if (median <= 0)
            median = 1e-20;

        var rawPeaks = new List<CwProbeCandidatePeak>();
        for (var k = lo; k <= hi; k++)
        {
            var p = avgPower[k];
            if (p <= 0 || p < avgPower[k - 1] || p < avgPower[k + 1])
                continue;

            var snrDb = 10.0 * Math.Log10(p / median);
            if (snrDb < MinPeakAboveMedianDb)
                continue;

            var freqHz = ParabolicPeakHz(avgPower, k, binHz);
            if (freqHz < SearchMinHz || freqHz > SearchMaxHz)
                continue;

            var riseDb = baselinePower is not null && k < baselinePower.Length && baselinePower[k] > 0
                ? 10.0 * Math.Log10(p / baselinePower[k])
                : snrDb;

            rawPeaks.Add(new CwProbeCandidatePeak(freqHz, snrDb, riseDb));
        }

        // Non-maximum suppression: keep strongest local peak within ±40 Hz.
        rawPeaks.Sort((a, b) => b.SnrDb.CompareTo(a.SnrDb));
        var suppressed = new List<CwProbeCandidatePeak>();
        foreach (var peak in rawPeaks)
        {
            if (suppressed.Any(existing => Math.Abs(existing.FrequencyHz - peak.FrequencyHz) < PeakMinSeparationHz))
                continue;
            suppressed.Add(peak);
        }

        // Prefer peaks whose power rose above the pre-TX baseline over steady pre-existing carriers.
        var ordered = suppressed
            .OrderByDescending(p => p.SnrDb + Math.Clamp(p.RiseDb, -6.0, 20.0))
            .Take(MaxCandidates)
            .ToList();

        if (preferredBandCentreHz is { } centre
            && double.IsFinite(centre)
            && centre >= SearchMinHz
            && centre <= SearchMaxHz
            && ordered.All(p => Math.Abs(p.FrequencyHz - centre) >= PeakMinSeparationHz))
        {
            var k = Math.Clamp((int)Math.Round(centre / binHz), lo, hi);
            var snrDb = avgPower[k] > 0 ? 10.0 * Math.Log10(avgPower[k] / median) : 0;
            var riseDb = baselinePower is not null && k < baselinePower.Length && baselinePower[k] > 0 && avgPower[k] > 0
                ? 10.0 * Math.Log10(avgPower[k] / baselinePower[k])
                : 0;
            ordered.Add(new CwProbeCandidatePeak(centre, snrDb, riseDb));
        }

        return ordered;
    }

    private static List<float> IsolateToModelRate(
        ReadOnlySpan<float> samplesAt8k,
        double candidateHz,
        double modelCentreHz,
        CwModelMetadata metadata)
    {
        var filtered = new List<float>(samplesAt8k.Length);
        ApplyNarrowBandPass(samplesAt8k, candidateHz, IsolationHalfBandwidthHz, filtered);

        var shifter = new CwFrequencyShifter(SampleRate)
        {
            ShiftHz = modelCentreHz - candidateHz
        };
        shifter.Process(filtered);

        var resampler = new CwResampler(SampleRate, metadata.SampleRate, ResampleCutoffHz, ResampleTaps);
        var atModel = new List<float>((int)(filtered.Count * (double)metadata.SampleRate / SampleRate) + 16);
        resampler.Process(CollectionsMarshal.AsSpan(filtered), atModel);
        return atModel;
    }

    private static double? FindDominantShiftedFrequencyHz(ReadOnlySpan<float> audioAtModelRate, CwModelMetadata metadata)
    {
        if (audioAtModelRate.Length < metadata.FftLength)
            return null;

        var mags = CwSpectrogram.ComputeMagnitudes(audioAtModelRate, metadata, out var frames);
        var (startBin, stopBin) = CwSpectrogram.FrequencyBinRange(metadata);
        var bins = stopBin - startBin;
        if (frames <= 0 || bins < 3)
            return null;

        var power = new double[bins];
        for (var f = 0; f < frames; f++)
        {
            var row = f * bins;
            for (var b = 0; b < bins; b++)
            {
                double m = mags[row + b];
                power[b] += m * m;
            }
        }

        var peak = 0;
        for (var b = 1; b < bins; b++)
        {
            if (power[b] > power[peak])
                peak = b;
        }

        if (power[peak] <= 0)
            return null;

        var binHz = (double)metadata.SampleRate / metadata.FftLength;
        double delta = 0;
        if (peak > 0 && peak < bins - 1)
        {
            var a = Math.Log(Math.Max(power[peak - 1], 1e-20));
            var b = Math.Log(Math.Max(power[peak], 1e-20));
            var c = Math.Log(Math.Max(power[peak + 1], 1e-20));
            var denom = a - 2 * b + c;
            if (denom != 0)
                delta = Math.Clamp(0.5 * (a - c) / denom, -0.5, 0.5);
        }

        return (startBin + peak + delta) * binHz;
    }

    private static void ApplyNarrowBandPass(
        ReadOnlySpan<float> input,
        double centreHz,
        double halfBandwidthHz,
        List<float> output)
    {
        var taps = DesignBandPass(centreHz, halfBandwidthHz);
        var n = taps.Length;
        var history = new double[n * 2];
        var pos = 0;

        for (var s = 0; s < input.Length; s++)
        {
            history[pos] = input[s];
            history[pos + n] = input[s];
            pos = (pos + 1) % n;

            var acc = 0.0;
            for (var k = 0; k < n; k++)
                acc += taps[k] * history[pos + k];
            output.Add((float)acc);
        }
    }

    private static double[] DesignBandPass(double centreHz, double halfBandwidthHz)
    {
        var high = Math.Min(SampleRate / 2.0 - 50.0, centreHz + halfBandwidthHz);
        var low = centreHz - halfBandwidthHz;
        var taps = new double[BandPassTaps];
        var mid = (BandPassTaps - 1) / 2;

        for (var i = 0; i < BandPassTaps; i++)
        {
            var n = i - mid;
            var window = 0.42
                - 0.5 * Math.Cos(2 * Math.PI * i / (BandPassTaps - 1))
                + 0.08 * Math.Cos(4 * Math.PI * i / (BandPassTaps - 1));
            var response = LowPass(n, high) - (low > 40.0 ? LowPass(n, low) : 0.0);
            taps[i] = response * window;
        }

        return taps;
    }

    private static double LowPass(int n, double cutoffHz)
    {
        var fc = cutoffHz / SampleRate;
        return n == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * n) / (Math.PI * n);
    }

    private static double? ResolveDominantFrequencyInBand(
        double[] avgPower,
        double binHz,
        double centreHz,
        double halfBandwidthHz)
    {
        var lo = Math.Max(1, (int)Math.Ceiling(Math.Max(SearchMinHz, centreHz - halfBandwidthHz) / binHz));
        var hi = Math.Min(avgPower.Length - 2, (int)Math.Floor(Math.Min(SearchMaxHz, centreHz + halfBandwidthHz) / binHz));
        if (hi < lo)
            return null;

        var peak = lo;
        for (var k = lo + 1; k <= hi; k++)
        {
            if (avgPower[k] > avgPower[peak])
                peak = k;
        }

        if (avgPower[peak] <= 0)
            return null;

        return ParabolicPeakHz(avgPower, peak, binHz);
    }

    private static double ParabolicPeakHz(double[] power, int peak, double binHz)
    {
        if (peak <= 0 || peak >= power.Length - 1)
            return peak * binHz;

        var a = Math.Log(Math.Max(power[peak - 1], 1e-20));
        var b = Math.Log(Math.Max(power[peak], 1e-20));
        var c = Math.Log(Math.Max(power[peak + 1], 1e-20));
        var denom = a - 2 * b + c;
        var delta = denom == 0 ? 0 : 0.5 * (a - c) / denom;
        return (peak + Math.Clamp(delta, -0.5, 0.5)) * binHz;
    }

    private static double[]? ComputeAverageLinearPower(IReadOnlyList<float[]> rows)
    {
        if (rows.Count == 0)
            return null;

        var width = rows[0].Length;
        if (width == 0)
            return null;

        var sum = new double[width];
        var count = 0;
        foreach (var row in rows)
        {
            if (row.Length < width)
                continue;
            for (var i = 0; i < width; i++)
                sum[i] += Math.Pow(10.0, row[i] / 10.0);
            count++;
        }

        if (count == 0)
            return null;

        var inv = 1.0 / count;
        for (var i = 0; i < width; i++)
            sum[i] *= inv;
        return sum;
    }

    private static bool TryMatchExactCallsign(
        CwDecodeUpdate update,
        string normalizedTarget,
        out float bestMeanConfidence)
    {
        bestMeanConfidence = 0f;
        var text = update.CommittedText;
        var confidence = update.CommittedConfidence;
        if (string.IsNullOrWhiteSpace(text) || confidence is null || confidence.Length < text.Length)
            return false;

        var matched = false;
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            if (i >= text.Length)
                break;

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
                i++;
            var end = i;

            while (start < end && IsTrimmablePunctuation(text[start]))
                start++;
            while (end > start && IsTrimmablePunctuation(text[end - 1]))
                end--;

            var length = end - start;
            if (length != normalizedTarget.Length)
                continue;

            if (!text.AsSpan(start, length).Equals(normalizedTarget.AsSpan(), StringComparison.OrdinalIgnoreCase))
                continue;

            var minConf = float.MaxValue;
            var sumConf = 0f;
            for (var c = start; c < end; c++)
            {
                var p = confidence[c];
                if (p < minConf)
                    minConf = p;
                sumConf += p;
            }

            var meanConf = sumConf / length;
            if (minConf >= MinCharConfidence && meanConf >= MinMeanConfidence)
            {
                matched = true;
                if (meanConf > bestMeanConfidence)
                    bestMeanConfidence = meanConf;
            }
        }

        return matched;
    }

    private static List<CandidateMatch> ClusterMatches(List<CandidateMatch> matches)
    {
        // Order strongest/highest-confidence first so each cluster keeps its best representative.
        var ordered = matches
            .OrderByDescending(m => m.MeanConfidence)
            .ThenByDescending(m => m.SnrDb)
            .ToList();

        var clusters = new List<CandidateMatch>();
        foreach (var match in ordered)
        {
            if (clusters.Any(c => Math.Abs(c.FrequencyHz - match.FrequencyHz) <= AmbiguityToleranceHz))
                continue;
            clusters.Add(match);
        }

        return clusters;
    }

    private static string NormalizeWord(string value) =>
        (value ?? "").Trim().Trim(',', '.', '?').ToUpperInvariant();

    private static bool IsTrimmablePunctuation(char c) => c is ',' or '.' or '?';

    private readonly record struct CandidateMatch(
        double FrequencyHz,
        float MeanConfidence,
        double SnrDb,
        string DecodedPreview);
}
