using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;

namespace OscarWatch.Core.Services;

public static class SatelliteDatabaseModePicker
{
    public static SatelliteTransponderMode? ResolveDefaultMode(
        ISatelliteDatabaseService database,
        string satelliteName,
        string? noradId,
        IReadOnlyDictionary<string, SatelliteFrequencySelection>? frequencySelections)
    {
        var entry = database.TryGetEntry(satelliteName, noradId);
        if (entry is null || entry.Modes.Count == 0)
            return null;

        var selection = ResolveFrequencySelection(entry, satelliteName, frequencySelections);
        return ResolveMode(entry.Modes, selection);
    }

    public static HamsAtActivationHints ToActivationHints(
        SatelliteTransponderMode? mode,
        SatelliteFrequencySelection? selection = null,
        bool cwKeepSidebandDownlink = false)
    {
        if (mode is null)
            return HamsAtActivationHints.Empty;

        var cwUplink = selection?.GetCwUplinkForMode(mode.Type) ?? false;
        var (uplinkMode, downlinkMode) = TransponderOperatingModes.GetEffectiveModes(
            mode,
            cwUplink,
            cwKeepSidebandDownlink);

        var available = ResolveAvailableApiModes(mode, uplinkMode, downlinkMode);
        var suggested = ResolveSuggestedApiMode(mode, uplinkMode, downlinkMode, cwUplink, available);

        return new HamsAtActivationHints(
            suggested,
            available,
            ToMhz(mode.UplinkKHz),
            ToMhz(mode.DownlinkKHz));
    }

    private static SatelliteFrequencySelection ResolveFrequencySelection(
        SatelliteRadioEntry entry,
        string satelliteName,
        IReadOnlyDictionary<string, SatelliteFrequencySelection>? frequencySelections)
    {
        if (frequencySelections is null)
            return new SatelliteFrequencySelection();

        if (frequencySelections.TryGetValue(entry.Name, out var selection))
            return selection;

        var trimmedName = satelliteName.Trim();
        if (frequencySelections.TryGetValue(trimmedName, out selection))
            return selection;

        return new SatelliteFrequencySelection();
    }

    private static SatelliteTransponderMode? ResolveMode(
        IReadOnlyList<SatelliteTransponderMode> modes,
        SatelliteFrequencySelection selection)
    {
        if (modes.Count == 0)
            return null;

        if (selection.ModeIndex >= 0 && selection.ModeIndex < modes.Count)
            return modes[selection.ModeIndex];

        var byType = modes.FirstOrDefault(m =>
            m.Type.Equals(selection.ModeType, StringComparison.OrdinalIgnoreCase));
        return byType ?? modes[0];
    }

    /// <summary>
    /// Maps CAT/database mode names to hams.at API values: SSB, CW, Data, FM.
    /// </summary>
    public static string? ToApiMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return null;

        var normalized = TransponderCatModes.Normalize(mode);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (normalized.Contains("FM", StringComparison.OrdinalIgnoreCase))
            return HamsAtApiModes.Fm;

        if (normalized.Equals("CW", StringComparison.OrdinalIgnoreCase))
            return HamsAtApiModes.Cw;

        if (normalized.Contains("DATA", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("DIGI", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("PACKET", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("RTTY", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("FT8", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("FT4", StringComparison.OrdinalIgnoreCase))
        {
            return HamsAtApiModes.Data;
        }

        if (normalized.Equals("USB", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("LSB", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("SSB", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("AM", StringComparison.OrdinalIgnoreCase))
        {
            return HamsAtApiModes.Ssb;
        }

        return null;
    }

    internal static IReadOnlyList<string> ResolveAvailableApiModes(
        SatelliteTransponderMode mode,
        string uplinkMode,
        string downlinkMode)
    {
        if (mode.IsFmMode)
            return HamsAtApiModes.FmOnly;

        var mapped = new HashSet<string>(StringComparer.Ordinal);
        var uplinkApi = ToApiMode(uplinkMode);
        var downlinkApi = ToApiMode(downlinkMode);
        if (uplinkApi is not null)
            mapped.Add(uplinkApi);
        if (downlinkApi is not null)
            mapped.Add(downlinkApi);

        if (mode.IsBeaconOnly)
        {
            if (mapped.Contains(HamsAtApiModes.Cw))
                return HamsAtApiModes.CwOnly;
            if (mapped.Contains(HamsAtApiModes.Data))
                return HamsAtApiModes.DataOnly;
            if (mapped.Contains(HamsAtApiModes.Fm))
                return HamsAtApiModes.FmOnly;
            return mapped.Count > 0 ? mapped.OrderBy(RankApiMode).ToArray() : HamsAtApiModes.CwOnly;
        }

        // Linear / multi-mode birds: hams.at accepts SSB, CW, and Data.
        if (mapped.Contains(HamsAtApiModes.Ssb)
            || mapped.Contains(HamsAtApiModes.Cw)
            || LooksLinear(mode))
        {
            return HamsAtApiModes.Linear;
        }

        if (mapped.Count == 1 && mapped.Contains(HamsAtApiModes.Data))
            return HamsAtApiModes.DataOnly;

        if (mapped.Count == 1 && mapped.Contains(HamsAtApiModes.Fm))
            return HamsAtApiModes.FmOnly;

        return mapped.Count > 0
            ? mapped.OrderBy(RankApiMode).ToArray()
            : HamsAtApiModes.All;
    }

    internal static string? ResolveSuggestedApiMode(
        SatelliteTransponderMode mode,
        string uplinkMode,
        string downlinkMode,
        bool cwUplink,
        IReadOnlyList<string> available)
    {
        if (available.Count == 0)
            return null;

        if (cwUplink && available.Contains(HamsAtApiModes.Cw))
            return HamsAtApiModes.Cw;

        var downlinkApi = ToApiMode(downlinkMode);
        if (downlinkApi is not null && available.Contains(downlinkApi))
            return downlinkApi;

        var uplinkApi = ToApiMode(uplinkMode);
        if (uplinkApi is not null && available.Contains(uplinkApi))
            return uplinkApi;

        if (mode.IsFmMode && available.Contains(HamsAtApiModes.Fm))
            return HamsAtApiModes.Fm;

        if (available.Contains(HamsAtApiModes.Ssb))
            return HamsAtApiModes.Ssb;

        return available[0];
    }

    private static bool LooksLinear(SatelliteTransponderMode mode)
    {
        var type = mode.Type ?? "";
        return type.Contains("SSB", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Linear", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Transponder", StringComparison.OrdinalIgnoreCase);
    }

    private static int RankApiMode(string mode) => mode switch
    {
        HamsAtApiModes.Ssb => 0,
        HamsAtApiModes.Cw => 1,
        HamsAtApiModes.Data => 2,
        HamsAtApiModes.Fm => 3,
        _ => 9
    };

    internal static double? ToMhz(double kHz) => kHz > 0 ? kHz / 1000.0 : null;
}
