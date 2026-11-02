using OscarWatch.Core.Geo;

namespace OscarWatch.Controls;

/// <summary>Everything that decides what the globe raster looks like.</summary>
internal readonly record struct RasterKey(
    GlobeCamera Camera,
    int SizePx,
    double Zoom,
    long UtcMinute,
    bool DarkTheme,
    bool MapAvailable);

/// <summary>When the Earth View needs a new raster, and how big it should be. Pure, so it can be tested.</summary>
internal static class GlobeRasterPolicy
{
    public const int MinRasterPx = 64;

    /// <summary>
    /// Largest raster edge. Measured on an Apple M4 Pro with a full-size map: about 5.7 ms for 768 pixels and
    /// 9 to 11 ms for 1024, or about 45 ms for 1024 when limited to one CPU. The cached hit table grows with the
    /// square of the size (about 17 MB at 1024, plus 4 MB each for the pixel buffer and the bitmap). 1024 was
    /// chosen over 768 because the globe is visibly soft when a large window stretches a smaller raster. The
    /// raster is stretched to the control, and the overlays are drawn at full resolution on top.
    /// </summary>
    public const int MaxRasterPx = 1024;

    /// <summary>Sizes are rounded to this step, so dragging a window edge does not render for every pixel.</summary>
    public const int SizeStepPx = 32;

    /// <summary>
    /// The subpoint must move more than this many degrees of arc before the raster is redone. At typical
    /// window sizes this is under one pixel, so the globe never visibly jumps. A low orbit satellite moves
    /// 0.05 degrees about once a second, which is well within what the renderer can afford.
    /// </summary>
    public const double SubpointToleranceDeg = 0.05;

    /// <summary>The altitude must change by more than this many kilometres. Matches the rasteriser's table tolerance.</summary>
    public const double AltitudeToleranceKm = 1.0;

    /// <summary>
    /// Raster edge for a control whose shorter side is <paramref name="sidePx"/> logical pixels on a display
    /// with the given scaling. Rounded to <see cref="SizeStepPx"/> and kept between the minimum and maximum.
    /// </summary>
    public static int RasterSize(double sidePx, double renderScaling)
    {
        if (!double.IsFinite(sidePx) || sidePx <= 0)
            return MinRasterPx;

        var scale = double.IsFinite(renderScaling) && renderScaling > 0 ? renderScaling : 1.0;
        var stepped = (int)(Math.Round(sidePx * scale / SizeStepPx) * SizeStepPx);
        return Math.Clamp(stepped, MinRasterPx, MaxRasterPx);
    }

    /// <summary>
    /// The zoom to give the rasteriser for a control of the given size. The raster covers the longer side but
    /// the globe's size follows the shorter side, so the zoom is scaled by shorter over longer. Rounded to four
    /// decimals so a one pixel change in size does not rebuild the rasteriser's tables every time; the error
    /// that introduces is under 0.05 percent, well under a pixel.
    /// </summary>
    public static double EffectiveZoom(double zoom, double width, double height)
    {
        var shorter = Math.Min(width, height);
        var longer = Math.Max(width, height);
        return longer > 0 && shorter > 0 ? Math.Round(zoom * shorter / longer, 4) : zoom;
    }

    /// <summary>Whole minutes since the epoch. The night shading is redone when this changes.</summary>
    public static long UtcMinute(DateTime utc) => utc.Ticks / TimeSpan.TicksPerMinute;

    /// <summary>True when a raster made for <paramref name="current"/> would not do for <paramref name="desired"/>.</summary>
    public static bool NeedsRender(RasterKey? current, RasterKey desired)
    {
        if (current is not { } c)
            return true;

        return c.SizePx != desired.SizePx
            || c.Zoom != desired.Zoom
            || c.DarkTheme != desired.DarkTheme
            || c.MapAvailable != desired.MapAvailable
            || c.UtcMinute != desired.UtcMinute
            || Math.Abs(c.Camera.AltitudeKm - desired.Camera.AltitudeKm) > AltitudeToleranceKm
            || GlobeViewMath.CentralAngleDeg(
                   c.Camera.SubpointLatitudeDeg, c.Camera.SubpointLongitudeDeg,
                   desired.Camera.SubpointLatitudeDeg, desired.Camera.SubpointLongitudeDeg) > SubpointToleranceDeg
            || RotationDeg(c.Camera, desired.Camera) > SubpointToleranceDeg;
    }

    /// <summary>
    /// How far the north-up picture turns about its centre between two cameras, in degrees: the change in
    /// longitude times the sine of the latitude. It is tiny except near a pole, where the subpoint can
    /// swing from longitude 0 to 180 in a hair's breadth of arc and the whole map turns half way round.
    /// Takes the higher latitude of the two, so the answer is never too small. At the tolerance the
    /// edge of a 1024 pixel raster moves under one pixel.
    /// </summary>
    internal static double RotationDeg(GlobeCamera a, GlobeCamera b)
    {
        var deltaLon = Math.Abs((b.SubpointLongitudeDeg - a.SubpointLongitudeDeg) % 360.0);
        if (deltaLon > 180.0)
            deltaLon = 360.0 - deltaLon;

        var latRad = Math.Max(Math.Abs(a.SubpointLatitudeDeg), Math.Abs(b.SubpointLatitudeDeg)) * Math.PI / 180.0;
        return deltaLon * Math.Sin(latRad);
    }
}
