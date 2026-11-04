using OscarWatch.Core.Models;
using OscarWatch.Core.Rotator;
using OscarWatch.Rotator;

namespace OscarWatch.Tests;

public sealed class Gs232AzimuthOnlyTests
{
    [Fact]
    public void Azimuth_only_driver_moves_with_M_and_ignores_elevation()
    {
        var transport = new RecordingRotatorSerialTransport();
        using var rotator = new Gs232Rotator(transport, azimuthOnly: true);
        rotator.Open();

        rotator.SetPosition(90, 45, new RotatorSettings());

        Assert.Equal("M090\r", transport.Written[^1]);
    }

    [Fact]
    public void Azimuth_only_driver_keeps_three_digit_overlap_azimuth()
    {
        var transport = new RecordingRotatorSerialTransport();
        using var rotator = new Gs232Rotator(transport, azimuthOnly: true);
        rotator.Open();

        rotator.SetPosition(370, 0, new RotatorSettings { AzimuthRange = RotatorAzimuthRange.Deg450 });

        Assert.Equal("M370\r", transport.Written[^1]);
    }

    [Fact]
    public void Azimuth_only_driver_reads_position_with_C_only()
    {
        var transport = new RecordingRotatorSerialTransport { NextReadLine = "AZ=123" };
        using var rotator = new Gs232Rotator(transport, azimuthOnly: true);
        rotator.Open();

        Assert.Equal((123, null), rotator.GetPosition());
        Assert.Equal(["C\r"], transport.Written);
    }

    [Fact]
    public void Azimuth_only_driver_accepts_gs232a_reply()
    {
        var transport = new RecordingRotatorSerialTransport { NextReadLine = "+0123" };
        using var rotator = new Gs232Rotator(transport, azimuthOnly: true);
        rotator.Open();

        Assert.Equal((123, null), rotator.GetPosition());
    }

    [Fact]
    public void Azimuth_only_driver_stops_querying_after_the_controller_never_answers()
    {
        var transport = new RecordingRotatorSerialTransport();
        using var rotator = new Gs232Rotator(transport, azimuthOnly: true);
        rotator.Open();

        Assert.Equal((null, null), rotator.GetPosition());
        var queriesAfterFirstRead = transport.Written.Count;

        Assert.Equal((null, null), rotator.GetPosition());
        Assert.Equal(queriesAfterFirstRead, transport.Written.Count);
    }

    [Fact]
    public void Default_driver_still_sends_W_and_queries_C2()
    {
        var transport = new RecordingRotatorSerialTransport { NextReadLine = "AZ=090 EL=045" };
        using var rotator = new Gs232Rotator(transport);
        rotator.Open();

        rotator.SetPosition(90, 45, new RotatorSettings());
        Assert.Equal("W090 045\r", transport.Written[^1]);

        transport.Written.Clear();
        rotator.GetPosition();
        Assert.Equal("C2\r", transport.Written[0]);
    }

    [Fact]
    public void IsAzimuthOnly_applies_only_to_gs232()
    {
        Assert.True(new RotatorSettings { Type = RotatorType.YaesuGs232, AzimuthOnly = true }.IsAzimuthOnly);
        Assert.False(new RotatorSettings { Type = RotatorType.EasyComm, AzimuthOnly = true }.IsAzimuthOnly);
        Assert.False(new RotatorSettings { Type = RotatorType.YaesuGs232 }.IsAzimuthOnly);
    }

    [Fact]
    public void Controller_does_not_resend_when_only_satellite_elevation_changes()
    {
        var rotator = new RecordingRotatorDriver();
        using var controller = new RotatorController(_ => rotator);
        var settings = AzimuthOnlySettings();

        controller.UpdateSynchronously(settings, TrackTarget(45, 10));
        Assert.Equal(1, rotator.SetPositionCallCount);
        Assert.Equal(45, rotator.LastAzimuthDeg);
        Assert.Equal(0, rotator.LastElevationDeg);

        controller.UpdateSynchronously(settings, TrackTarget(45, 40));
        Assert.Equal(1, rotator.SetPositionCallCount);

        controller.UpdateSynchronously(settings, TrackTarget(50, 60));
        Assert.Equal(2, rotator.SetPositionCallCount);
        Assert.Equal(50, rotator.LastAzimuthDeg);
        Assert.Equal(0, rotator.LastElevationDeg);
    }

