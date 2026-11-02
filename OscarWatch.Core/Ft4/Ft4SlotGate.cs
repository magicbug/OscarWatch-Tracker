using OscarWatch.Core.Models;

namespace OscarWatch.Core.Ft4;

/// <summary>
/// When CAT Doppler is held between FT4 slot steps. FT4 is not a radio mode, so the
/// trigger is the FT4 transponder being selected while an FT4 decode session is running.
/// An open FT4 window alone does not hold the dial; other transponders track continuously.
/// </summary>
public static class Ft4SlotGate
{
    public const string TransponderType = "FT4";

    /// <summary>
    /// Catalogue FT4 transponder rows, including custom copies such as "FT4 Asia".
    /// </summary>
    public static bool IsFt4Transponder(SatelliteTransponderMode? mode)
    {
        if (mode is null)
            return false;

        var type = mode.Type.Trim();
        if (type.Length == 0)
            return false;

        return type.Equals(TransponderType, StringComparison.OrdinalIgnoreCase)
            || type.StartsWith(TransponderType, StringComparison.OrdinalIgnoreCase)
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
