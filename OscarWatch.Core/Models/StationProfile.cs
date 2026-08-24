using OscarWatch.Core.Geo;

namespace OscarWatch.Core.Models;

public sealed class StationProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "Home";
    public string Callsign { get; set; } = "";
    public double LatitudeDeg { get; set; } = 51.5;
    public double LongitudeDeg { get; set; } = -0.1;
    public double AltitudeMetersAsl { get; set; } = 50;
    public string GridSquare { get; set; } = "IO91wm";

    /// <summary>Optional skyline; empty means unused (scalar min elevation only).</summary>
    public HorizonMask HorizonMask { get; set; } = new();

    public GroundStation ToGroundStation() => new()
    {
        DisplayName = DisplayName,
        Callsign = MaidenheadLocator.NormalizeCallsign(Callsign),
        LatitudeDeg = LatitudeDeg,
        LongitudeDeg = LongitudeDeg,
        AltitudeMetersAsl = AltitudeMetersAsl,
        GridSquare = GridSquare,
        HorizonMask = HorizonMask?.Clone() ?? new HorizonMask()
    };

    public static StationProfile FromGroundStation(GroundStation gs, string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"),
        DisplayName = gs.DisplayName,
        Callsign = MaidenheadLocator.NormalizeCallsign(gs.Callsign),
        LatitudeDeg = gs.LatitudeDeg,
        LongitudeDeg = gs.LongitudeDeg,
        AltitudeMetersAsl = gs.AltitudeMetersAsl,
        GridSquare = gs.GridSquare,
        HorizonMask = gs.HorizonMask?.Clone() ?? new HorizonMask()
    };
}
