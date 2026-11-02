using OscarWatch.Controls;
using OscarWatch.Core.Geo;

namespace OscarWatch.Tests;

public sealed class GlobeRasterPolicyTests
{
    private static readonly DateTime T = new(2026, 6, 21, 12, 0, 20, DateTimeKind.Utc);

    private static RasterKey Key() => new(
        new GlobeCamera(10, 20, 420), SizePx: 512, Zoom: 1.0,
        UtcMinute: GlobeRasterPolicy.UtcMinute(T), DarkTheme: false, MapAvailable: true);

    [Theory]
    [InlineData(500.0, 1.0, 512)]
    [InlineData(512.0, 1.0, 512)]
    [InlineData(520.0, 1.0, 512)]    // rounds to the nearest step
    [InlineData(530.0, 1.0, 544)]
    [InlineData(300.0, 2.0, 608)]    // a 2x display needs twice the pixels
    [InlineData(10.0, 1.0, 64)]      // never below the minimum
    [InlineData(5000.0, 1.0, 1024)]  // never above the maximum
    [InlineData(400.0, 2.0, 800)]    // 800 wanted, under the cap
    [InlineData(600.0, 2.0, 1024)]   // 1200 wanted, capped
    public void Raster_size_is_stepped_and_limited(double side, double scaling, int expected) =>
        Assert.Equal(expected, GlobeRasterPolicy.RasterSize(side, scaling));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-10.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void An_unusable_side_gives_the_minimum_size(double side) =>
        Assert.Equal(GlobeRasterPolicy.MinRasterPx, GlobeRasterPolicy.RasterSize(side, 1.0));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void An_unusable_scaling_is_treated_as_one(double scaling) =>
        Assert.Equal(GlobeRasterPolicy.RasterSize(500, 1.0), GlobeRasterPolicy.RasterSize(500, scaling));

    [Fact]
    public void Raster_size_is_always_a_multiple_of_the_step_inside_the_limits()
    {
        for (var side = 1.0; side < 3000; side += 7.3)
        {
            var size = GlobeRasterPolicy.RasterSize(side, 1.25);
            Assert.InRange(size, GlobeRasterPolicy.MinRasterPx, GlobeRasterPolicy.MaxRasterPx);
            Assert.Equal(0, size % GlobeRasterPolicy.SizeStepPx);
        }
    }

    [Fact]
    public void Raster_size_never_shrinks_as_the_control_grows()
    {
        var previous = 0;
        for (var side = 1.0; side < 3000; side += 3.1)
        {
            var size = GlobeRasterPolicy.RasterSize(side, 1.0);
            Assert.True(size >= previous, $"shrank at {side}");
            previous = size;
        }
    }

    [Theory]
    [InlineData(1.0, 600.0, 660.0, 0.9091)]
    [InlineData(2.5, 600.0, 660.0, 2.2727)]
    [InlineData(1.0, 660.0, 600.0, 0.9091)]   // same either way round
    [InlineData(1.0, 500.0, 500.0, 1.0)]
    public void Effective_zoom_is_the_zoom_scaled_by_shorter_over_longer(double zoom, double w, double h, double expected) =>
        Assert.Equal(expected, GlobeRasterPolicy.EffectiveZoom(zoom, w, h), precision: 4);

    [Theory]
    [InlineData(0.0, 500.0)]
    [InlineData(500.0, 0.0)]
    [InlineData(-5.0, 500.0)]
    public void Effective_zoom_leaves_the_zoom_alone_for_an_unusable_size(double w, double h) =>
        Assert.Equal(2.0, GlobeRasterPolicy.EffectiveZoom(2.0, w, h));

    /// <summary>
    /// The picture comes from the raster and the lines are drawn from the shorter side, so the two scales must
    /// agree or the markers drift off the map. This is the contract between EarthViewControl's two paths.
    /// </summary>
    [Fact]
    public void Raster_and_overlay_scales_agree_for_any_pane_zoom_altitude_and_display_scaling()
    {
        var worst = 0.0;
        foreach (var (w, h) in new[] { (590.0, 650.0), (1190.0, 660.0), (300.0, 1200.0), (160.0, 160.0), (900.0, 880.0), (2000.0, 400.0) })
        {
            foreach (var zoom in new[] { 1.0, 1.7, 2.5, 4.0 })
            {
                foreach (var altitude in new[] { 400.0, 800.0, 35_786.0 })
                {
                    foreach (var scaling in new[] { 1.0, 2.0 })
                    {
                        var longer = Math.Max(w, h);
                        var shorter = Math.Min(w, h);
                        var raster = GlobeRasterPolicy.RasterSize(longer, scaling);

                        // Pixels of the pane per radian of arc, as the stretched raster delivers them.
                        var fromRaster = GlobeRasteriser.PixelsPerRadian(
                            raster, GlobeRasterPolicy.EffectiveZoom(zoom, w, h), altitude) * longer / raster;
                        // And as the overlays compute it.
                        var fromOverlay = GlobeRasteriser.PixelsPerRadian(shorter, zoom, altitude);

                        worst = Math.Max(worst, Math.Abs(fromRaster - fromOverlay) / fromOverlay);
                    }
                }
            }
        }

        Assert.True(worst < 5e-4, $"worst relative disagreement {worst:P3}");
    }

    [Fact]
    public void The_first_raster_is_always_needed() =>
        Assert.True(GlobeRasterPolicy.NeedsRender(null, Key()));

