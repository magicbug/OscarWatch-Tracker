using CosineKitty;
using OscarWatch.Core.Models;

namespace OscarWatch.Core.Eme;

/// <summary>
/// The intervals when the Moon is up at a station, as <see cref="PassInfo"/> entries so they can sit in the
/// upcoming pass list. "Up" is decided by a <see cref="MoonVisibilityRule"/>: the Moon's centre against an
/// elevation limit, with no allowance for refraction. With the default limit of 0 degrees that is a few minutes
/// inside the almanac rise and set times, which use the upper limb and refraction.
/// </summary>
public static class MoonWindows
{
    public const string MoonId = "MOON";

    /// <summary>How far before the start to look for a rise, so a Moon already up at the start is still listed.</summary>
    private const double LookBackDays = 2.0;

    private const double ScanStepMinutes = 1.0;

    /// <summary>
    /// Every interval overlapping [<paramref name="startUtc"/>, <paramref name="endUtc"/>) when the Moon is up,
    /// earliest first. A Moon already up at the start is included with its real rise time. When the Moon does not
    /// cross the limit inside the search (a limit above its highest point, or a polar day or night) the result is
    /// empty.
    /// </summary>
    public static IReadOnlyList<PassInfo> Find(
        MoonVisibilityRule rule,
        StationPosition station,
        DateTime startUtc,
        DateTime endUtc)
    {
        if (!double.IsFinite(station.LatitudeDeg) || !double.IsFinite(station.LongitudeDeg)
            || !double.IsFinite(station.HeightMetres) || endUtc <= startUtc)
            return [];
        if (MoonEphemeris.At(startUtc, station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres) is null)
            return [];
        return rule.IsPlainLimit
            ? FindByAltitudeSearch(rule, station, startUtc, endUtc)
            : FindByScan(rule, station, startUtc, endUtc);
    }

    private static IReadOnlyList<PassInfo> FindByAltitudeSearch(
        MoonVisibilityRule rule, StationPosition station, DateTime startUtc, DateTime endUtc)
    {
        var observer = new Observer(station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres);
        var start = ToAstroTime(startUtc);
        var end = ToAstroTime(endUtc);
        var searchFrom = start.AddDays(-LookBackDays);
        var searchDays = end.ut - searchFrom.ut + 1.0;
        var limit = rule.MinimumElevationDeg;
        var passes = new List<PassInfo>();
        var cursor = searchFrom;
        for (var guard = 0; guard < 64; guard++)
        {
            var rise = Astronomy.SearchAltitude(
                Body.Moon, observer, Direction.Rise, cursor, searchDays - (cursor.ut - searchFrom.ut), limit);
            if (rise is null || rise.ut >= end.ut)
                break;
            var set = Astronomy.SearchAltitude(Body.Moon, observer, Direction.Set, rise, 2.0, limit);
            if (set is null)
                break;
            if (set.ut > start.ut)
                passes.Add(BuildPass(station, rise.ToUtcDateTime(), set.ToUtcDateTime(), HighestPoint(observer, rise, set)));
            cursor = set;
        }
        return passes;
    }

    private static IReadOnlyList<PassInfo> FindByScan(
        MoonVisibilityRule rule, StationPosition station, DateTime startUtc, DateTime endUtc)
    {
        var from = startUtc;
        var step = TimeSpan.FromMinutes(ScanStepMinutes);
        var passes = new List<PassInfo>();
        DateTime? riseUtc = null;
        var previous = Sample(from);
        if (previous is null)
            return [];
        var previousUp = rule.IsUp(previous);
        if (previousUp)
            riseUtc = EarlierRise(rule, station, from);
        double bestElevation = previousUp ? previous.ElevationDeg : double.NegativeInfinity;
        var bestTime = from;
        var last = endUtc.AddDays(1);
        for (var t = from + step; t <= last; t += step)
        {
            var current = Sample(t);
            if (current is null)
                break;
            var up = rule.IsUp(current);
            if (up && !previousUp)
            {
                riseUtc = Bisect(rule, station, previous.TimeUtc, current.TimeUtc, wantUp: true);
                bestElevation = double.NegativeInfinity;
            }
            if (up && current.ElevationDeg > bestElevation)
            {
                bestElevation = current.ElevationDeg;
                bestTime = current.TimeUtc;
            }
            if (!up && previousUp && riseUtc is { } rise)
            {
                var set = Bisect(rule, station, previous.TimeUtc, current.TimeUtc, wantUp: false);
                if (set > startUtc && rise < endUtc)
                    passes.Add(BuildPass(station, rise, set, RefinePeak(station, bestTime, step, rise, set)));
                riseUtc = null;
            }
            previous = current;
            previousUp = up;
            if (!up && t > endUtc)
                break;
        }
        return passes;

        MoonSample? Sample(DateTime t) =>
            MoonEphemeris.At(t, station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres);
    }

