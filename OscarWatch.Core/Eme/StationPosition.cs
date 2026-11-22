namespace OscarWatch.Core.Eme;

/// <summary>A station on the Earth's surface: geodetic latitude and longitude in degrees, height above sea level in metres.</summary>
public readonly record struct StationPosition(double LatitudeDeg, double LongitudeDeg, double HeightMetres);
