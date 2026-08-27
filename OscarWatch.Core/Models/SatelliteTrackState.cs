namespace OscarWatch.Core.Models;

public sealed class SatelliteTrackState
{
    public required string Name { get; set; }
    public required string NoradId { get; init; }
    public required GeoCoordinate Subpoint { get; init; }
    public LookAngles? LookAngles { get; init; }
    /// <summary>Compass azimuth ~1–2 s ahead (rotator east-side north-wrap lookahead).</summary>
    public double? AheadAzimuthDeg { get; init; }
    /// <summary>Ground-track direction at the subpoint (degrees clockwise from north) for map footprint arrows.</summary>
    public double? MotionHeadingDeg { get; init; }
    public IReadOnlyList<GeoCoordinate> GroundTrack { get; init; } = [];
    /// <summary>Ground track for the next orbit (one period ahead), used for the multi-track overlay.</summary>
    public IReadOnlyList<GeoCoordinate> NextOrbitGroundTrack { get; init; } = [];
    public IReadOnlyList<GeoCoordinate> Footprint { get; init; } = [];
    /// <summary>Angular radius of the 0°-elevation footprint on Earth (degrees).</summary>
    public double FootprintRadiusDeg { get; init; }
    /// <summary>True when the spacecraft is in full sunlight; false when in Earth's shadow.</summary>
    public bool IsSunlit { get; init; } = true;
}
