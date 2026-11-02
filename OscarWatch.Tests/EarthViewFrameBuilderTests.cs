using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;

namespace OscarWatch.Tests;

public sealed class EarthViewFrameBuilderTests
{
    private static readonly DateTime Utc = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

    private static GroundStation Home(double lat = 51.5, double lon = -0.1, string callsign = "G4DPZ", string name = "Home") =>
        new() { LatitudeDeg = lat, LongitudeDeg = lon, Callsign = callsign, DisplayName = name };

    private static SatelliteTrackState State(
        double lat = 0, double lon = 0, double altitudeKm = 800,
        LookAngles? look = null, bool sunlit = true,
        IReadOnlyList<GeoCoordinate>? track = null) => new()
    {
        Name = "TEST-1",
        NoradId = "99999",
        Subpoint = new GeoCoordinate(lat, lon, altitudeKm),
        AltitudeKm = altitudeKm,
        LookAngles = look,
        IsSunlit = sunlit,
        GroundTrack = track ?? []
    };

    /// <summary>A station this many degrees of arc from the subpoint, due east.</summary>
    private static GroundStation HomeAtDistance(SatelliteTrackState state, double distanceDeg)
    {
        var (lat, lon) = GlobeViewMath.Destination(
            state.Subpoint.LatitudeDeg, state.Subpoint.LongitudeDeg, distanceDeg, bearingDeg: 90);
        return Home(lat, lon);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void An_unusable_altitude_gives_no_frame(double altitudeKm)
    {
        var unusable = new SatelliteTrackState
        {
            Name = "TEST-1",
            NoradId = "99999",
            Subpoint = new GeoCoordinate(0, 0, 800),
            AltitudeKm = altitudeKm
        };

        Assert.Null(EarthViewFrameBuilder.Build(unusable, Home(), 5, Utc));
    }

    [Fact]
    public void A_non_finite_subpoint_gives_no_frame()
    {
        var state = new SatelliteTrackState
        {
            Name = "X",
            NoradId = "1",
            Subpoint = new GeoCoordinate(double.NaN, 0, 800),
            AltitudeKm = 800
        };

        Assert.Null(EarthViewFrameBuilder.Build(state, Home(), 5, Utc));
    }

    [Fact]
    public void Camera_uses_the_subpoint_and_the_tracked_altitude()
    {
        var frame = EarthViewFrameBuilder.Build(State(lat: 12.5, lon: -140.25, altitudeKm: 412.3), Home(), 5, Utc);

        Assert.NotNull(frame);
        Assert.Equal(new GlobeCamera(12.5, -140.25, 412.3), frame!.Camera);
    }

    [Fact]
    public void Station_at_the_subpoint_is_usable()
    {
        var state = State();
        var frame = EarthViewFrameBuilder.Build(state, Home(0, 0), 5, Utc);

        Assert.Equal(EarthViewSituation.Usable, frame!.HomeSituation);
    }

    [Fact]
    public void A_station_under_a_400_km_satellite_is_usable_with_a_20_degree_minimum()
    {
        // Review finding: this used to give a zero radius, hiding the ring and reporting "below minimum".
        var state = State(altitudeKm: 400);
        var frame = EarthViewFrameBuilder.Build(state, Home(0, 0), 20, Utc)!;

        Assert.InRange(frame.MinimumElevationRadiusDeg, 7.8, 8.0);
        Assert.Equal(EarthViewSituation.Usable, frame.HomeSituation);

        // Just inside and just outside the 7.9 degree ring.
        Assert.Equal(EarthViewSituation.Usable,
            EarthViewFrameBuilder.Build(state, HomeAtDistance(state, 7.7), 20, Utc)!.HomeSituation);
        Assert.Equal(EarthViewSituation.AboveHorizon,
            EarthViewFrameBuilder.Build(state, HomeAtDistance(state, 8.2), 20, Utc)!.HomeSituation);
    }

    [Fact]
    public void Station_between_the_ring_and_the_horizon_is_above_the_horizon_only()
    {
        var state = State(altitudeKm: 800);
        var horizon = GlobeViewMath.FootprintRadiusDeg(800, 0);
        var ring = GlobeViewMath.FootprintRadiusDeg(800, 10);
        Assert.True(ring < horizon);

        var frame = EarthViewFrameBuilder.Build(state, HomeAtDistance(state, (ring + horizon) / 2), 10, Utc);

        Assert.Equal(EarthViewSituation.AboveHorizon, frame!.HomeSituation);
    }

    [Fact]
    public void Station_inside_the_ring_is_usable()
    {
        var state = State(altitudeKm: 800);
        var ring = GlobeViewMath.FootprintRadiusDeg(800, 10);

        var frame = EarthViewFrameBuilder.Build(state, HomeAtDistance(state, ring * 0.5), 10, Utc);

        Assert.Equal(EarthViewSituation.Usable, frame!.HomeSituation);
    }

    [Fact]
    public void Station_beyond_the_horizon_is_below_it()
    {
        var state = State(altitudeKm: 800);
        var horizon = GlobeViewMath.FootprintRadiusDeg(800, 0);

        var frame = EarthViewFrameBuilder.Build(state, HomeAtDistance(state, horizon + 3), 10, Utc);

        Assert.Equal(EarthViewSituation.BelowHorizon, frame!.HomeSituation);
    }

    [Fact]
    public void Ring_radius_follows_the_minimum_elevation()
    {
        var state = State(altitudeKm: 800);

        var at0 = EarthViewFrameBuilder.Build(state, Home(), 0, Utc)!;
        var at10 = EarthViewFrameBuilder.Build(state, Home(), 10, Utc)!;

        Assert.Equal(GlobeViewMath.FootprintRadiusDeg(800, 0), at0.MinimumElevationRadiusDeg, precision: 9);
        Assert.Equal(GlobeViewMath.FootprintRadiusDeg(800, 10), at10.MinimumElevationRadiusDeg, precision: 9);
        Assert.True(at10.MinimumElevationRadiusDeg < at0.MinimumElevationRadiusDeg);
    }

    [Fact]
    public void Unusable_minimum_elevation_is_clamped()
    {
        var state = State();

        Assert.Equal(0.0, EarthViewFrameBuilder.Build(state, Home(), double.NaN, Utc)!.MinimumElevationDeg);
        Assert.Equal(0.0, EarthViewFrameBuilder.Build(state, Home(), -20, Utc)!.MinimumElevationDeg);
        Assert.Equal(90.0, EarthViewFrameBuilder.Build(state, Home(), 200, Utc)!.MinimumElevationDeg);
    }

    [Fact]
    public void Geostationary_satellite_needs_no_pass_data_and_covers_a_distant_station()
    {
        // About QO-100: fixed over the equator at 25.5 degrees east, 35,786 km up.
        var state = State(lat: 0, lon: 25.5, altitudeKm: 35_786);

        var frame = EarthViewFrameBuilder.Build(state, Home(51.5, -0.1), 5, Utc);

        Assert.NotNull(frame);
        Assert.Equal(EarthViewSituation.Usable, frame!.HomeSituation);
        Assert.Empty(frame.GroundTrack);
    }

    [Fact]
    public void Home_station_marker_uses_the_callsign_then_the_name_then_nothing()
    {
        var state = State();

        Assert.Equal("G4DPZ", EarthViewFrameBuilder.Build(state, Home(callsign: " G4DPZ ", name: "Shack"), 5, Utc)!.Stations[0].Label);
        Assert.Equal("Shack", EarthViewFrameBuilder.Build(state, Home(callsign: "", name: " Shack "), 5, Utc)!.Stations[0].Label);
        Assert.Equal("", EarthViewFrameBuilder.Build(state, Home(callsign: " ", name: ""), 5, Utc)!.Stations[0].Label);
    }

    [Fact]
    public void Home_station_marker_carries_its_position_and_is_the_only_station()
    {
        var frame = EarthViewFrameBuilder.Build(State(), Home(40.7, -74.0), 5, Utc)!;

        var station = Assert.Single(frame.Stations);
        Assert.True(station.IsHome);
        Assert.Equal(40.7, station.LatitudeDeg);
        Assert.Equal(-74.0, station.LongitudeDeg);
    }

    [Fact]
    public void Subsolar_point_comes_from_the_display_time()
    {
        var frame = EarthViewFrameBuilder.Build(State(), Home(), 5, Utc)!;

        Assert.Equal(DayNightTerminator.GetSubsolarPoint(Utc), frame.Subsolar);
        Assert.Equal(Utc, frame.DisplayUtc);
    }

    [Fact]
    public void Sunlit_look_angles_and_track_pass_through()
    {
        var look = new LookAngles(120, 33, 1500);
        var track = new List<GeoCoordinate> { new(0, 0), new(0, 1) };

        var frame = EarthViewFrameBuilder.Build(State(look: look, sunlit: false, track: track), Home(), 5, Utc)!;

        Assert.False(frame.IsSunlit);
        Assert.Same(look, frame.LookFromHome);
        Assert.Same(track, frame.GroundTrack);
        Assert.Equal("99999", frame.NoradId);
        Assert.Equal("TEST-1", frame.SatelliteName);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => EarthViewFrameBuilder.Build(null!, Home(), 5, Utc));
        Assert.Throws<ArgumentNullException>(() => EarthViewFrameBuilder.Build(State(), null!, 5, Utc));
    }
}
