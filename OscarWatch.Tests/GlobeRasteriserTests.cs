using OscarWatch.Controls;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;

namespace OscarWatch.Tests;

public sealed class GlobeRasteriserTests
{
    private const int Odd = 101; // odd size: the centre pixel sits exactly on the subpoint
    private static readonly GlobePalette Palette = new(0xFF204080);
    private static readonly GeoCoordinate NoSun = new(0, 0);

    /// <summary>
    /// A smooth map: red rises towards the south, green rises towards the east, blue is constant.
    /// Smooth, so a sample is insensitive to tiny differences in where it is taken.
    /// </summary>
    private static BasemapPixels GradientMap()
    {
        const int w = 360, h = 180;
        var pixels = new uint[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var r = (uint)(y * 255 / (h - 1));
                var g = (uint)(x * 255 / (w - 1));
                pixels[y * w + x] = 0xFF000000u | (r << 16) | (g << 8) | 0x80u;
            }
        }

        return new BasemapPixels(pixels, w, h);
    }

    private static BasemapPixels WhiteMap() =>
        new(Enumerable.Repeat(0xFFFFFFFFu, 8 * 4).ToArray(), 8, 4);

    private static uint[] Render(
        GlobeCamera camera,
        int size = Odd,
        double zoom = 1.0,
        GeoCoordinate? sun = null,
        bool shade = false,
        BasemapPixels? map = null,
        GlobeRasteriser? rasteriser = null)
    {
        var buffer = new uint[size * size];
        (rasteriser ?? new GlobeRasteriser()).Render(
            camera, size, zoom, sun ?? NoSun, shade, Palette, map ?? GradientMap(), buffer);
        return buffer;
    }

    private static int MaxChannelDiff(uint a, uint b)
    {
        var worst = 0;
        for (var shift = 0; shift < 32; shift += 8)
            worst = Math.Max(worst, Math.Abs((int)((a >> shift) & 0xFF) - (int)((b >> shift) & 0xFF)));
        return worst;
    }

    private static uint Scale(uint bgra, double brightness)
    {
        uint Ch(int shift) => (uint)Math.Min(255.0, ((bgra >> shift) & 0xFF) * brightness + 0.5);
        return 0xFF000000u | (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }

    /// <summary>
    /// The colour a pixel should have, worked out independently of the rasteriser's cached tables:
    /// unproject the pixel to latitude and longitude, sample the map, and apply the limb factor.
    /// </summary>
    private static uint Expected(GlobeCamera camera, int size, double zoom, int px, int py, BasemapPixels map)
    {
        var ppr = GlobeRasteriser.PixelsPerRadian(size, zoom, camera.AltitudeKm);
        var planeX = (px + 0.5 - size / 2.0) / ppr;
        var planeY = (size / 2.0 - (py + 0.5)) / ppr;

        var basis = new GlobeBasis(camera);
        Assert.True(basis.TryHitLocal(planeX, planeY, out var hx, out var hy, out var hz));
        basis.LocalToGeo(hx, hy, hz, out var lat, out var lon);

        var p = GlobeProjection.Ratio(camera.AltitudeKm);
        var vx = -hx;
        var vy = -hy;
        var vz = p - hz;
        var cosView = Math.Clamp((hx * vx + hy * vy + hz * vz) / Math.Sqrt(vx * vx + vy * vy + vz * vz), 0.0, 1.0);
        var limb = GlobeRasteriser.LimbBrightness + (1.0 - GlobeRasteriser.LimbBrightness) * Math.Sqrt(cosView);

        return Scale(map.Sample(lat, lon), limb);
    }

    [Theory]
    [InlineData(10.0, 20.0, 400.0)]
    [InlineData(-45.0, 150.0, 800.0)]
    [InlineData(0.0, 0.0, 35_786.0)]
    public void Centre_pixel_shows_the_map_at_the_subpoint(double lat, double lon, double altitudeKm)
    {
        var map = GradientMap();
        var camera = new GlobeCamera(lat, lon, altitudeKm);

        var buffer = Render(camera, map: map);

        var centre = buffer[(Odd / 2) * Odd + Odd / 2];
        Assert.True(MaxChannelDiff(map.Sample(lat, lon), centre) <= 1,
            $"expected about 0x{map.Sample(lat, lon):X8}, got 0x{centre:X8}");
    }

    [Theory]
    [InlineData(10.0, 20.0, 400.0, 30, 40)]
    [InlineData(10.0, 20.0, 400.0, 70, 60)]
    [InlineData(-45.0, 150.0, 800.0, 50, 20)]
    [InlineData(-45.0, 150.0, 800.0, 25, 75)]
    [InlineData(60.0, -100.0, 35_786.0, 35, 35)]
    [InlineData(60.0, -100.0, 35_786.0, 75, 55)]
    public void Pixels_away_from_the_centre_show_the_expected_place(
        double lat, double lon, double altitudeKm, int px, int py)
    {
        var map = GradientMap();
        var camera = new GlobeCamera(lat, lon, altitudeKm);

        var buffer = Render(camera, map: map);

        var expected = Expected(camera, Odd, 1.0, px, py, map);
        var actual = buffer[py * Odd + px];
        Assert.True(MaxChannelDiff(expected, actual) <= 2,
            $"expected about 0x{expected:X8}, got 0x{actual:X8}");
    }

    [Fact]
    public void Pixels_outside_the_limb_are_transparent_and_inside_are_opaque()
    {
        const int size = 200;
        var camera = new GlobeCamera(0, 0, 800);

        var buffer = Render(camera, size);

        // The limb fills 92 percent of the half width: about 92 pixels from the centre.
        Assert.Equal(0u, buffer[100 * size + 195]);   // about 95 pixels out
        Assert.Equal(0u, buffer[5 * size + 100]);
        Assert.Equal(0xFFu, buffer[100 * size + 188] >> 24); // about 88 pixels out
        Assert.Equal(0xFFu, buffer[100 * size + 100] >> 24);
    }

    [Fact]
    public void Corners_are_transparent()
    {
        var buffer = Render(new GlobeCamera(0, 0, 35_786), Odd);

        Assert.Equal(0u, buffer[0]);
        Assert.Equal(0u, buffer[Odd - 1]);
        Assert.Equal(0u, buffer[(Odd - 1) * Odd]);
        Assert.Equal(0u, buffer[Odd * Odd - 1]);
    }

    [Fact]
    public void Limb_circle_fills_the_stated_share_of_the_half_width()
    {
        foreach (var altitude in new[] { 400.0, 800.0, 35_786.0 })
        {
            var ppr = GlobeRasteriser.PixelsPerRadian(200, 1.0, altitude);
            var limbPx = ppr * GlobeProjection.LimbRadius(altitude);

            Assert.Equal(GlobeRasteriser.LimbFill * 100.0, limbPx, precision: 9);
        }
    }

    [Fact]
    public void Doubling_the_zoom_doubles_the_pixel_scale()
    {
        var one = GlobeRasteriser.PixelsPerRadian(200, 1.0, 800);
        var two = GlobeRasteriser.PixelsPerRadian(200, 2.0, 800);

        Assert.Equal(2.0 * one, two, precision: 9);
    }

    [Fact]
    public void A_pixel_at_twice_the_distance_at_twice_the_zoom_shows_the_same_place()
    {
        var map = GradientMap();
        var camera = new GlobeCamera(20, 40, 800);

        var near = Render(camera, zoom: 1.0, map: map);
        var zoomed = Render(camera, zoom: 2.0, map: map);

        // Offsets of 10 and 20 pixels from the centre at zoom 1 and zoom 2 are the same plane point.
        var c = Odd / 2;
        var a = near[(c - 12) * Odd + (c + 10)];
        var b = zoomed[(c - 24) * Odd + (c + 20)];
        Assert.True(MaxChannelDiff(a, b) <= 2, $"0x{a:X8} vs 0x{b:X8}");
    }

    [Fact]
    public void Sun_overhead_leaves_the_centre_unshaded()
    {
        var map = GradientMap();
        var camera = new GlobeCamera(15, 30, 800);
        var sunOverhead = new GeoCoordinate(15, 30);

        var plain = Render(camera, map: map);
        var shaded = Render(camera, sun: sunOverhead, shade: true, map: map);

        var i = (Odd / 2) * Odd + Odd / 2;
        Assert.True(MaxChannelDiff(plain[i], shaded[i]) <= 1);
    }

    [Fact]
    public void Sun_on_the_far_side_darkens_the_centre_to_the_night_brightness()
    {
        var map = GradientMap();
        var camera = new GlobeCamera(15, 30, 800);
        var sunOpposite = new GeoCoordinate(-15, -150);

        var plain = Render(camera, map: map);
        var shaded = Render(camera, sun: sunOpposite, shade: true, map: map);

        var i = (Odd / 2) * Odd + Odd / 2;
        var expected = Scale(plain[i], GlobeRasteriser.NightBrightness);
        Assert.True(MaxChannelDiff(expected, shaded[i]) <= 1,
            $"expected about 0x{expected:X8}, got 0x{shaded[i]:X8}");
    }

    [Fact]
    public void Shading_off_ignores_the_sun()
    {
        var map = GradientMap();
        var camera = new GlobeCamera(15, 30, 800);

        var withoutSun = Render(camera, map: map);
        var sunOpposite = Render(camera, sun: new GeoCoordinate(-15, -150), shade: false, map: map);

        Assert.Equal(withoutSun, sunOpposite);
    }

    [Fact]
    public void Terminator_runs_through_the_centre_when_the_sun_is_ninety_degrees_east()
    {
        var camera = new GlobeCamera(0, 0, 800);
        var sunEast = new GeoCoordinate(0, 90);

        var buffer = Render(camera, sun: sunEast, shade: true, map: WhiteMap());

        var c = Odd / 2;
        var east = buffer[c * Odd + c + 15] & 0xFF; // day side
        var west = buffer[c * Odd + c - 15] & 0xFF; // night side
        Assert.True(east > west + 50, $"east {east}, west {west}");
        // Far from the terminator the two sides settle at day and night brightness (times the limb factor).
        Assert.True(buffer[c * Odd + c + 38] > buffer[c * Odd + c - 38]);
    }

    [Fact]
    public void Brightness_ramp_is_bounded_and_never_decreases_with_sun_height()
    {
        Assert.Equal(GlobeRasteriser.NightBrightness, GlobeRasteriser.DayBrightness(-1.0), precision: 12);
        Assert.Equal(1.0, GlobeRasteriser.DayBrightness(1.0), precision: 12);

        var previous = 0.0;
        for (var s = -1.0; s <= 1.0; s += 0.005)
        {
            var b = GlobeRasteriser.DayBrightness(s);
            Assert.InRange(b, GlobeRasteriser.NightBrightness - 1e-12, 1.0 + 1e-12);
            Assert.True(b >= previous - 1e-12, $"decreased at {s}");
            previous = b;
        }
    }

    [Fact]
    public void Unavailable_map_fills_the_disc_with_the_sea_colour()
    {
        var camera = new GlobeCamera(0, 0, 800);

        var buffer = Render(camera, map: BasemapPixels.Unavailable);

        var centre = buffer[(Odd / 2) * Odd + Odd / 2];
        Assert.True(MaxChannelDiff(Palette.SeaBgra | 0xFF000000u, centre) <= 1);
        Assert.Equal(0u, buffer[0]);
    }

    [Fact]
    public void Repeated_renders_are_identical()
    {
        var camera = new GlobeCamera(-20, 100, 600);
        var map = GradientMap();
        var rasteriser = new GlobeRasteriser();

        var first = Render(camera, map: map, rasteriser: rasteriser);
        var second = Render(camera, map: map, rasteriser: rasteriser);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Changing_altitude_matches_a_fresh_rasteriser_so_no_stale_table_is_used()
    {
        var map = GradientMap();
        var shared = new GlobeRasteriser();
        var low = new GlobeCamera(10, 10, 400);
        var high = new GlobeCamera(10, 10, 4_000);

        Render(low, map: map, rasteriser: shared);
        var reused = Render(high, map: map, rasteriser: shared);
        var fresh = Render(high, map: map, rasteriser: new GlobeRasteriser());

        Assert.Equal(fresh, reused);
    }

    [Fact]
    public void Small_altitude_changes_keep_working_with_the_cached_table()
    {
        var map = GradientMap();
        var shared = new GlobeRasteriser();

        var a = Render(new GlobeCamera(10, 10, 400.0), map: map, rasteriser: shared);
        var b = Render(new GlobeCamera(10, 10, 400.5), map: map, rasteriser: shared);

        var i = (Odd / 2) * Odd + Odd / 2;
        Assert.True(MaxChannelDiff(a[i], b[i]) <= 1);
    }

    [Fact]
    public async Task A_render_cancelled_while_waiting_for_the_lock_builds_no_table()
    {
        // Review finding: a superseded render used to get the lock and then build its full hit table.
        var rasteriser = new GlobeRasteriser();
        var type = typeof(GlobeRasteriser);
        var gate = type.GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(rasteriser)!;
        var tableField = type.GetField("_table", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        using var cts = new CancellationTokenSource();
        var buffer = new uint[Odd * Odd];
        Task render;
        lock (gate)
        {
            render = Task.Run(() => rasteriser.Render(
                new GlobeCamera(0, 0, 800), Odd, 1.0, NoSun, false, Palette, GradientMap(), buffer, cts.Token));

            // Give the task time to reach the lock, then cancel while it is waiting.
            Thread.Sleep(200);
            Assert.False(render.IsCompleted);
            cts.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => render);
        Assert.Null(tableField.GetValue(rasteriser));
    }

    [Fact]
    public void A_cancelled_token_stops_the_render()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var buffer = new uint[Odd * Odd];

        Assert.Throws<OperationCanceledException>(() => new GlobeRasteriser().Render(
            new GlobeCamera(0, 0, 800), Odd, 1.0, NoSun, false, Palette, GradientMap(), buffer, cts.Token));
    }

    [Fact]
    public void A_destination_that_is_too_small_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new GlobeRasteriser().Render(
            new GlobeCamera(0, 0, 800), 10, 1.0, NoSun, false, Palette, GradientMap(), new uint[50]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_non_positive_size_is_rejected(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GlobeRasteriser().Render(
            new GlobeCamera(0, 0, 800), size, 1.0, NoSun, false, Palette, GradientMap(), new uint[10]));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-10.0)]
    [InlineData(double.NaN)]
    public void An_unusable_camera_clears_the_buffer(double altitudeKm)
    {
        var buffer = Enumerable.Repeat(0xDEADBEEFu, Odd * Odd).ToArray();

        new GlobeRasteriser().Render(
            new GlobeCamera(0, 0, altitudeKm), Odd, 1.0, NoSun, false, Palette, GradientMap(), buffer);

        Assert.All(buffer, p => Assert.Equal(0u, p));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void An_unusable_zoom_clears_the_buffer(double zoom)
    {
        var buffer = Enumerable.Repeat(0xDEADBEEFu, Odd * Odd).ToArray();

        new GlobeRasteriser().Render(
            new GlobeCamera(0, 0, 800), Odd, zoom, NoSun, false, Palette, GradientMap(), buffer);

        Assert.All(buffer, p => Assert.Equal(0u, p));
    }

    [Fact]
    public void Geo_to_local_gives_the_sun_direction_in_camera_axes()
    {
        var basis = new GlobeBasis(new GlobeCamera(0, 0, 800));

        basis.GeoToLocal(0, 0, out var cx, out var cy, out var cz);
        Assert.Equal((0.0, 0.0, 1.0), (Math.Round(cx, 12), Math.Round(cy, 12), Math.Round(cz, 12)));

        basis.GeoToLocal(0, 90, out var ex, out var ey, out var ez);
        Assert.Equal((1.0, 0.0, 0.0), (Math.Round(ex, 12), Math.Round(ey, 12), Math.Round(ez, 12)));

        basis.GeoToLocal(90, 0, out var nx, out var ny, out var nz);
        Assert.Equal((0.0, 1.0, 0.0), (Math.Round(nx, 12), Math.Round(ny, 12), Math.Round(nz, 12)));
    }
}
