namespace OscarWatch.Core.Eme;

/// <summary>
/// Everything the Moon window shows at one instant, built from the Moon's position, the operator's settings and
/// the frequency. It holds no UI types, so it can be tested without a window.
/// </summary>
/// <param name="TimeUtc">The time the position is for (map time).</param>
/// <param name="Sample">The Moon's position and motion.</param>
/// <param name="IsUp">Whether the Moon counts as up under the operator's limits.</param>
/// <param name="ElevationLimitDeg">The elevation limit in use.</param>
/// <param name="BandId">The selected band.</param>
/// <param name="FrequencyMHz">The operating frequency, or null when the entry is not valid.</param>
/// <param name="EchoDelaySeconds">Echo delay at the topocentric distance.</param>
/// <param name="OwnEchoShiftHz">Shift of the operator's own echo, or null with no valid frequency.</param>
/// <param name="ExtraPathLossDb">Extra path loss against the reference distance.</param>
/// <param name="LossClass">The class of that loss.</param>
/// <param name="AbsolutePathLossDb">Free-space two-way loss, or null with no valid frequency.</param>
/// <param name="Circular">Whether the operator uses circular polarisation on this band.</param>
/// <param name="BeamwidthDeg">The antenna beamwidth, if entered.</param>
/// <param name="Sky">Phase, size, separations and apsides; null if they could not be worked out.</param>
/// <param name="Next">The next rise or set; null if there is none within three days.</param>
public sealed record EmeFrame(
    DateTime TimeUtc,
    MoonSample Sample,
    bool IsUp,
    double ElevationLimitDeg,
    string BandId,
    double? FrequencyMHz,
    double EchoDelaySeconds,
    double? OwnEchoShiftHz,
    double ExtraPathLossDb,
    PathLossClass LossClass,
    double? AbsolutePathLossDb,
    bool Circular,
    double? BeamwidthDeg,
    MoonSkyContext? Sky,
    MoonNextEvent? Next)
{
    /// <summary>
    /// True when a screen reader should be told about a change: the Moon going up or down, or a flag appearing
    /// or going. Second-by-second movement is not material.
    /// </summary>
    public bool IsMaterialChange(EmeFrame? previous) =>
        previous is null
        || previous.IsUp != IsUp
        || previous.LossClass != LossClass
        || previous.BandId != BandId
        || previous.Sky?.SunTooClose != Sky?.SunTooClose
        || previous.Sky?.GalacticCentreTooClose != Sky?.GalacticCentreTooClose;
}
