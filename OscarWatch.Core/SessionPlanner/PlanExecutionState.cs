namespace OscarWatch.Core.SessionPlanner;

/// <summary>
/// Represents the execution state of the session plan executor.
/// </summary>
public enum PlanExecutionState
{
    /// <summary>No plan is active.</summary>
    Idle,

    /// <summary>Plan is actively executing with automatic focus switching.</summary>
    Running,

    /// <summary>Plan execution is paused (manual override active).</summary>
    Paused,

    /// <summary>All scheduled passes have completed.</summary>
    Completed
}
