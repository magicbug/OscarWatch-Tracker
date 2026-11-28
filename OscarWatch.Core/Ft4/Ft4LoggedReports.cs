namespace OscarWatch.Core.Ft4;

/// <summary>
/// Signal reports to store when the operator logs a contact by hand.
/// The sequencer values win. A missing one is taken from the TX message, then from
/// the newest report already on the list. A CQ from either station ends the search,
/// so a previous contact with the same call is left alone.
/// </summary>
public static class Ft4LoggedReports
{
    public static (string Sent, string Received) Resolve(
        string? myCall,
        string? theirCall,
        string? reportSent,
        string? reportReceived,
        string? pendingTxMessage,
        IReadOnlyList<Ft4DecodedMessage> newestFirst)
    {
        var my = Ft4MessageCodec.NormalizeCall(myCall ?? "");
        var them = Ft4MessageCodec.NormalizeCall(theirCall ?? "");
        var sent = Kept(reportSent);
        var received = Kept(reportReceived);
        if (them.Length == 0 || my.Length == 0)
            return (sent ?? "", received ?? "");

        if (sent is not null && received is not null)
            return (sent, received);

        sent ??= ReportWeAreSending(my, them, pendingTxMessage);
        if (sent is not null && received is not null)
            return (sent, received);

        foreach (var message in newestFirst)
        {
            if (message.IsRejected)
                continue;
            if (!TryParts(message, out var callTo, out var callDe, out var extra))
                continue;

            if (IsOurCq(message, my, callTo, callDe) || IsTheirCq(message, them, callTo, callDe))
                break;

            if (!Ft4MessageCodec.IsReport(extra))
                continue;

            if (sent is null && IsReportWeSent(message, my, them, callTo, callDe))
                sent = Kept(extra);
            else if (received is null && IsReportTheySent(message, my, them, callTo, callDe))
                received = Kept(extra);

            if (sent is not null && received is not null)
                break;
        }

        return (sent ?? "", received ?? "");
    }

    private static string? ReportWeAreSending(string my, string them, string? message)
    {
        if (!Ft4MessageCodec.TryParse(message ?? "", out var callTo, out var callDe, out var extra))
            return null;
        if (!IsReportWeSent(transmitted: true, ownEcho: false, my, them, callTo, callDe, extra))
            return null;
        return Kept(extra);
    }

    private static bool IsOurCq(Ft4DecodedMessage message, string my, string? callTo, string? callDe) =>
        Ft4MessageCodec.IsCq(callTo)
        && callDe is not null
        && callDe.Equals(my, StringComparison.OrdinalIgnoreCase)
        && (message.IsTransmitted || message.IsOwnEcho || message.IsReceiveActivity);

    private static bool IsTheirCq(Ft4DecodedMessage message, string them, string? callTo, string? callDe) =>
        message.IsReceiveActivity
        && Ft4MessageCodec.IsCq(callTo)
        && callDe is not null
        && callDe.Equals(them, StringComparison.OrdinalIgnoreCase);

    private static bool IsReportWeSent(
        Ft4DecodedMessage message,
        string my,
        string them,
        string? callTo,
        string? callDe) =>
        IsReportWeSent(message.IsTransmitted, message.IsOwnEcho, my, them, callTo, callDe, messageExtra: null);

    private static bool IsReportWeSent(
        bool transmitted,
        bool ownEcho,
        string my,
        string them,
        string? callTo,
        string? callDe,
        string? messageExtra)
    {
        if (!transmitted && !ownEcho)
            return false;
        if (callTo is null || callDe is null)
            return false;
        if (!callTo.Equals(them, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!callDe.Equals(my, StringComparison.OrdinalIgnoreCase))
            return false;
        return messageExtra is null || Ft4MessageCodec.IsReport(messageExtra);
    }

    private static bool IsReportTheySent(
        Ft4DecodedMessage message,
        string my,
        string them,
        string? callTo,
        string? callDe) =>
        message.IsReceiveActivity
        && callTo is not null
        && callDe is not null
        && callTo.Equals(my, StringComparison.OrdinalIgnoreCase)
        && callDe.Equals(them, StringComparison.OrdinalIgnoreCase);

    private static bool TryParts(
        Ft4DecodedMessage message,
        out string? callTo,
        out string? callDe,
        out string? extra)
    {
        if (Ft4MessageCodec.TryParse(message.Text, out callTo, out callDe, out extra)
            && !string.IsNullOrWhiteSpace(callDe))
            return true;

        callTo = string.IsNullOrWhiteSpace(message.CallTo)
            ? null
            : Ft4MessageCodec.NormalizeCall(message.CallTo);
        callDe = string.IsNullOrWhiteSpace(message.CallDe)
            ? null
            : Ft4MessageCodec.NormalizeCall(message.CallDe);
        extra = string.IsNullOrWhiteSpace(message.Extra) ? null : message.Extra.Trim().ToUpperInvariant();
        return !string.IsNullOrWhiteSpace(callDe);
    }

    private static string? Kept(string? report)
    {
        var text = Ft4MessageCodec.NormalizeSnrReport(report);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
