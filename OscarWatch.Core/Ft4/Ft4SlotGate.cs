using OscarWatch.Core.Models;

namespace OscarWatch.Core.Ft4;

/// <summary>
/// When CAT Doppler is held between FT4 slot steps. FT4 is not a radio mode, so the
/// trigger is the selected transponder, including a custom copy such as FT4 Asia.
/// An open FT4 window on another transponder does not hold the dial.
/// </summary>
public static class Ft4SlotGate
{
    public const string TransponderType = "FT4";

    /// <summary>
    /// Catalogue FT4 rows, including a custom copy whose type starts with FT4
    /// (for example "FT4 Asia"). "FT40" is not a match.
    /// </summary>
    public static bool IsFt4Transponder(SatelliteTransponderMode? mode)
    {
        if (mode is null)
            return false;

        var type = mode.Type.Trim();
        if (type.Length == 0)
            return false;

        if (type.Equals(TransponderType, StringComparison.OrdinalIgnoreCase))
            return true;

        return type.StartsWith(TransponderType, StringComparison.OrdinalIgnoreCase)
            && type.Length > TransponderType.Length
            && !char.IsLetterOrDigit(type[TransponderType.Length]);
    }

    /// <summary>
    /// The dial only moves on the steps the running FT4 session sends each slot,
    /// so the hold needs a running session on an FT4 transponder or the dial would freeze.
    /// </summary>
    public static bool ShouldHold(bool sessionRunning, SatelliteTransponderMode? mode) =>
        sessionRunning && IsFt4Transponder(mode);
}
