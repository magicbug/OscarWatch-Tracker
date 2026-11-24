using OscarWatch.Core.Models;

namespace OscarWatch.Core.Eme;

/// <summary>
/// When the Moon counts as up: the elevation of its centre is at or above the limit (and the horizon mask, when
/// one is given) and at or below the maximum. The satellite minimum elevation is not used, because moon
/// operation sometimes relies on the Moon being right on the horizon.
/// </summary>
public sealed record MoonVisibilityRule(double MinimumElevationDeg, double MaximumElevationDeg, HorizonMask? Mask)
{
    /// <summary>The default rule: centre at or above 0 degrees, no maximum, no mask.</summary>
    public static MoonVisibilityRule Default { get; } = new(0.0, 90.0, null);

    public bool IsUp(MoonSample sample) => IsUp(sample.AzimuthDeg, sample.ElevationDeg);

    public bool IsUp(double azimuthDeg, double elevationDeg) =>
        elevationDeg >= Floor(azimuthDeg) && elevationDeg <= MaximumElevationDeg;

    /// <summary>The lowest elevation that counts at <paramref name="azimuthDeg"/>.</summary>
    public double Floor(double azimuthDeg) =>
        Mask is { HasPoints: true } mask
            ? mask.EffectiveFloor(azimuthDeg, MinimumElevationDeg)
            : MinimumElevationDeg;

    /// <summary>True when the rule is the plain limit, so the faster altitude search can be used.</summary>
    public bool IsPlainLimit => MaximumElevationDeg >= 90.0 && Mask is not { HasPoints: true };
}
