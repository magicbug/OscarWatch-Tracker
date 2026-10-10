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

    private readonly SstvMode _mode;
    private readonly SstvImage _image;

    // Robot 36: Y and the single chroma channel of every line, kept for pairing.
    private readonly double[][]? _robotY;
    private readonly double[][]? _robotChroma;
    private readonly bool[]? _robotIsBlue;
    private readonly bool[]? _robotHas;

    public SstvLineRenderer(SstvMode mode, SstvImage image)
    {
        _mode = mode;
        _image = image;
        if (mode.Id == SstvModeId.Robot36)
        {
            var lines = mode.TransmittedLines;
            _robotY = new double[lines][];
            _robotChroma = new double[lines][];
            _robotIsBlue = new bool[lines];
            _robotHas = new bool[lines];
        }
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
        for (var s = 0; s < _mode.Segments.Count; s++)
        {
            var seg = _mode.Segments[s];
            if (seg.Channel is not (SstvChannel.Sync or SstvChannel.Gap))
            {
                var values = new double[w];
                Scan(buffer, lineStart + t * samplesPerMs, seg.Ms * samplesPerMs, offsetHz, values);
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
                // The separator before the chroma is 1500 Hz ahead of R-Y and 2300 Hz ahead of B-Y.
                var a = lineStart + t * samplesPerMs;
                var hz = buffer.Mean(a + 0.5 * samplesPerMs, a + (seg.Ms - 0.5) * samplesPerMs) - offsetHz;
                if (!double.IsNaN(hz) && Math.Abs(hz - 1900) > 200)
                    robotBlue = hz > 1900;
            }

            t += seg.Ms;
        }

        var row = line * _mode.RowsPerLine;
        if (row >= _mode.Height)
            return;

        if (_mode.ColourSpace == SstvColourSpace.Rgb)
        {
            for (var x = 0; x < w; x++)
                _image.SetPixel(x, row, Level(r, x), Level(g, x), Level(b, x));
            return;
        }

        if (_mode.Id == SstvModeId.Robot36)
        {
            StoreRobotLine(line, y0!, alt!, robotBlue);
            RenderRobotRow(line);
            if (line > 0)
                RenderRobotRow(line - 1);
            return;
        }

        for (var x = 0; x < w; x++)
        {
            var (rr, gg, bb) = YCbCrToRgb(Level(y0, x), Level(by, x, 128), Level(ry, x, 128));
            _image.SetPixel(x, row, rr, gg, bb);
        }

        if (_mode.RowsPerLine == 2 && row + 1 < _mode.Height)
        {
            for (var x = 0; x < w; x++)
            {
                var (rr, gg, bb) = YCbCrToRgb(Level(y1, x), Level(by, x, 128), Level(ry, x, 128));
                _image.SetPixel(x, row + 1, rr, gg, bb);
            }
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
            values[x] = MeanClamped(buffer, a, a + px, lo, hi) is var hz && double.IsNaN(hz)
                ? 0
                : (hz - lo) / SpanHz * 255;
        }
    }

    /// <summary>
    /// Mean with every sample first limited to the picture range, so one noise click cannot
    /// swing a whole pixel. Samples cut by the pixel edges count by the part inside.
    /// </summary>
    private static double MeanClamped(SstvSignalBuffer buffer, double from, double to, double lo, double hi)
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
            sum += w * Math.Clamp(buffer[i], lo, hi);
            weight += w;
        }

        return weight > 0 ? sum / weight : double.NaN;
    }

    private static double Level(double[]? values, int x, double fallback = 0) =>
        values is null ? fallback : values[x];

    private void StoreRobotLine(int line, double[] y, double[] chroma, bool isBlue)
    {
        _robotY![line] = y;
        _robotChroma![line] = chroma;
        _robotIsBlue![line] = isBlue;
        _robotHas![line] = true;
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
        for (var x = 0; x < _mode.Width; x++)
        {
            var (rr, gg, bb) = YCbCrToRgb(y[x], Level(cb, x, 128), Level(cr, x, 128));
            _image.SetPixel(x, line, rr, gg, bb);
        }
    }

    private double[]? FindRobotPartner(int line, bool wantBlue)
    {
        // Lines pair up (even R-Y, odd B-Y); prefer the pair partner, then the other neighbour.
        var partner = line % 2 == 0 ? line + 1 : line - 1;
        var neighbour = line % 2 == 0 ? line - 1 : line + 1;
        foreach (var n in new[] { partner, neighbour })
        {
            if (n >= 0 && n < _robotHas!.Length && _robotHas[n] && _robotIsBlue![n] == wantBlue)
                return _robotChroma![n];
        }

        return null;
    }
}
