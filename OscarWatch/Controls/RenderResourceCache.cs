using Avalonia.Media;

namespace OscarWatch.Controls;

/// <summary>
/// Per-control cache for SolidColorBrush and Pen objects keyed by colour/thickness.
/// Eliminates per-frame allocation of drawing resources on the 4 Hz render path.
/// </summary>
internal sealed class RenderResourceCache
{
    private readonly Dictionary<Color, SolidColorBrush> _brushes = new();
    private readonly Dictionary<(Color Color, double Thickness), Pen> _pens = new();
    private readonly Dictionary<(Color Color, double Thickness), Pen> _dashedPens = new();
    private readonly Dictionary<(Color Color, double Thickness), Pen> _roundCapPens = new();

    /// <summary>
    /// Returns a cached SolidColorBrush for the given colour, creating one on first request.
    /// </summary>
    public SolidColorBrush GetBrush(Color color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(color);
            _brushes[color] = brush;
        }

        return brush;
    }

    /// <summary>
    /// Returns a cached Pen for the given colour and thickness, creating one on first request.
    /// </summary>
    public Pen GetPen(Color color, double thickness)
    {
        var key = (color, thickness);
        if (!_pens.TryGetValue(key, out var pen))
        {
            pen = new Pen(GetBrush(color), thickness);
            _pens[key] = pen;
        }

        return pen;
    }

    /// <summary>
    /// Returns a cached Pen with DashStyle.Dash for the given colour and thickness, creating one on first request.
    /// </summary>
    public Pen GetDashedPen(Color color, double thickness)
    {
        var key = (color, thickness);
        if (!_dashedPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(GetBrush(color), thickness) { DashStyle = DashStyle.Dash };
            _dashedPens[key] = pen;
        }

        return pen;
    }

    /// <summary>
    /// Returns a cached Pen with LineCap = Round and LineJoin = Round for the given colour and thickness.
    /// Used for pass path segments that need rounded line endings.
    /// </summary>
    public Pen GetRoundCapPen(Color color, double thickness)
    {
        var key = (color, thickness);
        if (!_roundCapPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(GetBrush(color), thickness)
            {
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            _roundCapPens[key] = pen;
        }

        return pen;
    }

    /// <summary>
    /// Clears all cached brushes and pens. Called when track states composition changes
    /// or the colour palette changes (theme switch).
    /// </summary>
    public void Clear()
    {
        _brushes.Clear();
        _pens.Clear();
        _dashedPens.Clear();
        _roundCapPens.Clear();
    }
}
