using OscarWatch.Core.Eme;
using OscarWatch.Core.Models;
using Xunit.Abstractions;

namespace OscarWatch.Tests;

/// <summary>
/// When the Moon is up at a station. Rise, transit and set against the US Naval Observatory almanac (fetched
/// 17 Nov 2026), allowing for the almanac using the upper limb and refraction while the rule uses the centre at
/// the elevation limit, and against the library's own elevation at the returned times.
/// </summary>
public sealed class MoonWindowsTests(ITestOutputHelper output)
{
    private static readonly StationPosition London = new(51.5, -0.1, 50);
    private static readonly StationPosition Sydney = new(-33.87, 151.21, 50);
    private static readonly MoonVisibilityRule Plain = MoonVisibilityRule.Default;

    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static double Elevation(StationPosition s, DateTime t) =>
        MoonEphemeris.At(t, s.LatitudeDeg, s.LongitudeDeg, s.HeightMetres)!.ElevationDeg;

    private static void AssertSeconds(DateTime expected, DateTime actual, double seconds = 2) =>
        Assert.True(Math.Abs((actual - expected).TotalSeconds) <= seconds, $"expected {expected:HH:mm:ss.fff}, got {actual:HH:mm:ss.fff}");

    private static void AssertWithinMinutes(DateTime expected, DateTime actual, double minutes, string what) =>
        Assert.True(Math.Abs((actual - expected).TotalMinutes) <= minutes, $"{what}: expected about {expected:HH:mm}, got {actual:HH:mm:ss}");

    [Fact]
    public void Moon_in_london_on_16_november_matches_the_almanac()
    {
        // USNO, 16 Nov 2026, 51.5 N 0.1 W, UT: rise 12:59, upper transit 17:32, set 22:15.
        var moon = Assert.Single(MoonWindows.Find(Plain, London, Utc(2026, 11, 16, 6), Utc(2026, 11, 17)),
            p => p.AosUtc.Date == new DateTime(2026, 11, 16));
        AssertWithinMinutes(Utc(2026, 11, 16, 17, 32), moon.MaxElevationUtc, 2, "transit");
        AssertWithinMinutes(Utc(2026, 11, 16, 12, 59), moon.AosUtc, 9, "rise");
        AssertWithinMinutes(Utc(2026, 11, 16, 22, 15), moon.LosUtc, 9, "set");
        Assert.Equal(CelestialBodyKind.Moon, moon.Body);
        Assert.Equal("MOON", moon.NoradId);
        // The Moon rises in the south east and sets in the south west on this date.
        Assert.InRange(moon.AosAzimuthDeg, 105, 130);
        Assert.InRange(moon.LosAzimuthDeg, 230, 255);
    }

    [Fact]
    public void The_centre_rule_rises_after_and_sets_before_the_almanac_times()
    {
        var moon = MoonWindows.Find(Plain, London, Utc(2026, 11, 16, 6), Utc(2026, 11, 17)).First(p => p.AosUtc.Date == new DateTime(2026, 11, 16));
        output.WriteLine($"rise {moon.AosUtc:HH:mm:ss} (almanac 12:59), set {moon.LosUtc:HH:mm:ss} (almanac 22:15)");
        Assert.True(moon.AosUtc > Utc(2026, 11, 16, 12, 58), "centre at 0 degrees rises after the upper limb");
        Assert.True(moon.LosUtc < Utc(2026, 11, 16, 22, 16), "centre at 0 degrees sets before the upper limb");
    }

    [Fact]
    public void A_moon_already_up_at_the_start_is_listed_with_its_real_rise_time()
    {
        var passes = MoonWindows.Find(Plain, London, Utc(2026, 11, 16, 18), Utc(2026, 11, 17, 18));
        var first = passes[0];
        Assert.True(first.AosUtc < Utc(2026, 11, 16, 18), "rise is before the start");
        Assert.True(first.LosUtc > Utc(2026, 11, 16, 18), "set is after the start");
        AssertWithinMinutes(Utc(2026, 11, 16, 12, 59), first.AosUtc, 9, "rise");
    }

    [Fact]
    public void Entries_are_in_order_and_do_not_overlap()
    {
        var passes = MoonWindows.Find(Plain, London, Utc(2026, 11, 1), Utc(2026, 11, 15));
        Assert.True(passes.Count >= 12, $"{passes.Count} entries in 14 days");
        for (var i = 0; i < passes.Count; i++)
        {
            Assert.True(passes[i].LosUtc > passes[i].AosUtc);
            if (i > 0)
                Assert.True(passes[i].AosUtc > passes[i - 1].LosUtc);
        }
    }

