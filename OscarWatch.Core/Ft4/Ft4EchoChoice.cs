namespace OscarWatch.Core.Ft4;

/// <summary>Picks which copy of our own echo to show when the decoder reports it twice.</summary>
public static class Ft4EchoChoice
{
    /// <summary>
    /// True when <paramref name="candidateHz"/> should replace an echo already shown for this transmission.
    /// Only a copy clearly nearer the TX marker wins. A later pass on the same frequency is the same
    /// signal measured again, so the line already shown stays as it is.
    /// </summary>
    public static bool IsClearerCopy(float shownHz, float candidateHz, double txHz)
    {
        const double tieHz = 2.0;
        return Math.Abs(candidateHz - txHz) + tieHz < Math.Abs(shownHz - txHz);
    }
}
