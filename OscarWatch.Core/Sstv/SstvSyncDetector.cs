namespace OscarWatch.Core.Sstv;

public readonly record struct SstvSyncHit(long Position, double Score, double MeasuredHz);

/// <summary>
/// Locates line sync pulses. Each sample scores by how close it is to the expected sync
/// tone (1 on frequency, 0 at <see cref="ToleranceHz"/> away), so the porch and picture
/// either side score zero and the window sum peaks where the pulse lines up.
/// </summary>
public static class SstvSyncDetector
{
    public const double SyncHz = 1200;
    public const double ToleranceHz = 220;

    public static double SampleScore(float hz, double expectedHz) =>
        Math.Max(0, 1 - Math.Abs(hz - expectedHz) / ToleranceHz);

    /// <summary>Best sync start within ±<paramref name="halfWindow"/> of <paramref name="centre"/>.</summary>
    public static SstvSyncHit? Find(
        SstvSignalBuffer buffer,
        double centre,
        int halfWindow,
        int syncLength,
        double offsetHz)
    {
        var from = (long)Math.Round(centre) - halfWindow;
        var to = (long)Math.Round(centre) + halfWindow;
        // A real pulse has porch or picture (1500 Hz and up) either side. Receiver noise is
        // often strongest at low audio frequencies and can sit near 1200 Hz, but then it
        // does so on both sides as well, so the score is the contrast with the neighbours.
        var side = Math.Max(1, syncLength / 2);
        from = Math.Max(from, buffer.Start);
        to = Math.Min(to, buffer.End - syncLength - side);
        if (to < from || syncLength <= 0)
            return null;

        var expected = SyncHz + offsetHz;
        var origin = from - side;
        var span = (int)(to - from) + syncLength + 2 * side;
        // Score a short running mean: FM noise throws single samples far off the tone.
        // Samples are limited to the scoring range first, so bright picture before the
        // pulse and the dark porch after it blur in by the same amount and the peak stays put.
        var smooth = Math.Max(1, syncLength / 5);
        var lo = smooth / 2;
        var rawOrigin = origin - lo;
        var rawPrefix = new double[span + smooth + 1];
        var rawCount = new int[span + smooth + 1];
        for (var i = 0; i < span + smooth; i++)
        {
            var at = rawOrigin + i;
            var inside = at >= buffer.Start && at < buffer.End;
            var hz = inside ? Math.Clamp(buffer[at], expected - ToleranceHz, expected + ToleranceHz) : 0;
            rawPrefix[i + 1] = rawPrefix[i] + hz;
            rawCount[i + 1] = rawCount[i] + (inside ? 1 : 0);
        }

        var prefix = new double[span + 1];
        for (var i = 0; i < span; i++)
        {
            var n = rawCount[i + smooth] - rawCount[i];
            var score = n == 0 ? 0 : SampleScore((float)((rawPrefix[i + smooth] - rawPrefix[i]) / n), expected);
            prefix[i + 1] = prefix[i] + score;
        }

        var bestScore = double.MinValue;
        var bestStart = 0;
        var bestEnd = 0;
        for (var i = 0; i <= to - from; i++)
        {
            var s = i + side;
            var inside = (prefix[s + syncLength] - prefix[s]) / syncLength;
            var before = (prefix[s] - prefix[s - side]) / side;
            var after = (prefix[s + syncLength + side] - prefix[s + syncLength]) / side;
            var score = (inside - 0.5 * (before + after)) * syncLength;
            if (score > bestScore + 1e-9)
            {
                bestScore = score;
                bestStart = i;
                bestEnd = i;
            }
            else if (Math.Abs(score - bestScore) <= 1e-9 && i == bestEnd + 1)
            {
                bestEnd = i;
            }
        }

        // Centre of a flat-topped peak, so a slightly long pulse does not bias early.
        var pos = from + (bestStart + bestEnd) / 2;
        var normalised = bestScore / syncLength;
        return new SstvSyncHit(pos, normalised, MeasureHz(buffer, pos, syncLength, expected));
    }

    /// <summary>
    /// Median frequency over the middle of the pulse. Noise clicks pull an FM discriminator
    /// towards the middle of the band, which drags a mean off the tone but barely moves
    /// the median. NaN when the pulse is mostly off the expected tone.
    /// </summary>
    public static double MeasureHz(SstvSignalBuffer buffer, long start, int syncLength, double expectedHz)
    {
        var inset = syncLength / 5;
        var values = new List<float>(syncLength);
        var near = 0;
        for (var i = start + inset; i < start + syncLength - inset; i++)
        {
            if (i < buffer.Start || i >= buffer.End)
                continue;
            var hz = buffer[i];
            values.Add(hz);
            if (Math.Abs(hz - expectedHz) <= ToleranceHz)
                near++;
        }

        if (values.Count < 4 || near < values.Count / 2)
            return double.NaN;

        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }
}
