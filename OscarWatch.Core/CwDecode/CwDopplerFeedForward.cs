using OscarWatch.Core.Sstv;

namespace OscarWatch.Core.CwDecode;

/// <summary>
/// Note offset left over when rig control tunes in steps. Radios receive CW on the upper
/// sideband and CW-R on the lower, so the note moves with the dial gap the same way SSB does.
/// </summary>
public static class CwDopplerFeedForward
{
    /// <returns>Hz to subtract from the received note.</returns>
    public static double AudioOffsetHz(string? downlinkMode, double? idealReceiveKHz, long? rigReceiveHz) =>
        SstvDopplerFeedForward.AudioOffsetHz(AsSideband(downlinkMode), idealReceiveKHz, rigReceiveHz);

    private static string? AsSideband(string? mode)
    {
        var m = mode?.Trim().ToUpperInvariant() ?? "";
        if (m is "CW-R" or "CWR" or "CW-L" or "CW-LSB")
            return "LSB";
        if (m.StartsWith("CW", StringComparison.Ordinal))
            return "USB";
        return mode;
    }
}
