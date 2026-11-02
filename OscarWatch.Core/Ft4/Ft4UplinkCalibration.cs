using OscarWatch.Core.Models;

namespace OscarWatch.Core.Ft4;

/// <summary>Keys and labels for per-transponder FT4 uplink echo calibration.</summary>
public static class Ft4UplinkCalibration
{
    public static bool IsTransponderKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) && key.Contains('|', StringComparison.Ordinal);

    /// <summary>Stable key for one transponder row (satellite name + mode + nominal frequencies).</summary>
    public static string FormatTransponderKey(string? satelliteName, SatelliteTransponderMode? mode)
    {
        if (string.IsNullOrWhiteSpace(satelliteName) || mode is null)
            return "";

        return $"{satelliteName.Trim()}|{mode.Type.Trim()}|{mode.DownlinkKHz:F4}|{mode.UplinkKHz:F4}";
    }

    public static string FormatDisplayLabel(string key)
    {
        if (!IsTransponderKey(key))
            return key.Trim();

        var parts = key.Split('|');
        if (parts.Length < 2)
            return key;

        return $"{parts[0]} · {parts[1]}";
    }

    public static string? TryGetSatelliteFromKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var pipe = key.IndexOf('|');
        return pipe <= 0 ? key.Trim() : key[..pipe].Trim();
    }
}
