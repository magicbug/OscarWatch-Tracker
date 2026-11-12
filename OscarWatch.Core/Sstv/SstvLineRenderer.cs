namespace OscarWatch.Core.Sstv;

/// <summary>
/// Turns one transmitted line of demodulated frequency into image rows. Each pixel is
/// the mean frequency over its whole time slot, which is the main defence against noise.
/// 1500 Hz is black and 2300 Hz is white.
/// </summary>
internal sealed class SstvLineRenderer
{
    private const double BlackHz = 1500;
    private const double SpanHz = 800;

    /// <summary>
    /// Robot 36 chroma is averaged over a pair of rows. Taking 3/4 of the pair partner and 1/4
    /// of the other neighbour centres the missing channel on the row and lowers its noise.
    /// </summary>
    private const double RobotPartnerShare = 0.75;

    /// <summary>Robot 36 separators within this of 1900 Hz (where noise averages) say nothing.</summary>
    private const double RobotSeparatorDeadbandHz = 200;
    private const double RobotSeparatorMaxSpreadHz = 250;
    /// <summary>Mean sample weight below which the separator sat in an envelope dip and is unreadable.</summary>
    private const double RobotSeparatorMinWeight = 0.3;
    private const double RobotParityVoteLimit = 8;

    /// <summary>Span of the envelope steadiness measured around each pixel.</summary>
    private const double NoiseWindowMs = 8;

    /// <summary>
    /// Envelope variation (see <see cref="SstvSignalBuffer.WeightVariation"/>) where pixels start to fade
    /// and where they are fully faded. A picture at 4 dB carrier to noise (about 0.36) keeps its colour.
    /// </summary>
    internal const double NoiseFadeStart = 0.45;

    internal const double NoiseFadeFull = 0.7;

    /// <summary>A fully faded pixel is grey at this share of its brightness.</summary>
    internal const double NoiseGreyLevel = 0.5;

    private readonly SstvMode _mode;
    private readonly SstvImage _image;
    private readonly bool _chromaSmoothing;
    private readonly bool _weightPixels;
    private readonly bool _noiseFade;
    private readonly double[]? _noiseSum;
    private int _noiseChannels;
    private readonly double[][]? _robotFade;
    private double[]? _robotBlend;
    /// <summary>Positive while clear separators say odd lines carry B-Y (the usual pattern).</summary>
    private double _robotParityVote;

    // Robot 36: Y and the single chroma channel of every line, kept for pairing.
    private readonly double[][]? _robotY;
    private readonly double[][]? _robotChroma;
    private readonly bool[]? _robotIsBlue;
    private readonly bool[]? _robotHas;

    // Per-channel scratch for the pixel means of one line, reused across lines. The decoder
    // renders from a single thread and each buffer is consumed within the same Render call,
    // except Robot 36, which keeps its line arrays for pairing and so allocates its own.
    private readonly double[]?[]? _scratch;

    /// <param name="chromaSmoothing">Smooth colour-difference channels to reduce colour speckle.</param>
    /// <param name="weightPixels">Weigh pixel samples by their stored weight even when the buffer is unweighted.</param>
    /// <param name="noiseFade">
    /// Fade pixels towards a dim grey where the envelope says the signal is mostly noise, so noise shows as
    /// grey grain instead of full-strength random colour.
    /// </param>
    public SstvLineRenderer(SstvMode mode, SstvImage image, bool chromaSmoothing = false, bool weightPixels = false, bool noiseFade = false)
    {
        _mode = mode;
        _image = image;
        _chromaSmoothing = chromaSmoothing && mode.ColourSpace == SstvColourSpace.YCbCr;
        _weightPixels = weightPixels;
        _noiseFade = noiseFade;
        if (noiseFade)
            _noiseSum = new double[mode.Width];
        if (mode.Id == SstvModeId.Robot36)
        {
            var lines = mode.TransmittedLines;
            _robotY = new double[lines][];
            _robotChroma = new double[lines][];
            _robotIsBlue = new bool[lines];
            _robotHas = new bool[lines];
            if (noiseFade)
                _robotFade = new double[lines][];
        }
        else
        {
            // One buffer per SstvChannel value, allocated lazily per channel the mode uses.
            _scratch = new double[Enum.GetValues<SstvChannel>().Length][];
        }
    }

