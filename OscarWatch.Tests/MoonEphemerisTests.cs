using OscarWatch.Core.Eme;
using Xunit.Abstractions;
namespace OscarWatch.Tests;
/// <summary>
/// Moon azimuth, elevation, distance and range rate against Skyfield 1.55 with JPL DE421, which is a different
/// implementation and a different ephemeris from Astronomy Engine. The reference values were generated once
/// (apparent, geometric elevation of the Moon's centre, WGS84 height, no refraction) and are stored here as
/// constants. The generator script is not part of the repository.
/// </summary>
public sealed class MoonEphemerisTests(ITestOutputHelper output)
{
    private sealed record Reference(
        string Place, double Lat, double Lon, double Height, DateTime Utc,
        double AzimuthDeg, double ElevationDeg, double DistanceKm, double RangeChangeKmPerS, double GeocentricKm);
    private static Reference New(string place, double lat, double lon, double h, DateTime t, double az, double el, double dist, double dDist, double geo) =>
        new(place, lat, lon, h, t, az, el, dist, dDist, geo);
    private static readonly Reference[] References =
    [
        New("London",51.5074,-0.1278,11,new DateTime(2026,1,3,6,0,0,DateTimeKind.Utc),288.5406,19.6216,359721.0,0.27536,361901.5),
        New("London",51.5074,-0.1278,11,new DateTime(2026,3,20,12,30,0,DateTimeKind.Utc),163.7470,47.4986,364236.9,-0.08348,368968.3),
        New("London",51.5074,-0.1278,11,new DateTime(2026,6,15,0,0,0,DateTimeKind.Utc),1.7750,-11.5357,358435.3,-0.00823,357196.4),
        New("London",51.5074,-0.1278,11,new DateTime(2026,9,1,18,45,0,DateTimeKind.Utc),47.2254,-8.8589,376626.5,-0.23751,375685.1),
        New("London",51.5074,-0.1278,11,new DateTime(2026,11,16,13,0,0,DateTimeKind.Utc),119.5318,-0.6844,401196.4,-0.28669,401181.1),
        New("London",51.5074,-0.1278,11,new DateTime(2027,2,10,3,15,0,DateTimeKind.Utc),11.8557,-35.4207,396757.4,-0.09308,393085.6),
        New("Sydney",-33.8688,151.2093,58,new DateTime(2026,1,3,6,0,0,DateTimeKind.Utc),79.0643,-37.6430,365754.3,-0.26091,361901.5),
        New("Sydney",-33.8688,151.2093,58,new DateTime(2026,3,20,12,30,0,DateTimeKind.Utc),246.7868,-47.6811,373659.8,0.20889,368968.3),
        New("Sydney",-33.8688,151.2093,58,new DateTime(2026,6,15,0,0,0,DateTimeKind.Utc),25.5812,22.2781,354716.2,-0.14584,357196.4),
        New("Sydney",-33.8688,151.2093,58,new DateTime(2026,9,1,18,45,0,DateTimeKind.Utc),335.3877,33.3918,372126.2,0.10531,375685.1),
        New("Sydney",-33.8688,151.2093,58,new DateTime(2026,11,16,13,0,0,DateTimeKind.Utc),257.5573,12.5802,399749.0,0.32164,401181.1),
        New("Sydney",-33.8688,151.2093,58,new DateTime(2027,2,10,3,15,0,DateTimeKind.Utc),29.4794,48.2460,388298.1,-0.15823,393085.6),
        New("Tromso",69.6492,18.9553,10,new DateTime(2026,1,3,6,0,0,DateTimeKind.Utc),298.0688,18.0643,359885.5,0.15851,361901.5),
        New("Tromso",69.6492,18.9553,10,new DateTime(2026,3,20,12,30,0,DateTimeKind.Utc),189.3136,29.9033,365744.6,-0.01031,368968.3),
        New("Tromso",69.6492,18.9553,10,new DateTime(2026,6,15,0,0,0,DateTimeKind.Utc),18.7351,7.7635,356294.9,-0.04901,357196.4),
        New("Tromso",69.6492,18.9553,10,new DateTime(2026,9,1,18,45,0,DateTimeKind.Utc),64.2668,9.0155,374642.1,-0.17434,375685.1),
        New("Tromso",69.6492,18.9553,10,new DateTime(2026,11,16,13,0,0,DateTimeKind.Utc),135.8529,-4.2457,401591.8,-0.15233,401181.1),
        New("Tromso",69.6492,18.9553,10,new DateTime(2027,2,10,3,15,0,DateTimeKind.Utc),29.8309,-15.4810,394747.0,-0.12244,393085.6)
    ];
    public static TheoryData<int> Indexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < References.Length; i++)
            data.Add(i);
        return data;
    }
    [Theory]
    [MemberData(nameof(Indexes))]
    public void Position_agrees_with_skyfield_and_de421(int index)
    {
        var r = References[index];
        var s = MoonEphemeris.At(r.Utc, r.Lat, r.Lon, r.Height);
        Assert.NotNull(s);
        var dAz = Math.Abs(((s.AzimuthDeg - r.AzimuthDeg + 540) % 360) - 180);
        var dEl = Math.Abs(s.ElevationDeg - r.ElevationDeg);
        output.WriteLine($"{r.Place} {r.Utc:yyyy-MM-dd HH:mm}: az {s.AzimuthDeg:0.0000} ({dAz:0.0000}), el {s.ElevationDeg:0.0000} ({dEl:0.0000}), dist {s.DistanceKm:0.0} ({s.DistanceKm - r.DistanceKm:0.0} km), rate {s.RangeRateKmPerS:0.0000} vs {r.RangeChangeKmPerS:0.0000}");
        // Azimuth error matters less near the zenith; elevation is the one that matters most. Requirement 1.6: 0.1 degrees.
        Assert.True(dEl <= 0.1, $"elevation off by {dEl} degrees");
        Assert.True(dAz * Math.Cos(Math.Min(Math.Abs(r.ElevationDeg), 89) * Math.PI / 180) <= 0.1, $"azimuth off by {dAz} degrees");
        Assert.True(Math.Abs(s.DistanceKm - r.DistanceKm) / r.DistanceKm <= 0.0005, $"distance off by {s.DistanceKm - r.DistanceKm} km");
        Assert.True(Math.Abs(s.GeocentricDistanceKm - r.GeocentricKm) / r.GeocentricKm <= 0.0005);
    }
    [Theory]
    [MemberData(nameof(Indexes))]
    public void Range_rate_agrees_with_the_reference_and_with_a_finite_difference(int index)
    {
        var r = References[index];
        var s = MoonEphemeris.At(r.Utc, r.Lat, r.Lon, r.Height)!;
        // Skyfield's change in distance over one second, to a few metres per second.
        Assert.Equal(r.RangeChangeKmPerS, s.RangeRateKmPerS, 0.01);
        // And the library's own distance either side of the time.
        var before = MoonEphemeris.At(r.Utc.AddSeconds(-30), r.Lat, r.Lon, r.Height)!;
        var after = MoonEphemeris.At(r.Utc.AddSeconds(30), r.Lat, r.Lon, r.Height)!;
        var difference = (after.DistanceKm - before.DistanceKm) / 60.0;
        Assert.Equal(difference, s.RangeRateKmPerS, 0.005);
    }
    [Fact]
    public void Own_echo_shift_at_144_mhz_follows_minus_two_f_v_over_c()
    {
        // Skyfield gives -0.28669 km/s at London on 16 Nov 2026 13:00 UTC (the Moon approaching), so the own
        // echo is shifted up by 2 x 144.174 MHz x 286.69 m/s / c = +275.7 Hz. The Moon is below the horizon then,
        // which does not matter for the Doppler arithmetic. Receding gives a negative shift, as in the design
        // check of -209 Hz at a range rate of +0.217 km/s.
        var s = MoonEphemeris.At(new DateTime(2026, 11, 16, 13, 0, 0, DateTimeKind.Utc), 51.5074, -0.1278, 11)!;
        var shiftHz = -2.0 * 144.174e6 * (s.RangeRateKmPerS * 1000.0) / 299_792_458.0;
        output.WriteLine($"distance {s.DistanceKm:0} km, rate {s.RangeRateKmPerS:0.0000} km/s, shift {shiftHz:0.0} Hz");
        Assert.InRange(shiftHz, 274.0, 277.5);
        var designCheck = -2.0 * 144.174e6 * 217.0 / 299_792_458.0;
        Assert.Equal(-209, designCheck, 0.5);
    }

    [Fact]
    public void The_station_height_changes_the_distance_by_about_the_height_times_sin_elevation()
    {
        var t = new DateTime(2026, 3, 20, 12, 30, 0, DateTimeKind.Utc);
        var low = MoonEphemeris.At(t, 51.5074, -0.1278, 0)!;
        var high = MoonEphemeris.At(t, 51.5074, -0.1278, 3000)!;
        var expected = 3.0 * Math.Sin(low.ElevationDeg * Math.PI / 180);
        Assert.Equal(expected, low.DistanceKm - high.DistanceKm, 0.15);
    }
    [Fact]
    public void Hour_angle_is_zero_at_the_meridian_and_the_sublunar_point_is_overhead()
    {
        // At the sublunar point the Moon is almost overhead, and its local hour angle is about zero.
        var t = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var any = MoonEphemeris.At(t, 0, 0, 0)!;
        var overhead = MoonEphemeris.At(t, any.SublunarLatitudeDeg, any.SublunarLongitudeDeg, 0)!;
        output.WriteLine($"sublunar {any.SublunarLatitudeDeg:0.00}, {any.SublunarLongitudeDeg:0.00}: elevation there {overhead.ElevationDeg:0.00}");
        Assert.True(overhead.ElevationDeg > 89.0, $"elevation {overhead.ElevationDeg}");
        var ha = overhead.LocalHourAngleDeg;
        Assert.True(ha < 1.5 || ha > 358.5, $"hour angle {ha}");
        // Greenwich hour angle plus east longitude gives the local hour angle (geocentric and topocentric agree to about a degree).
        var local = MoonEphemeris.At(t, 40, 30, 0)!;
        var expected = (local.GreenwichHourAngleDeg + 30 + 360) % 360;
        Assert.True(Math.Abs(((local.LocalHourAngleDeg - expected + 540) % 360) - 180) < 1.5);
    }
    [Fact]
    public void Declination_and_right_ascension_are_in_range_and_declination_stays_within_29_degrees()
    {
        for (var d = 0; d < 60; d++)
        {
            var s = MoonEphemeris.At(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(d), 51.5, -0.1, 50)!;
            Assert.InRange(s.RightAscensionHours, 0, 24);
            Assert.InRange(s.DeclinationDeg, -30, 30);
            Assert.InRange(s.SublunarLatitudeDeg, -29.5, 29.5);
            Assert.InRange(s.SublunarLongitudeDeg, -180, 180);
        }
    }
    [Theory]
    [InlineData(double.NaN, 0, 0)]
    [InlineData(0, double.PositiveInfinity, 0)]
    [InlineData(0, 0, double.NaN)]
    [InlineData(95, 0, 0)]
    public void A_station_that_is_not_a_number_gives_no_position(double lat, double lon, double height)
    {
        Assert.Null(MoonEphemeris.At(new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc), lat, lon, height));
    }
    [Fact]
    public void A_time_outside_the_supported_range_gives_no_position_and_does_not_throw()
    {
        Assert.Null(MoonEphemeris.At(DateTime.MinValue, 51.5, -0.1, 0));
        Assert.Null(MoonEphemeris.At(DateTime.MaxValue, 51.5, -0.1, 0));
    }
    [Theory]
    [InlineData(90)]
    [InlineData(-90)]
    [InlineData(0)]
    public void The_poles_and_the_equator_work(double lat)
    {
        var s = MoonEphemeris.At(new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc), lat, 10, 0);
        Assert.NotNull(s);
        Assert.True(double.IsFinite(s.AzimuthDeg) && double.IsFinite(s.ElevationDeg));
    }
    [Fact]
    public void The_kind_of_the_time_does_not_matter()
    {
        var utc = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var a = MoonEphemeris.At(utc, 51.5, -0.1, 0)!;
        var b = MoonEphemeris.At(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified), 51.5, -0.1, 0)!;
        Assert.Equal(a.ElevationDeg, b.ElevationDeg);
        Assert.Equal(DateTimeKind.Utc, a.TimeUtc.Kind);
    }
}
