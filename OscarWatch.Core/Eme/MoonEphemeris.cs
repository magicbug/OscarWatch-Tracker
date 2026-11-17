using CosineKitty;

namespace OscarWatch.Core.Eme;

/// <summary>
/// The Moon's position at a station, from Astronomy Engine (MIT). This is the only class that talks to the
/// library, so it can be replaced in one place. Elevation is geometric (no refraction), the elevation the app
/// shows for satellites. The station is a point on the WGS84 ellipsoid at its height, so parallax and the
/// flattening of the Earth are allowed for.
/// </summary>
public static class MoonEphemeris
{
    private const double KmPerAu = 149_597_870.7;
    private const double SecondsPerDay = 86_400.0;

    /// <summary>The Moon at <paramref name="utc"/> for the station, or null when an input is not a finite number.</summary>
    public static MoonSample? At(DateTime utc, double latDeg, double lonDeg, double heightMetres)
    {
        if (!double.IsFinite(latDeg) || !double.IsFinite(lonDeg) || !double.IsFinite(heightMetres)
            || Math.Abs(latDeg) > 90)
            return null;
        if (utc.Year < 1800 || utc.Year > 2200)
            return null;
        try
        {
            var time = new AstroTime(utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc));
            var observer = new Observer(latDeg, lonDeg, heightMetres);

            var topo = Astronomy.Equator(Body.Moon, time, observer, EquatorEpoch.OfDate, Aberration.Corrected);
            var horizon = Astronomy.Horizon(time, observer, topo.ra, topo.dec, Refraction.None);

            // Range rate: line-of-sight component of the Moon's velocity relative to the station.
            var moonState = Astronomy.GeoMoonState(time);
            var stationState = Astronomy.ObserverState(time, observer, EquatorEpoch.J2000);
            var rx = moonState.x - stationState.x;
            var ry = moonState.y - stationState.y;
            var rz = moonState.z - stationState.z;
            var vx = moonState.vx - stationState.vx;
            var vy = moonState.vy - stationState.vy;
            var vz = moonState.vz - stationState.vz;
            var range = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            var rateKmPerS = (rx * vx + ry * vy + rz * vz) / range * KmPerAu / SecondsPerDay;

            // Sublunar point: the Moon's geocentric declination and its Greenwich hour angle.
            var geo = Astronomy.GeoMoon(time);
            var geoOfDate = Astronomy.RotateVector(Astronomy.Rotation_EQJ_EQD(time), geo);
            var geoEq = Astronomy.EquatorFromVector(geoOfDate);
            var gast = Astronomy.SiderealTime(time);

            // Hour angle is sidereal time minus right ascension. The local one uses the topocentric right ascension
            // and the station's longitude; the Greenwich one uses the geocentric right ascension.
            var localHa = Wrap360((gast - topo.ra) * 15.0 + lonDeg);
            var sublunarLon = Wrap180((geoEq.ra - gast) * 15.0);

            return new MoonSample(
                time.ToUtcDateTime(),
                horizon.azimuth,
                horizon.altitude,
                topo.dist * KmPerAu,
                geo.Length() * KmPerAu,
                rateKmPerS,
                topo.ra,
                topo.dec,
                localHa,
                Wrap360((gast - geoEq.ra) * 15.0),
                geoEq.dec,
                sublunarLon);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static double Wrap360(double deg)
    {
        var d = deg % 360.0;
        return d < 0 ? d + 360.0 : d;
    }

    private static double Wrap180(double deg)
    {
        var d = Wrap360(deg);
        return d > 180.0 ? d - 360.0 : d;
    }
}
