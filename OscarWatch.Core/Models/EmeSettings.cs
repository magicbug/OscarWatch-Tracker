namespace OscarWatch.Core.Models;

/// <summary>Settings for the Moon and EME (Earth-Moon-Earth) features.</summary>
public sealed class EmeSettings
{
    public const double MinimumLimitDeg = -10.0;
    public const double MaximumLimitDeg = 90.0;

    /// <summary>List the Moon's rise and set with the upcoming passes. Off by default.</summary>
    public bool ShowMoonInPasses { get; set; }

    /// <summary>
    /// The Moon counts as up when the elevation of its centre is at or above this, in degrees. Default 0 so a Moon
    /// on the horizon counts. May be negative for a station with a negative horizon. The satellite minimum
    /// elevation is not used for the Moon.
    /// </summary>
    public double ElevationLimitDeg { get; set; }

    /// <summary>The Moon stops counting above this elevation, for an antenna that cannot reach the whole sky.</summary>
    public double MaximumElevationDeg { get; set; } = 90.0;

    /// <summary>Also apply the station's horizon mask to the Moon. Off by default.</summary>
    public bool UseHorizonMask { get; set; }

    /// <summary>Clamps every value so an old or hand-edited settings file cannot cause trouble.</summary>
    public void Normalise()
    {
        ElevationLimitDeg = double.IsFinite(ElevationLimitDeg)
            ? Math.Clamp(ElevationLimitDeg, MinimumLimitDeg, MaximumLimitDeg)
            : 0.0;
        MaximumElevationDeg = double.IsFinite(MaximumElevationDeg)
            ? Math.Clamp(MaximumElevationDeg, ElevationLimitDeg, MaximumLimitDeg)
            : MaximumLimitDeg;
    }
}
