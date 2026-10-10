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

    private readonly int _rate;

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
        var bestHits = 0;
        var bestError = double.MaxValue;
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

                var hits = 0;
                var error = 0.0;
                for (var k = 1; k <= StepsChecked; k++)
                {
                    var expected = p.Start + k * period;
                    var q = Nearest(pulses, expected);
                    if (q is { } hit && Math.Abs(hit.Start - expected) <= tol && LengthMatches(hit.Length, syncLen))
                    {
                        hits++;
                        error += Math.Abs(hit.Start - expected);
                    }
                }

                if (hits < HitsNeeded)
                    continue;

                error /= hits;
                if (hits > bestHits || (hits == bestHits && error < bestError))
                {
                    bestMode = mode;
                    bestStart = p.Start;
                    bestHits = hits;
                    bestError = error;
                }

                break;
            }
        }

        if (bestMode is null)
            return false;

        var len = (int)Math.Round(bestMode.SyncMs * _rate / 1000.0);
        var syncHz = SstvSyncDetector.SyncHz + offsetHz;
        var measured = SstvSyncDetector.MeasureHz(buffer, bestStart, len, syncHz);
        var offset = double.IsNaN(measured) ? offsetHz : measured - SstvSyncDetector.SyncHz;
        result = new SstvSyncTrainResult(bestMode, bestStart, offset);
        return true;
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
        var inRun = false;
        long runStart = 0;
        for (var i = from; i < to; i++)
        {
            sum += SstvSyncDetector.SampleScore(buffer[i], expected);
            if (i - win >= from)
                sum -= SstvSyncDetector.SampleScore(buffer[i - win], expected);
            if (i - from < win)
                continue;

            var on = sum / win > 0.5;
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

        return pulses;
    }
}
