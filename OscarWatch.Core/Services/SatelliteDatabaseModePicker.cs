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
        uplinkMode = NormalizeHamsAtMode(uplinkMode);
        downlinkMode = NormalizeHamsAtMode(downlinkMode);

        var uplinkMhz = ToMhz(mode.UplinkKHz);
        var downlinkMhz = ToMhz(mode.DownlinkKHz);

        return new HamsAtActivationHints(
            string.IsNullOrWhiteSpace(uplinkMode) ? null : uplinkMode,
            string.IsNullOrWhiteSpace(downlinkMode) ? null : downlinkMode,
            uplinkMhz,
            downlinkMhz,
            uplinkMhz is > 0 ? MhzDirectionForBand(mode, uplink: true) : null,
            downlinkMhz is > 0 ? MhzDirectionForBand(mode, uplink: false) : null);
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

    public static string? ResolveDefaultActivationMode(
        string? uplinkMode,
        string? downlinkMode,
        bool hasUplink,
        bool hasDownlink)
    {
        uplinkMode = NormalizeOptionalHamsAtMode(uplinkMode);
        downlinkMode = NormalizeOptionalHamsAtMode(downlinkMode);

        if (!hasUplink)
            return downlinkMode;

        if (!hasDownlink)
            return uplinkMode;

        if (string.IsNullOrWhiteSpace(uplinkMode))
            return downlinkMode;

        if (string.IsNullOrWhiteSpace(downlinkMode))
            return uplinkMode;

        if (string.Equals(uplinkMode, downlinkMode, StringComparison.OrdinalIgnoreCase))
            return uplinkMode;

        return downlinkMode;
    }

    internal static string NormalizeHamsAtMode(string mode)
    {
        var normalized = TransponderCatModes.Normalize(mode);
        return normalized switch
        {
            "FMN" => "FM",
            _ => normalized
        };
    }

    internal static string? NormalizeOptionalHamsAtMode(string? mode) =>
        string.IsNullOrWhiteSpace(mode) ? null : NormalizeHamsAtMode(mode);

    internal static double? ToMhz(double kHz) => kHz > 0 ? kHz / 1000.0 : null;

    internal static string MhzDirectionForBand(SatelliteTransponderMode mode, bool uplink)
    {
        var reverse = mode.DopplerCorrection == DopplerCorrection.Reverse;
        return uplink
            ? reverse ? "up" : "down"
            : reverse ? "down" : "up";
    }
}