    [Fact]
    public void Controller_reports_no_elevation_when_azimuth_only()
    {
        var rotator = new RecordingRotatorDriver();
        using var controller = new RotatorController(_ => rotator);

        controller.UpdateSynchronously(AzimuthOnlySettings(), TrackTarget(45, 30));

        var status = controller.GetPositionStatus();
        Assert.Equal(45, status.AzimuthDeg);
        Assert.Null(status.ElevationDeg);
        Assert.Null(status.CommandedElevationDeg);
    }

    [Fact]
    public void Controller_parks_with_azimuth_only()
    {
        var rotator = new RecordingRotatorDriver();
        using var controller = new RotatorController(_ => rotator);
        var settings = AzimuthOnlySettings();
        settings.ParkAzimuthDeg = 180;
        settings.ParkElevationDeg = 30;

        controller.UpdateSynchronously(settings, TrackTarget(45, 2));

        Assert.True(controller.GetPositionStatus().IsParked);
        Assert.Equal(180, rotator.LastAzimuthDeg);
        Assert.Equal(0, rotator.LastElevationDeg);
    }

    [Fact]
    public void Controller_does_not_plan_keyhole_flip_when_azimuth_only()
    {
        var rotator = new RecordingRotatorDriver();
        using var controller = new RotatorController(_ => rotator);
        var settings = AzimuthOnlySettings();
        settings.KeyholeAvoidanceEnabled = true;

        controller.SetActivePassSynchronously(MakePass(DateTime.UtcNow.AddMinutes(-2)));
        controller.UpdateSynchronously(settings, TrackTarget(45, 60));

        Assert.Null(controller.CurrentKeyholePlan);
        Assert.False(controller.GetPositionStatus().IsKeyholeAvoidanceActive);
    }

    [Fact]
    public void Controller_ignores_a_flipped_plan_when_azimuth_only()
    {
        var rotator = new RecordingRotatorDriver();
        using var controller = new RotatorController(_ => rotator);
        var settings = AzimuthOnlySettings();

        controller.UpdateSynchronously(settings, TrackTarget(30, 2));
        controller.SetActivePassSynchronously(MakePass(DateTime.UtcNow.AddMinutes(-1)));
        controller.SetKeyholePlanForTests(new KeyholePlan(
            Strategy: KeyholeStrategy.FlippedStart,
            FlippedStartAzimuthDeg: 210,
            PrePositionLeadTime: TimeSpan.FromSeconds(75),
            NormalSignalLossWindow: TimeSpan.FromSeconds(30),
            FlippedSignalLossWindow: TimeSpan.FromSeconds(10)));

        controller.UpdateSynchronously(settings, TrackTarget(30, 10));

        Assert.False(controller.IsKeyholeFlippedActive);
        Assert.Equal(30, rotator.LastAzimuthDeg);
    }

    [Fact]
    public void Controller_reopens_the_driver_when_azimuth_only_changes()
    {
        var created = new List<RotatorSettings>();
        using var controller = new RotatorController(s =>
        {
            created.Add(s);
            return new RecordingRotatorDriver();
        });
        var settings = AzimuthOnlySettings();
        settings.AzimuthOnly = false;

        controller.UpdateSynchronously(settings, TrackTarget(45, 10));
        Assert.Single(created);

        var azimuthOnly = AzimuthOnlySettings();
        controller.UpdateSynchronously(azimuthOnly, TrackTarget(45, 10));
        Assert.Equal(2, created.Count);
        Assert.True(created[^1].IsAzimuthOnly);
    }

    private static RotatorSettings AzimuthOnlySettings() => new()
    {
        Enabled = true,
        Port = "COM3",
        BaudRate = 9600,
        Type = RotatorType.YaesuGs232,
        AzimuthOnly = true,
        TrackStartElevationDeg = 5,
        SmartAzimuth450 = false,
        ParkAfterPass = true
    };

    private static SatelliteTrackState TrackTarget(double azimuthDeg, double elevationDeg) => new()
    {
        Name = "TEST",
        NoradId = "99999",
        Subpoint = new GeoCoordinate(0, 0),
        LookAngles = new LookAngles(azimuthDeg, elevationDeg, 800, 0)
    };

    private static PassInfo MakePass(DateTime aosUtc) => new()
    {
        SatelliteName = "TEST-SAT",
        NoradId = "99999",
        AosUtc = aosUtc,
        LosUtc = aosUtc.AddMinutes(10),
        MaxElevationDeg = 88,
        MaxElevationUtc = aosUtc.AddMinutes(5),
        AosAzimuthDeg = 30,
        LosAzimuthDeg = 200
    };
}
