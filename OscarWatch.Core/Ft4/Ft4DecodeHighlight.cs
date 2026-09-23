namespace OscarWatch.Core.Ft4;

public enum Ft4DecodeHighlightKind
{
    None = 0,
    CallingMe,
    Replying
}

/// <summary>
/// Which decode rows are painted. A station addressing us is "calling me".
/// Once that station is the QSO partner, their lines are "replying" instead.
/// </summary>
public static class Ft4DecodeHighlight
{
    public const string DefaultCallingMeColour = "#66E6B15A";
    public const string DefaultReplyingColour = "#665CB88A";

    public static Ft4DecodeHighlightKind Classify(
        Ft4DecodedMessage message,
        string? myCall,
        string? partnerCall)
    {
        if (!message.IsReceiveActivity)
            return Ft4DecodeHighlightKind.None;

        var mine = Ft4MessageCodec.NormalizeCall(myCall ?? "");
        if (mine.Length == 0
            || string.IsNullOrWhiteSpace(message.CallTo)
            || !message.CallTo.Equals(mine, StringComparison.OrdinalIgnoreCase))
        {
            return Ft4DecodeHighlightKind.None;
        }

        var partner = Ft4MessageCodec.NormalizeCall(partnerCall ?? "");
        if (partner.Length > 0
            && !string.IsNullOrWhiteSpace(message.CallDe)
            && message.CallDe.Equals(partner, StringComparison.OrdinalIgnoreCase))
        {
            return Ft4DecodeHighlightKind.Replying;
        }

        return Ft4DecodeHighlightKind.CallingMe;
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
