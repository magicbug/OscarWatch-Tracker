using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;

namespace OscarWatch.Controls;

/// <summary>Colours the rasteriser needs. Resolved from the shared palette by the caller.</summary>
/// <param name="SeaBgra">Fill used for the disc when the world map is not available.</param>
internal readonly record struct GlobePalette(uint SeaBgra)
{
    /// <summary>Takes the fallback sea colour from the shared palette.</summary>
    public static GlobePalette From(UiPalette palette) => new(ToBgra(palette.GlobeSeaFallback));

    internal static uint ToBgra(Avalonia.Media.Color c) =>
        ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
}

/// <summary>
/// Draws the Earth centred on a satellite's subpoint into a square BGRA pixel buffer. For every pixel it finds
/// the place on the Earth that the pixel shows (see <see cref="GlobeProjection"/>) and samples the
/// equirectangular world map there. Pixels outside the footprint are left fully transparent (0).
/// </summary>
/// <remarks>
/// For a given size, zoom and altitude, the surface point each pixel shows (in the local axes at the
/// subpoint) never changes, whatever the satellite's latitude and longitude. Those points are cached,
/// so a refresh only converts them to latitude and longitude, samples the map and shades. Renders are
/// serialised by a lock, so one instance can be shared, but only one render runs at a time.
/// </remarks>
internal sealed class GlobeRasteriser
{
    /// <summary>Share of the half-width that the limb circle fills at zoom 1.</summary>
    public const double LimbFill = 0.92;

    /// <summary>
    /// Brightness of the fully dark side of the globe (1 is unshaded). At 0.35 the night side was too dark to
    /// read the land; 0.5 still reads clearly as night and keeps the terrain visible.
    /// </summary>
    public const double NightBrightness = 0.5;

    /// <summary>Brightness at the very edge of the disc. Rises to 1 at the centre. A gentle sphere cue.</summary>
    public const double LimbBrightness = 0.7;

    /// <summary>A cached table is reused while the altitude stays within this many kilometres.</summary>
    private const double AltitudeToleranceKm = 1.0;

    // Sun elevation range across which night fades to day, as the sine of the elevation (-6 to +1 degrees).
    private static readonly double TwilightLow = Math.Sin(-6.0 * Math.PI / 180.0);
    private static readonly double TwilightHigh = Math.Sin(1.0 * Math.PI / 180.0);

    private readonly object _gate = new();
    private HitTable? _table;

    /// <summary>
    /// How many pixels one radian of arc spans on the projection plane at this size, zoom and altitude.
    /// Overlays drawn on top of the raster use this so they line up with it.
    /// </summary>
    public static double PixelsPerRadian(double sizePx, double zoom, double altitudeKm)
    {
        var limb = GlobeProjection.LimbRadius(altitudeKm);
        return limb > 0 ? zoom * LimbFill * (sizePx / 2.0) / limb : 0;
    }

    /// <summary>
    /// Renders the globe into <paramref name="destination"/>, which must hold at least
    /// <c>sizePx * sizePx</c> pixels. Does nothing useful and clears the buffer when the satellite
    /// altitude is not a positive number.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled. The buffer contents are then undefined.</exception>
    public void Render(
        GlobeCamera camera,
        int sizePx,
        double zoom,
        GeoCoordinate subsolar,
        bool shadeNight,
        GlobePalette palette,
        BasemapPixels basemap,
        uint[] destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(basemap);
        ArgumentNullException.ThrowIfNull(destination);
        if (sizePx <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizePx));
        if (destination.Length < checked(sizePx * sizePx))
            throw new ArgumentException("Destination is smaller than the requested raster.", nameof(destination));

        cancellationToken.ThrowIfCancellationRequested();

        var basis = new GlobeBasis(camera);
        if (!basis.IsValid || !double.IsFinite(zoom) || zoom <= 0)
        {
            Array.Clear(destination, 0, sizePx * sizePx);
            return;
        }

