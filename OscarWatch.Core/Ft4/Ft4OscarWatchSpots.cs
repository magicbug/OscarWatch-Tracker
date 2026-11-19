using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Core.Ft4;

/// <summary>Maps an FT4 receive decode onto an OscarWatch.org satellite spot.</summary>
public static class Ft4OscarWatchSpots
{
    public const string Mode = "FT4";

    public static bool HasApiToken(string? apiToken) =>
        !string.IsNullOrWhiteSpace(apiToken);

    /// <summary>
    /// Spot for an ordinary receive decode. The reporter callsign and grid come from the
    /// OscarWatch account, so they are not set here. Own transmissions and own echoes are skipped.
    /// Nothing is spotted when FT4 is blocked on this satellite (FM, FO-29, AO-7) or the
    /// radio is not on a sideband.
    /// </summary>
    public static bool TryCreate(
        Ft4DecodedMessage decode,
        LiveTrackerSnapshot snapshot,
        string? myCallsign,
        string? client,
        out OscarWatchSatelliteSpot spot,
        string mode = Mode) =>
        TryCreate(decode, snapshot, myCallsign, client, noradId: null, out spot, mode);

    /// <inheritdoc cref="TryCreate(Ft4DecodedMessage, LiveTrackerSnapshot, string?, string?, out OscarWatchSatelliteSpot, string)"/>
    public static bool TryCreate(
        Ft4DecodedMessage decode,
        LiveTrackerSnapshot snapshot,
        string? myCallsign,
        string? client,
        string? noradId,
        out OscarWatchSatelliteSpot spot,
        string mode = Mode)
    {
        spot = null!;
        if (!decode.IsReceiveActivity || decode.IsRejected)
            return false;

        var satellite = snapshot.SatelliteName?.Trim() ?? "";
        if (satellite.Length is 0 or > 64)
            return false;

        if (!CanReport(satellite, noradId, snapshot))
            return false;

        var call = Ft4MessageCodec.NormalizeCall(decode.CallDe ?? "");
        if (!Ft4PskReporterSpots.IsPlausibleCallsign(call))
            return false;

        var mine = Ft4MessageCodec.NormalizeCall(myCallsign ?? "");
        if (mine.Length > 0 && call.Equals(mine, StringComparison.Ordinal))
            return false;

        var heardAt = decode.SlotUtc.Kind switch
        {
            DateTimeKind.Utc => decode.SlotUtc,
            DateTimeKind.Local => decode.SlotUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(decode.SlotUtc, DateTimeKind.Utc)
        };

        var snr = float.IsFinite(decode.SnrDb) ? (int)Math.Round(decode.SnrDb) : 0;
        spot = new OscarWatchSatelliteSpot(
            satellite,
            call,
            TryHeardGrid(decode.Extra),
            Math.Clamp(snr, -50, 50),
            FrequencyOrNull(snapshot.UplinkHz),
            FrequencyOrNull(snapshot.DownlinkHz),
            heardAt,
            TrimTo(decode.Text, 64),
            TrimTo(client, 128),
            mode);
        return true;
    }

    /// <summary>
    /// Same rule as operating FT4: not on an FM satellite, and not on FO-29 or AO-7.
    /// A snapshot with no sideband is skipped too, so a focus change cannot stamp
    /// the new satellite onto a spot before its transponder mode is known.
    /// </summary>
    private static bool CanReport(string satellite, string? noradId, LiveTrackerSnapshot snapshot)
    {
        var radio = new SatelliteTransponderMode
        {
            UplinkMode = snapshot.Mode ?? "",
            DownlinkMode = snapshot.ModeRx ?? ""
        };
        if (!Ft4SatelliteEligibility.IsAllowed(satellite, noradId, radio))
            return false;

        return IsSideband(snapshot.Mode) || IsSideband(snapshot.ModeRx);
    }

    private static bool IsSideband(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return false;

        return mode.Contains("USB", StringComparison.OrdinalIgnoreCase)
            || mode.Contains("LSB", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("SSB", StringComparison.OrdinalIgnoreCase);
    }

    private static long? FrequencyOrNull(long hz) => hz > 0 ? hz : null;

    private static string? TryHeardGrid(string? extra)
    {
        if (string.IsNullOrWhiteSpace(extra) || Ft4MessageCodec.IsRr73(extra))
            return null;

        var grid = extra.Trim();
        if (grid.Length is not (4 or 6) || !Ft4MessageCodec.IsGrid(grid))
            return null;
        if (grid.Length == 6 && !(char.IsLetter(grid[4]) && char.IsLetter(grid[5])))
            return null;

        return grid.Length == 6
            ? grid[..4].ToUpperInvariant() + grid[4..].ToLowerInvariant()
            : grid.ToUpperInvariant();
    }

    private static string? TrimTo(string? value, int max)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0)
            return null;
        return text.Length <= max ? text : text[..max];
    }
}
