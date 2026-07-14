using OscarWatch.Core.Models;

namespace OscarWatch.Core.SessionPlanner;

/// <summary>
/// Pure static scoring functions for pass quality assessment.
/// No dependencies — directly testable.
/// </summary>
public static class PassQualityScorer
{
    /// <summary>
    /// Computes the quality score in [0.0, 1.0] for a candidate pass.
    /// Formula: clamp(elev/90)*0.5 + clamp(dur/15)*0.3 + transponderFactor*0.2
    /// </summary>
    public static double ComputeScore(
        double maxElevationDeg,
        double durationMinutes,
        TransponderCategory transponderCategory)
    {
        var elevationComponent = ClampToUnit(maxElevationDeg / 90.0);
        var durationComponent = ClampToUnit(durationMinutes / 15.0);
        var transponderFactor = GetTransponderFactor(transponderCategory);

        return elevationComponent * 0.5 + durationComponent * 0.3 + transponderFactor * 0.2;
    }

    /// <summary>
    /// Computes the composite score for a candidate pass.
    /// Formula: qualityScore × (11 − satellitePriority).
    /// Result is in [0.0, 10.0] when qualityScore ∈ [0,1] and priority ∈ [1,10].
    /// </summary>
    public static double ComputeCompositeScore(double qualityScore, int satellitePriority)
    {
        return qualityScore * (11 - satellitePriority);
    }

    /// <summary>
    /// Clamps a value to [0.0, 1.0], treating NaN and negative infinity as 0.0
    /// and positive infinity as 1.0.
    /// </summary>
    private static double ClampToUnit(double value)
    {
        if (double.IsNaN(value) || value <= 0.0) return 0.0;
        if (value >= 1.0) return 1.0;
        return value;
    }

    /// <summary>
    /// Determines transponder category from satellite radio entry modes.
    /// Examines uplink/downlink mode strings to classify as Linear, Fm, Mixed, or Unknown.
    /// </summary>
    public static TransponderCategory ClassifyTransponder(SatelliteRadioEntry? radioEntry)
    {
        if (radioEntry is null || radioEntry.Modes.Count == 0)
            return TransponderCategory.Unknown;

        var hasLinear = false;
        var hasFm = false;

        foreach (var mode in radioEntry.Modes)
        {
            // Skip beacon-only entries — they don't represent usable transponders
            if (mode.IsBeaconOnly)
                continue;

            if (mode.IsFmMode)
            {
                hasFm = true;
            }
            else if (IsLinearMode(mode))
            {
                hasLinear = true;
            }
        }

        return (hasLinear, hasFm) switch
        {
            (true, true) => TransponderCategory.Mixed,
            (true, false) => TransponderCategory.Linear,
            (false, true) => TransponderCategory.Fm,
            _ => TransponderCategory.Unknown
        };
    }

    /// <summary>
    /// Returns the scoring factor for a given transponder category.
    /// </summary>
    private static double GetTransponderFactor(TransponderCategory category) => category switch
    {
        TransponderCategory.Linear => 1.0,
        TransponderCategory.Fm => 0.6,
        TransponderCategory.Mixed => 0.8,
        TransponderCategory.Unknown => 0.7,
        _ => 0.7
    };

    /// <summary>
    /// Checks if a transponder mode is linear (SSB, CW, or similar non-FM modes).
    /// </summary>
    private static bool IsLinearMode(SatelliteTransponderMode mode)
    {
        return ContainsLinearIndicator(mode.UplinkMode)
            || ContainsLinearIndicator(mode.DownlinkMode);
    }

    private static bool ContainsLinearIndicator(string modeString)
    {
        return modeString.Contains("SSB", StringComparison.OrdinalIgnoreCase)
            || modeString.Contains("CW", StringComparison.OrdinalIgnoreCase)
            || modeString.Contains("Linear", StringComparison.OrdinalIgnoreCase);
    }
}
