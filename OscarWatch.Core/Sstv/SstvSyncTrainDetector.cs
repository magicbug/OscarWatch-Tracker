namespace OscarWatch.Core.Sstv;

/// <param name="Mode">Mode whose line period and sync length matched.</param>
/// <param name="FirstSyncIndex">Sync start of the earliest line in the run.</param>
/// <param name="OffsetHz">Measured sync frequency minus 1200 Hz.</param>
public readonly record struct SstvSyncTrainResult(SstvMode Mode, long FirstSyncIndex, double OffsetHz);

/// <summary>
/// Picks the mode from the picture itself when the VIS header was lost, which is common
/// at the start of a noisy pass: sync pulses repeat at the mode's line period and have
/// the mode's sync length, so a regular run of them identifies the mode.
/// </summary>
public sealed class SstvSyncTrainDetector
{
    private const int StepsChecked = 5;
    private const int HitsNeeded = 4;

    /// <summary>
    /// Longer run checked when the short one misses too many syncs. For a pulse that lands on
    /// the grid by chance with probability p, 7 of 10 (about 120p⁷) is rarer than 4 of 5 (5p⁴).
    /// </summary>
    private const int LongStepsChecked = 10;
    private const int LongHitsNeeded = 7;

    /// <summary>
    /// A noisy sync can dip for a couple of milliseconds and be seen as two short pulses.
    /// Fragments closer than this are one pulse. Distinct lines are much further apart.
    /// </summary>
    private const double BridgeMs = 4;

    /// <summary>How far from the middle of a line another sync may sit and still count.</summary>
    private const double GapSyncWindowMs = 20;

    /// <summary>Syncs exactly mid-line that switch a long mode to its half-length mode.</summary>
    private const int HalfModeGapSyncs = 3;

    /// <summary>
    /// Shortest mid-line pulse, as a fraction of the sync length, that counts towards
    /// <see cref="HalfModeGapSyncs"/>. Weak syncs on a fading start come out much shorter than
    /// full length, and picture content never dips to the sync tone exactly on the line grid.
    /// </summary>
    private const double HalfModeMinPulseFraction = 0.12;

    /// <summary>Most and fewest lines the stacked search adds up.</summary>
    private const int StackLines = 12;
    private const int MinStackLines = 6;
    /// <summary>Average sync contrast (sync score inside the pulse against the line either side) the stacked peak needs.</summary>
    private const double StackMinContrast = 0.4;
    /// <summary>How far the stacked peak must stand above the other line positions, in robust standard deviations.</summary>
    private const double StackMinPeakSigmas = 8;
    /// <summary>Contrast that makes one line's sync count as present.</summary>
    private const double StackLineContrast = 0.3;
    /// <summary>Share of the stacked lines that must each show the sync, so one strong burst cannot pass.</summary>
    private const double StackMinLineShare = 0.5;

    private readonly int _rate;
    private double[] _scoreSums = [];
    private long _scoreOrigin;
    private int _scoreSpan;

    public SstvSyncTrainDetector(int sampleRate) => _rate = sampleRate;

