namespace OscarWatch.Core.Sstv;

/// <param name="Code">7-bit VIS code.</param>
/// <param name="Mode">Mode for the code, or null when the code is not one we decode.</param>
/// <param name="StartBitIndex">Sample where the 1200 Hz start bit begins.</param>
/// <param name="ImageStartIndex">Sample just after the stop bit.</param>
/// <param name="OffsetHz">Leader frequency minus 1900 Hz: the audio tuning error.</param>
/// <param name="FromTonePower">True when the header was read from tone power because its leader was lost in noise.</param>
public readonly record struct SstvVisResult(
    int Code,
    SstvMode? Mode,
    long StartBitIndex,
    long ImageStartIndex,
    double OffsetHz,
    bool FromTonePower = false);

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

    /// <summary>Share of the power the 1200 Hz start and stop bits need when read from tone power.</summary>
    private const double ToneSyncBitFraction = 0.4;
    /// <summary>Share of the power the winning tone of a data bit needs when read from tone power.</summary>
    private const double ToneDataBitFraction = 0.3;
    /// <summary>How many times the losing tone's power the winning tone of a data bit needs.</summary>
    private const double ToneDataBitRatio = 2.5;
    /// <summary>Tuning error searched either side of the expected offset when measuring a tone header's offset.</summary>
    private const double ToneOffsetSearchHz = 60;

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

    /// <summary>
    /// Search demodulator I/Q, same indices as the frequency buffer. When set, a header whose
    /// leader is lost in noise can still be read from the power of each bit's tone.
    /// </summary>
    public SstvBasebandBuffer? Baseband { get; set; }

    /// <summary>Audio tuning error expected while searching, in Hz (the last measured offset).</summary>
    public double SearchOffsetHz { get; set; }

    /// <summary>
    /// Offset in Hz present in <see cref="Baseband"/> but already removed from the frequency
    /// buffer (the rig-control feed-forward).
    /// </summary>
    public double BasebandShiftHz { get; set; }

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
            {
                if (requireFullHeader || Baseband is null || !TryReadToneHeader(Baseband, t, out result))
                    continue;
                _cursor = result.ImageStartIndex;
                return true;
            }
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

    /// <summary>
    /// Header read from tone power alone, for when noise has buried the leader and the
    /// discriminator. Every cell must be clearly its own tone, the parity must hold with no
    /// bit guessed, and the mode must be one the station is known to send (when known).
    /// Picture content never holds the 1100 and 1300 Hz tones, and noise does not keep one
    /// tone for 30 ms, let alone ten cells in a row.
    /// </summary>
    private bool TryReadToneHeader(SstvBasebandBuffer bb, long t, out SstvVisResult result)
    {
        result = default;
        var shift = Math.Clamp(SearchOffsetHz, -MaxOffsetHz, MaxOffsetHz) + BasebandShiftHz;
        if (t + 10 * _bit > bb.End || t - _bit < bb.Start)
            return false;

        // Cheap tests first: a 1200 Hz start bit that did not start earlier, and a 1200 Hz stop bit.
        var start = SyncBitFraction(bb, t, shift);
        if (!(start >= ToneSyncBitFraction))
            return false;
        if (!(SyncBitFraction(bb, t + 9 * _bit, shift) >= ToneSyncBitFraction * 0.75))
            return false;
        if (!(SyncBitFraction(bb, t - _bit, shift) < 0.5 * start))
            return false;

        var edge = RefineToneEdge(bb, t, shift);
        var guard = 3 * _ms;
        if (!(SyncBitFraction(bb, edge, shift) >= ToneSyncBitFraction)
            || !(SyncBitFraction(bb, edge + 9 * _bit, shift) >= ToneSyncBitFraction))
            return false;

        var code = 0;
        var ones = 0;
        for (var i = 0; i < 8; i++)
        {
            var a = edge + (i + 1) * _bit;
            var one = SstvTonePower.Fraction(bb, a + guard, a + _bit - guard, 1100 + shift);
            var zero = SstvTonePower.Fraction(bb, a + guard, a + _bit - guard, 1300 + shift);
            var win = Math.Max(one, zero);
            if (!(win >= ToneDataBitFraction) || win < ToneDataBitRatio * Math.Min(one, zero))
                return false;
            if (one > zero)
            {
                ones++;
                if (i < 7)
                    code |= 1 << i;
            }
        }

        if ((ones & 1) != 0)
            return false;
        var mode = SstvModeTable.FromVis(code);
        if (mode is null || (ExpectedModes is { } known && !known.Contains(mode.Id)))
            return false;

        var offset = MeasureToneOffset(bb, edge, shift) - BasebandShiftHz;
        result = new SstvVisResult(code, mode, edge, edge + 10 * _bit, offset, FromTonePower: true);
        return true;
    }

    /// <summary>1200 Hz share of a bit cell, or 0 when 1100 or 1300 Hz is stronger.</summary>
    private double SyncBitFraction(SstvBasebandBuffer bb, long cell, double shift)
    {
        var guard = 3 * _ms;
        var from = cell + guard;
        var to = cell + _bit - guard;
        var sync = SstvTonePower.Fraction(bb, from, to, SyncHz + shift);
        if (!(sync > 0))
            return 0;
        // Too weak to pass any test: skip the comparison with the data tones.
        if (sync < 0.25)
            return sync;
        var low = SstvTonePower.Fraction(bb, from, to, 1100 + shift);
        var high = SstvTonePower.Fraction(bb, from, to, 1300 + shift);
        return sync >= low && sync >= high ? sync : 0;
    }

    /// <summary>Edge position, near <paramref name="t"/>, where the ten cells are each most clearly one tone.</summary>
    private long RefineToneEdge(SstvBasebandBuffer bb, long t, double shift)
    {
        var guard = 3 * _ms;
        var best = t;
        var bestScore = double.MinValue;
        for (var e = t - 5 * _ms; e <= t + 12 * _ms; e += _ms)
        {
            if (e - _bit < bb.Start || e + 10 * _bit > bb.End)
                continue;
            var score = SyncBitFraction(bb, e, shift) + SyncBitFraction(bb, e + 9 * _bit, shift);
            for (var i = 1; i <= 8; i++)
            {
                var a = e + i * _bit;
                var one = SstvTonePower.Fraction(bb, a + guard, a + _bit - guard, 1100 + shift);
                var zero = SstvTonePower.Fraction(bb, a + guard, a + _bit - guard, 1300 + shift);
                score += Math.Abs(one - zero);
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = e;
            }
        }

        return best;
    }

    /// <summary>
    /// Tuning offset (relative to nominal tones, including <see cref="BasebandShiftHz"/>) at which
    /// the header's tones are strongest, measured coherently over whole cells.
    /// </summary>
    private double MeasureToneOffset(SstvBasebandBuffer bb, long edge, double shift)
    {
        var guard = 3 * _ms;
        var tones = new double[10];
        for (var i = 0; i < 10; i++)
        {
            var a = edge + i * _bit;
            tones[i] = i is 0 or 9
                ? SyncHz
                : SstvTonePower.Fraction(bb, a + guard, a + _bit - guard, 1100 + shift) > SstvTonePower.Fraction(bb, a + guard, a + _bit - guard, 1300 + shift)
                    ? 1100
                    : 1300;
        }

        var best = shift;
        var bestPower = double.MinValue;
        for (var d = -ToneOffsetSearchHz; d <= ToneOffsetSearchHz; d += 5)
        {
            var total = 0.0;
            for (var i = 0; i < 10; i++)
            {
                var a = edge + i * _bit;
                total += SstvTonePower.Fraction(bb, a + guard, a + _bit - guard, tones[i] + shift + d, blockMs: 24);
            }

            if (total > bestPower)
            {
                bestPower = total;
                best = shift + d;
            }
        }

        return best;
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
