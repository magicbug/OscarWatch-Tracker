using OscarWatch.Core.Eme;

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

    /// <summary>The band shown in the Moon window. One of the ids in <see cref="EmeBands"/>.</summary>
    public string SelectedBandId { get; set; } = EmeBands.Default.Id;

    /// <summary>The frequency, in megahertz, the operator last used on each band, keyed by band id.</summary>
    public Dictionary<string, double> FrequencyMHzByBand { get; set; } = new(StringComparer.Ordinal);

    /// <summary>True or false where the operator has chosen circular or linear polarisation for a band; keyed by band id.</summary>
    public Dictionary<string, bool> CircularByBand { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The half-power beamwidth of the operator's antenna in degrees, optional, keyed by band id.</summary>
    public Dictionary<string, double> BeamwidthDegByBand { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Show azimuth and elevation in very large numbers in the Moon window.</summary>
    public bool LargeDigits { get; set; }

    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    /// <summary>The frequency for <paramref name="bandId"/>: the one the operator entered, or the band's start value.</summary>
    public double FrequencyMHzFor(string bandId)
    {
        if (FrequencyMHzByBand.TryGetValue(bandId, out var f) && EmeLinkMath.IsValidFrequency(f))
            return f;
        return EmeBands.Find(bandId)?.NominalMHz ?? EmeBands.Default.NominalMHz;
    }

    /// <summary>True when the operator uses circular polarisation on <paramref name="bandId"/>.</summary>
    public bool IsCircular(string bandId) =>
        CircularByBand.TryGetValue(bandId, out var c) ? c : EmeBands.Find(bandId)?.DefaultCircular ?? false;

    /// <summary>The antenna beamwidth for <paramref name="bandId"/>, or null when none was entered.</summary>
    public double? BeamwidthFor(string bandId) =>
        BeamwidthDegByBand.TryGetValue(bandId, out var b) && double.IsFinite(b) && b > 0 && b <= 180 ? b : null;

    /// <summary>Clamps every value so an old or hand-edited settings file cannot cause trouble.</summary>
    public void Normalise()
    {
        ElevationLimitDeg = double.IsFinite(ElevationLimitDeg)
            ? Math.Clamp(ElevationLimitDeg, MinimumLimitDeg, MaximumLimitDeg)
            : 0.0;
        MaximumElevationDeg = double.IsFinite(MaximumElevationDeg)
            ? Math.Clamp(MaximumElevationDeg, ElevationLimitDeg, MaximumLimitDeg)
            : MaximumLimitDeg;

        if (EmeBands.Find(SelectedBandId) is null)
            SelectedBandId = EmeBands.Default.Id;
        FrequencyMHzByBand = Cleaned(FrequencyMHzByBand, EmeLinkMath.IsValidFrequency);
        BeamwidthDegByBand = Cleaned(BeamwidthDegByBand, b => double.IsFinite(b) && b > 0 && b <= 180);
        CircularByBand = CircularByBand is null
            ? new(StringComparer.Ordinal)
            : CircularByBand.Where(kv => EmeBands.Find(kv.Key) is not null)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    private static Dictionary<string, double> Cleaned(Dictionary<string, double>? source, Func<double, bool> valid) =>
        source is null
            ? new(StringComparer.Ordinal)
            : source.Where(kv => EmeBands.Find(kv.Key) is not null && valid(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
}