    public bool TryFind(
        SstvSignalBuffer buffer,
        long from,
        double offsetHz,
        IReadOnlyList<SstvMode> candidates,
        out SstvSyncTrainResult result)
    {
        result = default;
        var pulses = FindPulses(buffer, Math.Max(from, buffer.Start), buffer.End, offsetHz);
        if (pulses.Count < HitsNeeded + 1)
            return false;

        SstvMode? bestMode = null;
        var bestStart = 0L;
        var bestHits = 0.0;
        var bestError = double.MaxValue;
        var bestPeriod = 0.0;
        var bestSync = 0.0;
        var bestTol = 0.0;
        foreach (var mode in candidates)
        {
            var period = mode.LineMs * _rate / 1000.0;
            var syncLen = mode.SyncMs * _rate / 1000.0;
            var tol = 0.004 * period + 0.002 * _rate;
            if (buffer.End - buffer.Start < period * (StepsChecked + 1))
                continue;

            for (var i = 0; i < pulses.Count; i++)
            {
                var p = pulses[i];
                if (!LengthMatches(p.Length, syncLen))
                    continue;
                if (p.Start + period * StepsChecked > buffer.End)
                    break;

                var (hits, error) = CountOnGrid(pulses, p.Start, period, syncLen, tol, StepsChecked);
                var steps = StepsChecked;
                // In a fade whole runs of syncs go missing. A longer run that still lines up
                // mostly on the grid is less likely from noise than the short test.
                if (hits < HitsNeeded && p.Start + period * LongStepsChecked <= buffer.End)
                {
                    (hits, error) = CountOnGrid(pulses, p.Start, period, syncLen, tol, LongStepsChecked);
                    steps = LongStepsChecked;
                    if (hits < LongHitsNeeded)
                        continue;
                }
                else if (hits < HitsNeeded)
                    continue;
                var hitShare = hits / (double)steps;

                // Robot 72 is twice Robot 36 and uses the same sync, so every other
                // Robot 36 pulse sits on the Robot 72 grid. Syncs in the gaps mean
                // the shorter line is the one actually sent.
                if (GapsHoldAnotherSync(pulses, p.Start, period, syncLen))
                    continue;

                // On a weak start the shorter mode can miss too many syncs to pass on its
                // own while the longer one, needing only every other pulse, gets through.
                // A sync-length pulse never belongs mid-line, so a few lined up there
                // mean the half-length mode is the one being sent.
                var found = mode;
                var foundPeriod = period;
                var foundSync = syncLen;
                var foundTol = tol;
                if (HalfPeriodMode(candidates, period, syncLen) is { } half
                    && GapSyncsOnGrid(pulses, p.Start, period, syncLen, tol) >= HalfModeGapSyncs)
                {
                    found = half;
                    foundPeriod = half.LineMs * _rate / 1000.0;
                    foundSync = half.SyncMs * _rate / 1000.0;
                    foundTol = 0.004 * foundPeriod + 0.002 * _rate;
                }

                error /= hits;
                if (bestMode is not null && IsHarmonicPeriod(foundPeriod, bestPeriod, foundSync, bestSync))
                    break;

                var shorterFits = bestMode is not null && IsHarmonicPeriod(bestPeriod, foundPeriod, bestSync, foundSync);
                if (shorterFits || hitShare > bestHits || (hitShare == bestHits && error < bestError))
                {
                    bestMode = found;
                    bestStart = p.Start;
                    bestHits = hitShare;
                    bestError = error;
                    bestPeriod = foundPeriod;
                    bestSync = foundSync;
                    bestTol = foundTol;
                }

                break;
            }
        }

        if (bestMode is null)
            return false;

        bestStart = EarliestOnGrid(pulses, bestStart, bestPeriod, bestSync, bestTol);
        var len = (int)Math.Round(bestMode.SyncMs * _rate / 1000.0);
        var syncHz = SstvSyncDetector.SyncHz + offsetHz;
        var measured = SstvSyncDetector.MeasureHz(buffer, bestStart, len, syncHz);
        var offset = double.IsNaN(measured) ? offsetHz : measured - SstvSyncDetector.SyncHz;
        result = new SstvSyncTrainResult(bestMode, bestStart, offset);
        return true;
    }

