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
    private double _anchor;
    private bool _hasAnchor;

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

    public void Accept(int line, double position, double weight)
    {
        _points.RemoveAll(p => p.Line == line);
        _points.Add((line, position, Math.Max(weight, 1e-3)));
    }

    /// <summary>Predicted sync position for <paramref name="line"/>, using lines up to it.</summary>
    public double Predict(int line) => Evaluate(line, centred: false);

    /// <summary>Fitted sync position, using lines either side when they exist.</summary>
    public double Fitted(int line) => Evaluate(line, centred: true);

    /// <summary>Line period in samples at <paramref name="line"/>.</summary>
    public double PeriodAt(int line, bool centred) => Fit(line, centred).Slope;

    private double Evaluate(int line, bool centred)
    {
        var (slope, intercept) = Fit(line, centred);
        return intercept + slope * line;
    }

    private (double Slope, double Intercept) Fit(int line, bool centred)
    {
        var lo = centred ? line - FitWindowLines / 2 : line - FitWindowLines;
        var hi = centred ? line + FitWindowLines / 2 : line;
        var selected = _points.Where(p => p.Line >= lo && p.Line <= hi).ToList();
        if (selected.Count < MinFitPoints)
        {
            selected = _points
                .OrderBy(p => Math.Abs(p.Line - line))
                .Take(FitWindowLines)
                .ToList();
        }

        if (!AutoSlant)
            return FixedSlope(ManualPeriod, selected);

        if (selected.Count < MinFitPoints || Span(selected) < MinFitPoints)
            return FixedSlope(_nominal, selected);

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

    private static int Span(List<(int Line, double Position, double Weight)> points) =>
        points.Max(p => p.Line) - points.Min(p => p.Line);

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }
}
