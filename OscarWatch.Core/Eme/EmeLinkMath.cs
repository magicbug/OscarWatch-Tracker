namespace OscarWatch.Core.Eme;

/// <summary>How much the Moon's distance costs today, from best (good) to worst (bad).</summary>
public enum PathLossClass
{
    Good,
    Fair,
    Poor,
    Bad
}

/// <summary>
/// The arithmetic of an Earth-Moon-Earth path: echo delay, Doppler shift of the echo, and path loss. These are
/// free-space figures for isotropic antennas, not a link budget.
/// </summary>
public static class EmeLinkMath
{
    public const double SpeedOfLightKmPerS = 299_792.458;

    /// <summary>
    /// The distance the extra path loss is measured against. WSJT-X uses 356,903 km, taken as half the echo time
    /// times the speed of light, so the figure here can be compared with WSJT-X's own. Zero is then the best case:
    /// at the Moon's closest approach the figure is about -0.03 dB.
    /// </summary>
    public const double ReferenceDistanceKm = 356_903.0;

    /// <summary>The Moon's mean radius in kilometres.</summary>
    public const double MoonRadiusKm = 1737.4;

    /// <summary>
    /// The fraction of the Moon's geometric cross section that returns the signal. This is an assumption: the
    /// reflectivity varies with frequency, and 6.5 percent is the usual figure for EME link calculations.
    /// </summary>
    public const double Reflectivity = 0.065;

    public const double MinimumFrequencyMHz = 30.0;
    public const double MaximumFrequencyMHz = 100_000.0;

    /// <summary>True when <paramref name="frequencyMHz"/> is a number from 30 MHz to 100 GHz.</summary>
    public static bool IsValidFrequency(double frequencyMHz) =>
        double.IsFinite(frequencyMHz) && frequencyMHz >= MinimumFrequencyMHz && frequencyMHz <= MaximumFrequencyMHz;

    /// <summary>The time for a signal to go to the Moon and back, 2 d / c.</summary>
    public static double EchoDelaySeconds(double distanceKm) => 2.0 * distanceKm / SpeedOfLightKmPerS;

    /// <summary>
    /// The shift of your own echo, in hertz: minus 2 f v / c, with v the range rate in kilometres per second
    /// (positive when the Moon is moving away, which lowers the frequency).
    /// </summary>
    public static double OwnEchoShiftHz(double frequencyHz, double rangeRateKmPerS) =>
        -2.0 * frequencyHz * rangeRateKmPerS / SpeedOfLightKmPerS;

    /// <summary>
    /// The shift of a signal sent from one station and received at another, minus f (v1 + v2) / c, with each
    /// range rate measured from its own station.
    /// </summary>
    public static double DxShiftHz(double frequencyHz, double rangeRateMeKmPerS, double rangeRateDxKmPerS) =>
        -frequencyHz * (rangeRateMeKmPerS + rangeRateDxKmPerS) / SpeedOfLightKmPerS;

    /// <summary>
    /// The extra loss, in decibels, because the Moon is farther than <see cref="ReferenceDistanceKm"/>:
    /// 40 log10 (d / reference), since the round trip loss follows the fourth power of distance.
    /// </summary>
    public static double ExtraPathLossDb(double distanceKm) =>
        40.0 * Math.Log10(distanceKm / ReferenceDistanceKm);

    /// <summary>
    /// The free-space two-way path loss in decibels at <paramref name="frequencyHz"/>, from the radar equation
    /// with isotropic antennas: 10 log10 ((4 pi)^3 d^4 / (sigma lambda^2)).
    /// </summary>
    public static double AbsolutePathLossDb(double frequencyHz, double distanceKm)
    {
        var d = distanceKm * 1000.0;
        var wavelength = SpeedOfLightKmPerS * 1000.0 / frequencyHz;
        var radius = MoonRadiusKm * 1000.0;
        var sigma = Reflectivity * Math.PI * radius * radius;
        return 10.0 * Math.Log10(Math.Pow(4.0 * Math.PI, 3) * Math.Pow(d, 4) / (sigma * wavelength * wavelength));
    }

    /// <summary>Good under 2 dB, fair 2 to 4, poor 4 to 6, bad over 6.</summary>
    public static PathLossClass ClassOf(double extraLossDb) => extraLossDb switch
    {
        < 2.0 => PathLossClass.Good,
        < 4.0 => PathLossClass.Fair,
        < 6.0 => PathLossClass.Poor,
        _ => PathLossClass.Bad
    };
}