    [Fact]
    public void Maximum_elevation_is_the_highest_the_moon_gets_between_rise_and_set()
    {
        var moon = MoonWindows.Find(Plain, London, Utc(2026, 11, 16, 6), Utc(2026, 11, 17))[0];
        var highest = double.MinValue;
        for (var t = moon.AosUtc; t <= moon.LosUtc; t = t.AddMinutes(1))
            highest = Math.Max(highest, Elevation(London, t));
        output.WriteLine($"reported {moon.MaxElevationDeg:0.000}, sampled {highest:0.000}");
        Assert.Equal(highest, moon.MaxElevationDeg, 0.05);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(-5)]
    [InlineData(45)]
    public void The_moon_is_at_the_limit_at_the_returned_rise_and_set(double limit)
    {
        var rule = new MoonVisibilityRule(limit, 90, null);
        var passes = MoonWindows.Find(rule, Sydney, Utc(2026, 11, 14), Utc(2026, 11, 20));
        Assert.NotEmpty(passes);
        foreach (var pass in passes)
        {
            Assert.Equal(limit, Elevation(Sydney, pass.AosUtc), 0.05);
            Assert.Equal(limit, Elevation(Sydney, pass.LosUtc), 0.05);
        }
    }

    [Fact]
    public void A_lower_limit_gives_a_longer_window()
    {
        var day = (Utc(2026, 11, 16, 6), Utc(2026, 11, 17));
        var zero = MoonWindows.Find(Plain, London, day.Item1, day.Item2)[0];
        var below = MoonWindows.Find(new MoonVisibilityRule(-10, 90, null), London, day.Item1, day.Item2)[0];
        var above = MoonWindows.Find(new MoonVisibilityRule(10, 90, null), London, day.Item1, day.Item2)[0];
        Assert.True(below.Duration > zero.Duration);
        Assert.True(zero.Duration > above.Duration);
    }

    [Fact]
    public void A_limit_above_the_highest_point_gives_no_window_and_no_error()
    {
        // From London the Moon never gets above about 67 degrees.
        Assert.Empty(MoonWindows.Find(new MoonVisibilityRule(75, 90, null), London, Utc(2026, 11, 1), Utc(2026, 12, 1)));
        Assert.Empty(MoonWindows.Find(new MoonVisibilityRule(90, 90, null), London, Utc(2026, 11, 1), Utc(2026, 11, 8)));
    }

    [Theory]
    [InlineData(35.78, -78.64, 2024, 8, 16, 26.1)]
    [InlineData(35.78, -78.64, 2024, 8, 28, 82.2)]
    public void Maximum_elevation_follows_90_minus_latitude_plus_declination(double lat, double lon, int y, int m, int d, double expected)
    {
        // The W4ATC guide (WY3O, August 2024) works Raleigh through: the Moon's declination of -28.1 degrees on
        // 16 August gives 26.1 degrees and about +28 on 28 August gives 82.2. The topocentric elevation is about
        // a degree lower in the first case because of parallax, so the tolerance is 1.5 degrees.
        var station = new StationPosition(lat, lon, 100);
        var best = MoonWindows.Find(Plain, station, Utc(y, m, d), Utc(y, m, d + 1)).Max(p => p.MaxElevationDeg);
        output.WriteLine($"{y}-{m:00}-{d:00}: {best:0.00} (guide {expected})");
        Assert.InRange(best, expected - 1.5, expected + 1.0);
    }

    [Fact]
    public void The_polar_regions_can_have_no_rise_at_all_and_never_throw()
    {
        // At 89 degrees north the Moon stays above or below the horizon for days at a time.
        var passes = MoonWindows.Find(Plain, new StationPosition(89, 0, 0), Utc(2026, 11, 1), Utc(2026, 11, 20));
        Assert.All(passes, p => Assert.True(p.LosUtc > p.AosUtc));
        Assert.True(passes.Count <= 2);
    }

    [Fact]
    public void A_bad_range_or_position_gives_nothing()
    {
        Assert.Empty(MoonWindows.Find(Plain, London, Utc(2026, 11, 17), Utc(2026, 11, 16)));
        Assert.Empty(MoonWindows.Find(Plain, new StationPosition(double.NaN, 0, 0), Utc(2026, 11, 16), Utc(2026, 11, 17)));
        Assert.Empty(MoonWindows.Find(Plain, new StationPosition(0, 0, double.NaN), Utc(2026, 11, 16), Utc(2026, 11, 17)));
    }

