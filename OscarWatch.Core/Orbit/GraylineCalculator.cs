namespace OscarWatch.Core.Orbit;

public static class GraylineCalculator
{
    private const double EarthRadiusDeg = 90.0;

    public static (double LatitudeDeg, double LongitudeDeg) GetSubsolarPoint(DateTime utc)
    {
        var sun = SunPositionCalculator.GetPosition(utc);

        var distKm = Math.Sqrt(sun.XKm * sun.XKm + sun.YKm * sun.YKm + sun.ZKm * sun.ZKm);
        if (distKm <= 0)
            return (0, 0);

        var declinationRad = Math.Asin(sun.ZKm / distKm);
        var latDeg = declinationRad * 180.0 / Math.PI;

        var raRad = Math.Atan2(sun.YKm, sun.XKm);

        var gmstRad = GetGmstRad(utc);

        var lonRad = raRad - gmstRad;
        var lonDeg = lonRad * 180.0 / Math.PI;
        lonDeg = ((lonDeg + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;

        return (latDeg, lonDeg);
    }

    public static List<(double LatDeg, double LonDeg)> GetTerminatorRing(
        DateTime utc,
        int steps = 360)
    {
        var (subLat, subLon) = GetSubsolarPoint(utc);
        return GetTerminatorRingFromSubsolar(subLat, subLon, steps);
    }

    public static List<(double LatDeg, double LonDeg)> GetTerminatorRingFromSubsolar(
        double subLatDeg,
        double subLonDeg,
        int steps = 360)
    {
        var points = new List<(double, double)>(steps + 1);

        var subLatRad = subLatDeg * Math.PI / 180.0;
        var subLonRad = subLonDeg * Math.PI / 180.0;
        var termAngleRad = Math.PI / 2.0;

        for (var i = 0; i <= steps; i++)
        {
            var azRad = 2.0 * Math.PI * i / steps;

            var sinLat = Math.Sin(subLatRad) * Math.Cos(termAngleRad)
                       + Math.Cos(subLatRad) * Math.Sin(termAngleRad) * Math.Cos(azRad);
            sinLat = Math.Clamp(sinLat, -1.0, 1.0);
            var latRad = Math.Asin(sinLat);

            var cosLat = Math.Cos(latRad);
            double lonRad;
            if (Math.Abs(cosLat) < 1e-10)
            {
                lonRad = subLonRad;
            }
            else
            {
                var sinDLon = Math.Sin(termAngleRad) * Math.Sin(azRad) / cosLat;
                var cosDLon = (Math.Cos(termAngleRad) - Math.Sin(subLatRad) * sinLat)
                              / (Math.Cos(subLatRad) * cosLat);
                lonRad = subLonRad + Math.Atan2(sinDLon, cosDLon);
            }

            var latDeg = latRad * 180.0 / Math.PI;
            var lonDeg = lonRad * 180.0 / Math.PI;
            lonDeg = ((lonDeg + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;

            points.Add((latDeg, lonDeg));
        }

        return points;
    }

    public static bool IsNightSide(
        double latDeg,
        double lonDeg,
        double subLatDeg,
        double subLonDeg)
    {
        var latRad = latDeg * Math.PI / 180.0;
        var lonRad = lonDeg * Math.PI / 180.0;
        var subLatRad = subLatDeg * Math.PI / 180.0;
        var subLonRad = subLonDeg * Math.PI / 180.0;

        var dLat = latRad - subLatRad;
        var dLon = lonRad - subLonRad;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(latRad) * Math.Cos(subLatRad)
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var centralAngle = 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
        return centralAngle > Math.PI / 2.0;
    }

    private static double GetGmstRad(DateTime utc)
    {
        var jd = ToJulianDate(utc);
        var t = (jd - 2451545.0) / 36525.0;
        var gmstSec = 67310.54841
                    + (876600.0 * 3600.0 + 8640184.812866) * t
                    + 0.093104 * t * t
                    - 6.2e-6 * t * t * t;
        var gmstRad = (gmstSec % 86400.0) * 2.0 * Math.PI / 86400.0;
        if (gmstRad < 0) gmstRad += 2.0 * Math.PI;
        return gmstRad;
    }

    private static double ToJulianDate(DateTime utc)
    {
        var year = utc.Year;
        var month = utc.Month;
        if (month <= 2)
        {
            year--;
            month += 12;
        }
        var century = year / 100;
        var b = 2 - century + century / 4;
        return Math.Floor(365.25 * (year + 4716))
             + Math.Floor(30.6001 * (month + 1))
             + utc.Day
             + utc.TimeOfDay.TotalDays
             + b
             - 1524.5;
    }
}
