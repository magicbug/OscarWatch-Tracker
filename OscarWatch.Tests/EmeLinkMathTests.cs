using OscarWatch.Core.Eme;
using Xunit.Abstractions;

namespace OscarWatch.Tests;

public sealed class EmeLinkMathTests(ITestOutputHelper output)
{
    [Fact]
    public void Echo_delay_at_the_mean_distance_is_about_2_56_seconds()
    {
        Assert.Equal(2.5645, EmeLinkMath.EchoDelaySeconds(384_400), 0.001);
        // 2 d / c, so twice the distance takes twice as long.
        Assert.Equal(2 * EmeLinkMath.EchoDelaySeconds(300_000), EmeLinkMath.EchoDelaySeconds(600_000), 9);
    }

    [Fact]
    public void A_receding_moon_lowers_the_echo_and_an_approaching_one_raises_it()
    {
        Assert.True(EmeLinkMath.OwnEchoShiftHz(144e6, +0.217) < 0);
        Assert.True(EmeLinkMath.OwnEchoShiftHz(144e6, -0.217) > 0);
        Assert.Equal(0, EmeLinkMath.OwnEchoShiftHz(144e6, 0), 12);
    }

    [Fact]
    public void Own_echo_shift_matches_the_hand_worked_value()
    {
        // -2 x 144.174e6 x 217 m/s / 299,792,458 m/s = -208.7 Hz, the figure found when the design was checked.
        Assert.Equal(-208.7, EmeLinkMath.OwnEchoShiftHz(144.174e6, 0.217), 0.1);
    }

    [Fact]
    public void The_shift_is_proportional_to_the_frequency()
    {
        var at144 = EmeLinkMath.OwnEchoShiftHz(144e6, 0.25);
        Assert.Equal(3 * at144, EmeLinkMath.OwnEchoShiftHz(432e6, 0.25), 6);
        Assert.Equal(9 * at144, EmeLinkMath.OwnEchoShiftHz(1296e6, 0.25), 6);
    }

    [Fact]
    public void The_dx_shift_is_the_own_echo_shift_when_both_stations_see_the_same_rate()
    {
        Assert.Equal(EmeLinkMath.OwnEchoShiftHz(432e6, 0.2), EmeLinkMath.DxShiftHz(432e6, 0.2, 0.2), 9);
        // And with opposite rates the shift cancels.
        Assert.Equal(0, EmeLinkMath.DxShiftHz(432e6, 0.2, -0.2), 9);
    }

    [Fact]
    public void Extra_path_loss_is_zero_at_the_reference_and_follows_the_fourth_power()
    {
        Assert.Equal(0, EmeLinkMath.ExtraPathLossDb(EmeLinkMath.ReferenceDistanceKm), 12);
        // 10 percent farther: 40 log10 1.1 = 1.6557 dB.
        Assert.Equal(1.6557, EmeLinkMath.ExtraPathLossDb(EmeLinkMath.ReferenceDistanceKm * 1.1), 0.0001);
        // Apogee, about 406,700 km: 406,700 / 356,903 = 1.1395, and 40 log10 of that is 2.27 dB.
        Assert.Equal(2.27, EmeLinkMath.ExtraPathLossDb(406_700), 0.01);
    }

    [Fact]
    public void Absolute_path_loss_is_near_the_published_figures()
    {
        var at144 = EmeLinkMath.AbsolutePathLossDb(144.174e6, 384_400);
        var at1296 = EmeLinkMath.AbsolutePathLossDb(1296e6, 384_400);
        output.WriteLine($"144.174 MHz {at144:0.00} dB, 1296 MHz {at1296:0.00} dB");
        // Hand check: (4 pi)^3 d^4 / (sigma lambda^2) with sigma = 0.065 pi R^2 gives 252.1 dB at 144.174 MHz.
        Assert.Equal(252.1, at144, 0.1);
        // Published: about 251 to 252 dB at 144 MHz and about 270 dB at 1296 MHz, which are rounded figures.
        Assert.InRange(at144, 251.0, 253.0);
        Assert.InRange(at1296, 270.0, 272.0);
        // Ten times the frequency is 20 dB more loss, since the wavelength squared is in the denominator.
        Assert.Equal(20, EmeLinkMath.AbsolutePathLossDb(1441.74e6, 384_400) - at144, 0.001);
    }

    [Theory]
    [InlineData(-0.1, PathLossClass.Good)]
    [InlineData(0, PathLossClass.Good)]
    [InlineData(1.999, PathLossClass.Good)]
    [InlineData(2.0, PathLossClass.Fair)]
    [InlineData(3.999, PathLossClass.Fair)]
    [InlineData(4.0, PathLossClass.Poor)]
    [InlineData(5.999, PathLossClass.Poor)]
    [InlineData(6.0, PathLossClass.Bad)]
    [InlineData(9, PathLossClass.Bad)]
    public void The_classes_change_at_2_4_and_6_decibels(double loss, PathLossClass expected) =>
        Assert.Equal(expected, EmeLinkMath.ClassOf(loss));

    [Theory]
    [InlineData(30, true)]
    [InlineData(144.174, true)]
    [InlineData(100_000, true)]
    [InlineData(29.999, false)]
    [InlineData(100_000.1, false)]
    [InlineData(0, false)]
    [InlineData(-144, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void Only_30_mhz_to_100_ghz_is_a_valid_frequency(double mhz, bool valid) =>
        Assert.Equal(valid, EmeLinkMath.IsValidFrequency(mhz));
}