    [Fact]
    public void An_identical_key_does_not_need_a_new_raster() =>
        Assert.False(GlobeRasterPolicy.NeedsRender(Key(), Key()));

    [Fact]
    public void Small_movement_does_not_need_a_new_raster_but_larger_movement_does()
    {
        var k = Key();

        Assert.False(GlobeRasterPolicy.NeedsRender(k, k with { Camera = k.Camera with { SubpointLatitudeDeg = 10.04 } }));
        Assert.True(GlobeRasterPolicy.NeedsRender(k, k with { Camera = k.Camera with { SubpointLatitudeDeg = 10.06 } }));
        Assert.True(GlobeRasterPolicy.NeedsRender(k, k with { Camera = k.Camera with { SubpointLongitudeDeg = 20.2 } }));
    }

    [Fact]
    public void Near_a_pole_a_small_move_that_turns_the_picture_needs_a_new_raster()
    {
        // 0.2 degrees of longitude at 89 degrees north is only about 0.003 degrees of arc, but the
        // north-up picture turns by about 0.2 degrees, which moves its edge by more than a pixel.
        var k = Key() with { Camera = new GlobeCamera(89, 0, 420) };

        Assert.True(GlobeRasterPolicy.NeedsRender(k, k with { Camera = k.Camera with { SubpointLongitudeDeg = 0.2 } }));
    }

    [Fact]
    public void Swinging_across_a_pole_needs_a_new_raster_though_the_subpoint_barely_moves()
    {
        // Review finding: (89.99, 0) to (89.99, 180) is only 0.02 degrees of arc but turns the map half way round.
        var k = Key() with { Camera = new GlobeCamera(89.99, 0, 420) };
        var swung = k with { Camera = new GlobeCamera(89.99, 180, 420) };

        Assert.True(GlobeViewMath.CentralAngleDeg(89.99, 0, 89.99, 180) < GlobeRasterPolicy.SubpointToleranceDeg);
        Assert.True(GlobeRasterPolicy.NeedsRender(k, swung));
        Assert.True(GlobeRasterPolicy.NeedsRender(swung, k));
    }

    [Fact]
    public void Near_the_equator_a_small_longitude_change_does_not_turn_the_picture()
    {
        var k = Key() with { Camera = new GlobeCamera(0, 20, 420) };

        Assert.Equal(0.0, GlobeRasterPolicy.RotationDeg(k.Camera, k.Camera with { SubpointLongitudeDeg = 20.04 }), precision: 9);
        Assert.False(GlobeRasterPolicy.NeedsRender(k, k with { Camera = k.Camera with { SubpointLongitudeDeg = 20.04 } }));
    }

    [Theory]
    [InlineData(179.9, -179.9, 0.2)]   // across the antimeridian
    [InlineData(0.0, 360.0, 0.0)]      // a full turn is no turn
    [InlineData(10.0, 190.0, 180.0)]
    public void The_turn_uses_the_shortest_longitude_difference(double lonA, double lonB, double expectedDeltaDeg)
    {
        var a = new GlobeCamera(90, lonA, 420);
        var b = new GlobeCamera(90, lonB, 420);

        Assert.Equal(expectedDeltaDeg, GlobeRasterPolicy.RotationDeg(a, b), precision: 6);
    }

    [Fact]
    public void A_tiny_step_across_the_antimeridian_is_a_tiny_move()
    {
        var east = Key() with { Camera = new GlobeCamera(0, 179.99, 420) };
        var west = east with { Camera = new GlobeCamera(0, -179.99, 420) };

        Assert.False(GlobeRasterPolicy.NeedsRender(east, west));
    }

    [Fact]
    public void Altitude_matters_only_beyond_the_tolerance()
    {
        var k = Key();

        Assert.False(GlobeRasterPolicy.NeedsRender(k, k with { Camera = k.Camera with { AltitudeKm = 420.9 } }));
        Assert.True(GlobeRasterPolicy.NeedsRender(k, k with { Camera = k.Camera with { AltitudeKm = 421.1 } }));
    }

    [Fact]
    public void Size_zoom_theme_map_and_minute_changes_each_need_a_new_raster()
    {
        var k = Key();

        Assert.True(GlobeRasterPolicy.NeedsRender(k, k with { SizePx = 544 }));
        Assert.True(GlobeRasterPolicy.NeedsRender(k, k with { Zoom = 1.5 }));
        Assert.True(GlobeRasterPolicy.NeedsRender(k, k with { DarkTheme = true }));
        Assert.True(GlobeRasterPolicy.NeedsRender(k, k with { MapAvailable = false }));
        Assert.True(GlobeRasterPolicy.NeedsRender(k, k with { UtcMinute = k.UtcMinute + 1 }));
    }

    [Fact]
    public void Seconds_within_a_minute_share_a_minute_number()
    {
        Assert.Equal(GlobeRasterPolicy.UtcMinute(T), GlobeRasterPolicy.UtcMinute(T.AddSeconds(30)));
        Assert.NotEqual(GlobeRasterPolicy.UtcMinute(T), GlobeRasterPolicy.UtcMinute(T.AddSeconds(45)));
    }

    [Fact]
    public void A_geostationary_satellite_drifting_slightly_does_not_need_a_new_raster()
    {
        var k = Key() with { Camera = new GlobeCamera(0.0, 25.5, 35_786) };

        Assert.False(GlobeRasterPolicy.NeedsRender(k, k with { Camera = new GlobeCamera(0.01, 25.5004, 35_786.3) }));
    }
}
