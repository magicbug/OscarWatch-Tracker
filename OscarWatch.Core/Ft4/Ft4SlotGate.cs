using OscarWatch.Core.Models;

namespace OscarWatch.Core.Ft4;

/// <summary>
/// When CAT Doppler is held between slot steps, and which transponder rows the FT4 window
/// runs. FT4 and FT8 are not radio modes, so the trigger is the selected transponder,
/// including a custom copy such as FT4 Asia. An open FT4 window on another transponder
/// does not hold the dial.
/// </summary>
public static class Ft4SlotGate
{
    public const string TransponderType = "FT4";

    public const string Ft8TransponderType = "FT8";

    /// <summary>
    /// Catalogue FT4 rows, including a custom copy whose type starts with FT4
    /// (for example "FT4 Asia"). "FT40" is not a match.
    /// </summary>
    public static bool IsFt4Transponder(SatelliteTransponderMode? mode) =>
        IsTypeOf(mode, TransponderType);

    /// <summary>
    /// Catalogue FT8 rows, including a custom copy whose type starts with FT8
    /// (for example "FT8 Asia"). "FT80" is not a match.
    /// </summary>
    public static bool IsFt8Transponder(SatelliteTransponderMode? mode) =>
        IsTypeOf(mode, Ft8TransponderType);

    /// <summary>True for a transponder the FT4 window runs in either protocol.</summary>
    public static bool IsModemTransponder(SatelliteTransponderMode? mode) =>
        IsFt4Transponder(mode) || IsFt8Transponder(mode);

    /// <summary>
    /// The dial only moves on the steps the running session sends each slot,
    /// so the hold needs a running session on an FT4 or FT8 transponder or the dial would freeze.
    /// </summary>
    public static bool ShouldHold(bool sessionRunning, SatelliteTransponderMode? mode) =>
        sessionRunning && IsModemTransponder(mode);

    private static bool IsTypeOf(SatelliteTransponderMode? mode, string typeName)
    {
        if (mode is null)
            return false;

        var type = mode.Type.Trim();
        if (type.Length == 0)
            return false;

        if (type.Equals(typeName, StringComparison.OrdinalIgnoreCase))
            return true;

        return type.StartsWith(typeName, StringComparison.OrdinalIgnoreCase)
            && type.Length > typeName.Length
            && !char.IsLetterOrDigit(type[typeName.Length]);
    }
}
