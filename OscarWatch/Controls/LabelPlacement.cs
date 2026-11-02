using Avalonia;

namespace OscarWatch.Controls;

/// <summary>Chooses where to put a text label beside a marker so that it stays on screen and clear of other labels.</summary>
internal static class LabelPlacement
{
    /// <summary>Distance from the marker centre to the nearest edge of the label.</summary>
    public const double Gap = 11;

    /// <summary>
    /// The first of right, left, above and below that lies fully inside <paramref name="bounds"/> and does not
    /// touch any rectangle in <paramref name="avoid"/>. If none is clear, the first one inside the bounds, and
    /// failing that the right-hand one clamped into the bounds.
    /// </summary>
    public static Rect Choose(Point anchor, Size size, IReadOnlyList<Rect> avoid, Rect bounds)
    {
        var halfHeight = size.Height / 2;
        Rect[] candidates =
        [
            new(anchor.X + Gap, anchor.Y - halfHeight, size.Width, size.Height),
            new(anchor.X - Gap - size.Width, anchor.Y - halfHeight, size.Width, size.Height),
            new(anchor.X - size.Width / 2, anchor.Y - Gap - size.Height, size.Width, size.Height),
            new(anchor.X - size.Width / 2, anchor.Y + Gap, size.Width, size.Height)
        ];

        Rect? firstInside = null;
        foreach (var candidate in candidates)
        {
            if (!Contains(bounds, candidate))
                continue;

            firstInside ??= candidate;
            if (!avoid.Any(a => a.Intersects(candidate)))
                return candidate;
        }

        if (firstInside is { } inside)
            return inside;

        var right = candidates[0];
        var x = Math.Clamp(right.X, bounds.X, Math.Max(bounds.X, bounds.Right - size.Width));
        var y = Math.Clamp(right.Y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - size.Height));
        return new Rect(x, y, size.Width, size.Height);
    }

    private static bool Contains(Rect outer, Rect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
}
