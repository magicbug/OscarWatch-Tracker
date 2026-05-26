namespace OscarWatch.Core.Models;

public sealed class SatelliteTrackState
{
    public required string Name { get; init; }
    public required string NoradId { get; init; }
    public required GeoCoordinate Subpoint { get; init; }
    public LookAngles? LookAngles { get; init; }
    /// <summary>Compass azimuth ~1–2 s ahead (rotator east-side north-wrap lookahead).</summary>
    public double? AheadAzimuthDeg { get; init; }
    public IReadOnlyList<GeoCoordinate> GroundTrack { get; init; } = [];
    public IReadOnlyList<GeoCoordinate> Footprint { get; init; } = [];
    /// <summary>Angular radius of the minimum-elevation footprint on Earth (degrees).</summary>
    public double FootprintRadiusDeg { get; init; }
    /// <summary>True when the spacecraft is in full sunlight; false when in Earth's shadow.</summary>
    public bool IsSunlit { get; init; } = true;
}
