namespace OscarWatch.Core.Ft4;

/// <summary>
/// Decides uplink corrections from Tune tone measurements (one per slot). A correction is only
/// made when two slots agree, and it is undone if the next slot does not show the tone on the
/// marker: then the peak was not our tone, or the trim never reached the radio.
/// </summary>
public sealed class Ft4TuneCalibrator
{
    public const double OnMarkerHz = 15;
    public const double AgreeHz = 20;
    public const double ConfirmHz = 40;

    private double? _candidateErrorHz;
    private double? _appliedCorrectionHz;

    public bool GaveUp { get; private set; }
    public double? CandidateErrorHz => _candidateErrorHz;
    public double? AwaitingConfirmHz => _appliedCorrectionHz;

    public enum ActionKind
    {
        None,
        Apply,
        Confirmed,
        Undo
    }

    public readonly record struct Decision(ActionKind Kind, double CorrectionHz = 0);

    public void Reset()
    {
        _candidateErrorHz = null;
        _appliedCorrectionHz = null;
        GaveUp = false;
    }

    /// <summary>Feed one slot's tone error (peak minus TX marker), or null when no tone was found.</summary>
    public Decision Next(double? errorHz)
    {
        if (GaveUp)
            return new Decision(ActionKind.None);

        if (_appliedCorrectionHz is { } applied)
        {
            _appliedCorrectionHz = null;
            if (errorHz is { } after && Math.Abs(after) <= ConfirmHz)
                return new Decision(ActionKind.Confirmed, after);

            GaveUp = true;
            return new Decision(ActionKind.Undo, -applied);
        }

        if (errorHz is not { } error || Math.Abs(error) < OnMarkerHz)
        {
            _candidateErrorHz = null;
            return new Decision(ActionKind.None);
        }

        if (_candidateErrorHz is not { } previous || Math.Abs(error - previous) > AgreeHz)
        {
            _candidateErrorHz = error;
            return new Decision(ActionKind.None);
        }

        var correction = (previous + error) / 2.0;
        _candidateErrorHz = null;
        _appliedCorrectionHz = correction;
        return new Decision(ActionKind.Apply, correction);
    }
}
