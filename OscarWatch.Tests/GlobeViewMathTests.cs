using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;

namespace OscarWatch.Tests;

public sealed class GlobeViewMathTests
{
    [Theory]
    [InlineData(400.0)]
    [InlineData(35_786.0)]
    public void At_zero_elevation_the_footprint_is_the_horizon(double altitudeKm) =>
        Assert.Equal(
            FootprintGeometry.HorizonRadiusDeg(altitudeKm),
            GlobeViewMath.FootprintRadiusDeg(altitudeKm, 0.0),
            precision: 9);

    // Worked from acos(R / (R + h) * cos(e)) - e with R = 6371 km. They agree with the review: about 7.9
    // degrees for 400 km at 20 degrees, and about 15.4 at 5 degrees (the old code gave 0 and 14.8).
    [Theory]
    [InlineData(400.0, 20.0, 7.85, 0.02)]
    [InlineData(400.0, 5.0, 15.39, 0.02)]
    [InlineData(35_786.0, 10.0, 71.5, 0.2)]
    public void Footprint_radius_matches_hand_worked_values(
        double altitudeKm, double minimumElevationDeg, double expectedDeg, double toleranceDeg) =>
        Assert.InRange(
            GlobeViewMath.FootprintRadiusDeg(altitudeKm, minimumElevationDeg),
            expectedDeg - toleranceDeg,
            expectedDeg + toleranceDeg);

    /// <summary>
    /// The elevation of a satellite seen from a point on the ground at a given central angle, worked out a
    /// different way (plane geometry, no inverse cosine): tan(el) = (cos(c) - R / (R + h)) / sin(c).
    /// </summary>
    private static double ElevationAtCentralAngleDeg(double altitudeKm, double centralAngleDeg)
    {
        const double r = 6371.0;
        var c = centralAngleDeg * Math.PI / 180.0;
        return Math.Atan2(Math.Cos(c) - r / (r + altitudeKm), Math.Sin(c)) * 180.0 / Math.PI;
    }

    [Theory]
    [InlineData(400.0, 0.0)]
    [InlineData(400.0, 5.0)]
    [InlineData(400.0, 20.0)]
    [InlineData(400.0, 60.0)]
    [InlineData(800.0, 10.0)]
    [InlineData(1_500.0, 45.0)]
    [InlineData(35_786.0, 5.0)]
    [InlineData(35_786.0, 10.0)]
    public void The_satellite_is_exactly_at_the_minimum_elevation_on_the_ring(double altitudeKm, double minimumElevationDeg) =>
        Assert.Equal(
            minimumElevationDeg,
            ElevationAtCentralAngleDeg(altitudeKm, GlobeViewMath.FootprintRadiusDeg(altitudeKm, minimumElevationDeg)),
            precision: 6);

    [Theory]
    [InlineData(400.0, 90.0)]
    [InlineData(400.0, 120.0)]
    [InlineData(400.0, double.NaN)]
    public void No_ring_at_or_above_ninety_degrees_or_for_a_non_finite_elevation(double altitudeKm, double minimumElevationDeg) =>
        Assert.Equal(0.0, GlobeViewMath.FootprintRadiusDeg(altitudeKm, minimumElevationDeg));

    [Fact]
    public void A_negative_minimum_elevation_is_treated_as_the_horizon() =>
        Assert.Equal(
            GlobeViewMath.FootprintRadiusDeg(800.0, 0.0),
            GlobeViewMath.FootprintRadiusDeg(800.0, -10.0));

