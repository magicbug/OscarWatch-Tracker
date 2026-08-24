namespace OscarWatch.Core.Models;

public sealed class GroundStation
{
    public string DisplayName { get; set; } = "Home";
    public string Callsign { get; set; } = "";
    public double LatitudeDeg { get; set; } = 51.5;
    public double LongitudeDeg { get; set; } = -0.1;
    public double AltitudeMetersAsl { get; set; } = 50;
    public string GridSquare { get; set; } = "IO91wm";

    /// <summary>Optional skyline; empty means unused (scalar min elevation only).</summary>
    public HorizonMask HorizonMask { get; set; } = new();

    public double AltitudeKm => AltitudeMetersAsl / 1000.0;
}
