// Feature: startup-io-rendering-optimisation, Task 8.2: PassPolarPlotControl render cache unit tests

using Avalonia.Media;
using FsCheck;
using FsCheck.Xunit;
using OscarWatch.Controls;

namespace OscarWatch.Tests.Performance;

/// <summary>
/// **Validates: Requirements 8.1, 8.3**
///
/// Verifies that <see cref="RenderResourceCache.GetRoundCapPen"/> returns the same object
/// reference on repeated calls, and that the returned pen has the expected LineCap/LineJoin properties.
/// </summary>
public class PassPolarPlotRenderCacheTests
{
    /// <summary>
    /// GetRoundCapPen returns the same reference for the same colour and thickness on repeated calls.
    /// </summary>
    [Property(MaxTest = 100)]
    public bool GetRoundCapPen_returns_same_reference(byte a, byte r, byte g, byte b, PositiveInt thicknessRaw)
    {
        var color = Color.FromArgb(a, r, g, b);
        var thickness = (double)thicknessRaw.Get / 10.0;
        var cache = new RenderResourceCache();

        var pen1 = cache.GetRoundCapPen(color, thickness);
        var pen2 = cache.GetRoundCapPen(color, thickness);

        return ReferenceEquals(pen1, pen2);
    }

    /// <summary>
    /// GetRoundCapPen returns a pen with LineCap = Round and LineJoin = Round.
    /// </summary>
    [Property(MaxTest = 100)]
    public bool GetRoundCapPen_has_round_cap_and_join(byte a, byte r, byte g, byte b, PositiveInt thicknessRaw)
    {
        var color = Color.FromArgb(a, r, g, b);
        var thickness = (double)thicknessRaw.Get / 10.0;
        var cache = new RenderResourceCache();

        var pen = cache.GetRoundCapPen(color, thickness);

        return pen.LineCap == PenLineCap.Round && pen.LineJoin == PenLineJoin.Round;
    }

    /// <summary>
    /// GetRoundCapPen returns a pen with the correct thickness and brush colour.
    /// </summary>
    [Property(MaxTest = 100)]
    public bool GetRoundCapPen_has_correct_thickness_and_color(byte a, byte r, byte g, byte b, PositiveInt thicknessRaw)
    {
        var color = Color.FromArgb(a, r, g, b);
        var thickness = (double)thicknessRaw.Get / 10.0;
        var cache = new RenderResourceCache();

        var pen = cache.GetRoundCapPen(color, thickness);
        var brush = pen.Brush as SolidColorBrush;

        if (brush is null)
            return false;

        return pen.Thickness == thickness && brush.Color == color;
    }

    /// <summary>
    /// Clear() removes round-cap pen entries so the next call creates a fresh pen.
    /// </summary>
    [Fact]
    public void Clear_removes_round_cap_pens()
    {
        var cache = new RenderResourceCache();
        var color = Colors.Red;

        var pen1 = cache.GetRoundCapPen(color, 2.5);
        cache.Clear();
        var pen2 = cache.GetRoundCapPen(color, 2.5);

        Assert.NotSame(pen1, pen2);
    }

    /// <summary>
    /// Different colours produce different pen references (not shared incorrectly).
    /// </summary>
    [Fact]
    public void GetRoundCapPen_different_colors_different_pens()
    {
        var cache = new RenderResourceCache();

        var pen1 = cache.GetRoundCapPen(Colors.Red, 2.5);
        var pen2 = cache.GetRoundCapPen(Colors.Blue, 2.5);

        Assert.NotSame(pen1, pen2);
    }
}
