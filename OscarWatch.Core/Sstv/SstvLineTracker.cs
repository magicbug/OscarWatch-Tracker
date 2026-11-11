namespace OscarWatch.Core.Sstv;

/// <summary>
/// Fits sync positions against line number. The slope is the true line period in
/// samples, so a soundcard or transmitter clock error is measured instead of showing
/// as slant. A local window lets the fit follow slow changes through a pass.
/// </summary>
public sealed class SstvLineTracker
{
    private const int FitWindowLines = 48;
    private const int MinFitPoints = 3;
    private const int MinPointsForOutlierCheck = 6;
    private const double MaxClockError = 0.02;

    private readonly List<(int Line, double Position, double Weight)> _points = new();
    private readonly double _nominal;
    /// <summary>Known Doppler stretch per line in ppm, and its running total in samples before each line.</summary>
    private readonly List<double> _dopplerPpm = new();
    private readonly List<double> _dopplerShift = new() { 0 };
    private double _anchor;
    private bool _hasAnchor;

    /// <summary>
    /// Lines at which the line timing stepped (a dropout in the recording, a pause in the sender's audio). A
    /// line is fitted only from the lines of its own stretch between two steps, so the old timing does not
    /// pull the new one back, and the new one does not bend the old.
    /// </summary>
    private readonly List<int> _stepLines = new();

    public SstvLineTracker(double nominalSamplesPerLine)
    {
        _nominal = nominalSamplesPerLine;
        ManualPeriod = nominalSamplesPerLine;
    }

    public double NominalSamplesPerLine => _nominal;

    /// <summary>When false, line positions follow <see cref="ManualPeriod"/> from the anchor.</summary>
    public bool AutoSlant { get; set; } = true;

    public double ManualPeriod { get; set; }

    public int AcceptedCount => _points.Count;

    /// <summary>Expected sync position for line 0 before any sync is accepted.</summary>
    public void SetAnchor(double line0SyncPosition)
    {
        _anchor = line0SyncPosition;
        _hasAnchor = true;
    }

    /// <summary>
    /// The line timing stepped at <paramref name="line"/>: lines from here on are fitted from their own stretch
    /// only. Lines before it keep their own fit.
    /// </summary>
    public void StartSegment(int line)
    {
        if (_stepLines.Contains(line))
            return;
        _stepLines.Add(line);
        _stepLines.Sort();
    }

    /// <summary>
    /// Takes back <see cref="StartSegment"/> for <paramref name="line"/> and drops the sync points from that
    /// line on, for a step that did not hold up.
    /// </summary>
    public void UndoSegment(int line)
    {
        _stepLines.Remove(line);
        _points.RemoveAll(p => p.Line >= line);
    }

    /// <summary>The lines [first, end) that share a stretch of line timing with <paramref name="line"/>.</summary>
    private (int First, int End) SegmentOf(int line)
    {
        var first = int.MinValue;
        var end = int.MaxValue;
        foreach (var start in _stepLines)
        {
            if (start <= line)
                first = start;
            else
            {
                end = start;
                break;
            }
        }

        return (first, end);
    }

    /// <summary>
    /// Doppler time compression known from the orbit for <paramref name="line"/> (positive when
    /// the satellite is moving away and lines arrive stretched). Lines must be given in order.
    /// The fit then only has to follow the clock error, which stays put through the pass.
    /// </summary>
    public void SetDopplerPpm(int line, double ppm)
    {
        if (line < _dopplerPpm.Count)
        {
            _dopplerPpm[line] = ppm;
            return;
        }

        while (_dopplerPpm.Count <= line)
        {
            _dopplerShift.Add(_dopplerShift[^1] + _nominal * ppm * 1e-6);
            _dopplerPpm.Add(ppm);
        }
    }

    private double DopplerShift(int line) =>
        line < 0 ? 0
        : line < _dopplerShift.Count ? _dopplerShift[line]
        : _dopplerShift[^1] + (line - _dopplerShift.Count + 1) * _nominal * (_dopplerPpm.Count > 0 ? _dopplerPpm[^1] : 0) * 1e-6;

    private double DopplerPpm(int line) =>
        _dopplerPpm.Count == 0 ? 0 : _dopplerPpm[Math.Clamp(line, 0, _dopplerPpm.Count - 1)];

    public void Accept(int line, double position, double weight)
    {
        _points.RemoveAll(p => p.Line == line);
        _points.Add((line, position - DopplerShift(line), Math.Max(weight, 1e-3)));
    }

    /// <summary>Predicted sync position for <paramref name="line"/>, using lines up to it.</summary>
    public double Predict(int line) => Evaluate(line, centred: false);

    /// <summary>Fitted sync position, using lines either side when they exist.</summary>
    public double Fitted(int line) => Evaluate(line, centred: true);

    /// <summary>Line period in samples at <paramref name="line"/>.</summary>
    public double PeriodAt(int line, bool centred) => Fit(line, centred).Slope + _nominal * DopplerPpm(line) * 1e-6;

