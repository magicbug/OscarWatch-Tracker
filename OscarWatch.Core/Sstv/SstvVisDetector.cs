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

    /// <summary>20 ms slices in the 200 ms of leader before the start bit.</summary>
    private const int LeaderSlices = 10;
    /// <summary>Steady slices needed when part of the leader faded.</summary>
    private const int MinSteadyLeaderSlices = 5;
    /// <summary>Spread allowed in one leader slice, and in each bit cell, after a faded leader.</summary>
    private const double FadedSliceSpreadHz = 250;

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

    /// <summary>
    /// Modes the station is known to send, or null for no preference. A header for any other
    /// mode must be complete (first leader and break), and a header with one doubtful bit
    /// is read as the expected mode it is one bit away from.
    /// </summary>
    public IReadOnlyCollection<SstvModeId>? ExpectedModes { get; set; }

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
            if (!LooksLikeEdge(buffer, t, out var leader, out var fadedLeader))
                continue;
            if (fadedLeader && requireFullHeader)
                continue;

            var edge = RefineEdge(buffer, t);
            if (!TryReadHeader(buffer, edge, leader, requireFullHeader, fadedLeader, out result))
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
    private bool LooksLikeEdge(SstvSignalBuffer b, long t, out double leader, out bool fadedLeader)
    {
        fadedLeader = false;
        if (!SteadyLeader(b, t, out leader))
        {
            if (!PartLeader(b, t, out leader))
                return false;
            fadedLeader = true;
        }

        if (Math.Abs(leader - LeaderHz) > MaxOffsetHz)
            return false;

        var start = b.Mean(t + 5 * _ms, t + 25 * _ms);
        return leader - start > MinStartBitDropHz;
    }

    private bool SteadyLeader(SstvSignalBuffer b, long t, out double leader)
    {
        leader = 0;
        var leadA = b.Mean(t - 210 * _ms, t - 110 * _ms);
        var leadB = b.Mean(t - 110 * _ms, t - 10 * _ms);
        if (double.IsNaN(leadA) || double.IsNaN(leadB) || Math.Abs(leadA - leadB) > 80)
            return false;

        leader = (leadA + leadB) / 2;
        // A real leader is one steady tone; noise averages to the band centre but spreads widely.
        return b.StdDev(t - 210 * _ms, t - 10 * _ms) <= MaxLeaderSpreadHz;
    }

    /// <summary>
    /// A fade across part of the leader spreads the whole window. Accept a leader when most of
    /// its 20 ms slices are each a steady tone and agree with one another.
    /// </summary>
    private bool PartLeader(SstvSignalBuffer b, long t, out double leader)
    {
        leader = 0;
        var means = new List<double>(LeaderSlices);
        for (var k = 0; k < LeaderSlices; k++)
        {
            var from = t - 210 * _ms + k * 20 * _ms;
            var mean = b.Mean(from, from + 20 * _ms);
            if (!double.IsNaN(mean) && b.StdDev(from, from + 20 * _ms) <= FadedSliceSpreadHz)
                means.Add(mean);
        }

        if (means.Count < MinSteadyLeaderSlices)
            return false;

        means.Sort();
        var median = means[means.Count / 2];
        var agreeing = means.Where(m => Math.Abs(m - median) <= 80).ToList();
        if (agreeing.Count < MinSteadyLeaderSlices)
            return false;

        leader = agreeing.Average();
        return true;
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

    private bool TryReadHeader(
        SstvSignalBuffer b, long edge, double leader, bool requireFullHeader, bool fadedLeader, out SstvVisResult result)
    {
        result = default;
        var guard = 5 * _ms;

        // The start bit is 1200 Hz, exactly between a 1 (1100 Hz) and a 0 (1300 Hz),
        // so its measured level is the decision threshold for the data bits.
        var startBit = b.Mean(edge + guard, edge + _bit - guard);
        if (leader - startBit < MinStartBitDropHz)
            return false;

        // With less leader to go on, every cell has to be a steady tone, as picture
        // content and noise are not, and no bit may be guessed.
        if (fadedLeader)
        {
            for (var i = 0; i < 10; i++)
            {
                var a = edge + i * _bit;
                if (b.StdDev(a + guard, a + _bit - guard) > FadedSliceSpreadHz)
                    return false;
            }
        }

        var code = 0;
        var ones = 0;
        var compression = Math.Clamp((leader - startBit) / (LeaderHz - SyncHz), 0.3, 1.2);
        var minSeparation = 25 * compression;
        var expected = fadedLeader ? null : ExpectedModes;
        var doubtful = -1;
        var weakest = -1;
        var weakestMargin = double.MaxValue;
        for (var i = 0; i < 8; i++)
        {
            var a = edge + (i + 1) * _bit;
            var m = b.Mean(a + guard, a + _bit - guard);
            if (leader - m < MinStartBitDropHz * 0.6)
                return false;
            var margin = Math.Abs(m - startBit);
            if (margin < minSeparation)
            {
                // With expected modes, one doubtful bit may be settled by the parity bit.
                if (expected is null || doubtful >= 0)
                    return false;
                doubtful = i;
            }

            if (margin < weakestMargin)
            {
                weakestMargin = margin;
                weakest = i;
            }

            var bit = m < startBit;
            if (bit)
                ones++;
            if (bit && i < 7)
                code |= 1 << i;
        }

        var softened = false;
        if ((ones & 1) != 0)
        {
            if (expected is null)
                return false;
            // One bit is wrong: assume it is the least certain one, and accept only an expected mode.
            var flip = doubtful >= 0 ? doubtful : weakest;
            if (flip < 7)
                code ^= 1 << flip;
            if (SstvModeTable.FromVis(code) is not { } corrected || !expected.Contains(corrected.Id))
                return false;
            softened = true;
        }
        else if (doubtful >= 0 && (SstvModeTable.FromVis(code) is not { } read || !expected!.Contains(read.Id)))
        {
            return false;
        }

        var stopStart = edge + 9 * _bit;
        var stop = b.Mean(stopStart + guard, stopStart + _bit - guard);
        if (Math.Abs(stop - startBit) > 120 * compression)
            return false;

        if (requireFullHeader && !HasFirstLeaderAndBreak(b, edge, leader))
            return false;

        var mode = SstvModeTable.FromVis(code);
        if (mode is null)
            return false;
        if (fadedLeader && ExpectedModes is { } known && !known.Contains(mode.Id))
            return false;
        if (expected is not null && !softened && !expected.Contains(mode.Id)
            && !requireFullHeader && !HasFirstLeaderAndBreak(b, edge, leader))
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