    /// <summary>
    /// Whether a Robot 36 line carries B-Y. The separator before the chroma is 1500 Hz ahead of
    /// R-Y and 2300 Hz ahead of B-Y. A clear separator decides the line on its own; in a fade
    /// it is noise, so the line follows the odd/even pattern the clear separators agreed on.
    /// </summary>
    private bool RobotChromaIsBlue(SstvSignalBuffer buffer, double from, double to, double offsetHz, int line)
    {
        var oddIsBlue = line % 2 == 1;
        var hz = buffer.Mean(from, to) - offsetHz;
        var weight = _weightPixels
            ? buffer.MeanStoredWeight((long)Math.Floor(from), (long)Math.Ceiling(to))
            : buffer.MeanWeight((long)Math.Floor(from), (long)Math.Ceiling(to));
        if (double.IsNaN(hz) || Math.Abs(hz - 1900) <= RobotSeparatorDeadbandHz || !(weight >= RobotSeparatorMinWeight))
            return oddIsBlue == _robotParityVote >= 0;

        var saysBlue = hz > 1900;
        var spread = buffer.StdDev((long)Math.Floor(from), (long)Math.Ceiling(to));
        var confidence = Math.Clamp((Math.Abs(hz - 1900) - RobotSeparatorDeadbandHz) / RobotSeparatorDeadbandHz, 0, 1)
            * (spread <= RobotSeparatorMaxSpreadHz ? 1 : 0.25);
        _robotParityVote = Math.Clamp(
            _robotParityVote + (saysBlue == oddIsBlue ? confidence : -confidence),
            -RobotParityVoteLimit,
            RobotParityVoteLimit);
        return confidence >= 0.5 ? saysBlue : oddIsBlue == _robotParityVote >= 0;
    }

    /// <summary>Reusable scratch for a channel's pixel means (Robot 36 keeps its own arrays).</summary>
    private double[] ChannelBuffer(SstvChannel channel, int width)
    {
        if (_scratch is null)
            return new double[width];
        return _scratch[(int)channel] ??= new double[width];
    }

    /// <param name="lineStart">Sample where the line starts (not where its sync starts).</param>
    /// <param name="samplesPerMs">Measured samples per millisecond, including clock error.</param>
    /// <param name="offsetHz">Audio tuning error to remove before mapping to brightness.</param>
    /// <param name="line">Transmitted line index.</param>
    public void Render(SstvSignalBuffer buffer, double lineStart, double samplesPerMs, double offsetHz, int line)
    {
        var w = _mode.Width;
        double[]? r = null, g = null, b = null, y0 = null, y1 = null, ry = null, by = null, alt = null;
        var t = 0.0;
        var robotBlue = line % 2 == 1;
        if (_noiseSum is not null)
        {
            Array.Clear(_noiseSum);
            _noiseChannels = 0;
        }

        for (var s = 0; s < _mode.Segments.Count; s++)
        {
            var seg = _mode.Segments[s];
            if (seg.Channel is not (SstvChannel.Sync or SstvChannel.Gap))
            {
                var values = ChannelBuffer(seg.Channel, w);
                Scan(buffer, lineStart + t * samplesPerMs, seg.Ms * samplesPerMs, offsetHz, values);
                if (_noiseSum is not null)
                    AddNoise(buffer, lineStart + t * samplesPerMs, seg.Ms * samplesPerMs, NoiseWindowMs * samplesPerMs);
                if (_chromaSmoothing && seg.Channel is SstvChannel.RMinusY or SstvChannel.BMinusY or SstvChannel.AlternatingChroma)
                    Smooth(values);
                switch (seg.Channel)
                {
                    case SstvChannel.Red: r = values; break;
                    case SstvChannel.Green: g = values; break;
                    case SstvChannel.Blue: b = values; break;
                    case SstvChannel.Y0: y0 = values; break;
                    case SstvChannel.Y1: y1 = values; break;
                    case SstvChannel.RMinusY: ry = values; break;
                    case SstvChannel.BMinusY: by = values; break;
                    case SstvChannel.AlternatingChroma: alt = values; break;
                }
            }
            else if (_mode.Id == SstvModeId.Robot36 && seg.Channel == SstvChannel.Gap && seg.Ms == 4.5)
            {
                var a = lineStart + t * samplesPerMs;
                robotBlue = RobotChromaIsBlue(buffer, a + 0.5 * samplesPerMs, a + (seg.Ms - 0.5) * samplesPerMs, offsetHz, line);
            }

            t += seg.Ms;
        }

        var row = line * _mode.RowsPerLine;
        if (row >= _mode.Height)
            return;

        var fade = NoiseFade();
        if (_mode.ColourSpace == SstvColourSpace.Rgb)
        {
            for (var x = 0; x < w; x++)
                Put(x, row, (Level(r, x), Level(g, x), Level(b, x)), fade);
            return;
        }

        if (_mode.Id == SstvModeId.Robot36)
        {
            StoreRobotLine(line, y0!, alt!, robotBlue, fade);
            RenderRobotRow(line);
            if (line > 0)
                RenderRobotRow(line - 1);
            return;
        }

        for (var x = 0; x < w; x++)
            Put(x, row, YCbCrToRgb(Level(y0, x), Level(by, x, 128), Level(ry, x, 128)), fade);

        if (_mode.RowsPerLine == 2 && row + 1 < _mode.Height)
        {
            for (var x = 0; x < w; x++)
                Put(x, row + 1, YCbCrToRgb(Level(y1, x), Level(by, x, 128), Level(ry, x, 128)), fade);
        }
    }

