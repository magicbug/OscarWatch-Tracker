namespace OscarWatch.Core.Geo;

/// <summary>A position on a polyline with the unit direction of travel there.</summary>
public readonly record struct PolylineTick(double X, double Y, double DirX, double DirY);

/// <summary>Evenly spaced marks along a polyline, used for the direction arrows on a ground track.</summary>
public static class PolylineTicks
{
    /// <summary>
    /// Marks at <paramref name="firstOffset"/> and then every <paramref name="spacing"/> units of path length,
    /// each with the direction of the segment it falls on. Empty for fewer than two points or a spacing that
    /// is not positive. A negative offset is treated as zero. Zero-length segments are skipped.
    /// </summary>
    public static IReadOnlyList<PolylineTick> Along(
        IReadOnlyList<(double X, double Y)> points, double spacing, double firstOffset)
    {
        var ticks = new List<PolylineTick>();
        if (points.Count < 2 || !double.IsFinite(spacing) || spacing <= 0)
            return ticks;

        var next = double.IsFinite(firstOffset) ? Math.Max(0.0, firstOffset) : 0.0;
        var travelled = 0.0;

        for (var i = 1; i < points.Count; i++)
        {
            var (ax, ay) = points[i - 1];
            var (bx, by) = points[i];
            var dx = bx - ax;
            var dy = by - ay;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 1e-9 || !double.IsFinite(length))
                continue;

            var ux = dx / length;
            var uy = dy / length;
            while (next <= travelled + length)
            {
                var t = next - travelled;
                ticks.Add(new PolylineTick(ax + ux * t, ay + uy * t, ux, uy));
                next += spacing;
            }

            travelled += length;
        }

        return ticks;
    }
}
