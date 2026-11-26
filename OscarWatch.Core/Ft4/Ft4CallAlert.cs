namespace OscarWatch.Core.Ft4;

/// <summary>
/// Decides when a station calling this operator is the start of a contact,
/// so a sound can play once instead of on every line of the QSO.
/// </summary>
public sealed class Ft4CallAlert
{
    /// <summary>
    /// How long an unanswered station can keep calling before the sound plays again.
    /// </summary>
    public static readonly TimeSpan RepeatQuietFor = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, DateTime> _announcedSlotUtc = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True the first time <paramref name="message"/> is a station calling
    /// <paramref name="myCall"/> to open a contact. Repeats from that station stay
    /// quiet while they are the station already being worked
    /// (<paramref name="partnerBeforeThisDecode"/>), and for
    /// <see cref="RepeatQuietFor"/> when the call has not been answered.
    /// </summary>
    public bool ShouldPlay(Ft4DecodedMessage message, string? myCall, string? partnerBeforeThisDecode)
    {
        if (!message.IsReceiveActivity || message.IsRejected)
            return false;

        var mine = Ft4MessageCodec.NormalizeCall(myCall ?? "");
        if (mine.Length == 0)
            return false;

        var callTo = message.CallTo;
        var callDe = message.CallDe;
        var extra = message.Extra;
        if (string.IsNullOrWhiteSpace(callTo) && string.IsNullOrWhiteSpace(callDe)
            && !Ft4MessageCodec.TryParse(message.Text, out callTo, out callDe, out extra))
            return false;

        if (!Ft4MessageCodec.IsAddressedTo(callTo, mine))
            return false;

        if (Ft4MessageCodec.IsClosing(extra))
            return false;

        var from = Ft4MessageCodec.NormalizeCall(callDe ?? "");
        if (from.Equals(mine, StringComparison.OrdinalIgnoreCase))
            return false;
        if (from.Length == 0)
            from = "?";

        var partner = Ft4MessageCodec.NormalizeCall(partnerBeforeThisDecode ?? "");
        if (partner.Length > 0 && partner.Equals(from, StringComparison.OrdinalIgnoreCase))
            return false;

        if (_announcedSlotUtc.TryGetValue(from, out var announced)
            && message.SlotUtc >= announced
            && message.SlotUtc - announced < RepeatQuietFor)
            return false;

        _announcedSlotUtc[from] = message.SlotUtc;
        return true;
    }

    /// <summary>
    /// The station being worked changed. Their next call is a new contact,
    /// even inside <see cref="RepeatQuietFor"/>.
    /// </summary>
    public void NotePartnerChanged(string? previous, string? current)
    {
        var prev = Ft4MessageCodec.NormalizeCall(previous ?? "");
        if (prev.Length == 0)
            return;

        var now = Ft4MessageCodec.NormalizeCall(current ?? "");
        if (prev.Equals(now, StringComparison.OrdinalIgnoreCase))
            return;

        _announcedSlotUtc.Remove(prev);
    }
}
