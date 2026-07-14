namespace OscarWatch.Core.SessionPlanner;

/// <summary>
/// Represents the action determined by a single PlanExecutor tick.
/// </summary>
public abstract record PlanExecutorAction
{
    private PlanExecutorAction() { }

    /// <summary>No action required at this tick.</summary>
    public sealed record NoAction : PlanExecutorAction;

    /// <summary>Switch the focused satellite to the specified NORAD ID.</summary>
    public sealed record SwitchFocus(string NoradId) : PlanExecutorAction;

    /// <summary>Raise a pre-alert notification for an upcoming pass.</summary>
    public sealed record RaisePreAlert(string SatelliteName, TimeSpan TimeUntilAos, double MaxElevationDeg) : PlanExecutorAction;

    /// <summary>The final pass has completed; mark the plan as done.</summary>
    public sealed record MarkCompleted : PlanExecutorAction;
}
