namespace OscarWatch.Core.Sstv;

/// <param name="Code">7-bit VIS code.</param>
/// <param name="Mode">Mode for the code, or null when the code is not one we decode.</param>
/// <param name="StartBitIndex">Sample where the 1200 Hz start bit begins.</param>
/// <param name="ImageStartIndex">Sample just after the stop bit.</param>
/// <param name="OffsetHz">Leader frequency minus 1900 Hz: the audio tuning error.</param>
public readonly record struct SstvVisResult(
    int Code,
    SstvMode? Mode,
    long StartBitIndex,
    long ImageStartIndex,
    double OffsetHz);

/// <summary>
/// Finds the VIS header: 1900 Hz leader, 1200 Hz start bit, seven data bits LSB first
/// (1100 Hz is 1, 1300 Hz is 0), an even parity bit and a 1200 Hz stop bit, 30 ms each.
/// </summary>
public sealed class SstvVisDetector
{
    private const double LeaderHz = 1900;
    private const double SyncHz = 1200;
    private const double MaxOffsetHz = 350;
    private const double MaxLeaderSpreadHz = 330;
    private const double MinStartBitDropHz = 280;

    private readonly int _rate;
    private readonly int _ms;
    private readonly int _bit;
    private long _cursor = long.MinValue;

    public SstvVisDetector(int sampleRate)
    {
        _rate = sampleRate;
        _ms = Math.Max(1, sampleRate / 1000);
        _bit = (int)Math.Round(0.030 * sampleRate);
    }

    /// <summary>Samples needed after the start-bit edge to read the whole header.</summary>
    public int HeaderTailSamples => 10 * _bit;

    /// <summary>Forget scan progress, for example after the buffer was cleared.</summary>
    public void Reset(long fromIndex) => _cursor = fromIndex;

    /// <summary>
    /// Scan new samples for a header. When <paramref name="requireFullHeader"/> is set the
    /// first leader and the 10 ms break must be present too, which picture content
    /// cannot imitate. Use it while an image is being decoded.
    /// </summary>
    public bool TryFind(SstvSignalBuffer buffer, bool requireFullHeader, out SstvVisResult result)
    {
        result = default;
        var lookBack = requireFullHeader ? 640 * _ms : 220 * _ms;
        var first = Math.Max(_cursor, buffer.Start + lookBack);
        var last = buffer.End - HeaderTailSamples - 20 * _ms;
        var step = _ms;

        for (var t = first; t <= last; t += step)
        {
            if (!LooksLikeEdge(buffer, t, out var leader))
                continue;

            var edge = RefineEdge(buffer, t);
            if (!TryReadHeader(buffer, edge, leader, requireFullHeader, out result))
                continue;

            _cursor = result.ImageStartIndex;
            return true;
        }

        if (last + step > _cursor)
            _cursor = last + step;
        return false;
    }

    /// <summary>
    /// Noise pulls an FM discriminator towards the middle of its band, which squeezes the
    /// header tones closer to the leader. So the tests below compare tones with each other
    /// rather than with their nominal frequencies.
    /// </summary>
    private bool LooksLikeEdge(SstvSignalBuffer b, long t, out double leader)
    {
        leader = 0;
        var leadA = b.Mean(t - 210 * _ms, t - 110 * _ms);
        var leadB = b.Mean(t - 110 * _ms, t - 10 * _ms);
        if (double.IsNaN(leadA) || double.IsNaN(leadB) || Math.Abs(leadA - leadB) > 80)
            return false;

        leader = (leadA + leadB) / 2;
        if (Math.Abs(leader - LeaderHz) > MaxOffsetHz)
            return false;

        // A real leader is one steady tone; noise averages to the band centre but spreads widely.
        if (b.StdDev(t - 210 * _ms, t - 10 * _ms) > MaxLeaderSpreadHz)
            return false;

        var start = b.Mean(t + 5 * _ms, t + 25 * _ms);
        return leader - start > MinStartBitDropHz;
    }

    /// <summary>Place the edge where the step from leader to start bit is steepest.</summary>
    private long RefineEdge(SstvSignalBuffer b, long t)
    {
        var best = t;
        var bestContrast = double.MinValue;
        var span = 15 * _ms;
        var width = 15 * _ms;
        for (var d = -span; d <= span; d++)
        {
            var e = t + d;
            var contrast = b.Mean(e - width, e) - b.Mean(e, e + width);
            if (contrast > bestContrast)
            {
                bestContrast = contrast;
                best = e;
            }
        }

        return best;
    }

    private bool TryReadHeader(SstvSignalBuffer b, long edge, double leader, bool requireFullHeader, out SstvVisResult result)
    {
        result = default;
        var guard = 5 * _ms;

        // The start bit is 1200 Hz, exactly between a 1 (1100 Hz) and a 0 (1300 Hz),
        // so its measured level is the decision threshold for the data bits.
        var startBit = b.Mean(edge + guard, edge + _bit - guard);
        if (leader - startBit < MinStartBitDropHz)
            return false;

        var code = 0;
        var ones = 0;
        var compression = Math.Clamp((leader - startBit) / (LeaderHz - SyncHz), 0.3, 1.2);
        var minSeparation = 25 * compression;
        for (var i = 0; i < 8; i++)
        {
            var a = edge + (i + 1) * _bit;
            var m = b.Mean(a + guard, a + _bit - guard);
            if (Math.Abs(m - startBit) < minSeparation || leader - m < MinStartBitDropHz * 0.6)
                return false;

            var bit = m < startBit;
            if (bit)
                ones++;
            if (bit && i < 7)
                code |= 1 << i;
        }

        if ((ones & 1) != 0)
            return false;

        var stopStart = edge + 9 * _bit;
        var stop = b.Mean(stopStart + guard, stopStart + _bit - guard);
        if (Math.Abs(stop - startBit) > 120 * compression)
            return false;

        if (requireFullHeader && !HasFirstLeaderAndBreak(b, edge, leader))
            return false;

        var mode = SstvModeTable.FromVis(code);
        if (mode is null)
            return false;

        result = new SstvVisResult(code, mode, edge, edge + 10 * _bit, leader - LeaderHz);
        return true;
    }

    private bool HasFirstLeaderAndBreak(SstvSignalBuffer b, long edge, double leader)
    {
        // Leader 2 is the 300 ms before the start bit; the 10 ms break sits before it.
        var breakMean = b.Mean(edge - 312 * _ms, edge - 298 * _ms);
        var leader1 = b.Mean(edge - 600 * _ms, edge - 330 * _ms);
        return leader - breakMean > 200
            && Math.Abs(leader1 - leader) < 90
            && b.StdDev(edge - 600 * _ms, edge - 330 * _ms) < MaxLeaderSpreadHz;
    }
}