    /// <summary>
    /// For fades that take out too many syncs for a regular run of pulses: at each candidate
    /// mode's line period, add up the sync contrast of the last few lines at every position in
    /// the line. Missing and noisy syncs average out while the rest add up, so the line position
    /// shows as one clear peak. The demodulated frequency is used rather than tone power: it
    /// only shows syncs where the signal is good enough to show the picture too, so a picture
    /// is not started from syncs heard well before any of its rows can be seen.
    /// </summary>
    public bool TryFindStacked(
        SstvSignalBuffer buffer,
        long from,
        double offsetHz,
        IReadOnlyList<SstvMode> candidates,
        out SstvSyncTrainResult result)
    {
        result = default;
        from = Math.Max(from, buffer.Start);
        var span = (int)(buffer.End - from);
        if (span <= 0)
            return false;
        BuildScoreSums(buffer, from, span, SstvSyncDetector.SyncHz + offsetHz);

        var found = new List<(SstvMode Mode, double Sigmas, double FirstSync, double Contrast)>();
        foreach (var mode in candidates)
        {
            if (StackMode(mode, span) is { } hit)
                found.Add(hit);
        }

        if (found.Count == 0)
            return false;

        var best = found.MaxBy(f => f.Sigmas);
        // Robot 72 is twice Robot 36 with the same sync: when both stack up, the shorter line is the one sent.
        var bestPeriod = best.Mode.LineMs;
        var half = found.FirstOrDefault(f =>
            Math.Abs(bestPeriod / f.Mode.LineMs - 2) <= 0.04 && f.Sigmas >= 0.7 * best.Sigmas
            && IsHarmonicPeriod(bestPeriod, f.Mode.LineMs, best.Mode.SyncMs, f.Mode.SyncMs));
        if (half.Mode is not null)
            best = half;

        var start = (long)Math.Round(best.FirstSync);
        var len = (int)Math.Round(best.Mode.SyncMs * _rate / 1000.0);
        var syncHz = SstvSyncDetector.SyncHz + offsetHz;
        var measured = SstvSyncDetector.MeasureHz(buffer, start, len, syncHz);
        var offset = double.IsNaN(measured) ? offsetHz : measured - SstvSyncDetector.SyncHz;
        result = new SstvSyncTrainResult(best.Mode, start, offset);
        return true;
    }

    /// <summary>Stacked peak for one mode over the newest lines, or null when it does not pass.</summary>
    private (SstvMode Mode, double Sigmas, double FirstSync, double Contrast)? StackMode(SstvMode mode, int span)
    {
        var period = mode.LineMs * _rate / 1000.0;
        var syncLen = (int)Math.Round(mode.SyncMs * _rate / 1000.0);
        var side = Math.Max(1, syncLen / 2);
        var lines = Math.Min(StackLines, (int)(span / period) - 1);
        if (lines < MinStackLines)
            return null;

        // Line k's sync sits at origin + phase + k * period; the newest line ends inside the buffer.
        var origin = span - (lines + 1) * period;
        var step = Math.Max(1, syncLen / 6);
        var phases = (int)(period / step);
        var stacked = new double[phases];
        for (var p = 0; p < phases; p++)
        {
            var sum = 0.0;
            for (var k = 0; k < lines; k++)
                sum += LineContrast(origin + p * step + k * period, syncLen, side);
            stacked[p] = sum / lines;
        }

        var peak = 0;
        for (var p = 1; p < phases; p++)
        {
            if (stacked[p] > stacked[peak])
                peak = p;
        }

        var sorted = (double[])stacked.Clone();
        Array.Sort(sorted);
        var median = sorted[phases / 2];
        for (var p = 0; p < phases; p++)
            sorted[p] = Math.Abs(stacked[p] - median);
        Array.Sort(sorted);
        var sigma = 1.4826 * sorted[phases / 2] + 1e-6;

        // Refine to the sample.
        var bestPhase = (double)peak * step;
        var bestValue = double.MinValue;
        for (var d = -step; d <= step; d++)
        {
            var phase = peak * step + d;
            var sum = 0.0;
            for (var k = 0; k < lines; k++)
                sum += LineContrast(origin + phase + k * period, syncLen, side);
            if (sum > bestValue)
            {
                bestValue = sum;
                bestPhase = phase;
            }
        }

        var contrast = bestValue / lines - median;
        var sigmas = (bestValue / lines - median) / sigma;
        if (contrast < StackMinContrast || sigmas < StackMinPeakSigmas)
            return null;

        var present = 0;
        for (var k = 0; k < lines; k++)
        {
            if (LineContrast(origin + bestPhase + k * period, syncLen, side) >= StackLineContrast)
                present++;
        }

        if (present < StackMinLineShare * lines)
            return null;

        // Walk back to the first line whose sync still shows, so the picture is not cropped.
        var first = origin + bestPhase;
        while (first - period - side >= 0 && LineContrast(first - period, syncLen, side) >= StackLineContrast)
            first -= period;

        return (mode, sigmas, _scoreOrigin + first, contrast);
    }

