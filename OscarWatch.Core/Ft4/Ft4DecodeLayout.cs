namespace OscarWatch.Core.Ft4;

/// <summary>How the FT4 window lays out decodes.</summary>
public enum Ft4DecodeLayout
{
    /// <summary>One list with every decode.</summary>
    Basic = 0,

    /// <summary>Every decode on the left, and calls to this station plus its own sends on the right.</summary>
    Advanced
}

/// <summary>Which lines also appear in the "calling me" column of the advanced layout.</summary>
public static class Ft4CallingMeColumn
{
    /// <summary>
    /// True for a line this station sent, or a received line addressed to <paramref name="myCall"/>.
    /// Echoes of our own sends and rejected AP hints stay in the full list only.
    /// </summary>
    public static bool Includes(Ft4DecodedMessage message, string? myCall)
    {
        if (message.IsTransmitted)
            return true;
        if (!message.IsReceiveActivity || message.IsRejected)
            return false;

        var mine = Ft4MessageCodec.NormalizeCall(myCall ?? "");
        return mine.Length > 0
            && !string.IsNullOrWhiteSpace(message.CallTo)
            && Ft4MessageCodec.NormalizeCall(message.CallTo).Equals(mine, StringComparison.OrdinalIgnoreCase);
    }
}
