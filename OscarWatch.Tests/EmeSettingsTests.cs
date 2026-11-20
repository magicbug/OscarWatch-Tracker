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
}
