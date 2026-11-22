namespace OscarWatch.Core.Eme;

/// <summary>The Moon as seen from one station at one instant.</summary>
/// <param name="TimeUtc">The instant.</param>
/// <param name="AzimuthDeg">Azimuth of the Moon's centre, degrees clockwise from north.</param>
/// <param name="ElevationDeg">Geometric elevation of the Moon's centre (no refraction), degrees. Negative below the horizon.</param>
/// <param name="DistanceKm">Distance from the station to the Moon's centre.</param>
/// <param name="GeocentricDistanceKm">Distance from the Earth's centre to the Moon's centre.</param>
/// <param name="RangeRateKmPerS">Rate of change of <paramref name="DistanceKm"/>. Positive when the Moon is moving away.</param>
/// <param name="RightAscensionHours">Topocentric right ascension of date, hours.</param>
/// <param name="DeclinationDeg">Topocentric declination of date, degrees.</param>
/// <param name="LocalHourAngleDeg">Hour angle at the station, 0 to 360 degrees, increasing westwards from the meridian.</param>
/// <param name="GreenwichHourAngleDeg">Hour angle at Greenwich, 0 to 360 degrees.</param>
/// <param name="SublunarLatitudeDeg">Latitude of the point on the Earth directly below the Moon.</param>
/// <param name="SublunarLongitudeDeg">Longitude of that point, minus 180 to plus 180 degrees, east positive.</param>
public sealed record MoonSample(
    DateTime TimeUtc,
    double AzimuthDeg,
    double ElevationDeg,
    double DistanceKm,
    double GeocentricDistanceKm,
    double RangeRateKmPerS,
    double RightAscensionHours,
    double DeclinationDeg,
    double LocalHourAngleDeg,
    double GreenwichHourAngleDeg,
    double SublunarLatitudeDeg,
    double SublunarLongitudeDeg);
