using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;

namespace OscarWatch.Core.Geo;

/// <summary>Day/night terminator on Earth for map overlays (0° solar elevation).</summary>
public static class DayNightTerminator
{
    /// <summary>Terminator sample spacing (smaller = smoother map edge).</summary>
    public const double LongitudeStepDeg = 0.75;
    private const double PoleLatitudeLimit = 89.9;

    private static readonly object CacheLock = new();
    private static long _cacheKey;
    private static DayNightGeometry _cached = DayNightGeometry.Empty;

    public static DayNightGeometry GetGeometry(DateTime utc)
    {
        var key = FloorToUtcMinute(utc);
        lock (CacheLock)
        {
            if (key == _cacheKey)
                return _cached;

            _cached = BuildGeometry(utc);
            _cacheKey = key;
            return _cached;
        }
    }

    public static GeoCoordinate GetSubsolarPoint(DateTime utc)
    {
        var (latDeg, lonDeg) = ComputeSubsolar(utc);
        return new GeoCoordinate(latDeg, lonDeg);
    }

    public static bool IsSunAboveHorizon(double latitudeDeg, double longitudeDeg, DateTime utc)
    {
        var (subLat, subLon) = ComputeSubsolar(utc);
        return SolarElevationDeg(latitudeDeg, longitudeDeg, subLat, subLon) > 0;
    }

    public static double GetTerminatorLatitudeDeg(double longitudeDeg, DateTime utc)
    {
        var (subLat, subLon) = ComputeSubsolar(utc);
        var decRad = subLat * Math.PI / 180.0;
        return TerminatorLatitudeDeg(longitudeDeg, subLon, decRad) ?? subLat;
    }

    private static DayNightGeometry BuildGeometry(DateTime utc)
    {
        var (subLat, subLon) = ComputeSubsolar(utc);

        if (subLat >= PoleLatitudeLimit)
            return new DayNightGeometry([], subLat, NightTowardSouth: true, FullNightHalf: true, DrawTerminatorLine: false);

        if (subLat <= -PoleLatitudeLimit)
            return new DayNightGeometry([], subLat, NightTowardSouth: false, FullNightHalf: true, DrawTerminatorLine: false);

        var terminator = BuildTerminatorRing(subLat, subLon);
        if (terminator.Count < 2)
            return DayNightGeometry.Empty;

        return new DayNightGeometry(
            terminator,
            subLat,
            NightTowardSouth: subLat >= 0,
            FullNightHalf: false,
            DrawTerminatorLine: true);
    }

    private static List<GeoCoordinate> BuildTerminatorRing(double subsolarLatDeg, double subsolarLonDeg)
    {
        var decRad = subsolarLatDeg * Math.PI / 180.0;
        var points = new List<GeoCoordinate>();

        for (var lon = -180.0; lon <= 180.0; lon += LongitudeStepDeg)
        {
            var lat = TerminatorLatitudeDeg(lon, subsolarLonDeg, decRad);
            if (lat is null)
                continue;

            points.Add(new GeoCoordinate(lat.Value, lon));
        }

        if (points.Count > 0 && points[0].LongitudeDeg > -180.0 + 0.01)
            points.Insert(0, new GeoCoordinate(points[0].LatitudeDeg, -180.0));

        var last = points[^1];
        if (last.LongitudeDeg < 180.0 - 0.01)
            points.Add(new GeoCoordinate(last.LatitudeDeg, 180.0));

        return points;
    }

    private static double? TerminatorLatitudeDeg(double longitudeDeg, double subsolarLonDeg, double declinationRad)
    {
        var hourAngleRad = (longitudeDeg - subsolarLonDeg) * Math.PI / 180.0;
        var sinDec = Math.Sin(declinationRad);

        if (Math.Abs(sinDec) < 1e-5)
        {
            var hCos = Math.Cos(hourAngleRad);
            if (Math.Abs(hCos) < 0.02)
                return longitudeDeg > subsolarLonDeg ? 90.0 : -90.0;

            // Whole meridian is day (cos > 0) or night. Park the terminator on a pole
            // so the night fill covers the night column and leaves the day column clear.
            // Northern declination shades south; southern declination shades north.
            var daySide = hCos > 0;
            if (declinationRad >= 0)
                return daySide ? -90.0 : 90.0;

            return daySide ? 90.0 : -90.0;
        }

        // atan(y/x) stays in (-90°, 90°). Atan2(y, x) leaves that range when sin(dec) < 0,
        // and the clamped result shades the daylight side from the September equinox
        // through to March.
        var latRad = Math.Atan(-Math.Cos(declinationRad) * Math.Cos(hourAngleRad) / sinDec);
        return latRad * 180.0 / Math.PI;
    }

    private static (double LatDeg, double LonDeg) ComputeSubsolar(DateTime utc)
    {
        var sun = SunPositionCalculator.GetPosition(utc);
        var distance = Math.Sqrt(sun.XKm * sun.XKm + sun.YKm * sun.YKm + sun.ZKm * sun.ZKm);
        if (distance <= 0)
            return (0, 0);

        var declinationRad = Math.Asin(sun.ZKm / distance);
        var rightAscensionRad = Math.Atan2(sun.YKm, sun.XKm);
        var gmstRad = GreenwichMeanSiderealTimeRadians(utc);
        var subsolarLonRad = rightAscensionRad - gmstRad;
        var lonDeg = NormalizeLongitudeDeg(subsolarLonRad * 180.0 / Math.PI);
        var latDeg = declinationRad * 180.0 / Math.PI;
        return (latDeg, lonDeg);
    }

    private static double SolarElevationDeg(
        double latitudeDeg,
        double longitudeDeg,
        double subsolarLatDeg,
        double subsolarLonDeg)
    {
        var latRad = latitudeDeg * Math.PI / 180.0;
        var decRad = subsolarLatDeg * Math.PI / 180.0;
        var hourAngleRad = (longitudeDeg - subsolarLonDeg) * Math.PI / 180.0;
        var sinEl = Math.Sin(latRad) * Math.Sin(decRad)
            + Math.Cos(latRad) * Math.Cos(decRad) * Math.Cos(hourAngleRad);
        sinEl = Math.Clamp(sinEl, -1.0, 1.0);
        return Math.Asin(sinEl) * 180.0 / Math.PI;
    }

    private static double GreenwichMeanSiderealTimeRadians(DateTime utc)
    {
        var jd = AstronomyMath.ToJulianDate(utc);
        var t = (jd - 2451545.0) / 36525.0;
        var gmstDeg = 280.46061837
            + 360.98564736629 * (jd - 2451545.0)
            + 0.000387933 * t * t
            - t * t * t / 38710000.0;
        return NormalizeLongitudeDeg(gmstDeg) * Math.PI / 180.0;
    }

    private static double NormalizeLongitudeDeg(double degrees)
    {
        var normalized = degrees % 360.0;
        if (normalized > 180.0)
            normalized -= 360.0;
        if (normalized <= -180.0)
            normalized += 360.0;

        return normalized;
    }

    private static long FloorToUtcMinute(DateTime utc)
    {
        var minute = new DateTime(
            utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);
        return minute.Ticks;
    }
}

public sealed record DayNightGeometry(
    IReadOnlyList<GeoCoordinate> Terminator,
    double SubsolarLatitudeDeg,
    bool NightTowardSouth,
    bool FullNightHalf,
    bool DrawTerminatorLine)
{
    public static DayNightGeometry Empty { get; } =
        new([], 0, true, false, false);
}