    /// <summary>
    /// Adds each pixel's envelope variation for one channel scan, measured over
    /// <paramref name="window"/> samples centred on the pixel.
    /// </summary>
    private void AddNoise(SstvSignalBuffer buffer, double start, double length, double window)
    {
        var sum = _noiseSum!;
        var px = length / sum.Length;
        var half = window / 2;
        for (var x = 0; x < sum.Length; x++)
        {
            var mid = start + (x + 0.5) * px;
            var v = buffer.WeightVariation((long)Math.Floor(mid - half), (long)Math.Ceiling(mid + half));
            sum[x] += double.IsNaN(v) ? 0 : v;
        }

        _noiseChannels++;
    }

    /// <summary>How far each pixel of the line fades to grey, 0 to 1, or null when fading is off.</summary>
    private double[]? NoiseFade()
    {
        if (_noiseSum is null || _noiseChannels == 0)
            return null;

        var fade = _mode.Id == SstvModeId.Robot36 ? new double[_noiseSum.Length] : _noiseSum;
        for (var x = 0; x < fade.Length; x++)
            fade[x] = Smoothstep(_noiseSum[x] / _noiseChannels, NoiseFadeStart, NoiseFadeFull);
        return fade;
    }

    private void Put(int x, int row, (double R, double G, double B) c, double[]? fade)
    {
        var f = fade is null ? 0 : fade[x];
        if (f <= 0)
        {
            _image.SetPixel(x, row, c.R, c.G, c.B);
            return;
        }

        var (r, g, b) = Faded(c.R, c.G, c.B, f);
        _image.SetPixel(x, row, r, g, b);
    }

    /// <summary>Moves a colour by <paramref name="fade"/> towards grey at <see cref="NoiseGreyLevel"/> of its brightness.</summary>
    internal static (double R, double G, double B) Faded(double r, double g, double b, double fade)
    {
        r = Math.Clamp(r, 0, 255);
        g = Math.Clamp(g, 0, 255);
        b = Math.Clamp(b, 0, 255);
        var grey = (0.299 * r + 0.587 * g + 0.114 * b) * NoiseGreyLevel;
        return (r + fade * (grey - r), g + fade * (grey - g), b + fade * (grey - b));
    }

