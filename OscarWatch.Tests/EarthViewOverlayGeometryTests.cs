using Avalonia;
using FsCheck.Xunit;
using OscarWatch.Controls;
using OscarWatch.Core.Geo;

namespace OscarWatch.Tests;

public sealed class EarthViewOverlayGeometryTests
{
    // ---- ring radius -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(400.0)]
    [InlineData(800.0)]
    [InlineData(35_786.0)]
    public void Ring_at_the_horizon_angle_is_the_limb(double altitudeKm)
    {
        var horizonDeg = FootprintGeometry.HorizonRadiusDeg(altitudeKm);

        Assert.Equal(
            GlobeProjection.LimbRadius(altitudeKm),
            GlobeProjection.RingRadius(altitudeKm, horizonDeg),
            precision: 9);
    }

    [Fact]
    public void Ring_beyond_the_horizon_is_clamped_to_the_limb()
    {
        Assert.Equal(
            GlobeProjection.LimbRadius(800),
            GlobeProjection.RingRadius(800, 89), precision: 9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    [InlineData(double.NaN)]
    public void Ring_with_an_unusable_angle_has_no_radius(double angleDeg) =>
        Assert.Equal(0.0, GlobeProjection.RingRadius(800, angleDeg));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void Ring_with_an_unusable_altitude_has_no_radius(double altitudeKm) =>
        Assert.Equal(0.0, GlobeProjection.RingRadius(altitudeKm, 10));

    [Fact]
    public void Ring_radius_grows_with_the_angle()
    {
        var previous = 0.0;
        for (var angle = 1.0; angle <= 28; angle += 1)
        {
            var r = GlobeProjection.RingRadius(800, angle);
            Assert.True(r > previous, $"not increasing at {angle}");
            previous = r;
        }
    }

    /// <summary>
    /// The ring is drawn as a circle, so every point at that angle, in any direction, must project onto it.
    /// </summary>
    [Property]
    public bool Every_point_at_the_ring_angle_projects_onto_the_ring_circle(
        double rawLat, double rawLon, double rawAlt, double rawFraction, double rawBearing)
    {
        if (!double.IsFinite(rawLat) || !double.IsFinite(rawLon) || !double.IsFinite(rawAlt)
            || !double.IsFinite(rawFraction) || !double.IsFinite(rawBearing))
            return true;

        var camera = new GlobeCamera(
            -85 + Math.Abs(rawLat % 1.0) * 170,
            -180 + Math.Abs(rawLon % 1.0) * 360,
            200 + Math.Abs(rawAlt % 1.0) * 39_800);
        var horizon = FootprintGeometry.HorizonRadiusDeg(camera.AltitudeKm);
        var angle = Math.Max(0.5, Math.Abs(rawFraction % 1.0) * 0.95 * horizon);
        var bearing = Math.Abs(rawBearing % 1.0) * 360;

        var (lat, lon) = GlobeViewMath.Destination(camera.SubpointLatitudeDeg, camera.SubpointLongitudeDeg, angle, bearing);
        var point = GlobeProjection.Project(camera, lat, lon);
        if (!point.Visible)
            return false;

        var radius = Math.Sqrt(point.X * point.X + point.Y * point.Y);
        return Math.Abs(radius - GlobeProjection.RingRadius(camera.AltitudeKm, angle)) < 1e-9;
    }

    [Fact]
    public void Minimum_elevation_ring_sits_inside_the_limb_and_shrinks_as_elevation_rises()
    {
        var at5 = GlobeProjection.RingRadius(800, GlobeViewMath.FootprintRadiusDeg(800, 5));
        var at20 = GlobeProjection.RingRadius(800, GlobeViewMath.FootprintRadiusDeg(800, 20));
        var limb = GlobeProjection.LimbRadius(800);

        Assert.True(0 < at20 && at20 < at5 && at5 < limb, $"{at20} {at5} {limb}");
    }

    // ---- ticks along a polyline --------------------------------------------------------------------------------

    [Fact]
    public void Ticks_on_a_straight_line_are_evenly_spaced_and_point_along_it()
    {
        var ticks = PolylineTicks.Along([(0, 0), (100, 0)], spacing: 30, firstOffset: 15);

        Assert.Equal([15.0, 45.0, 75.0], ticks.Select(t => t.X));
        Assert.All(ticks, t =>
        {
            Assert.Equal(0.0, t.Y, precision: 9);
            Assert.Equal(1.0, t.DirX, precision: 9);
            Assert.Equal(0.0, t.DirY, precision: 9);
        });
    }

    [Fact]
    public void Ticks_carry_over_a_corner_and_take_the_direction_of_their_own_segment()
    {
        // Right 50, then down 50. A tick every 40 starting at 40: at 40 (first leg), at 80 (second leg, 30 down).
        var ticks = PolylineTicks.Along([(0, 0), (50, 0), (50, 50)], spacing: 40, firstOffset: 40);

        Assert.Equal(2, ticks.Count);
        Assert.Equal((40.0, 0.0), (ticks[0].X, ticks[0].Y));
        Assert.Equal((50.0, 30.0), (Math.Round(ticks[1].X, 9), Math.Round(ticks[1].Y, 9)));
        Assert.Equal((1.0, 0.0), (ticks[0].DirX, ticks[0].DirY));
        Assert.Equal((0.0, 1.0), (ticks[1].DirX, ticks[1].DirY));
    }

    [Fact]
    public void Tick_directions_are_unit_vectors()
    {
        var ticks = PolylineTicks.Along([(0, 0), (30, 40), (30, 140), (-20, 90)], spacing: 17, firstOffset: 5);

        Assert.NotEmpty(ticks);
        Assert.All(ticks, t => Assert.Equal(1.0, Math.Sqrt(t.DirX * t.DirX + t.DirY * t.DirY), precision: 9));
    }

    [Fact]
    public void Ticks_are_spaced_by_path_length_not_by_point_count()
    {
        var ticks = PolylineTicks.Along([(0, 0), (1, 0), (2, 0), (3, 0), (100, 0)], spacing: 25, firstOffset: 0);

        Assert.Equal([0.0, 25.0, 50.0, 75.0, 100.0], ticks.Select(t => Math.Round(t.X, 9)));
    }

    [Fact]
    public void Zero_length_segments_are_skipped()
    {
        var ticks = PolylineTicks.Along([(0, 0), (0, 0), (10, 0), (10, 0)], spacing: 5, firstOffset: 0);

        Assert.Equal([0.0, 5.0, 10.0], ticks.Select(t => t.X));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    public void An_unusable_spacing_gives_no_ticks(double spacing) =>
        Assert.Empty(PolylineTicks.Along([(0, 0), (10, 0)], spacing, 0));

    [Fact]
    public void Too_few_points_give_no_ticks()
    {
        Assert.Empty(PolylineTicks.Along([], 5, 0));
        Assert.Empty(PolylineTicks.Along([(1, 1)], 5, 0));
    }

    [Fact]
    public void A_negative_first_offset_starts_at_the_beginning() =>
        Assert.Equal(0.0, PolylineTicks.Along([(0, 0), (10, 0)], 5, -3)[0].X);

    // ---- label placement ---------------------------------------------------------------------------------------

    private static readonly Rect Bounds = new(0, 0, 400, 300);
    private static readonly Size Label = new(80, 20);

    [Fact]
    public void A_label_goes_to_the_right_when_there_is_room()
    {
        var rect = LabelPlacement.Choose(new Point(100, 100), Label, [], Bounds);

        Assert.Equal(new Rect(111, 90, 80, 20), rect);
    }

    [Fact]
    public void A_label_goes_to_the_left_near_the_right_edge()
    {
        var rect = LabelPlacement.Choose(new Point(380, 100), Label, [], Bounds);

        Assert.Equal(new Rect(289, 90, 80, 20), rect);
    }

    [Fact]
    public void A_label_avoids_another_label()
    {
        var other = new Rect(111, 90, 80, 20); // exactly where the right-hand label would go

        var rect = LabelPlacement.Choose(new Point(100, 100), Label, [other], Bounds);

        Assert.False(rect.Intersects(other));
        Assert.Equal(new Rect(9, 90, 80, 20), rect); // the left-hand position
    }

    [Fact]
    public void A_label_goes_above_when_right_and_left_are_blocked()
    {
        // A thin band across the marker's row blocks the right and left positions but not the one above.
        var band = new Rect(0, 192, 400, 16);

        var rect = LabelPlacement.Choose(new Point(200, 200), Label, [band], Bounds);

        Assert.Equal(new Rect(160, 169, 80, 20), rect);
    }

    [Fact]
    public void A_label_goes_below_when_right_left_and_above_are_blocked()
    {
        var blocked = new[] { new Rect(0, 160, 400, 48) }; // covers the row and the space above it

        var rect = LabelPlacement.Choose(new Point(200, 200), Label, blocked, Bounds);

        Assert.Equal(new Rect(160, 211, 80, 20), rect);
    }

    [Theory]
    [InlineData(5.0, 5.0)]
    [InlineData(395.0, 5.0)]
    [InlineData(5.0, 295.0)]
    [InlineData(395.0, 295.0)]
    [InlineData(200.0, 150.0)]
    public void A_label_always_stays_inside_the_bounds(double x, double y)
    {
        var rect = LabelPlacement.Choose(new Point(x, y), Label, [], Bounds);

        Assert.True(rect.X >= 0 && rect.Y >= 0 && rect.Right <= 400 && rect.Bottom <= 300, rect.ToString());
    }

    [Fact]
    public void If_every_position_is_blocked_the_label_still_lands_inside_the_bounds()
    {
        var everything = new[] { new Rect(-1000, -1000, 5000, 5000) };

        var rect = LabelPlacement.Choose(new Point(200, 150), Label, everything, Bounds);

        Assert.True(rect.X >= 0 && rect.Right <= 400 && rect.Y >= 0 && rect.Bottom <= 300);
    }

    [Fact]
    public void A_label_larger_than_the_bounds_is_clamped_without_throwing()
    {
        var rect = LabelPlacement.Choose(new Point(10, 10), new Size(900, 900), [], Bounds);

        Assert.Equal(0, rect.X);
        Assert.Equal(0, rect.Y);
    }
}