    [Fact]
    public void Higher_minimum_elevation_gives_a_smaller_footprint()
    {
        var horizon = GlobeViewMath.FootprintRadiusDeg(800.0, 0.0);
        var usable = GlobeViewMath.FootprintRadiusDeg(800.0, 10.0);

        Assert.True(usable > 0 && usable < horizon, $"horizon {horizon}, usable {usable}");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void Footprint_radius_is_zero_for_unusable_altitude(double altitudeKm) =>
        Assert.Equal(0.0, GlobeViewMath.FootprintRadiusDeg(altitudeKm, 0.0));

    [Theory]
    [InlineData(0.0, 0.0, 12.0)]
    [InlineData(51.5, -0.1, 20.0)]
    [InlineData(-45.0, 170.0, 5.0)]   // ring crosses the antimeridian
    [InlineData(80.0, 35.0, 25.0)]    // ring encloses the pole
    public void Every_ring_point_lies_at_the_stated_distance_from_the_subpoint(
        double lat, double lon, double radiusDeg)
    {
        var subpoint = new GeoCoordinate(lat, lon);

        var ring = GlobeViewMath.FootprintRing(subpoint, radiusDeg);

        Assert.Equal(180, ring.Count);
        foreach (var p in ring)
        {
            var distance = GlobeViewMath.CentralAngleDeg(lat, lon, p.LatitudeDeg, p.LongitudeDeg);
            Assert.Equal(radiusDeg, distance, precision: 6);
            Assert.InRange(p.LongitudeDeg, -180.0, 180.0);
        }
    }

    [Fact]
    public void Ring_points_respect_the_requested_count()
    {
        Assert.Equal(36, GlobeViewMath.FootprintRing(new GeoCoordinate(0, 0), 10.0, points: 36).Count);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    [InlineData(double.NaN)]
    public void Ring_is_empty_for_an_unusable_radius(double radiusDeg) =>
        Assert.Empty(GlobeViewMath.FootprintRing(new GeoCoordinate(0, 0), radiusDeg));

    [Fact]
    public void Station_inside_the_footprint_is_detected()
    {
        var subpoint = new GeoCoordinate(51.0, 0.0);

        Assert.True(GlobeViewMath.IsInsideFootprint(subpoint, 52.0, 1.0, radiusDeg: 10.0));
        Assert.True(GlobeViewMath.IsInsideFootprint(subpoint, 51.0, 0.0, radiusDeg: 0.001));
    }

    [Fact]
    public void Station_outside_the_footprint_is_rejected()
    {
        var subpoint = new GeoCoordinate(51.0, 0.0);

        Assert.False(GlobeViewMath.IsInsideFootprint(subpoint, 40.0, 40.0, radiusDeg: 10.0));
        Assert.False(GlobeViewMath.IsInsideFootprint(subpoint, -51.0, 180.0, radiusDeg: 10.0));
    }

    [Fact]
    public void Footprint_test_works_near_a_pole_where_longitudes_converge()
    {
        var subpoint = new GeoCoordinate(88.0, 0.0);

        // 2 degrees from the pole on the far side is 4 degrees from the subpoint over the pole.
        Assert.True(GlobeViewMath.IsInsideFootprint(subpoint, 88.0, 180.0, radiusDeg: 5.0));
        Assert.False(GlobeViewMath.IsInsideFootprint(subpoint, 80.0, 180.0, radiusDeg: 5.0));
    }

    [Theory]
    [InlineData(0.0, double.NaN)]
    [InlineData(double.NaN, 0.0)]
    public void Station_with_unusable_coordinates_is_outside(double lat, double lon) =>
        Assert.False(GlobeViewMath.IsInsideFootprint(new GeoCoordinate(0, 0), lat, lon, radiusDeg: 90.0));

    [Fact]
    public void Track_entirely_in_view_is_one_run()
    {
        var camera = new GlobeCamera(0.0, 0.0, 800.0);
        var track = Enumerable.Range(0, 10)
            .Select(i => new GeoCoordinate(0.0, i * 0.5))
            .ToList();

        var runs = GlobeViewMath.ProjectTrack(camera, track);

        var run = Assert.Single(runs);
        Assert.Equal(10, run.Count);
    }

    [Fact]
    public void Track_points_beyond_the_limb_are_omitted_but_the_run_reaches_the_limb()
    {
        // Horizon radius at 800 km is about 28 degrees, so points from 40 degrees east are hidden.
        var camera = new GlobeCamera(0.0, 0.0, 800.0);
        var track = new List<GeoCoordinate>
        {
            new(0, 0), new(0, 10), new(0, 20), new(0, 40), new(0, 50)
        };

        var runs = GlobeViewMath.ProjectTrack(camera, track);

        // Three visible samples plus the point where the line to the 40 degree sample meets the limb.
        var run = Assert.Single(runs);
        Assert.Equal(4, run.Count);
        var limb = GlobeProjection.LimbRadius(800);
        Assert.Equal(limb, Math.Sqrt(run[^1].X * run[^1].X + run[^1].Y * run[^1].Y), precision: 6);
    }

    [Theory]
    [InlineData(400.0)]
    [InlineData(800.0)]
    [InlineData(35_786.0)]
    public void A_run_leaving_and_entering_the_view_ends_exactly_on_the_limb(double altitudeKm)
    {
        // Review finding: runs used to stop at the last visible sample, short of the edge.
        var camera = new GlobeCamera(10.0, 20.0, altitudeKm);
        var horizon = GlobeViewMath.FootprintRadiusDeg(altitudeKm, 0);
        var limb = GlobeProjection.LimbRadius(altitudeKm);
        double Radius((double X, double Y) q) => Math.Sqrt(q.X * q.X + q.Y * q.Y);

        // East at 70% of the horizon, then 20% beyond it, then back again.
        (double lat, double lon) At(double fraction) => GlobeViewMath.Destination(10, 20, horizon * fraction, 90);
        var track = new[] { 0.0, 0.7, 1.2, 1.4, 0.7, 0.0 }
            .Select(f => { var (la, lo) = At(f); return new GeoCoordinate(la, lo); })
            .ToList();

        var runs = GlobeViewMath.ProjectTrack(camera, track);

        Assert.Equal(2, runs.Count);
        Assert.Equal(limb, Radius(runs[0][^1]), precision: 6);  // leaving
        Assert.Equal(limb, Radius(runs[1][0]), precision: 6);   // coming back
        Assert.Equal(3, runs[0].Count);                          // two samples and the crossing
        Assert.Equal(3, runs[1].Count);                          // the crossing and two samples
    }

    [Fact]
    public void Near_a_pole_the_limb_crossing_follows_the_great_circle_not_a_straight_line_in_lat_and_lon()
    {
        // Review finding. From a camera at (70 N, 0) the pole is 20 degrees away and the 400 km horizon is
        // 19.8 degrees, so the track (89 N, 0) to (87 N, 180) leaves the view just short of the pole. The
        // real path runs up the 0/180 meridian, which is the vertical centre line of a north-up picture, so
        // the crossing must have x = 0. Interpolating latitude and longitude separately bent it about
        // 0.033 radians sideways, which is roughly 29 pixels on a 600 pixel globe.
        var camera = new GlobeCamera(70.0, 0.0, 400.0);
        var limb = GlobeProjection.LimbRadius(400);

        var run = Assert.Single(GlobeViewMath.ProjectTrack(camera, [new(89, 0), new(87, 180)]));

        Assert.Equal(2, run.Count);
        var end = run[^1];
        Assert.Equal(0.0, end.X, precision: 4);
        Assert.Equal(limb, Math.Sqrt(end.X * end.X + end.Y * end.Y), precision: 6);
    }

    [Fact]
    public void A_crossing_lies_on_the_short_arc_between_its_two_samples()
    {
        // The same idea away from the poles and across the antimeridian: the crossing is on the great circle
        // through both samples, so its distance from each is the sum of the arcs.
        var camera = new GlobeCamera(0.0, 179.0, 800.0);
        var horizon = GlobeViewMath.FootprintRadiusDeg(800, 0);
        var (inLat, inLon) = GlobeViewMath.Destination(0, 179, horizon * 0.8, 90);
        var (outLat, outLon) = GlobeViewMath.Destination(0, 179, horizon * 1.3, 90);

        var run = Assert.Single(GlobeViewMath.ProjectTrack(camera, [new(0, 179), new(inLat, inLon), new(outLat, outLon)]));

        Assert.Equal(3, run.Count);
        var limb = GlobeProjection.LimbRadius(800);
        Assert.Equal(limb, Math.Sqrt(run[^1].X * run[^1].X + run[^1].Y * run[^1].Y), precision: 6);
        Assert.Equal(0.0, run[^1].Y, precision: 6); // due east along the equator stays on the horizontal line
    }

    [Fact]
    public void A_nan_gap_is_not_bridged_to_the_limb()
    {
        var camera = new GlobeCamera(0.0, 0.0, 800.0);
        var track = new List<GeoCoordinate>
        {
            new(0, 0), new(0, 10),
            new(double.NaN, double.NaN),
            new(0, 60), new(0, 12), new(0, 5)
        };

        var runs = GlobeViewMath.ProjectTrack(camera, track);

        // First run: just the two samples before the gap, no crossing added at the NaN.
        // Second run: the hidden sample after the gap is not a neighbour of the visible ones before it.
        Assert.Equal(2, runs.Count);
        Assert.Equal(2, runs[0].Count);
        Assert.Equal(3, runs[1].Count); // crossing from the hidden 60 degree sample, then 12 and 5
    }

    [Fact]
    public void Track_that_leaves_and_returns_is_split_into_two_runs()
    {
        var camera = new GlobeCamera(0.0, 0.0, 800.0);
        var track = new List<GeoCoordinate>
        {
            new(0, -10), new(0, 0), new(0, 10),   // in view
            new(0, 60), new(0, 90),               // hidden
            new(0, 12), new(0, 5), new(0, -5)     // back in view
        };

        var runs = GlobeViewMath.ProjectTrack(camera, track);

        // Each run also gets the point where it crosses the limb. The line from the hidden 90 degree
        // sample to the visible 12 degree one meets the limb, so the second run starts there.
        Assert.Equal(2, runs.Count);
        Assert.Equal(4, runs[0].Count);
        Assert.Equal(4, runs[1].Count);
    }

    [Fact]
    public void Nan_gap_marker_splits_a_run_even_when_both_sides_are_visible()
    {
        var camera = new GlobeCamera(0.0, 0.0, 800.0);
        var track = new List<GeoCoordinate>
        {
            new(0, 0), new(0, 1), new(0, 2),
            new(double.NaN, double.NaN),
            new(0, 3), new(0, 4)
        };

        var runs = GlobeViewMath.ProjectTrack(camera, track);

        Assert.Equal(2, runs.Count);
    }

    [Fact]
    public void A_lone_visible_sample_between_hidden_ones_is_a_short_line_to_the_limb_each_side()
    {
        var camera = new GlobeCamera(0.0, 0.0, 800.0);
        var track = new List<GeoCoordinate> { new(0, 0), new(0, 90), new(0, 1), new(0, 90) };

        // (0,0) leaves towards the limb: the sample and the crossing. (0,1) enters from the limb and
        // leaves again: the entry crossing, the sample and the exit crossing.
        var runs = GlobeViewMath.ProjectTrack(camera, track);

        Assert.Equal(2, runs.Count);
        Assert.Equal(2, runs[0].Count);
        Assert.Equal(3, runs[1].Count);
    }

    [Fact]
    public void A_lone_visible_sample_with_gaps_each_side_cannot_form_a_line()
    {
        var camera = new GlobeCamera(0.0, 0.0, 800.0);
        var nan = new GeoCoordinate(double.NaN, double.NaN);
        var track = new List<GeoCoordinate> { new(0, 40), nan, new(0, 0), nan, new(0, 40) };

        Assert.Empty(GlobeViewMath.ProjectTrack(camera, track));
    }

    [Fact]
    public void Track_across_the_antimeridian_stays_continuous()
    {
        var camera = new GlobeCamera(0.0, 179.0, 800.0);
        var track = new List<GeoCoordinate>
        {
            new(0, 178.0), new(0, 179.0), new(0, 180.0), new(0, -179.0), new(0, -178.0)
        };

        var run = Assert.Single(GlobeViewMath.ProjectTrack(camera, track));

        Assert.Equal(5, run.Count);
        for (var i = 1; i < run.Count; i++)
            Assert.True(run[i].X > run[i - 1].X, $"x should increase eastwards at index {i}");
    }

    [Fact]
    public void Track_with_an_unusable_camera_projects_nothing()
    {
        var track = new List<GeoCoordinate> { new(0, 0), new(0, 1) };

        Assert.Empty(GlobeViewMath.ProjectTrack(new GlobeCamera(0.0, 0.0, 0.0), track));
    }

    [Fact]
    public void Short_tracks_project_nothing()
    {
        var camera = new GlobeCamera(0.0, 0.0, 800.0);

        Assert.Empty(GlobeViewMath.ProjectTrack(camera, []));
        Assert.Empty(GlobeViewMath.ProjectTrack(camera, [new GeoCoordinate(0, 0)]));
    }

    [Fact]
    public void Ring_at_the_horizon_radius_projects_onto_the_limb_circle()
    {
        var camera = new GlobeCamera(35.0, -20.0, 800.0);
        var subpoint = new GeoCoordinate(camera.SubpointLatitudeDeg, camera.SubpointLongitudeDeg, camera.AltitudeKm);
        var radiusDeg = GlobeViewMath.FootprintRadiusDeg(camera.AltitudeKm, 0.0) - 1e-6;
        var limb = GlobeProjection.LimbRadius(camera.AltitudeKm);

        foreach (var p in GlobeViewMath.FootprintRing(subpoint, radiusDeg))
        {
            var projected = GlobeProjection.Project(camera, p.LatitudeDeg, p.LongitudeDeg);
            Assert.True(projected.Visible);
            Assert.Equal(limb, Math.Sqrt(projected.X * projected.X + projected.Y * projected.Y), precision: 5);
        }
    }
}