        lock (_gate)
        {
            // A render superseded while it waited for the lock must not build a table nobody wants.
            cancellationToken.ThrowIfCancellationRequested();

            var table = GetTable(sizePx, zoom, camera.AltitudeKm);

            basis.GeoToLocal(subsolar.LatitudeDeg, subsolar.LongitudeDeg, out var sx, out var sy, out var sz);
            var useMap = basemap.IsAvailable;
            var sea = palette.SeaBgra | 0xFF000000u;

            var options = new ParallelOptions { CancellationToken = cancellationToken };
            Parallel.For(0, sizePx, options, y =>
            {
                var row = y * sizePx;
                for (var x = 0; x < sizePx; x++)
                {
                    var i = row + x;
                    var hz = table.Hz[i];
                    if (hz < 0f)
                    {
                        destination[i] = 0;
                        continue;
                    }

                    var hx = table.Hx[i];
                    var hy = table.Hy[i];

                    uint colour;
                    if (useMap)
                    {
                        basis.LocalToGeo(hx, hy, hz, out var lat, out var lon);
                        colour = basemap.Sample(lat, lon);
                    }
                    else
                    {
                        colour = sea;
                    }

                    var brightness = (double)table.Limb[i];
                    if (shadeNight)
                        brightness *= DayBrightness(hx * sx + hy * sy + hz * sz);

                    destination[i] = Scale(colour, brightness);
                }
            });
        }
    }

    /// <summary>
    /// Brightness for a surface point given the sine of the sun's elevation there: 1 by day,
    /// <see cref="NightBrightness"/> at night, with a smooth twilight between.
    /// </summary>
    internal static double DayBrightness(double sineSunElevation)
    {
        var t = (sineSunElevation - TwilightLow) / (TwilightHigh - TwilightLow);
        t = Math.Clamp(t, 0.0, 1.0);
        var smooth = t * t * (3.0 - 2.0 * t);
        return NightBrightness + (1.0 - NightBrightness) * smooth;
    }

    private static uint Scale(uint bgra, double brightness)
    {
        var b = (uint)Math.Min(255.0, (bgra & 0xFF) * brightness + 0.5);
        var g = (uint)Math.Min(255.0, ((bgra >> 8) & 0xFF) * brightness + 0.5);
        var r = (uint)Math.Min(255.0, ((bgra >> 16) & 0xFF) * brightness + 0.5);
        return 0xFF000000u | (r << 16) | (g << 8) | b;
    }

    private HitTable GetTable(int sizePx, double zoom, double altitudeKm)
    {
        var table = _table;
        if (table is not null
            && table.SizePx == sizePx
            && table.Zoom == zoom
            && Math.Abs(table.AltitudeKm - altitudeKm) <= AltitudeToleranceKm)
        {
            return table;
        }

        table = HitTable.Build(sizePx, zoom, altitudeKm);
        _table = table;
        return table;
    }

    /// <summary>For every pixel: the unit surface normal where its ray hits (local axes), and a limb factor.</summary>
    private sealed class HitTable
    {
        public int SizePx { get; private init; }
        public double Zoom { get; private init; }
        public double AltitudeKm { get; private init; }

        /// <summary>Local X, Y, Z of the hit. <see cref="Hz"/> is negative for a ray that misses the Earth.</summary>
        public float[] Hx { get; private init; } = [];
        public float[] Hy { get; private init; } = [];
        public float[] Hz { get; private init; } = [];

        /// <summary>Brightness from 1 at the centre to <see cref="LimbBrightness"/> at the edge of the disc.</summary>
        public float[] Limb { get; private init; } = [];

        public static HitTable Build(int sizePx, double zoom, double altitudeKm)
        {
            var count = checked(sizePx * sizePx);
            var hx = new float[count];
            var hy = new float[count];
            var hz = new float[count];
            var limb = new float[count];

            var basis = new GlobeBasis(new GlobeCamera(0, 0, altitudeKm));
            var p = GlobeProjection.Ratio(altitudeKm);
            var pixelsPerRadius = PixelsPerRadian(sizePx, zoom, altitudeKm);
            var centre = sizePx / 2.0;

            Parallel.For(0, sizePx, y =>
            {
                // Screen y grows downwards; plane Y grows upwards (north).
                var planeY = (centre - (y + 0.5)) / pixelsPerRadius;
                for (var x = 0; x < sizePx; x++)
                {
                    var i = y * sizePx + x;
                    var planeX = (x + 0.5 - centre) / pixelsPerRadius;

                    if (!basis.TryHitLocal(planeX, planeY, out var x0, out var y0, out var z0))
                    {
                        hz[i] = -1f;
                        continue;
                    }

                    hx[i] = (float)x0;
                    hy[i] = (float)y0;
                    hz[i] = (float)z0;

                    // A gentle sphere cue, independent of how pixels are placed on the picture: the cosine of
                    // the angle between the surface normal and the direction back to the satellite, 1 when
                    // facing it and 0 at the limb. n.v = hz * P - 1 and |v| = sqrt(P^2 - 2 P hz + 1).
                    var toCameraLength = Math.Sqrt(p * p - 2.0 * p * z0 + 1.0);
                    var cosView = Math.Clamp((z0 * p - 1.0) / toCameraLength, 0.0, 1.0);
                    limb[i] = (float)(LimbBrightness + (1.0 - LimbBrightness) * Math.Sqrt(cosView));
                }
            });

            return new HitTable
            {
                SizePx = sizePx,
                Zoom = zoom,
                AltitudeKm = altitudeKm,
                Hx = hx,
                Hy = hy,
                Hz = hz,
                Limb = limb
            };
        }
    }
}
