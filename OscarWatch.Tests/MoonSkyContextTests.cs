using CosineKitty;
using OscarWatch.Core.Eme;

namespace OscarWatch.Tests;

public sealed class MoonSkyContextTests
{
    private static readonly StationPosition London = new(51.5, -0.1, 50);
    private static DateTime Utc(int y, int mo, int d, int h = 0) => new(y, mo, d, h, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, MoonPhaseName.New)]
    [InlineData(22.4, MoonPhaseName.New)]
    [InlineData(22.6, MoonPhaseName.WaxingCrescent)]
    [InlineData(45, MoonPhaseName.WaxingCrescent)]
    [InlineData(90, MoonPhaseName.FirstQuarter)]
    [InlineData(135, MoonPhaseName.WaxingGibbous)]
    [InlineData(180, MoonPhaseName.Full)]
    [InlineData(225, MoonPhaseName.WaningGibbous)]
    [InlineData(270, MoonPhaseName.LastQuarter)]
    [InlineData(315, MoonPhaseName.WaningCrescent)]
    [InlineData(337.4, MoonPhaseName.WaningCrescent)]
    [InlineData(337.6, MoonPhaseName.New)]
    [InlineData(359.9, MoonPhaseName.New)]
    [InlineData(-90, MoonPhaseName.LastQuarter)]
    [InlineData(450, MoonPhaseName.FirstQuarter)]
    public void The_phase_name_follows_the_phase_angle(double angle, MoonPhaseName expected) =>
        Assert.Equal(expected, MoonSkyContext.NameOf(angle));

    [Fact]
    public void Separation_between_directions_is_right_at_the_obvious_points()
    {
        Assert.Equal(0, MoonSkyContext.Separation(5, 20, 5, 20), 6);
        Assert.Equal(90, MoonSkyContext.Separation(0, 0, 6, 0), 6);
        Assert.Equal(180, MoonSkyContext.Separation(0, 0, 12, 0), 6);
        Assert.Equal(180, MoonSkyContext.Separation(3, 90, 9, -90), 6);
        Assert.Equal(60, MoonSkyContext.Separation(0, 0, 0, 60), 6);
    }

    [Fact]
    public void Illumination_and_phase_angle_agree_with_each_other()
    {
        // The lit fraction of the disc is (1 - cos(elongation)) / 2, with the elongation the angle from the Sun.
        for (var day = 0; day < 30; day += 3)
        {
            var c = MoonSkyContext.At(Utc(2026, 11, 1).AddDays(day), London, 144, null, 384_400)!;
            var expected = 50.0 * (1.0 - Math.Cos(c.PhaseAngleDeg * Math.PI / 180.0));
            Assert.Equal(expected, c.IlluminatedPercent, 3.0);
        }
    }

    [Fact]
    public void The_apparent_diameter_follows_the_distance()
    {
        var mean = MoonSkyContext.At(Utc(2026, 11, 16), London, 144, null, 384_400)!;
        var near = MoonSkyContext.At(Utc(2026, 11, 16), London, 144, null, 356_500)!;
        var far = MoonSkyContext.At(Utc(2026, 11, 16), London, 144, null, 406_700)!;
        // 2 asin(1737.4 / 384,400) = 0.5186 degrees = 31.1 arcminutes.
        Assert.Equal(31.1, mean.ApparentDiameterArcmin, 0.05);
        Assert.True(near.ApparentDiameterArcmin > mean.ApparentDiameterArcmin);
        Assert.True(far.ApparentDiameterArcmin < mean.ApparentDiameterArcmin);
        Assert.Equal(33.5, near.ApparentDiameterArcmin, 0.1);
        Assert.Equal(29.4, far.ApparentDiameterArcmin, 0.1);
    }

    [Fact]
    public void The_sun_separation_agrees_with_the_geocentric_elongation_to_within_parallax()
    {
        for (var day = 0; day < 30; day += 4)
        {
            var t = Utc(2026, 11, 1).AddDays(day);
            var c = MoonSkyContext.At(t, London, 144, null, 384_400)!;
            var elongation = Astronomy.AngleFromSun(Body.Moon, new AstroTime(t));
            Assert.Equal(elongation, c.SunSeparationDeg, 1.2);
        }
    }

    [Fact]
    public void The_sun_flag_is_ten_degrees_or_the_beamwidth_if_that_is_larger()
    {
        var t = Utc(2026, 11, 16);
        Assert.Equal(10, MoonSkyContext.At(t, London, 144, null, 384_400)!.SunFlagAngleDeg);
        Assert.Equal(10, MoonSkyContext.At(t, London, 144, 5, 384_400)!.SunFlagAngleDeg);
        Assert.Equal(15, MoonSkyContext.At(t, London, 144, 15, 384_400)!.SunFlagAngleDeg);
        Assert.Equal(10, MoonSkyContext.At(t, London, 144, double.NaN, 384_400)!.SunFlagAngleDeg);
    }

    [Fact]
    public void The_flag_is_raised_when_the_moon_is_inside_the_flag_angle()
    {
        // New Moon on 9 Nov 2026 is within about a degree or two of the Sun in the sky, and the full Moon is not.
        var days = Enumerable.Range(0, 30).Select(d => MoonSkyContext.At(Utc(2026, 11, 1).AddDays(d), London, 144, null, 384_400)!).ToList();
        Assert.Contains(days, c => c.SunTooClose);
        Assert.Contains(days, c => !c.SunTooClose);
        Assert.All(days, c => Assert.Equal(c.SunSeparationDeg < 10, c.SunTooClose));
    }

    [Theory]
    [InlineData(144, true)]
    [InlineData(999.9, true)]
    [InlineData(1000, false)]
    [InlineData(1296, false)]
    [InlineData(10368, false)]
    public void The_galactic_centre_is_only_worked_out_below_1_ghz(double mhz, bool present)
    {
        var c = MoonSkyContext.At(Utc(2026, 11, 16), London, mhz, null, 384_400)!;
        Assert.Equal(present, c.GalacticCentreSeparationDeg is not null);
        if (present)
            Assert.InRange(c.GalacticCentreSeparationDeg!.Value, 0, 180);
    }

    [Fact]
    public void The_moon_passes_close_to_the_galactic_centre_about_once_a_month()
    {
        // The Moon's path crosses the ecliptic about 5 degrees from the Galactic Centre's direction, so over a
        // year it comes within 10 degrees of it several times, and is often far away.
        var separations = Enumerable.Range(0, 365)
            .Select(d => MoonSkyContext.At(Utc(2026, 1, 1).AddDays(d), London, 144, null, 384_400)!.GalacticCentreSeparationDeg!.Value)
            .ToList();
        Assert.Contains(separations, s => s < 10);
        Assert.Contains(separations, s => s > 120);
    }

    [Fact]
    public void The_apsides_are_in_the_right_order_and_the_right_range()
    {
        for (var day = 0; day < 60; day += 5)
        {
            var t = Utc(2026, 11, 1).AddDays(day);
            var c = MoonSkyContext.At(t, London, 144, null, 384_400)!;
            Assert.True(c.NextPerigee.Utc > t && c.NextApogee.Utc > t);
            Assert.InRange(c.NextPerigee.DistanceKm, 356_300, 370_500);
            Assert.InRange(c.NextApogee.DistanceKm, 404_000, 406_800);
            // Successive apsides are between about 11 and 17 days apart, because the Sun disturbs the orbit.
            Assert.InRange(c.DaysSinceLastApsis, 0, 17.5);
            // The apsis that follows the last one is of the other kind, so it comes before the next of the same kind.
            Assert.Equal(c.LastApsisWasPerigee, c.NextApogee.Utc < c.NextPerigee.Utc);
        }
    }

    [Fact]
    public void A_station_or_distance_that_is_not_a_number_gives_no_context()
    {
        var t = Utc(2026, 11, 16);
        Assert.Null(MoonSkyContext.At(t, new StationPosition(double.NaN, 0, 0), 144, null, 384_400));
        Assert.Null(MoonSkyContext.At(t, London, 144, null, double.NaN));
        Assert.Null(MoonSkyContext.At(t, London, 144, null, 0));
    }

    [Fact]
    public void The_galactic_centre_constant_is_where_sagittarius_a_star_is()
    {
        // 17h 45m 40s, -29 deg 00' 28": the Sun is near it in the sky around 18 December.
        Assert.Equal(17.761, MoonSkyContext.GalacticCentreRaHours, 0.001);
        Assert.Equal(-29.008, MoonSkyContext.GalacticCentreDecDeg, 0.001);
    }
}
