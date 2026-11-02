using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;

namespace OscarWatch.Tests;

public sealed class EarthViewFrameTests
{
    private static readonly IReadOnlyList<GeoCoordinate> Track = [new(0, 0), new(0, 1)];
    private static readonly IReadOnlyList<EarthViewStation> Stations = [new("G4DPZ", 51.5, -0.1, true)];
    private static readonly DateTime T0 = new(2026, 6, 21, 12, 0, 10, DateTimeKind.Utc);

    private static EarthViewFrame Frame() => new(
        NoradId: "25544",
        SatelliteName: "ISS",
        Camera: new GlobeCamera(10, 20, 420),
        DisplayUtc: T0,
        GroundTrack: Track,
        MinimumElevationDeg: 5,
        MinimumElevationRadiusDeg: 18,
        Stations: Stations,
        Subsolar: new GeoCoordinate(23, 0),
        IsSunlit: true,
        LookFromHome: new LookAngles(100, 30, 900),
        HomeSituation: EarthViewSituation.Usable);

    [Fact]
    public void First_frame_always_needs_drawing()
    {
        Assert.True(Frame().NeedsRedraw(null));
        Assert.True(Frame().IsMaterialChange(null));
    }

    [Fact]
    public void An_identical_frame_does_not_need_drawing()
    {
        var frame = Frame();

        Assert.False(frame.NeedsRedraw(frame with { }));
        Assert.False(frame.IsMaterialChange(frame with { }));
    }

    [Fact]
    public void Changing_the_minimum_elevation_changes_the_spoken_summary()
    {
        // Review finding: on QO-100 the elevation does not move, so only the setting changes.
        var frame = Frame();

        Assert.True((frame with { MinimumElevationDeg = 10 }).IsMaterialChange(frame));
    }

    [Fact]
    public void Movement_below_the_tolerance_is_not_redrawn_but_above_it_is()
    {
        var frame = Frame();

        Assert.False((frame with { Camera = frame.Camera with { SubpointLatitudeDeg = 10.001 } }).NeedsRedraw(frame));
        Assert.False((frame with { Camera = frame.Camera with { SubpointLongitudeDeg = 20.001 } }).NeedsRedraw(frame));
        Assert.True((frame with { Camera = frame.Camera with { SubpointLatitudeDeg = 10.01 } }).NeedsRedraw(frame));
        Assert.True((frame with { Camera = frame.Camera with { SubpointLongitudeDeg = 20.01 } }).NeedsRedraw(frame));
    }

    [Fact]
    public void Tiny_longitude_movement_across_the_antimeridian_is_not_a_big_jump()
    {
        var east = Frame() with { Camera = new GlobeCamera(0, 179.9995, 420) };
        var west = Frame() with { Camera = new GlobeCamera(0, -179.9995, 420) };

        // 0.001 degrees apart, not 359.999.
        Assert.False(west.NeedsRedraw(east));
    }

    [Fact]
    public void Altitude_changes_matter_only_above_the_tolerance()
    {
        var frame = Frame();

        Assert.False((frame with { Camera = frame.Camera with { AltitudeKm = 420.3 } }).NeedsRedraw(frame));
        Assert.True((frame with { Camera = frame.Camera with { AltitudeKm = 421.0 } }).NeedsRedraw(frame));
    }

    [Fact]
    public void A_new_ground_track_list_needs_drawing_but_the_same_list_does_not()
    {
        var frame = Frame();

        Assert.False((frame with { GroundTrack = Track }).NeedsRedraw(frame));
        Assert.True((frame with { GroundTrack = [new GeoCoordinate(0, 0), new GeoCoordinate(0, 1)] }).NeedsRedraw(frame));
    }

    [Fact]
    public void A_new_minute_needs_drawing_but_seconds_within_a_minute_do_not()
    {
        var frame = Frame();

        Assert.False((frame with { DisplayUtc = T0.AddSeconds(30) }).NeedsRedraw(frame));
        Assert.True((frame with { DisplayUtc = T0.AddSeconds(55) }).NeedsRedraw(frame));
    }

    [Fact]
    public void Changes_that_alter_the_picture_need_drawing()
    {
        var frame = Frame();

        Assert.True((frame with { NoradId = "43700" }).NeedsRedraw(frame));
        Assert.True((frame with { IsSunlit = false }).NeedsRedraw(frame));
        Assert.True((frame with { MinimumElevationDeg = 10 }).NeedsRedraw(frame));
        Assert.True((frame with { HomeSituation = EarthViewSituation.AboveHorizon }).NeedsRedraw(frame));
        Assert.True((frame with { Stations = [new EarthViewStation("G4DPZ", 52.0, -0.1, true)] }).NeedsRedraw(frame));
        Assert.True((frame with { Stations = [] }).NeedsRedraw(frame));
    }

    [Fact]
    public void Equal_stations_in_a_different_list_do_not_need_drawing()
    {
        var frame = Frame();

        Assert.False((frame with { Stations = [new EarthViewStation("G4DPZ", 51.5, -0.1, true)] }).NeedsRedraw(frame));
    }

    [Fact]
    public void Look_angle_changes_alone_do_not_redraw()
    {
        // The picture does not show az/el, only the text does.
        var frame = Frame();

        Assert.False((frame with { LookFromHome = new LookAngles(200, 55, 700) }).NeedsRedraw(frame));
    }

    [Fact]
    public void Summary_changes_only_when_elevation_moves_a_full_step()
    {
        var frame = Frame();

        Assert.False((frame with { LookFromHome = new LookAngles(101, 30.5, 900) }).IsMaterialChange(frame));
        Assert.True((frame with { LookFromHome = new LookAngles(100, 31.0, 900) }).IsMaterialChange(frame));
        Assert.True((frame with { LookFromHome = new LookAngles(100, 29.0, 900) }).IsMaterialChange(frame));
    }

    [Fact]
    public void Summary_changes_for_a_new_satellite_a_new_situation_or_when_look_angles_appear_or_go()
    {
        var frame = Frame();

        Assert.True((frame with { NoradId = "43700" }).IsMaterialChange(frame));
        Assert.True((frame with { HomeSituation = EarthViewSituation.BelowHorizon }).IsMaterialChange(frame));
        Assert.True((frame with { LookFromHome = null }).IsMaterialChange(frame));
        Assert.True(frame.IsMaterialChange(frame with { LookFromHome = null }));
    }

    [Fact]
    public void Summary_ignores_movement_and_time()
    {
        var frame = Frame();
        var moved = frame with
        {
            Camera = new GlobeCamera(11, 21, 421),
            DisplayUtc = T0.AddMinutes(3),
            IsSunlit = false
        };

        Assert.False(moved.IsMaterialChange(frame));
    }
}
