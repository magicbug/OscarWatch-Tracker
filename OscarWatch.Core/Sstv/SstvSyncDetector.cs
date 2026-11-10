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

    /// <summary>Samples weighing less than this are left out of the tone measurement.</summary>
    private const float MinMedianWeight = 0.2f;

    public static double SampleScore(float hz, double expectedHz) =>
        Math.Max(0, 1 - Math.Abs(hz - expectedHz) / ToleranceHz);

    /// <summary>Best sync start within ±<paramref name="halfWindow"/> of <paramref name="centre"/>.</summary>
    /// <param name="baseband">
    /// When given, each position also scores by how much of the subcarrier power is the sync
    /// tone, and the two scores are averaged.
    /// </param>
    /// <param name="basebandShiftHz">Offset present in <paramref name="baseband"/> but already removed from <paramref name="buffer"/>.</param>
    public static SstvSyncHit? Find(
        SstvSignalBuffer buffer,
        double centre,
        int halfWindow,
        int syncLength,
        double offsetHz,
        SstvBasebandBuffer? baseband = null,
        double basebandShiftHz = 0)
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
        // With a weighted buffer, untrustworthy samples count for little in the running mean.
        var rawPrefix = new double[span + smooth + 1];
        var rawCount = new double[span + smooth + 1];
        for (var i = 0; i < span + smooth; i++)
        {
            var at = rawOrigin + i;
            var inside = at >= buffer.Start && at < buffer.End;
            var w = inside ? buffer.Weight(at) : 0;
            var hz = inside ? Math.Clamp(buffer[at], expected - ToleranceHz, expected + ToleranceHz) : 0;
            rawPrefix[i + 1] = rawPrefix[i] + w * hz;
            rawCount[i + 1] = rawCount[i] + w;
        }

        var prefix = new double[span + 1];
        for (var i = 0; i < span; i++)
        {
            var n = rawCount[i + smooth] - rawCount[i];
            var score = n <= 1e-9 ? 0 : SampleScore((float)((rawPrefix[i + smooth] - rawPrefix[i]) / n), expected);
            prefix[i + 1] = prefix[i] + score;
        }

        var tone = baseband is null ? null : ToneSums(baseband, origin, span, expected + basebandShiftHz);
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
            if (tone is { } t)
            {
                var toneScore = t.Fraction(s, syncLength) - 0.5 * (t.Fraction(s - side, side) + t.Fraction(s + syncLength, side));
                score = 0.5 * (score + toneScore * syncLength);
            }
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
    /// Tone power is summed coherently over blocks this long and the blocks added by power, so
    /// a phase jump in the sender's audio does not cancel a pulse. 3 ms still puts a 1500 Hz
    /// porch almost a whole cycle off the tone.
    /// </summary>
    private const double ToneBlockMs = 3;

    /// <summary>Running sums for the fraction of subcarrier power at the sync tone.</summary>
    private sealed class ToneWindow(double[] re, double[] im, double[] power)
    {
        private static readonly int BlockLength = (int)Math.Round(ToneBlockMs * SstvDecoder.InternalRate / 1000);

        /// <summary>Share of the power in [offset, offset + length) at the tone: 1 for a clean tone.</summary>
        public double Fraction(int offset, int length)
        {
            var p = power[offset + length] - power[offset];
            if (p <= 1e-12 || length <= 0)
                return 0;

            var blocks = Math.Max(1, (int)Math.Round(length / (double)BlockLength));
            var coherent = 0.0;
            for (var b = 0; b < blocks; b++)
            {
                var from = offset + length * b / blocks;
                var to = offset + length * (b + 1) / blocks;
                var r = re[to] - re[from];
                var m = im[to] - im[from];
                coherent += (r * r + m * m) / Math.Max(1, to - from);
            }

            return coherent / p;
        }
    }

    private static ToneWindow ToneSums(SstvBasebandBuffer baseband, long origin, int span, double expectedHz)
    {
        var omega = 2 * Math.PI * (expectedHz - SstvFmDemodulator.CentreHz) / SstvDecoder.InternalRate;
        var re = new double[span + 1];
        var im = new double[span + 1];
        var power = new double[span + 1];
        for (var k = 0; k < span; k++)
        {
            var at = origin + k;
            double i = 0, q = 0;
            if (at >= baseband.Start && at < baseband.End)
            {
                i = baseband.I(at);
                q = baseband.Q(at);
            }

            var phase = -omega * at;
            var c = Math.Cos(phase);
            var s = Math.Sin(phase);
            re[k + 1] = re[k] + i * c - q * s;
            im[k + 1] = im[k] + i * s + q * c;
            power[k + 1] = power[k] + i * i + q * q;
        }

        return new ToneWindow(re, im, power);
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
            if (i < buffer.Start || i >= buffer.End || buffer.Weight(i) < MinMedianWeight)
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
