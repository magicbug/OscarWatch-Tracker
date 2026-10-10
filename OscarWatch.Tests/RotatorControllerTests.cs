using OscarWatch.Core.Models;
using OscarWatch.Rotator;

namespace OscarWatch.Tests;

public sealed class RotatorControllerTests
{
    [Fact]
    public void Update_runs_on_worker_thread_and_tracks_satellite()
    {
        var rotator = new RecordingRotatorDriver();
        using var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            BaudRate = 9600,
            Type = RotatorType.YaesuGs232,
            TrackStartElevationDeg = 5
        };

        var target = new SatelliteTrackState
        {
            Name = "ISS",
            NoradId = "25544",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(45, 20, 800, 0)
        };

        controller.UpdateSynchronously(settings, target);

        Assert.Equal(1, rotator.SetPositionCallCount);
        Assert.Equal(45, rotator.LastAzimuthDeg);
        Assert.Equal(20, rotator.LastElevationDeg);
        Assert.False(controller.GetPositionStatus().IsParked);

        var status = controller.GetPositionStatus();
        Assert.True(status.IsConnected);
        Assert.Equal(45, status.AzimuthDeg);
        Assert.Equal(20, status.ElevationDeg);
    }

    [Fact]
    public void Update_parks_when_satellite_drops_below_track_start()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            TrackStartElevationDeg = 5,
            ParkAzimuthDeg = 180,
            ParkElevationDeg = 0,
            ParkAfterPass = true
        };

        controller.UpdateSynchronously(settings, TrackTarget("25544", 45, 3));

        Assert.Equal(180, rotator.LastAzimuthDeg);
        Assert.Equal(0, rotator.LastElevationDeg);
        Assert.True(controller.GetPositionStatus().IsParked);
    }

    [Fact]
    public void Update_does_not_park_on_single_tick_below_track_start_after_tracking()
    {
        var rotator = new RecordingRotatorDriver();
        using var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            TrackStartElevationDeg = -3,
            ParkAzimuthDeg = 180,
            ParkElevationDeg = 0,
            ParkAfterPass = true,
            MovementThresholdDeg = 0
        };

        controller.UpdateSynchronously(settings, TrackTarget("25544", 45, 5));
        Assert.False(controller.GetPositionStatus().IsParked);
        Assert.Equal(45, rotator.LastAzimuthDeg);

        var callsAfterTrack = rotator.SetPositionCallCount;
        controller.UpdateSynchronously(settings, TrackTarget("25544", 50, -5));
        Assert.False(controller.GetPositionStatus().IsParked);
        Assert.Equal(callsAfterTrack, rotator.SetPositionCallCount);
        Assert.Equal(45, rotator.LastAzimuthDeg);

        controller.UpdateSynchronously(settings, TrackTarget("25544", 55, 6));
        Assert.False(controller.GetPositionStatus().IsParked);
        Assert.Equal(55, rotator.LastAzimuthDeg);
    }

    [Fact]
    public void Update_does_not_park_on_single_missing_target_tick_after_tracking()
    {
        var rotator = new RecordingRotatorDriver();
        using var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            TrackStartElevationDeg = -3,
            ParkAzimuthDeg = 180,
            ParkElevationDeg = 0,
            ParkAfterPass = true,
            MovementThresholdDeg = 0
        };

        controller.UpdateSynchronously(settings, TrackTarget("25544", 45, 5));
        var callsAfterTrack = rotator.SetPositionCallCount;

        controller.UpdateSynchronously(settings, null);
        Assert.False(controller.GetPositionStatus().IsParked);
        Assert.Equal(callsAfterTrack, rotator.SetPositionCallCount);

        controller.UpdateSynchronously(settings, TrackTarget("25544", 50, 6));
        Assert.False(controller.GetPositionStatus().IsParked);
        Assert.Equal(50, rotator.LastAzimuthDeg);
    }

    [Fact]
    public void Update_parks_after_confirmed_below_track_start_streak()
    {
        var rotator = new RecordingRotatorDriver();
        using var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            TrackStartElevationDeg = -3,
            ParkAzimuthDeg = 180,
            ParkElevationDeg = 0,
            ParkAfterPass = true,
            MovementThresholdDeg = 0
        };

        controller.UpdateSynchronously(settings, TrackTarget("25544", 45, 5));
        Assert.False(controller.GetPositionStatus().IsParked);

        for (var i = 0; i < RotatorController.ParkAfterPassConfirmTicks - 1; i++)
        {
            controller.UpdateSynchronously(settings, TrackTarget("25544", 50, -5));
            Assert.False(controller.GetPositionStatus().IsParked);
        }

        controller.UpdateSynchronously(settings, TrackTarget("25544", 50, -5));
        Assert.True(controller.GetPositionStatus().IsParked);
        Assert.Equal(180, rotator.LastAzimuthDeg);
        Assert.Equal(0, rotator.LastElevationDeg);
    }

    [Fact]
    public void Update_skips_automatic_park_after_pass_when_disabled()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            TrackStartElevationDeg = 5,
            ParkAzimuthDeg = 180,
            ParkElevationDeg = 0,
            ParkAfterPass = false
        };

        controller.UpdateSynchronously(settings, TrackTarget("25544", 45, 3));

        Assert.Equal(0, rotator.SetPositionCallCount);
        Assert.False(controller.GetPositionStatus().IsParked);
    }

    [Fact]
    public void DisconnectAndWait_releases_driver_and_does_not_reconnect()
    {
        var rotator = new RecordingRotatorDriver();
        var createCount = 0;
        var controller = new RotatorController(_ =>
        {
            createCount++;
            return rotator;
        });
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            BaudRate = 9600,
            Type = RotatorType.YaesuGs232,
            TrackStartElevationDeg = 5
        };

        controller.UpdateSynchronously(settings, TrackTarget("25544", 45, 20));
        Assert.Equal(1, createCount);
        Assert.Equal(1, rotator.OpenCallCount);
        Assert.True(controller.GetPositionStatus().IsConnected);

        // DisconnectAndWait completes after TearDown; the same loop iteration then runs
        // RunTrackingIteration — which must not reopen when cached settings were cleared.
        controller.DisconnectAndWait();

        Assert.Equal(1, createCount);
        Assert.Equal(1, rotator.OpenCallCount);
        Assert.Equal(1, rotator.DisposeCallCount);
        Assert.False(controller.GetPositionStatus().IsConnected);

        controller.Dispose();
    }

    [Fact]
    public void Manual_park_still_works_when_park_after_pass_disabled()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            ParkAzimuthDeg = 180,
            ParkElevationDeg = 0,
            ParkAfterPass = false
        };

        controller.Park(settings);
        controller.DrainCommandQueueForTests();

        Assert.Equal(180, rotator.LastAzimuthDeg);
        Assert.True(controller.GetPositionStatus().IsParked);
    }

    [Fact]
    public void Park_command_sends_park_position()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            ParkAzimuthDeg = 180,
            ParkElevationDeg = 0
        };

        controller.Park(settings);
        controller.DrainCommandQueueForTests();

        Assert.Equal(180, rotator.LastAzimuthDeg);
        Assert.Equal(0, rotator.LastElevationDeg);
        Assert.True(controller.GetPositionStatus().IsParked);
    }

    [Fact]
    public void Tracking_applies_azimuth_and_elevation_calibration_offsets()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            TrackStartElevationDeg = 5,
            AzimuthOffsetDeg = 2.5,
            ElevationOffsetDeg = -1.0
        };

        var target = new SatelliteTrackState
        {
            Name = "ISS",
            NoradId = "25544",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(45, 20, 800, 0)
        };

        controller.UpdateSynchronously(settings, target);

        Assert.Equal(47.5, rotator.LastAzimuthDeg);
        Assert.Equal(19, rotator.LastElevationDeg);
    }

    [Fact]
    public void Park_ignores_calibration_offsets()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            ParkAzimuthDeg = 180,
            ParkElevationDeg = 10,
            AzimuthOffsetDeg = -72,
            ElevationOffsetDeg = 2
        };

        controller.Park(settings);
        controller.DrainCommandQueueForTests();

        Assert.Equal(180, rotator.LastAzimuthDeg);
        Assert.Equal(10, rotator.LastElevationDeg);
    }

    [Fact]
    public void Standby_does_not_park_when_park_after_pass_disabled()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            ParkAzimuthDeg = 180,
            ParkElevationDeg = 0,
            ParkAfterPass = false
        };

        controller.SetStandby(true, settings);
        controller.DrainCommandQueueForTests();

        Assert.Equal(0, rotator.SetPositionCallCount);
        Assert.False(controller.GetPositionStatus().IsParked);

        controller.UpdateSynchronously(settings, null);
        Assert.Equal(0, rotator.SetPositionCallCount);
    }

    [Fact]
    public void Standby_survives_disconnect_and_does_not_track()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            ParkAfterPass = false
        };

        controller.SetStandby(true, settings);
        controller.DrainCommandQueueForTests();
        controller.Disconnect();
        controller.DrainCommandQueueForTests();

        controller.UpdateSynchronously(settings, TrackTarget("44909", 120, 30));

        Assert.Equal(0, rotator.SetPositionCallCount);
    }

    [Fact]
    public void Manual_move_applies_calibration_offsets()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            AzimuthOffsetDeg = 1,
            ElevationOffsetDeg = 0.5
        };

        controller.SetStandby(true, settings);
        controller.DrainCommandQueueForTests();
        controller.MoveTo(90, 30, settings);
        controller.DrainCommandQueueForTests();

        Assert.Equal(91, rotator.LastAzimuthDeg);
        Assert.Equal(30.5, rotator.LastElevationDeg);
    }

    [Fact]
    public void Manual_move_during_standby_is_not_re_parked()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            ParkAzimuthDeg = 0,
            ParkElevationDeg = 0
        };

        controller.SetStandby(true, settings);
        controller.DrainCommandQueueForTests();
        var callsAfterPark = rotator.SetPositionCallCount;

        controller.MoveTo(90, 45, settings);
        controller.DrainCommandQueueForTests();
        Assert.Equal(90, rotator.LastAzimuthDeg);
        Assert.Equal(45, rotator.LastElevationDeg);

        controller.UpdateSynchronously(settings, null);
        Assert.Equal(90, rotator.LastAzimuthDeg);
        Assert.Equal(callsAfterPark + 1, rotator.SetPositionCallCount);
    }

    [Fact]
    public void Stop_during_standby_sends_stop_command()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings { Enabled = true, Port = "COM3" };

        controller.SetStandby(true, settings);
        controller.DrainCommandQueueForTests();
        controller.Stop(settings);
        controller.DrainCommandQueueForTests();

        Assert.Equal(1, rotator.StopCallCount);
        Assert.False(controller.GetPositionStatus().IsTrackingHeld);
    }

    [Fact]
    public void Stop_during_tracking_sends_stop_and_holds_tracking()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            TrackStartElevationDeg = 5
        };

        controller.UpdateSynchronously(settings, TrackTarget("25544", 45, 20));
        Assert.Equal(1, rotator.SetPositionCallCount);

        controller.Stop(settings);
        controller.DrainCommandQueueForTests();

        Assert.Equal(1, rotator.StopCallCount);
        Assert.True(controller.GetPositionStatus().IsTrackingHeld);

        controller.UpdateSynchronously(settings, TrackTarget("25544", 90, 25));
        Assert.Equal(1, rotator.SetPositionCallCount);
    }

    [Fact]
    public void ResumeTracking_clears_hold_and_allows_tracking()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            TrackStartElevationDeg = 5
        };

        controller.UpdateSynchronously(settings, TrackTarget("25544", 45, 20));
        controller.Stop(settings);
        controller.DrainCommandQueueForTests();
        Assert.True(controller.GetPositionStatus().IsTrackingHeld);

        controller.ResumeTracking(settings);
        controller.DrainCommandQueueForTests();
        Assert.False(controller.GetPositionStatus().IsTrackingHeld);

        controller.UpdateSynchronously(settings, TrackTarget("25544", 90, 25));
        Assert.Equal(2, rotator.SetPositionCallCount);
    }

    [Fact]
    public void Park_when_already_parked_sends_park_position_again()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            ParkAzimuthDeg = 180,
            ParkElevationDeg = 0
        };

        controller.Park(settings);
        controller.DrainCommandQueueForTests();
        Assert.Equal(1, rotator.SetPositionCallCount);

        controller.Park(settings);
        controller.DrainCommandQueueForTests();

        Assert.Equal(2, rotator.SetPositionCallCount);
        Assert.Equal(180, rotator.LastAzimuthDeg);
        Assert.Equal(0, rotator.LastElevationDeg);
        Assert.True(controller.GetPositionStatus().IsParked);
    }

    [Fact]
    public void Park_after_stop_clears_tracking_hold()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            TrackStartElevationDeg = 5,
            ParkAzimuthDeg = 180,
            ParkElevationDeg = 0
        };

        controller.UpdateSynchronously(settings, TrackTarget("25544", 45, 20));
        controller.Stop(settings);
        controller.DrainCommandQueueForTests();
        Assert.True(controller.GetPositionStatus().IsTrackingHeld);

        controller.Park(settings);
        controller.DrainCommandQueueForTests();

        Assert.False(controller.GetPositionStatus().IsTrackingHeld);
        Assert.Equal(180, rotator.LastAzimuthDeg);
        Assert.True(controller.GetPositionStatus().IsParked);
    }

    [Fact]
    public void Park_during_standby_sends_park_position()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            ParkAzimuthDeg = 180,
            ParkElevationDeg = 10
        };

        controller.SetStandby(true, settings);
        controller.DrainCommandQueueForTests();
        controller.MoveTo(90, 45, settings);
        controller.DrainCommandQueueForTests();
        controller.Park(settings);
        controller.DrainCommandQueueForTests();

        Assert.Equal(180, rotator.LastAzimuthDeg);
        Assert.Equal(10, rotator.LastElevationDeg);
    }

    [Fact]
    public void PublishTarget_is_non_blocking()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings { Enabled = true, Port = "COM3" };
        var target = new SatelliteTrackState
        {
            Name = "TEST",
            NoradId = "1",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(10, 15, 500, 0)
        };

        controller.Update(settings, target);
        Assert.Equal(0, rotator.SetPositionCallCount);

        controller.DrainCommandQueueForTests();
        controller.UpdateSynchronously(settings, target);

        Assert.True(rotator.SetPositionCallCount >= 1);
    }

    [Fact]
    public void Disabled_settings_disconnects_on_worker()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings { Enabled = true, Port = "COM3" };
        var target = new SatelliteTrackState
        {
            Name = "TEST",
            NoradId = "1",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(10, 15, 500, 0)
        };

        controller.UpdateSynchronously(settings, target);
        Assert.True(controller.GetPositionStatus().IsConnected);

        controller.UpdateSynchronously(new RotatorSettings { Enabled = false }, null);
        Assert.False(controller.GetPositionStatus().IsConnected);
    }

    [Fact]
    public void Smart450_uses_extended_azimuth_at_north_wrap()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            AzimuthRange = RotatorAzimuthRange.Deg450,
            SmartAzimuth450 = true,
            TrackStartElevationDeg = 5
        };

        var norad = "25544";
        controller.UpdateSynchronously(settings, TrackTarget(norad, 350, 20));
        Assert.Equal(350, rotator.LastAzimuthDeg);

        controller.UpdateSynchronously(settings, TrackTarget(norad, 10, 20));
        Assert.Equal(370, rotator.LastAzimuthDeg);

        var status = controller.GetPositionStatus();
        Assert.Equal(370, status.CommandedAzimuthDeg);
        Assert.Equal(10, status.CompassAzimuthDeg);
    }

    [Fact]
    public void Smart450_with_negative_azimuth_offset_wraps_in_compass_space()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            AzimuthRange = RotatorAzimuthRange.Deg450,
            SmartAzimuth450 = true,
            TrackStartElevationDeg = 5,
            AzimuthOffsetDeg = -72
        };

        var norad = "25544";
        controller.UpdateSynchronously(settings, TrackTarget(norad, 350, 20));
        Assert.Equal(278, rotator.LastAzimuthDeg);

        controller.UpdateSynchronously(settings, TrackTarget(norad, 11, 20));
        Assert.Equal(299, rotator.LastAzimuthDeg);
    }

    [Fact]
    public void Smart450_disabled_uses_compass_azimuth_at_north_wrap()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            AzimuthRange = RotatorAzimuthRange.Deg450,
            SmartAzimuth450 = false,
            TrackStartElevationDeg = 5
        };

        var norad = "25544";
        controller.UpdateSynchronously(settings, TrackTarget(norad, 350, 20));
        controller.UpdateSynchronously(settings, TrackTarget(norad, 10, 20));
        Assert.Equal(10, rotator.LastAzimuthDeg);
    }

    [Fact]
    public void Smart450_uses_polled_position_after_target_change()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            AzimuthRange = RotatorAzimuthRange.Deg450,
            SmartAzimuth450 = true,
            TrackStartElevationDeg = 5
        };

        controller.UpdateSynchronously(settings, TrackTarget("1", 350, 20));
        controller.UpdateSynchronously(settings, TrackTarget("2", 15, 20));
        Assert.Equal(375, rotator.LastAzimuthDeg);
    }

    [Fact]
    public void Smart450_west_side_north_wrap_after_tca()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            AzimuthRange = RotatorAzimuthRange.Deg450,
            SmartAzimuth450 = true,
            TrackStartElevationDeg = 5
        };

        var norad = "25544";
        controller.UpdateSynchronously(settings, TrackTarget(norad, 15, 45));
        controller.UpdateSynchronously(settings, TrackTarget(norad, 330, 30));
        Assert.Equal(375, rotator.LastAzimuthDeg);

        // Next tick: already in extended, shortest path to west is primary 330°.
        controller.UpdateSynchronously(settings, TrackTarget(norad, 330, 30));
        Assert.Equal(330, rotator.LastAzimuthDeg);
    }

    [Fact]
    public void Smart450_mid_pass_join_at_34_deg_uses_extended_before_west_jump()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            AzimuthRange = RotatorAzimuthRange.Deg450,
            SmartAzimuth450 = true,
            TrackStartElevationDeg = 5
        };

        var norad = "25544";
        controller.UpdateSynchronously(settings, TrackTarget("other", 180, 30));
        controller.UpdateSynchronously(settings, TrackTarget(norad, 34, 25));
        controller.UpdateSynchronously(settings, TrackTarget(norad, 330, 20, aheadAzimuthDeg: 325));
        Assert.Equal(394, rotator.LastAzimuthDeg);

        controller.UpdateSynchronously(settings, TrackTarget(norad, 330, 20, aheadAzimuthDeg: 325));
        Assert.Equal(330, rotator.LastAzimuthDeg);
    }

    [Fact]
    public void Smart450_northbound_without_north_crossing_stays_primary()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            AzimuthRange = RotatorAzimuthRange.Deg450,
            SmartAzimuth450 = true,
            TrackStartElevationDeg = 5
        };

        var aos = new DateTime(2026, 8, 15, 20, 31, 0, DateTimeKind.Utc);
        controller.SetActivePassSynchronously(new PassInfo
        {
            SatelliteName = "RS-44",
            NoradId = "44909",
            AosUtc = aos,
            LosUtc = aos.AddMinutes(16),
            MaxElevationDeg = 30,
            MaxElevationUtc = aos.AddMinutes(9),
            AosAzimuthDeg = 146,
            LosAzimuthDeg = 20
        });

        var norad = "44909";
        controller.UpdateSynchronously(settings, TrackTarget(norad, 80, 28));
        controller.UpdateSynchronously(settings, TrackTarget(norad, 50, 26));
        controller.UpdateSynchronously(settings, TrackTarget(norad, 40, 24));
        Assert.Equal(40, rotator.LastAzimuthDeg);

        controller.UpdateSynchronously(settings, TrackTarget(norad, 28, 14));
        Assert.Equal(28, rotator.LastAzimuthDeg);
    }

    [Fact]
    public void Smart450_east_descent_commits_when_los_is_west_of_north()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            AzimuthRange = RotatorAzimuthRange.Deg450,
            SmartAzimuth450 = true,
            TrackStartElevationDeg = 5
        };

        var aos = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
        controller.SetActivePassSynchronously(new PassInfo
        {
            SatelliteName = "TEST",
            NoradId = "25544",
            AosUtc = aos,
            LosUtc = aos.AddMinutes(12),
            MaxElevationDeg = 45,
            MaxElevationUtc = aos.AddMinutes(6),
            AosAzimuthDeg = 80,
            LosAzimuthDeg = 320
        });

        var norad = "25544";
        controller.UpdateSynchronously(settings, TrackTarget(norad, 80, 20));
        controller.UpdateSynchronously(settings, TrackTarget(norad, 50, 20));
        controller.UpdateSynchronously(settings, TrackTarget(norad, 25, 20));
        Assert.Equal(385, rotator.LastAzimuthDeg);
    }

    [Fact]
    public void Smart450_fo29_aos_near_20_uses_overlap_when_los_is_southwest()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            AzimuthRange = RotatorAzimuthRange.Deg450,
            SmartAzimuth450 = true,
            TrackStartElevationDeg = 5
        };

        var aos = new DateTime(2026, 9, 8, 19, 0, 0, DateTimeKind.Utc);
        controller.SetActivePassSynchronously(new PassInfo
        {
            SatelliteName = "FO-29",
            NoradId = "24278",
            AosUtc = aos,
            LosUtc = aos.AddMinutes(12),
            MaxElevationDeg = 35,
            MaxElevationUtc = aos.AddMinutes(6),
            AosAzimuthDeg = 20,
            LosAzimuthDeg = 232
        });

        controller.UpdateSynchronously(
            settings, TrackTarget("24278", 20, 8, aheadAzimuthDeg: 18));
        Assert.Equal(380, rotator.LastAzimuthDeg);

        controller.UpdateSynchronously(
            settings, TrackTarget("24278", 12, 12, aheadAzimuthDeg: 10));
        Assert.Equal(372, rotator.LastAzimuthDeg);
    }

    [Fact]
    public void Smart450_east_side_north_crossing_commits_before_compass_wrap()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            AzimuthRange = RotatorAzimuthRange.Deg450,
            SmartAzimuth450 = true,
            TrackStartElevationDeg = 5
        };

        var norad = "25544";
        controller.UpdateSynchronously(settings, TrackTarget(norad, 80, 20));
        controller.UpdateSynchronously(settings, TrackTarget(norad, 50, 20));
        controller.UpdateSynchronously(settings, TrackTarget(norad, 25, 20));
        controller.UpdateSynchronously(settings, TrackTarget(norad, 20, 20, aheadAzimuthDeg: 355));
        Assert.Equal(380, rotator.LastAzimuthDeg);

        controller.UpdateSynchronously(settings, TrackTarget(norad, 15, 20, aheadAzimuthDeg: 355));
        controller.UpdateSynchronously(settings, TrackTarget(norad, 355, 20));
        Assert.Equal(355, rotator.LastAzimuthDeg);
        Assert.InRange(Math.Abs(rotator.LastAzimuthDeg!.Value - 380), 0, 30);
    }

    [Fact]
    public void Azimuth360_does_not_use_extended_azimuth()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            AzimuthRange = RotatorAzimuthRange.Deg360,
            SmartAzimuth450 = true,
            TrackStartElevationDeg = 5
        };

        var norad = "25544";
        controller.UpdateSynchronously(settings, TrackTarget(norad, 350, 20));
        controller.UpdateSynchronously(settings, TrackTarget(norad, 10, 20));
        Assert.Equal(10, rotator.LastAzimuthDeg);
    }

    [Fact]
    public void Update_does_not_move_when_the_port_is_not_the_rotator()
    {
        var rotator = new RecordingRotatorDriver { ConfirmLink = false };
        using var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            Type = RotatorType.YaesuGs232,
            TrackStartElevationDeg = 5
        };

        controller.UpdateSynchronously(settings, TrackTarget("25544", 45, 20));

        Assert.Equal(0, rotator.SetPositionCallCount);
        var status = controller.GetPositionStatus();
        Assert.False(status.IsConnected);
        Assert.Equal(RotatorConnectionKind.IdentityMismatch, status.ConnectionKind);
        Assert.Equal("COM3", status.ConnectionDetail);
    }

    [Fact]
    public void Standby_does_not_park_when_the_port_is_not_the_rotator()
    {
        var rotator = new RecordingRotatorDriver { ConfirmLink = false };
        using var controller = new RotatorController(_ => rotator);
        var settings = new RotatorSettings
        {
            Enabled = true,
            Port = "COM3",
            Type = RotatorType.YaesuGs232,
            ParkAzimuthDeg = 180,
            ParkElevationDeg = 0,
            ParkAfterPass = true
        };

        controller.SetStandby(true, settings);
        controller.DrainCommandQueueForTests();

        Assert.Equal(0, rotator.SetPositionCallCount);
        Assert.Equal(RotatorConnectionKind.IdentityMismatch, controller.GetPositionStatus().ConnectionKind);
    }

    private static SatelliteTrackState TrackTarget(
        string noradId,
        double azimuthDeg,
        double elevationDeg,
        double? aheadAzimuthDeg = null) =>
        new()
        {
            Name = "TEST",
            NoradId = noradId,
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(azimuthDeg, elevationDeg, 800, 0),
            AheadAzimuthDeg = aheadAzimuthDeg
        };
}
