using OscarWatch.Core.Models;
using OscarWatch.ViewModels;

namespace OscarWatch.Tests;

public sealed class RotatorAzimuthDisplayTests
{
    [Fact]
    public void FormatRotatorAzimuthText_shows_commanded_and_compass_when_differ()
    {
        var text = MainViewModel.FormatRotatorAzimuthText(
            new RotatorPositionStatus(true, 365, 20, 370, 10));
        Assert.Equal("370° (10° sat)", text);
    }

    [Fact]
    public void FormatRotatorAzimuthText_shows_polled_when_command_matches_compass()
    {
        var text = MainViewModel.FormatRotatorAzimuthText(
            new RotatorPositionStatus(true, 350, 20, 350, 350));
        Assert.Equal("350°", text);
    }

    [Fact]
    public void FormatRotatorAzimuthText_folds_negative_polled_azimuth_to_compass()
    {
        var text = MainViewModel.FormatRotatorAzimuthText(
            new RotatorPositionStatus(true, -86, 80));
        Assert.Equal("274°", text);
    }

    [Fact]
    public void FormatRotatorAzimuthText_keeps_extended_dial_polled_azimuth()
    {
        var text = MainViewModel.FormatRotatorAzimuthText(
            new RotatorPositionStatus(true, 400, 20));
        Assert.Equal("400°", text);
    }

    [Fact]
    public void FormatRotatorAzimuthText_disconnected_shows_dash()
    {
        var text = MainViewModel.FormatRotatorAzimuthText(
            new RotatorPositionStatus(false, null, null));
        Assert.Equal("—", text);
    }

    [Fact]
    public void FormatRotatorAzimuthText_falls_back_to_commanded_when_no_poll()
    {
        var text = MainViewModel.FormatRotatorAzimuthText(
            new RotatorPositionStatus(true, null, null, CommandedAzimuthDeg: 250, CompassAzimuthDeg: 250));
        Assert.Equal("250°", text);
    }

    [Fact]
    public void FormatRotatorElevationText_falls_back_to_commanded_when_no_poll()
    {
        var text = MainViewModel.FormatRotatorElevationText(
            new RotatorPositionStatus(true, null, null, CommandedElevationDeg: 6));
        Assert.Equal("6°", text);
    }

    [Fact]
    public void FormatRotatorElevationText_prefers_polled_over_commanded()
    {
        var text = MainViewModel.FormatRotatorElevationText(
            new RotatorPositionStatus(true, null, 12, CommandedElevationDeg: 6));
        Assert.Equal("12°", text);
    }

    [Fact]
    public void FormatRotatorElevationText_disconnected_shows_dash()
    {
        var text = MainViewModel.FormatRotatorElevationText(
            new RotatorPositionStatus(false, null, null, CommandedElevationDeg: 6));
        Assert.Equal("—", text);
    }

    [Fact]
    public void ResolveSkyPlotRotatorAzimuthDeg_prefers_polled_over_commanded_compass()
    {
        var azimuth = MainViewModel.ResolveSkyPlotRotatorAzimuthDeg(
            new RotatorPositionStatus(true, 90, 20, CommandedAzimuthDeg: 180, CompassAzimuthDeg: 180),
            azimuthOffsetDeg: 0);
        Assert.Equal(90, azimuth);
    }

    [Fact]
    public void ResolveSkyPlotRotatorAzimuthDeg_maps_overlap_mechanical_to_compass()
    {
        var azimuth = MainViewModel.ResolveSkyPlotRotatorAzimuthDeg(
            new RotatorPositionStatus(true, 365, 20, CommandedAzimuthDeg: 370, CompassAzimuthDeg: 10),
            azimuthOffsetDeg: 0);
        Assert.Equal(5, azimuth);
    }

    [Fact]
    public void ResolveSkyPlotRotatorAzimuthDeg_subtracts_azimuth_offset()
    {
        var azimuth = MainViewModel.ResolveSkyPlotRotatorAzimuthDeg(
            new RotatorPositionStatus(true, 100, 10, CommandedAzimuthDeg: 180, CompassAzimuthDeg: 180),
            azimuthOffsetDeg: 10);
        Assert.Equal(90, azimuth);
    }

    [Fact]
    public void ResolveSkyPlotRotatorAzimuthDeg_falls_back_to_compass_when_unpolled()
    {
        var azimuth = MainViewModel.ResolveSkyPlotRotatorAzimuthDeg(
            new RotatorPositionStatus(true, null, 20, CommandedAzimuthDeg: 370, CompassAzimuthDeg: 10),
            azimuthOffsetDeg: 0);
        Assert.Equal(10, azimuth);
    }

    [Fact]
    public void ResolveSkyPlotRotatorAzimuthDeg_disconnected_is_null()
    {
        var azimuth = MainViewModel.ResolveSkyPlotRotatorAzimuthDeg(
            new RotatorPositionStatus(false, 90, 20, CommandedAzimuthDeg: 180, CompassAzimuthDeg: 180),
            azimuthOffsetDeg: 0);
        Assert.Null(azimuth);
    }

    [Fact]
    public void ResolveSkyPlotRotatorBeam_unmirrors_flipped_position()
    {
        var beam = MainViewModel.ResolveSkyPlotRotatorBeam(
            new RotatorPositionStatus(
                true,
                210,
                170,
                IsKeyholeAvoidanceActive: true,
                CommandedAzimuthDeg: 210,
                CompassAzimuthDeg: 210,
                CommandedElevationDeg: 170),
            azimuthOffsetDeg: 0);

        Assert.Equal(30, beam.AzimuthDeg);
        Assert.Equal(10, beam.ElevationDeg);
    }

    [Fact]
    public void ResolveSkyPlotRotatorBeam_leaves_normal_tracking_unchanged()
    {
        var beam = MainViewModel.ResolveSkyPlotRotatorBeam(
            new RotatorPositionStatus(true, 30, 10, CommandedAzimuthDeg: 30, CompassAzimuthDeg: 30),
            azimuthOffsetDeg: 0);

        Assert.Equal(30, beam.AzimuthDeg);
        Assert.Equal(10, beam.ElevationDeg);
    }
}
