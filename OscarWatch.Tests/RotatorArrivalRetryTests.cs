using OscarWatch.Core.Models;
using OscarWatch.Rotator;

namespace OscarWatch.Tests;

public sealed class RotatorArrivalRetryTests
{
    [Fact]
    public void Tracking_reissues_command_when_polled_azimuth_has_not_arrived()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = EnabledSettings();

        controller.UpdateSynchronously(settings, TrackTarget(40, 20));
        Assert.Equal(1, rotator.SetPositionCallCount);

        rotator.ForceReportedPosition = true;
        rotator.ForcedAzimuth = 70;
        rotator.ForcedElevation = 20;

        controller.UpdateSynchronously(settings, TrackTarget(40, 20));
        Assert.Equal(2, rotator.SetPositionCallCount);
        Assert.Equal(40, rotator.LastAzimuthDeg);
        Assert.Equal(20, rotator.LastElevationDeg);
    }

    [Fact]
    public void Tracking_does_not_reissue_when_polled_position_has_arrived()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = EnabledSettings();

        controller.UpdateSynchronously(settings, TrackTarget(40, 20));
        var calls = rotator.SetPositionCallCount;

        controller.UpdateSynchronously(settings, TrackTarget(40, 20));
        Assert.Equal(calls, rotator.SetPositionCallCount);
    }

    [Fact]
    public void Tracking_does_not_reissue_when_feedback_is_missing()
    {
        var rotator = new RecordingRotatorDriver
        {
            ForceReportedPosition = true,
            ForcedAzimuth = null,
            ForcedElevation = null
        };
        var controller = new RotatorController(_ => rotator);
        var settings = EnabledSettings();

        controller.UpdateSynchronously(settings, TrackTarget(40, 20));
        Assert.Equal(1, rotator.SetPositionCallCount);

        controller.UpdateSynchronously(settings, TrackTarget(40, 20));
        Assert.Equal(1, rotator.SetPositionCallCount);
    }

    [Fact]
    public void Tracking_does_not_reissue_when_overlap_feedback_matches_command()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = EnabledSettings(smart450: true);

        controller.UpdateSynchronously(settings, TrackTarget(350, 20));
        controller.UpdateSynchronously(settings, TrackTarget(15, 20));
        Assert.Equal(375, rotator.LastAzimuthDeg);
        var calls = rotator.SetPositionCallCount;

        rotator.ForceReportedPosition = true;
        rotator.ForcedAzimuth = 15;
        rotator.ForcedElevation = 20;

        controller.UpdateSynchronously(settings, TrackTarget(15, 20));
        Assert.Equal(calls, rotator.SetPositionCallCount);
        Assert.Equal(375, rotator.LastAzimuthDeg);
    }

    [Fact]
    public void Smart450_keeps_last_commanded_azimuth_when_poll_is_noisy()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = EnabledSettings(smart450: true);

        controller.UpdateSynchronously(settings, TrackTarget(350, 20));
        Assert.Equal(350, rotator.LastAzimuthDeg);

        rotator.ForceReportedPosition = true;
        rotator.ForcedAzimuth = 15;
        rotator.ForcedElevation = 20;

        controller.UpdateSynchronously(settings, TrackTarget(340, 20));
        Assert.Equal(340, rotator.LastAzimuthDeg);
    }

    [Fact]
    public void Manual_rotate_reissues_until_arrived()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = EnabledSettings();

        controller.SetStandby(true, settings);
        controller.DrainCommandQueueForTests();
        controller.MoveTo(90, 45, settings);
        controller.DrainCommandQueueForTests();
        controller.UpdateSynchronously(settings, null);
        var callsAfterMove = rotator.SetPositionCallCount;

        rotator.ForceReportedPosition = true;
        rotator.ForcedAzimuth = 40;
        rotator.ForcedElevation = 45;

        controller.UpdateSynchronously(settings, null);
        Assert.Equal(callsAfterMove + 1, rotator.SetPositionCallCount);
        Assert.Equal(90, rotator.LastAzimuthDeg);
        Assert.Equal(45, rotator.LastElevationDeg);
    }

    [Fact]
    public void Stop_cancels_arrival_retry()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = EnabledSettings();

        controller.SetStandby(true, settings);
        controller.DrainCommandQueueForTests();
        controller.MoveTo(90, 45, settings);
        controller.DrainCommandQueueForTests();
        controller.UpdateSynchronously(settings, null);

        controller.Stop(settings);
        controller.DrainCommandQueueForTests();
        controller.UpdateSynchronously(settings, null);
        var callsAfterStop = rotator.SetPositionCallCount;

        rotator.ForceReportedPosition = true;
        rotator.ForcedAzimuth = 40;
        rotator.ForcedElevation = 45;

        controller.UpdateSynchronously(settings, null);
        Assert.Equal(callsAfterStop, rotator.SetPositionCallCount);
        Assert.Equal(1, rotator.StopCallCount);
    }

    [Fact]
    public void Park_reissues_when_feedback_has_not_arrived()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = EnabledSettings();
        settings.ParkAzimuthDeg = 180;
        settings.ParkElevationDeg = 0;
        settings.ParkAfterPass = true;

        controller.Park(settings);
        controller.DrainCommandQueueForTests();
        controller.UpdateSynchronously(settings, null);
        var callsAfterPark = rotator.SetPositionCallCount;

        rotator.ForceReportedPosition = true;
        rotator.ForcedAzimuth = 90;
        rotator.ForcedElevation = 0;

        controller.UpdateSynchronously(settings, null);
        Assert.Equal(callsAfterPark + 1, rotator.SetPositionCallCount);
        Assert.Equal(180, rotator.LastAzimuthDeg);
        Assert.Equal(0, rotator.LastElevationDeg);
    }

    [Fact]
    public void Tracking_reissues_when_elevation_has_not_arrived()
    {
        var rotator = new RecordingRotatorDriver();
        var controller = new RotatorController(_ => rotator);
        var settings = EnabledSettings();

        controller.UpdateSynchronously(settings, TrackTarget(40, 30));
        Assert.Equal(1, rotator.SetPositionCallCount);

        rotator.ForceReportedPosition = true;
        rotator.ForcedAzimuth = 40;
        rotator.ForcedElevation = 10;

        controller.UpdateSynchronously(settings, TrackTarget(40, 30));
        Assert.Equal(2, rotator.SetPositionCallCount);
        Assert.Equal(30, rotator.LastElevationDeg);
    }

    private static RotatorSettings EnabledSettings(bool smart450 = false) =>
        new()
        {
            Enabled = true,
            Port = "COM3",
            TrackStartElevationDeg = -90,
            MovementThresholdDeg = 1.0,
            SmartAzimuth450 = smart450,
            AzimuthRange = smart450 ? RotatorAzimuthRange.Deg450 : RotatorAzimuthRange.Deg360
        };

    private static SatelliteTrackState TrackTarget(double azimuthDeg, double elevationDeg) =>
        new()
        {
            Name = "TEST",
            NoradId = "99999",
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(azimuthDeg, elevationDeg, 800, 0)
        };
}