    private static double Smoothstep(double value, double low, double high)
    {
        var t = Math.Clamp((value - low) / (high - low), 0.0, 1.0);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// [1, 2, 1] / 4 along the line, in place. The eye resolves colour far less finely than
    /// brightness, and the colour conversion amplifies chroma noise.
    /// </summary>
    private static void Smooth(double[] values)
    {
        var w = values.Length;
        if (w < 3)
            return;
        var prev = values[0];
        for (var x = 0; x < w; x++)
        {
            var cur = values[x];
            var next = x + 1 < w ? values[x + 1] : cur;
            values[x] = (prev + 2 * cur + next) / 4;
            prev = cur;
        }
    }

    /// <summary>Full-range BT.601, as used by the Robot and PD colour modes.</summary>
    public static (double R, double G, double B) YCbCrToRgb(double y, double cb, double cr)
    {
        var r = y + 1.402 * (cr - 128);
        var g = y - 0.344136 * (cb - 128) - 0.714136 * (cr - 128);
        var b = y + 1.772 * (cb - 128);
        return (r, g, b);
    }

    private void Scan(SstvSignalBuffer buffer, double start, double length, double offsetHz, double[] values)
    {
        var w = values.Length;
        var px = length / w;
        var lo = BlackHz + offsetHz;
        var hi = BlackHz + SpanHz + offsetHz;
        for (var x = 0; x < w; x++)
        {
            var a = start + x * px;
            values[x] = MeanClamped(buffer, a, a + px, lo, hi, _weightPixels) is var hz && double.IsNaN(hz)
                ? 0
                : (hz - lo) / SpanHz * 255;
        }
    }

    /// <summary>
    /// Mean with every sample first limited to the picture range, so one noise click cannot
    /// swing a whole pixel. Samples cut by the pixel edges count by the part inside, and by
    /// their weight when the buffer is weighted or <paramref name="weighted"/> is set.
    /// </summary>
    private static double MeanClamped(SstvSignalBuffer buffer, double from, double to, double lo, double hi, bool weighted)
    {
        if (to <= from)
            return double.NaN;

        var first = (long)Math.Floor(from);
        var last = (long)Math.Ceiling(to);
        double sum = 0, weight = 0;
        for (var i = first; i < last; i++)
        {
            if (i < buffer.Start || i >= buffer.End)
                continue;
            var w = Math.Min(i + 1, to) - Math.Max(i, from);
            if (w <= 0)
                continue;
            if (weighted)
                w *= buffer.StoredWeight(i);
            else if (buffer.Weighted)
                w *= buffer.Weight(i);
            sum += w * Math.Clamp(buffer[i], lo, hi);
            weight += w;
        }

        return weight > 0 ? sum / weight : double.NaN;
    }

    private static double Level(double[]? values, int x, double fallback = 0) =>
        values is null ? fallback : values[x];

    private void StoreRobotLine(int line, double[] y, double[] chroma, bool isBlue, double[]? fade)
    {
        _robotY![line] = y;
        _robotChroma![line] = chroma;
        _robotIsBlue![line] = isBlue;
        _robotHas![line] = true;
        if (_robotFade is not null)
            _robotFade[line] = fade!;
    }

    private void RenderRobotRow(int line)
    {
        if (!_robotHas![line])
            return;

        var y = _robotY![line];
        var own = _robotChroma![line];
        var ownBlue = _robotIsBlue![line];
        var other = FindRobotPartner(line, !ownBlue);
        var cr = ownBlue ? other : own;
        var cb = ownBlue ? own : other;
        var fade = _robotFade?[line];
        for (var x = 0; x < _mode.Width; x++)
            Put(x, line, YCbCrToRgb(y[x], Level(cb, x, 128), Level(cr, x, 128)), fade);
    }

    private double[]? FindRobotPartner(int line, bool wantBlue)
    {
        // Lines pair up (even R-Y, odd B-Y); prefer the pair partner, then the other neighbour.
        var partner = line % 2 == 0 ? line + 1 : line - 1;
        var neighbour = line % 2 == 0 ? line - 1 : line + 1;
        var fromPartner = RobotChroma(partner, wantBlue);
        var fromNeighbour = RobotChroma(neighbour, wantBlue);
        if (_chromaSmoothing && fromPartner is not null && fromNeighbour is not null)
        {
            var blend = _robotBlend ??= new double[_mode.Width];
            for (var x = 0; x < blend.Length; x++)
                blend[x] = RobotPartnerShare * fromPartner[x] + (1 - RobotPartnerShare) * fromNeighbour[x];
            return blend;
        }

        return fromPartner ?? fromNeighbour;
    }

    private double[]? RobotChroma(int line, bool wantBlue) =>
        line >= 0 && line < _robotHas!.Length && _robotHas[line] && _robotIsBlue![line] == wantBlue
            ? _robotChroma![line]
            : null;
}