    /// <summary>The real rise time of a Moon that is already up at <paramref name="from"/>, found by stepping back.</summary>
    private static DateTime EarlierRise(MoonVisibilityRule rule, StationPosition station, DateTime from)
    {
        var step = TimeSpan.FromMinutes(ScanStepMinutes);
        var limit = from.AddDays(-LookBackDays);
        var later = from;
        for (var t = from - step; t >= limit; t -= step)
        {
            var s = MoonEphemeris.At(t, station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres);
            if (s is null)
                break;
            if (!rule.IsUp(s))
                return Bisect(rule, station, t, later, wantUp: true);
            later = t;
        }
        return later;
    }

    /// <summary>Finds the moment between two scan samples when the rule changes, to about a second.</summary>
    private static DateTime Bisect(
        MoonVisibilityRule rule, StationPosition station, DateTime before, DateTime after, bool wantUp)
    {
        var lo = before;
        var hi = after;
        for (var i = 0; i < 12 && (hi - lo).TotalSeconds > 0.5; i++)
        {
            var mid = lo + (hi - lo) / 2;
            var s = MoonEphemeris.At(mid, station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres);
            if (s is null)
                break;
            // The state at lo is the opposite of wantUp, and at hi equals wantUp.
            if (rule.IsUp(s) == wantUp)
                hi = mid;
            else
                lo = mid;
        }
        return hi;
    }

    private static (DateTime Utc, double ElevationDeg) RefinePeak(
        StationPosition station, DateTime sampleTime, TimeSpan step, DateTime rise, DateTime set)
    {
        var lo = sampleTime - step;
        var hi = sampleTime + step;
        if (lo < rise) lo = rise;
        if (hi > set) hi = set;
        for (var i = 0; i < 30 && (hi - lo).TotalSeconds > 1; i++)
        {
            var third = (hi - lo) / 3;
            var a = lo + third;
            var b = hi - third;
            var ea = MoonEphemeris.At(a, station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres)?.ElevationDeg ?? double.NegativeInfinity;
            var eb = MoonEphemeris.At(b, station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres)?.ElevationDeg ?? double.NegativeInfinity;
            if (ea < eb) lo = a; else hi = b;
        }
        var mid = lo + (hi - lo) / 2;
        var best = MoonEphemeris.At(mid, station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres);
        return (mid, best?.ElevationDeg ?? double.NaN);
    }

    /// <summary>The upper transit between rise and set, which is the highest point; the middle if it falls outside.</summary>
    private static (DateTime Utc, double ElevationDeg) HighestPoint(Observer observer, AstroTime rise, AstroTime set)
    {
        var transit = Astronomy.SearchHourAngle(Body.Moon, observer, 0.0, rise).time;
        if (transit.ut < rise.ut || transit.ut > set.ut)
            transit = rise.AddDays((set.ut - rise.ut) / 2.0);
        var equator = Astronomy.Equator(Body.Moon, transit, observer, EquatorEpoch.OfDate, Aberration.Corrected);
        var horizon = Astronomy.Horizon(transit, observer, equator.ra, equator.dec, Refraction.None);
        return (transit.ToUtcDateTime(), horizon.altitude);
    }

    private static PassInfo BuildPass(
        StationPosition station, DateTime riseUtc, DateTime setUtc, (DateTime Utc, double ElevationDeg) peak)
    {
        var aos = MoonEphemeris.At(riseUtc, station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres);
        var los = MoonEphemeris.At(setUtc, station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres);
        return new PassInfo
        {
            SatelliteName = "Moon",
            NoradId = MoonId,
            AosUtc = riseUtc,
            LosUtc = setUtc,
            MaxElevationDeg = peak.ElevationDeg,
            MaxElevationUtc = peak.Utc,
            AosAzimuthDeg = aos?.AzimuthDeg ?? 0,
            LosAzimuthDeg = los?.AzimuthDeg ?? 0,
            Body = CelestialBodyKind.Moon
        };
    }

    private static AstroTime ToAstroTime(DateTime utc) =>
        new(utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
