namespace OscarWatch.Core.Sstv;

public enum SstvAutoStartAction
{
    None,
    Start,
    Stop,
}

/// <summary>One sample of the inputs that decide an automatic SSTV start or stop.</summary>
public readonly record struct SstvAutoStartInput(
    bool Enabled,
    string? FocusedNoradId,
    double? ElevationDeg,
    double StartElevationDeg,
    bool ReceiverRunning,
    bool ReceiverAutoStarted);

/// <summary>
/// Decides when SSTV listening should start and stop for the focused satellite. Call it at about
/// 1 Hz while the SSTV window is open.
/// <para>
/// Edge triggered: a rise above the start elevation starts listening once. A manual Stop during the
/// pass is not overridden. The trigger re-arms only after the satellite has set.
/// </para>
/// </summary>
public sealed class SstvAutoStartCoordinator
{
    /// <summary>Below this elevation the satellite counts as set. Listening stops and re-arms.</summary>
    public const double StopElevationDeg = 0.0;

    /// <summary>
    /// Consecutive below-stop samples before stopping or re-arming, so one noisy sample does not end a pass.
    /// </summary>
    public const int BelowStopConfirmTicks = 3;

    private bool _hasSample;
    private double _previousElevationDeg = -90.0;
    private string? _trackedNoradId;
    private int _belowStopTicks;
    private bool _armed = true;

    public SstvAutoStartAction Process(SstvAutoStartInput input)
    {
        if (!input.Enabled)
        {
            var stopForDisable = input.ReceiverRunning && input.ReceiverAutoStarted
                ? SstvAutoStartAction.Stop
                : SstvAutoStartAction.None;
            ResetTracking();
            return stopForDisable;
        }

        if (string.IsNullOrWhiteSpace(input.FocusedNoradId))
        {
            ResetTracking();
            return SstvAutoStartAction.None;
        }

        if (_trackedNoradId is not null
            && !string.Equals(_trackedNoradId, input.FocusedNoradId, StringComparison.Ordinal))
        {
            // Focus moved to another satellite: an automatic session belongs to the old one.
            var stopForFocus = input.ReceiverRunning && input.ReceiverAutoStarted
                ? SstvAutoStartAction.Stop
                : SstvAutoStartAction.None;
            ResetTracking();
            _trackedNoradId = input.FocusedNoradId;
            return stopForFocus;
        }

        _trackedNoradId = input.FocusedNoradId;

        // Propagation can miss a sample. Keep history and do nothing rather than read it as set.
        if (input.ElevationDeg is not { } elevation || !double.IsFinite(elevation))
            return SstvAutoStartAction.None;

        if (elevation < StopElevationDeg)
        {
            _belowStopTicks++;
            if (_belowStopTicks >= BelowStopConfirmTicks)
            {
                _armed = true;
                if (input.ReceiverRunning && input.ReceiverAutoStarted)
                {
                    RecordSample(elevation);
                    return SstvAutoStartAction.Stop;
                }
            }
        }
        else
        {
            _belowStopTicks = 0;
            if (!input.ReceiverRunning && _armed)
            {
                var crossedStart = _hasSample
                    && _previousElevationDeg < input.StartElevationDeg
                    && elevation >= input.StartElevationDeg;
                var alreadyAboveOnFirstSample = !_hasSample && elevation >= input.StartElevationDeg;
                if (crossedStart || alreadyAboveOnFirstSample)
                {
                    _armed = false;
                    RecordSample(elevation);
                    return SstvAutoStartAction.Start;
                }
            }
        }

        RecordSample(elevation);
        return SstvAutoStartAction.None;
    }

    /// <summary>Forget the current pass. The next rise is treated as a new pass.</summary>
    public void ResetTracking()
    {
        _hasSample = false;
        _previousElevationDeg = -90.0;
        _trackedNoradId = null;
        _belowStopTicks = 0;
        _armed = true;
    }

    private void RecordSample(double elevation)
    {
        _hasSample = true;
        _previousElevationDeg = elevation;
    }
}
