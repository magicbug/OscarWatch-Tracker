using OscarWatch.Core.Models;

namespace OscarWatch.Core.Ft4;

/// <summary>
/// Policy for where OscarWatch FT4 may be used. FM satellites are unsuitable for FT4.
/// FO-29's licence does not permit this digital mode, and AMSAT has asked that FT4
/// not be used on AO-7.
/// </summary>
public static class Ft4SatelliteEligibility
{
    /// <summary>NORAD catalog number for FO-29 (Fuji-OSCAR 29).</summary>
    public const string Fo29NoradId = "24278";

    public const string Fo29Name = "FO-29";

    /// <summary>NORAD catalog number for AO-7 (AMSAT-OSCAR 7), without leading zeros.</summary>
    public const string Ao7NoradId = "7530";

    public const string Ao7Name = "AO-7";

    public enum BlockReason
    {
        None = 0,
        FmMode,
        Fo29,
        Ao7,
    }

    public static bool IsFo29(string? satelliteName, string? noradId)
    {
        if (NoradEquals(noradId, Fo29NoradId))
            return true;

        if (string.IsNullOrWhiteSpace(satelliteName))
            return false;

        var name = satelliteName.Trim();
        return name.Equals(Fo29Name, StringComparison.OrdinalIgnoreCase)
            || name.Equals("Fuji-OSCAR 29", StringComparison.OrdinalIgnoreCase)
            || name.Equals("FO29", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAo7(string? satelliteName, string? noradId)
    {
        if (NoradEquals(noradId, Ao7NoradId))
            return true;

        if (string.IsNullOrWhiteSpace(satelliteName))
            return false;

        var name = satelliteName.Trim();
        return name.Equals(Ao7Name, StringComparison.OrdinalIgnoreCase)
            || name.Equals("AO-07", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AO7", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AO07", StringComparison.OrdinalIgnoreCase)
            || name.Equals("OSCAR 7", StringComparison.OrdinalIgnoreCase)
            || name.Equals("OSCAR-7", StringComparison.OrdinalIgnoreCase);
    }

    public static BlockReason Evaluate(string? satelliteName, string? noradId, SatelliteTransponderMode? mode)
    {
        if (IsFo29(satelliteName, noradId))
            return BlockReason.Fo29;

        if (IsAo7(satelliteName, noradId))
            return BlockReason.Ao7;

        if (mode?.IsFmMode == true)
            return BlockReason.FmMode;

        return BlockReason.None;
    }

    public static bool IsAllowed(string? satelliteName, string? noradId, SatelliteTransponderMode? mode) =>
        Evaluate(satelliteName, noradId, mode) == BlockReason.None;

    /// <summary>Localisation key for <see cref="BlockReason"/> (empty when allowed).</summary>
    public static string StatusKey(BlockReason reason) => reason switch
    {
        BlockReason.FmMode => "Ft4.Blocked.FmSatellite",
        BlockReason.Fo29 => "Ft4.Blocked.Fo29",
        BlockReason.Ao7 => "Ft4.Blocked.Ao7",
        _ => ""
    };

    private static bool NoradEquals(string? noradId, string catalog)
    {
        if (string.IsNullOrWhiteSpace(noradId))
            return false;

        var trimmed = noradId.Trim().TrimStart('0');
        if (trimmed.Length == 0)
            trimmed = "0";
        return trimmed.Equals(catalog, StringComparison.OrdinalIgnoreCase);
    }
}
