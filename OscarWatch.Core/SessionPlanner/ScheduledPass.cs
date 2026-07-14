namespace OscarWatch.Core.SessionPlanner;

/// <summary>
/// A pass selected for inclusion in a session plan.
/// </summary>
public sealed class ScheduledPass
{
    public required ScoredPass Scored { get; init; }

    /// <summary>Why this pass was included in the plan.</summary>
    public PassSelectionReason Reason { get; init; }
}

/// <summary>
/// Indicates how a pass was selected for the session plan.
/// </summary>
public enum PassSelectionReason
{
    /// <summary>Selected by the weighted interval scheduling algorithm.</summary>
    AlgorithmSelected,

    /// <summary>Manually force-included by the operator.</summary>
    ForceIncluded
}
