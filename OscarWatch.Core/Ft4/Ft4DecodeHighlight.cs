namespace OscarWatch.Core.Ft4;

public enum Ft4DecodeHighlightKind
{
    None = 0,
    CallingMe,
    Replying,
    NewCall,
    NewGrid
}

/// <summary>
/// Which decode rows are painted. A station addressing us is "calling me".
/// Once that station is the QSO partner, their lines are "replying" instead.
/// Otherwise, receive lines for a callsign or grid not yet in the logbook use
/// "new call" / "new grid".
/// </summary>
public static class Ft4DecodeHighlight
{
    public const string DefaultCallingMeColour = "#66E6B15A";
    public const string DefaultReplyingColour = "#665CB88A";
    public const string DefaultNewCallColour = "#664D9DE8";
    public const string DefaultNewGridColour = "#66C07AD0";

    public static Ft4DecodeHighlightKind Classify(
        Ft4DecodedMessage message,
        string? myCall,
        string? partnerCall,
        IReadOnlySet<string>? workedCalls = null,
        IReadOnlySet<string>? workedGridFields = null)
    {
        if (!message.IsReceiveActivity)
            return Ft4DecodeHighlightKind.None;

        var mine = Ft4MessageCodec.NormalizeCall(myCall ?? "");
        if (mine.Length > 0
            && !string.IsNullOrWhiteSpace(message.CallTo)
            && message.CallTo.Equals(mine, StringComparison.OrdinalIgnoreCase))
        {
            var partner = Ft4MessageCodec.NormalizeCall(partnerCall ?? "");
            if (partner.Length > 0
                && !string.IsNullOrWhiteSpace(message.CallDe)
                && message.CallDe.Equals(partner, StringComparison.OrdinalIgnoreCase))
            {
                return Ft4DecodeHighlightKind.Replying;
            }

            return Ft4DecodeHighlightKind.CallingMe;
        }

        var de = Ft4MessageCodec.NormalizeCall(message.CallDe ?? "");
        if (de.Length == 0 || (mine.Length > 0 && de.Equals(mine, StringComparison.Ordinal)))
            return Ft4DecodeHighlightKind.None;

        if (workedCalls is not null && !workedCalls.Contains(de))
            return Ft4DecodeHighlightKind.NewCall;

        var field = GridField(message.Extra);
        if (field is not null
            && workedGridFields is not null
            && !workedGridFields.Contains(field))
        {
            return Ft4DecodeHighlightKind.NewGrid;
        }

        return Ft4DecodeHighlightKind.None;
    }

    /// <summary>Four-character Maidenhead field from an FT4 extra token, or null.</summary>
    public static string? GridField(string? extra)
    {
        if (!Ft4MessageCodec.IsGrid(extra)
            || Ft4MessageCodec.IsClosing(extra)
            || Ft4MessageCodec.IsReport(extra))
        {
            return null;
        }

        var grid = extra!.Trim().ToUpperInvariant();
        return grid.Length >= 4 ? grid[..4] : null;
    }

    /// <summary>Returns #AARRGGBB, or null when <paramref name="text"/> is not a colour.</summary>
    public static string? NormalizeColour(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var hex = text.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];

        if (hex.Length is not (3 or 4 or 6 or 8))
            return null;

        foreach (var c in hex)
        {
            if (!Uri.IsHexDigit(c))
                return null;
        }

        if (hex.Length == 3)
            hex = $"FF{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
        else if (hex.Length == 4)
            hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}{hex[3]}{hex[3]}";
        else if (hex.Length == 6)
            hex = "FF" + hex;

        return "#" + hex.ToUpperInvariant();
    }
}