    /// <summary>Mean sync score inside the pulse at <paramref name="position"/> (relative to the score sums) less that either side.</summary>
    private double LineContrast(double position, int syncLen, int side)
    {
        var s = (int)Math.Round(position);
        if (s - side < 0 || s + syncLen + side > _scoreSpan)
            return 0;
        var inside = (_scoreSums[s + syncLen] - _scoreSums[s]) / syncLen;
        var before = (_scoreSums[s] - _scoreSums[s - side]) / side;
        var after = (_scoreSums[s + syncLen + side] - _scoreSums[s + syncLen]) / side;
        return inside - 0.5 * (before + after);
    }

    /// <summary>Running sums of the per-sample sync score from <paramref name="from"/>.</summary>
    private void BuildScoreSums(SstvSignalBuffer buffer, long from, int span, double expectedHz)
    {
        if (_scoreSums.Length < span + 1)
            _scoreSums = new double[span + 1];
        _scoreOrigin = from;
        for (var k = 0; k < span; k++)
            _scoreSums[k + 1] = _scoreSums[k] + SstvSyncDetector.SampleScore(buffer[from + k], expectedHz);
        _scoreSpan = span;
    }

    /// <summary>Sync-length pulses within <paramref name="tol"/> of each of the next <paramref name="steps"/> lines, and their summed timing error.</summary>
    private static (int Hits, double Error) CountOnGrid(
        List<(long Start, double Length)> pulses,
        long start,
        double period,
        double syncLen,
        double tol,
        int steps)
    {
        var hits = 0;
        var error = 0.0;
        for (var k = 1; k <= steps; k++)
        {
            var expected = start + k * period;
            var q = Nearest(pulses, expected);
            if (q is { } hit && Math.Abs(hit.Start - expected) <= tol && LengthMatches(hit.Length, syncLen))
            {
                hits++;
                error += Math.Abs(hit.Start - expected);
            }
        }

        return (hits, error);
    }

    /// <summary>
    /// True when <paramref name="longer"/> is an integer multiple of <paramref name="shorter"/>
    /// and the two modes could be seeing the same sync pulses.
    /// </summary>
    private static bool IsHarmonicPeriod(double longer, double shorter, double longerSync, double shorterSync)
    {
        if (shorter <= 0 || longer < shorter * 1.5)
            return false;
        if (!LengthMatches(longerSync, shorterSync) || !LengthMatches(shorterSync, longerSync))
            return false;

        var ratio = longer / shorter;
        var nearest = Math.Round(ratio);
        return nearest >= 2 && Math.Abs(ratio - nearest) <= 0.02 * nearest;
    }

    /// <summary>
    /// Sync-length pulses near the middle of several lines. A genuine line has porch and
    /// picture there, so this is a mode whose period is a multiple of the real one.
    /// </summary>
    private bool GapsHoldAnotherSync(
        List<(long Start, double Length)> pulses,
        long start,
        double period,
        double syncLen)
    {
        var window = GapSyncWindowMs * _rate / 1000.0;
        var found = 0;
        for (var k = 0; k < StepsChecked; k++)
        {
            var mid = start + (k + 0.5) * period;
            if (HasPulseBetween(pulses, mid - window, mid + window, syncLen))
                found++;
        }

        return found >= (StepsChecked + 1) / 2;
    }

    /// <summary>A candidate with half of <paramref name="period"/> and the same sync length, if any.</summary>
    private SstvMode? HalfPeriodMode(IReadOnlyList<SstvMode> candidates, double period, double syncLen)
    {
        foreach (var other in candidates)
        {
            var otherPeriod = other.LineMs * _rate / 1000.0;
            var otherSync = other.SyncMs * _rate / 1000.0;
            if (Math.Abs(period / otherPeriod - 2) <= 0.04 && IsHarmonicPeriod(period, otherPeriod, syncLen, otherSync))
                return other;
        }

        return null;
    }

    /// <summary>Pulses, even weak short ones, exactly half way between lines within the line tolerance.</summary>
    private static int GapSyncsOnGrid(
        List<(long Start, double Length)> pulses,
        long start,
        double period,
        double syncLen,
        double tol)
    {
        var minLength = HalfModeMinPulseFraction * syncLen;
        var found = 0;
        for (var k = 0; k < StepsChecked; k++)
        {
            var mid = start + (k + 0.5) * period;
            if (pulses.Any(p => Math.Abs(p.Start - mid) <= tol && p.Length >= minLength && p.Length <= syncLen * 1.6))
                found++;
        }

        return found;
    }

