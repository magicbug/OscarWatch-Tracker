using OscarWatch.Core.Models;

namespace OscarWatch.Tests;

public sealed class EmeSettingsTests
{
    [Fact]
    public void The_defaults_are_off_zero_and_ninety()
    {
        var s = new EmeSettings();
        Assert.False(s.ShowMoonInPasses);
        Assert.Equal(0, s.ElevationLimitDeg);
        Assert.Equal(90, s.MaximumElevationDeg);
        Assert.False(s.UseHorizonMask);
    }

    [Theory]
    [InlineData(-50, -10)]
    [InlineData(-10, -10)]
    [InlineData(-2.5, -2.5)]
    [InlineData(45, 45)]
    [InlineData(200, 90)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void The_limit_is_clamped_to_minus_10_to_plus_90(double given, double expected)
    {
        var s = new EmeSettings { ElevationLimitDeg = given };
        s.Normalise();
        Assert.Equal(expected, s.ElevationLimitDeg);
    }

    [Theory]
    [InlineData(10, 5, 10)]
    [InlineData(10, 120, 90)]
    [InlineData(10, 60, 60)]
    [InlineData(10, double.NaN, 90)]
    public void The_maximum_is_never_below_the_limit_or_above_90(double limit, double max, double expected)
    {
        var s = new EmeSettings { ElevationLimitDeg = limit, MaximumElevationDeg = max };
        s.Normalise();
        Assert.Equal(expected, s.MaximumElevationDeg);
    }

    [Fact]
    public void A_settings_file_with_wild_values_loads_with_sane_ones()
    {
        Assert.True(Core.Services.SettingsService.TryParse(
            "{ \"eme\": { \"elevationLimitDeg\": 500, \"maximumElevationDeg\": -400 } }", out var parsed, out _));
        Assert.Equal(90, parsed.Eme.ElevationLimitDeg);
        Assert.Equal(90, parsed.Eme.MaximumElevationDeg);
    }

    [Fact]
    public void A_settings_file_with_a_null_eme_section_still_loads()
    {
        Assert.True(Core.Services.SettingsService.TryParse("{ \"eme\": null }", out var parsed, out _));
        Assert.NotNull(parsed.Eme);
    }

    [Fact]
    public void Bands_start_from_their_band_number_and_144_is_the_default_band()
    {
        var s = new EmeSettings();
        Assert.Equal("144", s.SelectedBandId);
        Assert.Equal(144.0, s.FrequencyMHzFor("144"));
        Assert.Equal(1296.0, s.FrequencyMHzFor("1296"));
        Assert.Equal(10368.0, s.FrequencyMHzFor("10368"));
        Assert.Equal(13, Core.Eme.EmeBands.All.Count);
        Assert.Equal(Core.Eme.EmeBands.All.Count, Core.Eme.EmeBands.All.Select(b => b.Id).Distinct().Count());
    }

    [Fact]
    public void One_frequency_is_remembered_for_each_band()
    {
        var s = new EmeSettings();
        s.FrequencyMHzByBand["144"] = 144.13;
        s.FrequencyMHzByBand["432"] = 432.065;
        Assert.Equal(144.13, s.FrequencyMHzFor("144"));
        Assert.Equal(432.065, s.FrequencyMHzFor("432"));
        Assert.Equal(1296.0, s.FrequencyMHzFor("1296"));
    }

    [Fact]
    public void Linear_is_the_default_below_1_ghz_and_circular_from_1296_mhz()
    {
        var s = new EmeSettings();
        foreach (var id in new[] { "50", "144", "222", "432", "902" })
            Assert.False(s.IsCircular(id), id);
        foreach (var id in new[] { "1296", "2320", "3400", "5760" })
            Assert.True(s.IsCircular(id), id);
        s.CircularByBand["144"] = true;
        s.CircularByBand["1296"] = false;
        Assert.True(s.IsCircular("144"));
        Assert.False(s.IsCircular("1296"));
    }

    [Fact]
    public void Wild_band_settings_are_cleaned_when_normalised()
    {
        var s = new EmeSettings { SelectedBandId = "nonsense" };
        s.FrequencyMHzByBand["144"] = -1;
        s.FrequencyMHzByBand["432"] = 432.1;
        s.FrequencyMHzByBand["bogus"] = 100;
        s.BeamwidthDegByBand["432"] = 0;
        s.BeamwidthDegByBand["1296"] = 3;
        s.BeamwidthDegByBand["2320"] = double.NaN;
        s.CircularByBand["bogus"] = true;
        s.Normalise();
        Assert.Equal("144", s.SelectedBandId);
        Assert.Equal(["432"], s.FrequencyMHzByBand.Keys.ToList());
        Assert.Equal(["1296"], s.BeamwidthDegByBand.Keys.ToList());
        Assert.Empty(s.CircularByBand);
    }

    [Fact]
    public void Band_settings_survive_a_save_and_a_load()
    {
        var json = "{ \"eme\": { \"selectedBandId\": \"1296\", \"frequencyMHzByBand\": { \"1296\": 1296.065 }, "
            + "\"circularByBand\": { \"1296\": false }, \"beamwidthDegByBand\": { \"1296\": 2.4 }, \"largeDigits\": true, \"windowWidth\": 700 } }";
        Assert.True(Core.Services.SettingsService.TryParse(json, out var parsed, out _));
        Assert.Equal("1296", parsed.Eme.SelectedBandId);
        Assert.Equal(1296.065, parsed.Eme.FrequencyMHzFor("1296"));
        Assert.False(parsed.Eme.IsCircular("1296"));
        Assert.Equal(2.4, parsed.Eme.BeamwidthFor("1296"));
        Assert.True(parsed.Eme.LargeDigits);
        Assert.Equal(700, parsed.Eme.WindowWidth);
    }
}