    [Fact]
    public void Times_are_utc_and_the_start_kind_does_not_matter()
    {
        var fromUtc = MoonWindows.Find(Plain, London, Utc(2026, 11, 16, 6), Utc(2026, 11, 17));
        var fromUnspecified = MoonWindows.Find(Plain, London,
            DateTime.SpecifyKind(Utc(2026, 11, 16, 6), DateTimeKind.Unspecified),
            DateTime.SpecifyKind(Utc(2026, 11, 17), DateTimeKind.Unspecified));
        Assert.Equal(fromUtc.Select(p => p.AosUtc), fromUnspecified.Select(p => p.AosUtc));
        Assert.All(fromUtc, p => Assert.Equal(DateTimeKind.Utc, p.AosUtc.Kind));
    }

    [Fact]
    public void The_scan_path_agrees_with_the_altitude_search()
    {
        // A maximum just below 90 forces the general path, which scans in one minute steps. The Moon never
        // reaches that elevation, so the windows must match those from the faster search.
        var scan = new MoonVisibilityRule(0, 89.999, null);
        Assert.False(scan.IsPlainLimit);
        var fast = MoonWindows.Find(Plain, London, Utc(2026, 11, 14), Utc(2026, 11, 19));
        var slow = MoonWindows.Find(scan, London, Utc(2026, 11, 14), Utc(2026, 11, 19));
        Assert.Equal(fast.Count, slow.Count);
        for (var i = 0; i < fast.Count; i++)
        {
            AssertSeconds(fast[i].AosUtc, slow[i].AosUtc);
            AssertSeconds(fast[i].LosUtc, slow[i].LosUtc);
            Assert.Equal(fast[i].MaxElevationDeg, slow[i].MaxElevationDeg, 0.05);
        }
    }

    [Fact]
    public void A_maximum_elevation_splits_a_high_pass_into_two_windows()
    {
        var day = (Utc(2026, 11, 16, 6), Utc(2026, 11, 17));
        var whole = MoonWindows.Find(Plain, London, day.Item1, day.Item2)[0];
        var cap = whole.MaxElevationDeg - 5;
        var rule = new MoonVisibilityRule(0, cap, null);
        var parts = MoonWindows.Find(rule, London, day.Item1, day.Item2);
        Assert.Equal(2, parts.Count);
        AssertSeconds(whole.AosUtc, parts[0].AosUtc);
        AssertSeconds(whole.LosUtc, parts[1].LosUtc);
        Assert.Equal(cap, Elevation(London, parts[0].LosUtc), 0.05);
        Assert.Equal(cap, Elevation(London, parts[1].AosUtc), 0.05);
        Assert.True(parts[0].MaxElevationDeg <= cap + 0.05);
    }

    [Fact]
    public void A_mask_of_one_height_all_round_is_the_same_as_that_limit()
    {
        var mask = new HorizonMask { Points = [new(0, 20), new(90, 20), new(180, 20), new(270, 20)] };
        var byMask = MoonWindows.Find(new MoonVisibilityRule(0, 90, mask), Sydney, Utc(2026, 11, 14), Utc(2026, 11, 19));
        var byLimit = MoonWindows.Find(new MoonVisibilityRule(20, 90, null), Sydney, Utc(2026, 11, 14), Utc(2026, 11, 19));
        Assert.Equal(byLimit.Count, byMask.Count);
        for (var i = 0; i < byLimit.Count; i++)
        {
            AssertSeconds(byLimit[i].AosUtc, byMask[i].AosUtc);
            AssertSeconds(byLimit[i].LosUtc, byMask[i].LosUtc);
        }
    }

    [Fact]
    public void An_empty_mask_changes_nothing()
    {
        var plain = MoonWindows.Find(Plain, London, Utc(2026, 11, 14), Utc(2026, 11, 17));
        var empty = MoonWindows.Find(new MoonVisibilityRule(0, 90, new HorizonMask()), London, Utc(2026, 11, 14), Utc(2026, 11, 17));
        Assert.Equal(plain.Select(p => p.AosUtc), empty.Select(p => p.AosUtc));
    }

