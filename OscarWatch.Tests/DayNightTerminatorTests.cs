using OscarWatch.Core.Geo;

namespace OscarWatch.Tests;

public sealed class DayNightTerminatorTests
{
    [Fact]
    public void Subsolar_longitude_advances_with_utc()
    {
        var day = new DateTime(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc);
        var a = DayNightTerminator.GetSubsolarPoint(day);
        var b = DayNightTerminator.GetSubsolarPoint(day.AddHours(1));
        var delta = Math.Abs(b.LongitudeDeg - a.LongitudeDeg);
        if (delta > 180)
            delta = 360 - delta;

        Assert.InRange(delta, 10, 20);
    }

    [Fact]
    public void London_midday_june_is_daylight()
    {
        var utc = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(DayNightTerminator.IsSunAboveHorizon(51.5, -0.1, utc));
    }

    [Fact]
    public void Terminator_ring_has_valid_latitudes()
    {
        var utc = new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc);
        var geometry = DayNightTerminator.GetGeometry(utc);
        Assert.True(geometry.Terminator.Count >= 200);
        foreach (var p in geometry.Terminator)
        {
            Assert.InRange(p.LatitudeDeg, -90, 90);
            Assert.InRange(p.LongitudeDeg, -180, 180);
        }
    }

    [Fact]
    public void June_morning_terminator_lies_in_southern_hemisphere()
    {
        var utc = new DateTime(2026, 6, 4, 9, 28, 0, DateTimeKind.Utc);
        var geometry = DayNightTerminator.GetGeometry(utc);
        Assert.True(geometry.NightTowardSouth);
        Assert.True(geometry.SubsolarLatitudeDeg > 10);
        var lon0 = DayNightTerminator.GetTerminatorLatitudeDeg(0, utc);
        Assert.True(lon0 < 0, $"Expected southern terminator lat, got {lon0}");
    }

    [Theory]
    [InlineData(2026, 6, 21, 15, 0)]
    [InlineData(2026, 10, 9, 18, 30)]
    [InlineData(2026, 12, 21, 12, 0)]
    public void Night_side_of_terminator_matches_solar_elevation(int year, int month, int day, int hour, int minute)
    {
        var utc = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);
        var geometry = DayNightTerminator.GetGeometry(utc);
        Assert.Equal(geometry.SubsolarLatitudeDeg >= 0, geometry.NightTowardSouth);

        foreach (var lon in new[] { -120.0, -30.0, 0.0, 10.0, 90.0, 160.0 })
        {
            var termLat = DayNightTerminator.GetTerminatorLatitudeDeg(lon, utc);
            const double step = 1.5;
            var northLat = Math.Clamp(termLat + step, -89, 89);
            var southLat = Math.Clamp(termLat - step, -89, 89);
            if (Math.Abs(northLat - termLat) < 0.5 || Math.Abs(southLat - termLat) < 0.5)
                continue;

            var northDay = DayNightTerminator.IsSunAboveHorizon(northLat, lon, utc);
            var southDay = DayNightTerminator.IsSunAboveHorizon(southLat, lon, utc);

            if (geometry.NightTowardSouth)
            {
                Assert.True(northDay, $"Expected daylight north of terminator at lon {lon} (term {termLat:F1})");
                Assert.False(southDay, $"Expected night south of terminator at lon {lon} (term {termLat:F1})");
            }
            else
            {
                Assert.False(northDay, $"Expected night north of terminator at lon {lon} (term {termLat:F1})");
                Assert.True(southDay, $"Expected daylight south of terminator at lon {lon} (term {termLat:F1})");
            }
        }
    }

    [Fact]
    public void Uk_is_on_the_night_side_on_9_October_2026_evening()
    {
        // 18:30 UTC is 19:30 BST. London is past sunset; the map must shade it.
        var utc = new DateTime(2026, 10, 9, 18, 30, 0, DateTimeKind.Utc);
        const double londonLat = 51.5;
        const double londonLon = -0.12;

        Assert.False(DayNightTerminator.IsSunAboveHorizon(londonLat, londonLon, utc));
        Assert.True(DayNightTerminator.IsSunAboveHorizon(39.9, -75.3, utc));

        var geometry = DayNightTerminator.GetGeometry(utc);
        Assert.False(geometry.NightTowardSouth);
        var termLat = DayNightTerminator.GetTerminatorLatitudeDeg(londonLon, utc);
        Assert.True(termLat < londonLat, $"UK should lie north of the terminator, term {termLat:F1}");
    }

    [Fact]
    public void Geometry_is_cached_per_utc_minute()
    {
        var utc = new DateTime(2026, 6, 21, 12, 0, 30, DateTimeKind.Utc);
        var a = DayNightTerminator.GetGeometry(utc);
        var b = DayNightTerminator.GetGeometry(utc.AddSeconds(20));
        Assert.Same(a, b);
    }
}
