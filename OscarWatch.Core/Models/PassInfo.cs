namespace OscarWatch.Core.Models;

public sealed class PassInfo
{
    public required string SatelliteName { get; set; }
    public required string NoradId { get; init; }

    /// <summary>
    /// Position of this satellite in the enabled list. The map, sky plot and pass timeline
    /// colour from this, so one satellite keeps one colour across every pass.
    /// </summary>
    public int PaletteIndex { get; set; }
    public DateTime AosUtc { get; init; }
    public DateTime LosUtc { get; init; }
    public double MaxElevationDeg { get; init; }
    public DateTime MaxElevationUtc { get; init; }
    public double AosAzimuthDeg { get; init; }
    public double LosAzimuthDeg { get; init; }

    public TimeSpan Duration => LosUtc - AosUtc;
}