    private double Evaluate(int line, bool centred)
    {
        var (slope, intercept) = Fit(line, centred);
        return intercept + slope * line + DopplerShift(line);
    }

    private (double Slope, double Intercept) Fit(int line, bool centred)
    {
        var lo = centred ? line - FitWindowLines / 2 : line - FitWindowLines;
        var hi = centred ? line + FitWindowLines / 2 : line;
        var (first, end) = SegmentOf(line);
        var selected = _points.Where(p => p.Line >= lo && p.Line <= hi && p.Line >= first && p.Line < end).ToList();
        if (selected.Count < MinFitPoints)
        {
            selected = _points
                .Where(p => p.Line >= first && p.Line < end)
                .OrderBy(p => Math.Abs(p.Line - line))
                .Take(FitWindowLines)
                .ToList();
        }

        if (!AutoSlant)
            return FixedSlope(ManualPeriod, selected);

        if (selected.Count < MinFitPoints || Span(selected) < MinFitPoints)
            return FixedSlope(SparseSlope(first), selected);

        // A least squares fit is pulled off by a single false sync (noise as a fade recovers),
        // so first drop points far from a median based line, which ignores up to ~29% outliers.
        if (selected.Count >= MinPointsForOutlierCheck && TheilSen(selected) is { } robust
            && Math.Abs(robust.Slope / _nominal - 1) <= MaxClockError)
        {
            var residuals = selected.Select(p => Math.Abs(p.Position - (robust.Intercept + robust.Slope * p.Line))).ToList();
            var limit = Math.Max(3.0, 3.0 * Median(residuals));
            var kept = selected.Where((_, i) => residuals[i] <= limit).ToList();
            if (kept.Count >= MinPointsForOutlierCheck && Span(kept) >= MinFitPoints)
                selected = kept;
        }

        var fit = WeightedFit(selected);
        // Reject outliers (a noise burst that looked like a sync), then refit.
        for (var pass = 0; pass < 2 && selected.Count >= MinPointsForOutlierCheck; pass++)
        {
            var residuals = selected.Select(p => Math.Abs(p.Position - (fit.Intercept + fit.Slope * p.Line))).ToList();
            var limit = Math.Max(3.0, 3.0 * Median(residuals));
            var kept = selected.Where((_, i) => residuals[i] <= limit).ToList();
            if (kept.Count == selected.Count || kept.Count < MinPointsForOutlierCheck)
                break;
            selected = kept;
            fit = WeightedFit(selected);
        }

        if (Math.Abs(fit.Slope / _nominal - 1) > MaxClockError)
            return FixedSlope(_nominal, selected);
        return fit;
    }

    /// <summary>
    /// Line period to use while a stretch has too few points to fit its own. A step in the timing does not change
    /// the clock, so a stretch after a step keeps the period fitted just before it; the nominal period would lose
    /// the measured clock error and put the next line's prediction off by that error times the line length.
    /// </summary>
    private double SparseSlope(int segmentFirst) =>
        segmentFirst == int.MinValue ? _nominal : Fit(segmentFirst - 1, centred: false).Slope;

    private (double Slope, double Intercept) FixedSlope(double slope, List<(int Line, double Position, double Weight)> points)
    {
        if (points.Count == 0)
            return (slope, _hasAnchor ? _anchor : 0);

        var sw = 0.0;
        var sum = 0.0;
        foreach (var p in points)
        {
            sw += p.Weight;
            sum += p.Weight * (p.Position - slope * p.Line);
        }

        return (slope, sum / sw);
    }

    private static (double Slope, double Intercept) WeightedFit(List<(int Line, double Position, double Weight)> points)
    {
        double sw = 0, sx = 0, sy = 0;
        foreach (var p in points)
        {
            sw += p.Weight;
            sx += p.Weight * p.Line;
            sy += p.Weight * p.Position;
        }

        var mx = sx / sw;
        var my = sy / sw;
        double sxx = 0, sxy = 0;
        foreach (var p in points)
        {
            var dx = p.Line - mx;
            sxx += p.Weight * dx * dx;
            sxy += p.Weight * dx * (p.Position - my);
        }

        var slope = sxy / sxx;
        return (slope, my - slope * mx);
    }

    /// <summary>Median of the pairwise slopes, and the median intercept for that slope.</summary>
    private static (double Slope, double Intercept)? TheilSen(List<(int Line, double Position, double Weight)> points)
    {
        var slopes = new List<double>(points.Count * (points.Count - 1) / 2);
        for (var i = 0; i < points.Count; i++)
        {
            for (var j = i + 1; j < points.Count; j++)
            {
                var dx = points[j].Line - points[i].Line;
                if (dx != 0)
                    slopes.Add((points[j].Position - points[i].Position) / dx);
            }
        }

        if (slopes.Count == 0)
            return null;

        var slope = Median(slopes);
        var intercept = Median(points.Select(p => p.Position - slope * p.Line).ToList());
        return (slope, intercept);
    }

    private static int Span(List<(int Line, double Position, double Weight)> points) =>
        points.Max(p => p.Line) - points.Min(p => p.Line);

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }
}
