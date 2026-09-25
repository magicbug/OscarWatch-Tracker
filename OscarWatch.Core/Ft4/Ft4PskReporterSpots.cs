using OscarWatch.Core.Models;
using OscarWatch.Core.PskReporter;

namespace OscarWatch.Core.Ft4;

/// <summary>Maps FT4 receive decodes and live tracking onto PSK Reporter records.</summary>
public static class Ft4PskReporterSpots
{
    public const string Mode = "FT4";

    /// <summary>
    /// Receiver record for this station. The satellite name goes in the antenna field
    /// because PSK Reporter has no comment field.
    /// </summary>
    public static bool TryCreateReceiver(
        string? callsign,
        string? grid,
        string? satelliteName,
        string decodingSoftware,
        out PskReporterReceiver receiver)
    {
        receiver = null!;
        var call = Ft4MessageCodec.NormalizeCall(callsign ?? "");
        var locator = NormalizeLocator(grid);
        var sat = satelliteName?.Trim() ?? "";
        if (!IsPlausibleCallsign(call) || locator is null || sat.Length == 0)
            return false;

        receiver = new PskReporterReceiver(call, locator, decodingSoftware, sat);
        return true;
    }

    /// <summary>
    /// Sender record for an ordinary receive decode. The frequency is the Doppler-corrected uplink
    /// so PSK Reporter files the spot on the uplink band; there is no field for the downlink.
    /// </summary>
    public static bool TryCreateSpot(Ft4DecodedMessage decode, LiveTrackerSnapshot snapshot, out PskReporterSpot spot)
    {
        spot = null!;
        if (!decode.IsReceiveActivity || !snapshot.IsAvailable || snapshot.UplinkHz <= 0 || snapshot.UplinkHz > uint.MaxValue)
            return false;

        var call = Ft4MessageCodec.NormalizeCall(decode.CallDe ?? "");
        if (!IsPlausibleCallsign(call))
            return false;

        string? locator = null;
        if (Ft4MessageCodec.IsGrid(decode.Extra) && !Ft4MessageCodec.IsRr73(decode.Extra))
            locator = Ft4MessageCodec.NormalizeGrid(decode.Extra!);

        spot = new PskReporterSpot(
            call,
            locator,
            snapshot.UplinkHz,
            (int)Math.Round(decode.SnrDb),
            Mode,
            DateTime.SpecifyKind(decode.SlotUtc, DateTimeKind.Utc));
        return true;
    }

    /// <summary>Rejects CQ modifiers (DX, POTA), hashes, and junk: a call needs letters and a digit.</summary>
    public static bool IsPlausibleCallsign(string call)
    {
        if (call.Length is < 3 or > 13)
            return false;

        var hasDigit = false;
        var hasLetter = false;
        foreach (var c in call)
        {
            if (char.IsAsciiDigit(c))
                hasDigit = true;
            else if (char.IsAsciiLetterUpper(c))
                hasLetter = true;
            else if (c != '/')
                return false;
        }

        return hasDigit && hasLetter;
    }

    private static string? NormalizeLocator(string? grid)
    {
        var g = grid?.Trim() ?? "";
        if (g.Length > 6)
            g = g[..6];
        if (g.Length is not (4 or 6) || !Ft4MessageCodec.IsGrid(g))
            return null;
        return Ft4MessageCodec.NormalizeGrid(g[..4]) + g[4..].ToLowerInvariant();
    }
}
