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

    /// <summary>
    /// The longest the Moon can stay up or down that the search will follow, in days. Near the poles the Moon is
    /// up for about a fortnight at a time, so a Moon already up at the start can have risen days ago.
    /// </summary>
    private const double MaxSpanDays = 21.0;

    /// <summary>Step used to find the real rise of a Moon already up, or the set of one still up at the end.</summary>
    private const double CoarseStepMinutes = 5.0;

    private const double ScanStepMinutes = 1.0;

    /// <summary>
    /// Every interval overlapping [<paramref name="startUtc"/>, <paramref name="endUtc"/>) when the Moon is up,
    /// earliest first. A Moon already up at the start is included with its real rise time, and one still up at
    /// the end with its real set time. When the Moon does not cross the limit inside the search (a limit above
    /// its highest point, or a polar day or night) the result is empty.
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
        var limit = rule.MinimumElevationDeg;
        var passes = new List<PassInfo>();
        var cursor = start;

        var atStart = MoonEphemeris.At(startUtc, station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres);
        if (atStart is not null && rule.IsUp(atStart))
        {
            // Already up: find the real rise by stepping back, and the set by searching forward.
            var set = Astronomy.SearchAltitude(Body.Moon, observer, Direction.Set, start, MaxSpanDays, limit);
            if (set is null)
                return passes;
            var riseUtc = EarlierRise(rule, station, startUtc);
            passes.Add(BuildPass(station, riseUtc, set.ToUtcDateTime(), PeakOf(rule, station, observer, riseUtc, set.ToUtcDateTime())));
            cursor = set;
        }

        for (var guard = 0; guard < 64 && cursor.ut < end.ut; guard++)
        {
            var rise = Astronomy.SearchAltitude(Body.Moon, observer, Direction.Rise, cursor, end.ut - cursor.ut, limit);
            if (rise is null || rise.ut >= end.ut)
                break;
            var set = Astronomy.SearchAltitude(Body.Moon, observer, Direction.Set, rise, MaxSpanDays, limit);
            if (set is null)
                break;
            passes.Add(BuildPass(station, rise.ToUtcDateTime(), set.ToUtcDateTime(),
                PeakOf(rule, station, observer, rise.ToUtcDateTime(), set.ToUtcDateTime())));
            cursor = set;
        }
        return passes;
    }

    private static IReadOnlyList<PassInfo> FindByScan(
        MoonVisibilityRule rule, StationPosition station, DateTime startUtc, DateTime endUtc)
    {
        var step = TimeSpan.FromMinutes(ScanStepMinutes);
        var passes = new List<PassInfo>();
        var previous = Sample(startUtc);
        if (previous is null)
            return [];
        var previousUp = rule.IsUp(previous);
        DateTime? riseUtc = previousUp ? EarlierRise(rule, station, startUtc) : null;

        var t = startUtc;
        while (t < endUtc)
        {
            t += step;
            if (t > endUtc)
                t = endUtc;
            var current = Sample(t);
            if (current is null)
                break;
            var up = rule.IsUp(current);
            if (up && !previousUp)
                riseUtc = Bisect(rule, station, previous.TimeUtc, current.TimeUtc, wantUp: true);
            if (!up && previousUp && riseUtc is { } rise)
            {
                var set = Bisect(rule, station, previous.TimeUtc, current.TimeUtc, wantUp: false);
                if (set > startUtc)
                    passes.Add(BuildSampled(rule, station, rise, set));
                riseUtc = null;
            }
            previous = current;
            previousUp = up;
        }

        // Still up at the end of the range: find the real set, which may be days away near the poles.
        if (previousUp && riseUtc is { } open && LaterSet(rule, station, previous.TimeUtc) is { } openSet)
            passes.Add(BuildSampled(rule, station, open, openSet));
        return passes;

        MoonSample? Sample(DateTime time) =>
            MoonEphemeris.At(time, station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres);
    }

    /// <summary>
    /// The real rise time of a Moon that is up at <paramref name="from"/>, found by stepping back until it is
    /// not up and then bisecting. If it was up for the whole of <see cref="MaxSpanDays"/> the earliest time
    /// searched is returned.
    /// </summary>
    private static DateTime EarlierRise(MoonVisibilityRule rule, StationPosition station, DateTime from)
    {
        var step = TimeSpan.FromMinutes(CoarseStepMinutes);
        var limit = from.AddDays(-MaxSpanDays);
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

    /// <summary>The real set time of a Moon that is up at <paramref name="from"/>, or null if it stays up for the whole search.</summary>
    private static DateTime? LaterSet(MoonVisibilityRule rule, StationPosition station, DateTime from)
    {
        var step = TimeSpan.FromMinutes(CoarseStepMinutes);
        var limit = from.AddDays(MaxSpanDays);
        var earlier = from;
        for (var t = from + step; t <= limit; t += step)
        {
            var s = MoonEphemeris.At(t, station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres);
            if (s is null)
                return null;
            if (!rule.IsUp(s))
                return Bisect(rule, station, earlier, t, wantUp: false);
            earlier = t;
        }
        return null;
    }

    /// <summary>Finds the moment between two samples when the rule changes, to about a second.</summary>
    private static DateTime Bisect(
        MoonVisibilityRule rule, StationPosition station, DateTime before, DateTime after, bool wantUp)
    {
        var lo = before;
        var hi = after;
        for (var i = 0; i < 16 && (hi - lo).TotalSeconds > 0.5; i++)
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

    /// <summary>
    /// The highest point of a window found by sampling every minute from the rise to the set, so the figure is the
    /// same however late in the window the list is built, then refined around the best sample.
    /// </summary>
    private static (DateTime Utc, double ElevationDeg) SampledPeak(
        MoonVisibilityRule rule, StationPosition station, DateTime rise, DateTime set)
    {
        var step = TimeSpan.FromMinutes(ScanStepMinutes);
        var bestTime = rise;
        var bestElevation = double.NegativeInfinity;
        for (var t = rise; t <= set; t += step)
        {
            var s = MoonEphemeris.At(t, station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres);
            if (s is null || !rule.IsUp(s))
                continue;
            if (s.ElevationDeg > bestElevation)
            {
                bestElevation = s.ElevationDeg;
                bestTime = t;
            }
        }
        if (double.IsNegativeInfinity(bestElevation))
        {
            var mid = rise + (set - rise) / 2;
            var m = MoonEphemeris.At(mid, station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres);
            return (mid, m?.ElevationDeg ?? double.NaN);
        }
        return RefinePeak(rule, station, bestTime, step, rise, set);
    }

    private static (DateTime Utc, double ElevationDeg) RefinePeak(
        MoonVisibilityRule rule, StationPosition station, DateTime sampleTime, TimeSpan step, DateTime rise, DateTime set)
    {
        var lo = sampleTime - step;
        var hi = sampleTime + step;
        if (lo < rise) lo = rise;
        if (hi > set) hi = set;
        double Up(DateTime t)
        {
            var s = MoonEphemeris.At(t, station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres);
            return s is not null && rule.IsUp(s) ? s.ElevationDeg : double.NegativeInfinity;
        }
        for (var i = 0; i < 30 && (hi - lo).TotalSeconds > 1; i++)
        {
            var third = (hi - lo) / 3;
            var a = lo + third;
            var b = hi - third;
            if (Up(a) < Up(b)) lo = a; else hi = b;
        }
        var best = lo + (hi - lo) / 2;
        var e = Up(best);
        if (double.IsNegativeInfinity(e))
        {
            // The refinement left the up region (a maximum elevation cap): keep the sampled best.
            var s = MoonEphemeris.At(sampleTime, station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres);
            return (sampleTime, s?.ElevationDeg ?? double.NaN);
        }
        return (best, e);
    }

    /// <summary>
    /// The highest point of a window on the plain-limit path: the upper transit between rise and set when the
    /// window is short enough to hold one, and the sampled peak when it spans days (near the poles).
    /// </summary>
    private static (DateTime Utc, double ElevationDeg) PeakOf(
        MoonVisibilityRule rule, StationPosition station, Observer observer, DateTime rise, DateTime set)
    {
        if ((set - rise).TotalHours > 30)
            return SampledPeak(rule, station, rise, set);
        var riseTime = ToAstroTime(rise);
        var setTime = ToAstroTime(set);
        var transit = Astronomy.SearchHourAngle(Body.Moon, observer, 0.0, riseTime).time;
        if (transit.ut < riseTime.ut || transit.ut > setTime.ut)
            transit = riseTime.AddDays((setTime.ut - riseTime.ut) / 2.0);
        var equator = Astronomy.Equator(Body.Moon, transit, observer, EquatorEpoch.OfDate, Aberration.Corrected);
        var horizon = Astronomy.Horizon(transit, observer, equator.ra, equator.dec, Refraction.None);
        return (transit.ToUtcDateTime(), horizon.altitude);
    }

    private static PassInfo BuildSampled(MoonVisibilityRule rule, StationPosition station, DateTime rise, DateTime set) =>
        BuildPass(station, rise, set, SampledPeak(rule, station, rise, set));

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
