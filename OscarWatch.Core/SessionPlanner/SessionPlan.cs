namespace OscarWatch.Core.SessionPlanner;

/// <summary>
/// The complete session plan — immutable once generated.
/// </summary>
public sealed class SessionPlan
{
    public DateTime SessionStartUtc { get; init; }
    public DateTime SessionEndUtc { get; init; }

    public IReadOnlyList<ScheduledPass> ScheduledPasses { get; init; } = [];
    public IReadOnlyList<ScoredPass> AllCandidates { get; init; } = [];
    public IReadOnlySet<string> ExcludedIds { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> ForcedInclusionIds { get; init; } = new HashSet<string>();

    /// <summary>Sum of durations of all scheduled passes.</summary>
    public TimeSpan TotalOperatingTime =>
        ScheduledPasses.Aggregate(TimeSpan.Zero, (sum, sp) => sum + sp.Scored.Pass.Duration);

    /// <summary>Session duration minus total operating time.</summary>
    public TimeSpan TotalGapTime =>
        (SessionEndUtc - SessionStartUtc) - TotalOperatingTime;

    /// <summary>Number of passes in the schedule.</summary>
    public int ScheduledCount => ScheduledPasses.Count;
}