    [Fact]
    public void A_mask_that_is_higher_to_the_south_shortens_the_window_on_that_side()
    {
        // 16 Nov: the Moon rises in the south east and sets in the south west, low in the southern sky.
        var mask = new HorizonMask { Points = [new(0, 0), new(90, 0), new(180, 8), new(270, 0)] };
        var without = MoonWindows.Find(Plain, London, Utc(2026, 11, 16, 6), Utc(2026, 11, 17))[0];
        var with = MoonWindows.Find(new MoonVisibilityRule(0, 90, mask), London, Utc(2026, 11, 16, 6), Utc(2026, 11, 17))[0];
        output.WriteLine($"without {without.AosUtc:HH:mm:ss}-{without.LosUtc:HH:mm:ss} max {without.MaxElevationDeg:0.0}; with {with.AosUtc:HH:mm:ss}-{with.LosUtc:HH:mm:ss} max {with.MaxElevationDeg:0.0}");
        Assert.True(with.AosUtc > without.AosUtc);
        Assert.True(with.LosUtc < without.LosUtc);
    }

    /// <summary>Brute force, one minute at a time with the ephemeris alone, for the edges of the spell the Moon is in at <paramref name="at"/>.</summary>
    private static (DateTime Rise, DateTime Set) SpellAround(StationPosition st, DateTime at)
    {
        var rise = at;
        while (Elevation(st, rise.AddMinutes(-1)) >= 0)
            rise = rise.AddMinutes(-1);
        var set = at;
        while (Elevation(st, set.AddMinutes(1)) >= 0)
            set = set.AddMinutes(1);
        return (rise, set);
    }

    [Fact]
    public void A_moon_that_has_been_up_for_days_near_the_pole_is_listed_with_its_real_rise_and_set()
    {
        // At 80 N the Moon stays up for about a week. On 1 Nov 2026 at 00:00 UTC it had already been up for
        // 179 hours, which is more than the two day look-back the first version used.
        var pole = new StationPosition(80, 0, 0);
        var start = Utc(2026, 11, 1);
        Assert.True(Elevation(pole, start) > 0);
        var (rise, set) = SpellAround(pole, start);
        Assert.True((start - rise).TotalHours > 72, "the case must be a multi-day spell");

        foreach (var rule in new[] { MoonVisibilityRule.Default, new MoonVisibilityRule(0, 89.999, null) })
        {
            var first = MoonWindows.Find(rule, pole, start, start.AddDays(2))[0];
            AssertSeconds(rise, first.AosUtc, 90);
            AssertSeconds(set, first.LosUtc, 90);
        }
    }

    [Fact]
    public void The_rise_of_a_multi_day_moon_does_not_move_as_the_list_is_rebuilt()
    {
        var pole = new StationPosition(80, 0, 0);
        foreach (var rule in new[] { MoonVisibilityRule.Default, new MoonVisibilityRule(0, 89.999, null) })
        {
            var a = MoonWindows.Find(rule, pole, Utc(2026, 11, 1), Utc(2026, 11, 2))[0];
            var b = MoonWindows.Find(rule, pole, Utc(2026, 11, 1, 6), Utc(2026, 11, 2, 6))[0];
            AssertSeconds(a.AosUtc, b.AosUtc, 90);
            AssertSeconds(a.LosUtc, b.LosUtc, 90);
        }
    }

    [Fact]
    public void A_moon_still_up_at_the_end_of_the_range_has_its_real_set_in_both_paths()
    {
        var pole = new StationPosition(80, 0, 0);
        var start = Utc(2026, 10, 27);
        var end = start.AddDays(1);
        var (rise, set) = SpellAround(pole, end.AddHours(-1));
        Assert.True(set > end.AddDays(1), "the case must outlast the range");
        foreach (var rule in new[] { MoonVisibilityRule.Default, new MoonVisibilityRule(0, 89.999, null) })
        {
            var window = MoonWindows.Find(rule, pole, start, end).Last(p => p.AosUtc < end);
            AssertSeconds(set, window.LosUtc, 90);
        }
    }

    [Fact]
    public void The_peak_of_a_moon_already_up_is_the_peak_of_the_whole_window()
    {
        // From 20:00 the Moon is past its 17:36 transit. The scan path used to report the elevation at the
        // start (14.5 degrees) as the maximum instead of 20.8.
        foreach (var rule in new[] { MoonVisibilityRule.Default, new MoonVisibilityRule(0, 89.999, null) })
        {
            var whole = MoonWindows.Find(rule, London, Utc(2026, 11, 16, 6), Utc(2026, 11, 17))[0];
            var late = MoonWindows.Find(rule, London, Utc(2026, 11, 16, 20), Utc(2026, 11, 17))[0];
            Assert.Equal(whole.MaxElevationDeg, late.MaxElevationDeg, 0.05);
            Assert.True(Math.Abs((whole.MaxElevationUtc - late.MaxElevationUtc).TotalMinutes) <= 2);
            AssertSeconds(whole.AosUtc, late.AosUtc);
        }
    }
}
