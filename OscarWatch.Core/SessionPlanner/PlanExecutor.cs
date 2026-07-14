using OscarWatch.Core.Services;

namespace OscarWatch.Core.SessionPlanner;

/// <summary>
/// Event args for an AOS pre-alert notification.
/// </summary>
public sealed class AosPreAlertEventArgs : EventArgs
{
    public required string SatelliteName { get; init; }
    public TimeSpan TimeUntilAos { get; init; }
    public double MaxElevationDeg { get; init; }
}

/// <summary>
/// Event args for a focus switch event.
/// </summary>
public sealed class FocusSwitchEventArgs : EventArgs
{
    public required string NoradId { get; init; }
    public required string SatelliteName { get; init; }
}

/// <summary>
/// Timer-driven state machine that executes a session plan by switching focused satellite
/// at each pass's AOS time and raising pre-alert notifications. The caller (ViewModel)
/// drives the timer and calls <see cref="Tick"/> — this class does not own a timer.
/// </summary>
public sealed class PlanExecutor : IDisposable
{
    private readonly ILiveTrackingService _liveTracking;
    private int _preAlertMinutes;
    private string? _lastSwitchedNoradId;
    private readonly HashSet<string> _alertedPassIds = new();

    public PlanExecutor(ILiveTrackingService liveTracking)
    {
        _liveTracking = liveTracking ?? throw new ArgumentNullException(nameof(liveTracking));
    }

    /// <summary>The currently active session plan, or null if idle.</summary>
    public SessionPlan? ActivePlan { get; private set; }

    /// <summary>Current execution state.</summary>
    public PlanExecutionState State { get; private set; } = PlanExecutionState.Idle;

    /// <summary>The pass currently in progress (AOS ≤ now ≤ LOS), or null if in a gap.</summary>
    public ScheduledPass? CurrentPass { get; private set; }

    /// <summary>The next upcoming pass, or null if there are no more passes.</summary>
    public ScheduledPass? NextPass { get; private set; }

    /// <summary>Time remaining until the next focus switch (next AOS), or null if no upcoming pass.</summary>
    public TimeSpan? TimeToNextSwitch { get; private set; }

    /// <summary>True when manual override is active (plan is paused due to external focus change).</summary>
    public bool IsManualOverrideActive { get; private set; }

    /// <summary>Raised when a pre-alert notification should be shown to the operator.</summary>
    public event EventHandler<AosPreAlertEventArgs>? PreAlertRaised;

    /// <summary>Raised when the focused satellite is switched.</summary>
    public event EventHandler<FocusSwitchEventArgs>? FocusSwitched;

    /// <summary>Raised when all passes in the plan have completed.</summary>
    public event EventHandler? PlanCompleted;

    /// <summary>
    /// Start executing a session plan.
    /// </summary>
    /// <param name="plan">The plan to execute.</param>
    /// <param name="preAlertMinutes">Minutes before AOS to raise pre-alert (1–15, default 3).</param>
    public void Start(SessionPlan plan, int preAlertMinutes = 3)
    {
        ArgumentNullException.ThrowIfNull(plan);

        ActivePlan = plan;
        _preAlertMinutes = Math.Clamp(preAlertMinutes, 1, 15);
        _lastSwitchedNoradId = null;
        _alertedPassIds.Clear();
        IsManualOverrideActive = false;
        CurrentPass = null;
        NextPass = null;
        TimeToNextSwitch = null;
        State = PlanExecutionState.Running;
    }

    /// <summary>
    /// Stop execution and clear the active plan.
    /// </summary>
    public void Stop()
    {
        State = PlanExecutionState.Idle;
        ActivePlan = null;
        CurrentPass = null;
        NextPass = null;
        TimeToNextSwitch = null;
        IsManualOverrideActive = false;
        _lastSwitchedNoradId = null;
        _alertedPassIds.Clear();
    }

    /// <summary>
    /// Pause execution (manual override). Automatic switching is suspended.
    /// </summary>
    public void Pause()
    {
        if (State == PlanExecutionState.Running)
        {
            State = PlanExecutionState.Paused;
            IsManualOverrideActive = true;
        }
    }

    /// <summary>
    /// Resume automatic switching after a pause/manual override.
    /// </summary>
    public void Resume()
    {
        if (State == PlanExecutionState.Paused)
        {
            State = PlanExecutionState.Running;
            IsManualOverrideActive = false;
        }
    }

