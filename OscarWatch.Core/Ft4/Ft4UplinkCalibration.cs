using System.Globalization;
using OscarWatch.Core.Display;
using OscarWatch.Core.Models;

namespace OscarWatch.Core.Ft4;

/// <summary>Keys and labels for per-transponder FT4 uplink echo calibration.</summary>
public static class Ft4UplinkCalibration
{
    public static bool IsTransponderKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) && key.Contains('|', StringComparison.Ordinal);

    /// <summary>Stable key for one transponder row (satellite name, type, and nominal frequencies).</summary>
    public static string FormatTransponderKey(string? satelliteName, SatelliteTransponderMode? mode)
    {
        if (string.IsNullOrWhiteSpace(satelliteName) || mode is null)
            return "";

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}|{1}|{2:F4}|{3:F4}",
            satelliteName.Trim(),
            mode.Type.Trim(),
            mode.DownlinkKHz,
            mode.UplinkKHz);
    }

    public static string FormatDisplayLabel(string key)
    {
        if (!IsTransponderKey(key))
            return key.Trim();

        var parts = key.Split('|');
        if (parts.Length < 2)
            return key;

        if (parts.Length >= 3
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var downKHz)
            && downKHz > 0)
        {
            return $"{parts[0]} · {parts[1]} · {FrequencyDisplayFormat.FormatMHzCompact(downKHz)} MHz";
        }

        return $"{parts[0]} · {parts[1]}";
    }

    public static bool TryParseTransponderKey(
        string? key,
        out string satelliteName,
        out SatelliteTransponderMode mode)
    {
        mode = new SatelliteTransponderMode();
        satelliteName = "";
        if (string.IsNullOrWhiteSpace(key) || !key.Contains('|', StringComparison.Ordinal))
            return false;

        var parts = key.Split('|');
        if (parts.Length < 4)
            return false;

        satelliteName = parts[0].Trim();
        mode.Type = parts[1].Trim();
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var down))
            return false;
        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var up))
            return false;

        mode.DownlinkKHz = down;
        mode.UplinkKHz = up;
        return satelliteName.Length > 0 && Ft4SlotGate.IsFt4Transponder(mode);
    }
}
