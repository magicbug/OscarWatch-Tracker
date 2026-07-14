using OscarWatch.Core.Models;

namespace OscarWatch.Core.SessionPlanner;

/// <summary>
/// A candidate pass with its computed quality and composite scores.
/// </summary>
public sealed class ScoredPass
{
    public required PassInfo Pass { get; init; }

    /// <summary>Quality score in [0.0, 1.0] based on elevation, duration, and transponder type.</summary>
    public double QualityScore { get; init; }

    /// <summary>Operator-assigned satellite priority (1 = highest, 10 = lowest).</summary>
    public int SatellitePriority { get; init; }

    /// <summary>Composite score: QualityScore × (11 − SatellitePriority). Range [0.0, 10.0].</summary>
    public double CompositeScore { get; init; }

    /// <summary>Unique identifier for force-include/exclude operations.</summary>
    public string Id => $"{Pass.NoradId}:{Pass.AosUtc.Ticks}";
}
