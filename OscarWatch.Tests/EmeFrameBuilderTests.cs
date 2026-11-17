using OscarWatch.Core.Eme;
using OscarWatch.Core.Models;

namespace OscarWatch.Tests;

public sealed class EmeFrameBuilderTests
{
    private static readonly StationPosition London = new(51.5074, -0.1278, 11);

    // Skyfield and DE421 put the Moon at azimuth 163.7470, elevation 47.4986, 364,236.9 km on 20 March 2026 12:30 UTC,
    // and at elevation -0.6844 on 16 November 2026 13:00 UTC (see MoonEphemerisTests).
    private static readonly DateTime High = new(2026, 3, 20, 12, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime OnTheHorizon = new(2026, 11, 16, 13, 0, 0, DateTimeKind.Utc);

    private static EmeFrame Build(DateTime utc, EmeSettings? eme = null, HorizonMask? mask = null, MoonNextEvent? next = null) =>
        EmeFrameBuilder.Build(utc, London, eme ?? new EmeSettings(), mask, next)!;

    [Fact]
    public void The_frame_carries_the_position_the_echo_and_the_path_loss()
    {
        var f = Build(High);
        Assert.Equal(163.747, f.Sample.AzimuthDeg, 0.1);
        Assert.Equal(47.4986, f.Sample.ElevationDeg, 0.1);
        Assert.True(f.IsUp);
        Assert.Equal(2.0 * f.Sample.DistanceKm / 299_792.458, f.EchoDelaySeconds, 1e-9);
        Assert.Equal(EmeLinkMath.ExtraPathLossDb(f.Sample.DistanceKm), f.ExtraPathLossDb, 1e-9);
        Assert.Equal(PathLossClass.Good, f.LossClass);
        Assert.Equal("144", f.BandId);
        Assert.Equal(144.0, f.FrequencyMHz);
        Assert.NotNull(f.OwnEchoShiftHz);
        Assert.Equal(EmeLinkMath.AbsolutePathLossDb(144e6, f.Sample.DistanceKm), f.AbsolutePathLossDb!.Value, 1e-9);
        Assert.NotNull(f.Sky);
    }

    [Fact]
    public void The_echo_shift_follows_the_selected_frequency()
    {
        var eme = new EmeSettings { SelectedBandId = "432" };
        eme.FrequencyMHzByBand["432"] = 432.065;
        var f = Build(High, eme);
        Assert.Equal(432.065, f.FrequencyMHz);
        Assert.Equal(EmeLinkMath.OwnEchoShiftHz(432.065e6, f.Sample.RangeRateKmPerS), f.OwnEchoShiftHz!.Value, 1e-9);
        var at144 = Build(High);
        Assert.Equal(432.065 / 144.0, f.OwnEchoShiftHz.Value / at144.OwnEchoShiftHz!.Value, 1e-9);
    }

    [Fact]
    public void An_invalid_stored_frequency_falls_back_to_the_band_and_never_gives_nonsense()
    {
        var eme = new EmeSettings { SelectedBandId = "144" };
        eme.FrequencyMHzByBand["144"] = -5;
        var f = Build(High, eme);
        Assert.Equal(144.0, f.FrequencyMHz);
        Assert.True(double.IsFinite(f.OwnEchoShiftHz!.Value));
    }

    [Fact]
    public void The_elevation_limit_decides_whether_the_moon_is_up()
    {
        // At -0.6844 degrees the Moon is below the default limit of 0, and above a limit of -2.
        Assert.False(Build(OnTheHorizon).IsUp);
        Assert.True(Build(OnTheHorizon, new EmeSettings { ElevationLimitDeg = -2 }).IsUp);
        Assert.True(Build(High, new EmeSettings { ElevationLimitDeg = 47 }).IsUp);
        Assert.False(Build(High, new EmeSettings { ElevationLimitDeg = 48 }).IsUp);
    }

    [Fact]
    public void The_maximum_elevation_excludes_a_moon_above_it()
    {
        Assert.False(Build(High, new EmeSettings { MaximumElevationDeg = 40 }).IsUp);
        Assert.True(Build(High, new EmeSettings { MaximumElevationDeg = 60 }).IsUp);
    }

    [Fact]
    public void The_horizon_mask_counts_only_when_the_settings_ask_for_it()
    {
        var mask = new HorizonMask { Points = [new(0, 60), new(90, 60), new(180, 60), new(270, 60)] };
        Assert.True(Build(High, new EmeSettings { UseHorizonMask = false }, mask).IsUp);
        Assert.False(Build(High, new EmeSettings { UseHorizonMask = true }, mask).IsUp);
    }

    [Fact]
    public void The_beamwidth_and_polarisation_come_from_the_band()
    {
        var eme = new EmeSettings { SelectedBandId = "1296" };
        eme.BeamwidthDegByBand["1296"] = 2.5;
        var f = Build(High, eme);
        Assert.True(f.Circular);
        Assert.Equal(2.5, f.BeamwidthDeg);
        Assert.False(Build(High).Circular);
        Assert.Null(Build(High).BeamwidthDeg);
    }

    [Fact]
    public void The_galactic_centre_is_absent_on_the_microwave_bands()
    {
        Assert.NotNull(Build(High).Sky!.GalacticCentreSeparationDeg);
        Assert.Null(Build(High, new EmeSettings { SelectedBandId = "1296" }).Sky!.GalacticCentreSeparationDeg);
    }

    [Fact]
    public void A_station_that_is_not_a_number_gives_no_frame()
    {
        Assert.Null(EmeFrameBuilder.Build(High, new StationPosition(double.NaN, 0, 0), new EmeSettings(), null, null));
    }

    [Fact]
    public void The_same_inputs_always_give_the_same_frame()
    {
        Assert.Equal(Build(High).Sample, Build(High).Sample);
        Assert.Equal(Build(High).ExtraPathLossDb, Build(High).ExtraPathLossDb);
    }

    [Fact]
    public void A_screen_reader_is_told_about_the_moon_going_up_or_down_and_not_about_every_second()
    {
        var up = Build(High);
        var later = Build(High.AddSeconds(1));
        var down = Build(OnTheHorizon);
        Assert.True(up.IsMaterialChange(null));
        Assert.False(later.IsMaterialChange(up));
        Assert.True(down.IsMaterialChange(up));
        Assert.True(Build(High, new EmeSettings { SelectedBandId = "432" }).IsMaterialChange(up));
    }

    [Fact]
    public void The_next_event_is_passed_through()
    {
        var next = new MoonNextEvent(true, High.AddHours(5));
        Assert.Same(next, Build(High, next: next).Next);
    }
}