    /// <summary>
    /// Called by the timer tick (typically 1s interval). Evaluates the current time against
    /// the plan and returns the appropriate action. Pure state transition logic.
    /// </summary>
    /// <param name="utcNow">Current UTC time.</param>
    /// <returns>The action to perform at this tick.</returns>
    internal PlanExecutorAction Tick(DateTime utcNow)
    {
        if (ActivePlan is null || State == PlanExecutionState.Idle || State == PlanExecutionState.Completed)
            return new PlanExecutorAction.NoAction();

        var passes = ActivePlan.ScheduledPasses;
        if (passes.Count == 0)
        {
            State = PlanExecutionState.Completed;
            PlanCompleted?.Invoke(this, EventArgs.Empty);
            return new PlanExecutorAction.MarkCompleted();
        }

        // Find current pass: pass whose [AOS, LOS] contains utcNow
        var currentPass = FindCurrentPass(passes, utcNow);
        CurrentPass = currentPass;

        // Find next pass (first pass whose AOS > utcNow)
        var nextPass = FindNextPass(passes, utcNow);
        NextPass = nextPass;

        // Compute time to next switch
        TimeToNextSwitch = nextPass is not null
            ? nextPass.Scored.Pass.AosUtc - utcNow
            : null;

        // Check if final pass LOS has been exceeded → plan completed
        var finalPass = passes[^1];
        if (utcNow > finalPass.Scored.Pass.LosUtc)
        {
            State = PlanExecutionState.Completed;
            CurrentPass = null;
            NextPass = null;
            TimeToNextSwitch = null;
            PlanCompleted?.Invoke(this, EventArgs.Empty);
            return new PlanExecutorAction.MarkCompleted();
        }

        // If paused, don't perform automatic actions (but still update state above)
        if (State == PlanExecutionState.Paused)
            return new PlanExecutorAction.NoAction();

        // Pre-alert logic: fire at max(previousLOS, AOS - preAlertMinutes)
        var preAlertAction = CheckPreAlert(passes, utcNow);
        if (preAlertAction is not null)
            return preAlertAction;

        // Focus switch logic: if utcNow reaches a pass's AOS and we haven't switched yet
        if (currentPass is not null)
        {
            var noradId = currentPass.Scored.Pass.NoradId;
            if (_lastSwitchedNoradId != noradId)
            {
                _lastSwitchedNoradId = noradId;

                try
                {
                    _liveTracking.FocusedNoradId = noradId;
                }
                catch
                {
                    // Log error but continue to next pass (per error handling spec)
                }

                FocusSwitched?.Invoke(this, new FocusSwitchEventArgs
                {
                    NoradId = noradId,
                    SatelliteName = currentPass.Scored.Pass.SatelliteName
                });

                return new PlanExecutorAction.SwitchFocus(noradId);
            }
        }

        // During gaps, retain FocusedNoradId of most recently completed pass → NoAction
        return new PlanExecutorAction.NoAction();
    }

    private static ScheduledPass? FindCurrentPass(IReadOnlyList<ScheduledPass> passes, DateTime utcNow)
    {
        for (var i = 0; i < passes.Count; i++)
        {
            var pass = passes[i];
            if (utcNow >= pass.Scored.Pass.AosUtc && utcNow <= pass.Scored.Pass.LosUtc)
                return pass;
        }
        return null;
    }

    private static ScheduledPass? FindNextPass(IReadOnlyList<ScheduledPass> passes, DateTime utcNow)
    {
        for (var i = 0; i < passes.Count; i++)
        {
            if (passes[i].Scored.Pass.AosUtc > utcNow)
                return passes[i];
        }
        return null;
    }

    private PlanExecutorAction? CheckPreAlert(IReadOnlyList<ScheduledPass> passes, DateTime utcNow)
    {
        for (var i = 0; i < passes.Count; i++)
        {
            var pass = passes[i];
            var passId = pass.Scored.Id;

            // Skip already-alerted passes
            if (_alertedPassIds.Contains(passId))
                continue;

            var aos = pass.Scored.Pass.AosUtc;

            // Only consider passes whose AOS is in the future
            if (aos <= utcNow)
                continue;

            // Compute alert time: max(previousLOS, AOS - preAlertMinutes)
            var alertTime = aos - TimeSpan.FromMinutes(_preAlertMinutes);

            if (i > 0)
            {
                var previousLos = passes[i - 1].Scored.Pass.LosUtc;
                if (previousLos > alertTime)
                    alertTime = previousLos;
            }

            // Fire if we've reached or passed the alert time
            if (utcNow >= alertTime)
            {
                _alertedPassIds.Add(passId);

                var timeUntilAos = aos - utcNow;
                var maxElev = pass.Scored.Pass.MaxElevationDeg;
                var satName = pass.Scored.Pass.SatelliteName;

                PreAlertRaised?.Invoke(this, new AosPreAlertEventArgs
                {
                    SatelliteName = satName,
                    TimeUntilAos = timeUntilAos,
                    MaxElevationDeg = maxElev
                });

                return new PlanExecutorAction.RaisePreAlert(satName, timeUntilAos, maxElev);
            }
        }

        return null;
    }

    public void Dispose()
    {
        Stop();
    }
}
