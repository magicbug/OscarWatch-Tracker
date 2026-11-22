using CosineKitty;

namespace OscarWatch.Core.Eme;

/// <summary>The name of the Moon's phase, in eighths of the month.</summary>
public enum MoonPhaseName
{
    New,
    WaxingCrescent,
    FirstQuarter,
    WaxingGibbous,
    Full,
    WaningGibbous,
    LastQuarter,
    WaningCrescent
}

/// <summary>The Moon's phase, size, closeness to the Sun and the Galactic Centre, and its nearest apsides.</summary>
/// <param name="Phase">Phase name.</param>
/// <param name="PhaseAngleDeg">Ecliptic longitude of the Moon minus the Sun's, 0 (new) to 360.</param>
/// <param name="IlluminatedPercent">Percentage of the disc lit.</param>
/// <param name="ApparentDiameterArcmin">Apparent diameter at the station, in arcminutes.</param>
/// <param name="SunSeparationDeg">Topocentric angle between the Moon and the Sun.</param>
/// <param name="SunFlagAngleDeg">The angle under which the Sun's noise is flagged.</param>
/// <param name="GalacticCentreSeparationDeg">Angle to Sagittarius A, or null at 1 GHz and above where it does not matter.</param>
/// <param name="NextPerigee">Time and distance of the next perigee.</param>
/// <param name="NextApogee">Time and distance of the next apogee.</param>
/// <param name="LastApsisKind">Whether the most recent apsis was a perigee or an apogee.</param>
/// <param name="DaysSinceLastApsis">How long ago that was, in days.</param>
public sealed record MoonSkyContext(
    MoonPhaseName Phase,
    double PhaseAngleDeg,
    double IlluminatedPercent,
    double ApparentDiameterArcmin,
    double SunSeparationDeg,
    double SunFlagAngleDeg,
    double? GalacticCentreSeparationDeg,
    (DateTime Utc, double DistanceKm) NextPerigee,
    (DateTime Utc, double DistanceKm) NextApogee,
    bool LastApsisWasPerigee,
    double DaysSinceLastApsis)
{
    /// <summary>The Sun's noise swamps an echo when the Moon is this close to it, in degrees, unless the beam is wider.</summary>
    public const double DefaultSunFlagDeg = 10.0;

    /// <summary>The Galactic Centre flag angle in degrees.</summary>
    public const double GalacticFlagDeg = 10.0;

    /// <summary>Below this the Galactic Centre separation is shown, in MHz.</summary>
    public const double GalacticCentreBelowMHz = 1000.0;

    /// <summary>
    /// Sagittarius A*, J2000: right ascension 17h 45m 40.04s, declination -29 deg 00' 28.1". The position is
    /// well known and only needs to be right to a fraction of a degree here.
    /// </summary>
    public const double GalacticCentreRaHours = 17.0 + 45.0 / 60.0 + 40.04 / 3600.0;
    public const double GalacticCentreDecDeg = -(29.0 + 0.0 / 60.0 + 28.1 / 3600.0);

    public bool SunTooClose => SunSeparationDeg < SunFlagAngleDeg;
    public bool GalacticCentreTooClose => GalacticCentreSeparationDeg is { } g && g < GalacticFlagDeg;

    /// <summary>
    /// The Moon's context at <paramref name="utc"/>, or null when it cannot be worked out. The beam width, when
    /// given, widens the Sun flag angle if it is larger than 10 degrees. The Galactic Centre is worked out only
    /// below 1 GHz.
    /// </summary>
    public static MoonSkyContext? At(
        DateTime utc, StationPosition station, double frequencyMHz, double? beamwidthDeg, double moonDistanceKm)
    {
        if (!double.IsFinite(station.LatitudeDeg) || !double.IsFinite(station.LongitudeDeg)
            || !double.IsFinite(station.HeightMetres) || !double.IsFinite(moonDistanceKm) || moonDistanceKm <= 0)
            return null;
        try
        {
            var time = new AstroTime(utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc));
            var observer = new Observer(station.LatitudeDeg, station.LongitudeDeg, station.HeightMetres);

            var phaseAngle = Astronomy.MoonPhase(time);
            var illuminated = Astronomy.Illumination(Body.Moon, time).phase_fraction * 100.0;
            var diameter = 2.0 * Math.Asin(EmeLinkMath.MoonRadiusKm / moonDistanceKm) * 180.0 / Math.PI * 60.0;

            var moon = Astronomy.Equator(Body.Moon, time, observer, EquatorEpoch.J2000, Aberration.Corrected);
            var sun = Astronomy.Equator(Body.Sun, time, observer, EquatorEpoch.J2000, Aberration.Corrected);
            var sunSeparation = Separation(moon.ra, moon.dec, sun.ra, sun.dec);

            double? galactic = frequencyMHz < GalacticCentreBelowMHz
                ? Separation(moon.ra, moon.dec, GalacticCentreRaHours, GalacticCentreDecDeg)
                : null;

            var flag = beamwidthDeg is { } b && double.IsFinite(b) && b > DefaultSunFlagDeg ? b : DefaultSunFlagDeg;
            var (nextPerigee, nextApogee, lastWasPerigee, daysSince) = Apsides(time);
            return new MoonSkyContext(
                NameOf(phaseAngle), phaseAngle, illuminated, diameter, sunSeparation, flag, galactic,
                nextPerigee, nextApogee, lastWasPerigee, daysSince);
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

    public static MoonPhaseName NameOf(double phaseAngleDeg)
    {
        var a = ((phaseAngleDeg % 360.0) + 360.0) % 360.0;
        var index = (int)Math.Floor((a + 22.5) / 45.0) % 8;
        return (MoonPhaseName)index;
    }

    /// <summary>Angle between two directions given as right ascension in hours and declination in degrees.</summary>
    public static double Separation(double ra1Hours, double dec1Deg, double ra2Hours, double dec2Deg)
    {
        const double ToRad = Math.PI / 180.0;
        var d1 = dec1Deg * ToRad;
        var d2 = dec2Deg * ToRad;
        var dRa = (ra1Hours - ra2Hours) * 15.0 * ToRad;
        var cos = Math.Sin(d1) * Math.Sin(d2) + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(dRa);
        return Math.Acos(Math.Clamp(cos, -1.0, 1.0)) / ToRad;
    }

    private static ((DateTime, double), (DateTime, double), bool, double) Apsides(AstroTime now)
    {
        // Apsides come about 13.8 days apart, so starting 16 days back finds the last one before now.
        var apsis = Astronomy.SearchLunarApsis(now.AddDays(-16.0));
        ApsisInfo? last = null;
        for (var i = 0; i < 6 && apsis.time.ut <= now.ut; i++)
        {
            last = apsis;
            apsis = Astronomy.NextLunarApsis(apsis);
        }
        var firstAfter = apsis;
        var secondAfter = Astronomy.NextLunarApsis(firstAfter);
        var perigee = firstAfter.kind == ApsisKind.Pericenter ? firstAfter : secondAfter;
        var apogee = firstAfter.kind == ApsisKind.Apocenter ? firstAfter : secondAfter;
        var lastWasPerigee = last is { } l ? l.kind == ApsisKind.Pericenter : firstAfter.kind == ApsisKind.Apocenter;
        var days = last is { } l2 ? now.ut - l2.time.ut : double.NaN;
        return ((perigee.time.ToUtcDateTime(), perigee.dist_km), (apogee.time.ToUtcDateTime(), apogee.dist_km), lastWasPerigee, days);
    }
}