    private static bool HasPulseBetween(
        List<(long Start, double Length)> pulses,
        double lo,
        double hi,
        double syncLen)
    {
        var low = 0;
        var high = pulses.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (pulses[mid].Start < lo)
                low = mid + 1;
            else
                high = mid;
        }

        for (var i = low; i < pulses.Count && pulses[i].Start <= hi; i++)
        {
            if (LengthMatches(pulses[i].Length, syncLen))
                return true;
        }

        return false;
    }

    /// <summary>Walk back to the first sync still on this line period, so the picture is not cropped.</summary>
    private static long EarliestOnGrid(
        List<(long Start, double Length)> pulses,
        long start,
        double period,
        double syncLen,
        double tol)
    {
        for (var n = 0; n < 40; n++)
        {
            var expected = start - period;
            var q = Nearest(pulses, expected);
            if (q is not { } prev || prev.Start >= start)
                break;
            if (Math.Abs(prev.Start - expected) > tol || !LengthMatches(prev.Length, syncLen))
                break;
            start = prev.Start;
        }

        return start;
    }

    private static bool LengthMatches(double measured, double expected) =>
        measured >= expected * 0.55 && measured <= expected * 1.6;

    private static (long Start, double Length)? Nearest(List<(long Start, double Length)> pulses, double position)
    {
        var lo = 0;
        var hi = pulses.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (pulses[mid].Start < position)
                lo = mid + 1;
            else
                hi = mid;
        }

        (long Start, double Length)? best = null;
        for (var i = Math.Max(0, lo - 1); i <= Math.Min(pulses.Count - 1, lo); i++)
        {
            if (best is null || Math.Abs(pulses[i].Start - position) < Math.Abs(best.Value.Start - position))
                best = pulses[i];
        }

        return best;
    }

    /// <summary>Runs where a 2 ms average of the sync score stays above one half.</summary>
    private List<(long Start, double Length)> FindPulses(SstvSignalBuffer buffer, long from, long to, double offsetHz)
    {
        var pulses = new List<(long, double)>();
        var expected = SstvSyncDetector.SyncHz + offsetHz;
        var win = Math.Max(1, _rate / 500);
        var sum = 0.0;
        var weight = 0.0;
        var weighted = buffer.Weighted;
        var inRun = false;
        long runStart = 0;
        for (var i = from; i < to; i++)
        {
            var w = buffer.Weight(i);
            sum += w * SstvSyncDetector.SampleScore(buffer[i], expected);
            weight += w;
            if (i - win >= from)
            {
                var old = buffer.Weight(i - win);
                sum -= old * SstvSyncDetector.SampleScore(buffer[i - win], expected);
                weight -= old;
            }
            if (i - from < win)
                continue;

            var on = weighted ? weight > 1e-6 && sum / weight > 0.5 : sum / win > 0.5;
            var centre = i - win / 2;
            if (on && !inRun)
            {
                inRun = true;
                runStart = centre;
            }
            else if (!on && inRun)
            {
                inRun = false;
                pulses.Add((runStart, centre - runStart));
            }
        }

        return BridgeSplits(pulses);
    }

    /// <summary>Join fragments of one sync that a brief dip split apart.</summary>
    private List<(long Start, double Length)> BridgeSplits(List<(long Start, double Length)> pulses)
    {
        if (pulses.Count == 0)
            return pulses;

        var maxGap = BridgeMs * _rate / 1000.0;
        var bridged = new List<(long Start, double Length)>(pulses.Count);
        var start = pulses[0].Start;
        var end = pulses[0].Start + pulses[0].Length;
        for (var i = 1; i < pulses.Count; i++)
        {
            var pulse = pulses[i];
            var pulseEnd = pulse.Start + pulse.Length;
            if (pulse.Start - end <= maxGap)
                end = Math.Max(end, pulseEnd);
            else
            {
                bridged.Add((start, end - start));
                start = pulse.Start;
                end = pulseEnd;
            }
        }

        bridged.Add((start, end - start));
        return bridged;
    }
}
