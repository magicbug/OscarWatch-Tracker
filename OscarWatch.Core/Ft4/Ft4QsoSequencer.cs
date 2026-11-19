using System.Diagnostics.CodeAnalysis;

namespace OscarWatch.Core.Ft4;

public enum Ft4QsoPhase
{
    Idle,
    CallingCq,
    InQso,
    Finished
}

/// <summary>Auto-sequences standard satellite FT4 exchanges.</summary>
public sealed class Ft4QsoSequencer
{
    private readonly Func<string> _myCall;
    private readonly Func<string> _myGrid;
    private readonly Func<bool> _skipRrr;
    private readonly Func<bool> _holdTxFrequency;
    private readonly Func<bool> _autoReply;
    private readonly Func<string, bool> _isBlockedCall;
    private readonly Func<string> _cqModifier;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _heardLocators = new(StringComparer.OrdinalIgnoreCase);
    private bool? _autoReplyOverride;
    private bool _weSent73;
    private bool _theySent73;
    private AprioriUndo? _aprioriUndo;

    public Ft4QsoSequencer(
        Func<string> myCall,
        Func<string> myGrid,
        Func<bool> skipRrr,
        Func<bool>? holdTxFrequency = null,
        Func<bool>? autoReply = null,
        Func<string, bool>? isBlockedCall = null,
        Func<string>? cqModifier = null)
    {
        _myCall = myCall;
        _myGrid = myGrid;
        _skipRrr = skipRrr;
        _holdTxFrequency = holdTxFrequency ?? (() => true);
        _autoReply = autoReply ?? (() => true);
        _isBlockedCall = isBlockedCall ?? (_ => false);
        _cqModifier = cqModifier ?? (() => "");
    }

    private string BuildOwnCq() => Ft4MessageCodec.BuildCq(_myCall(), _myGrid(), _cqModifier());

    /// <summary>True when this callsign is on the block list. Does not change the contact.</summary>
    public bool IsBlockedCall(string? call) =>
        !string.IsNullOrWhiteSpace(call) && _isBlockedCall(call);

    /// <summary>
    /// The Auto reply checkbox. Overrides the settings read so a tick takes effect
    /// on the next decode, including one already on screen.
    /// </summary>
    public void SetAutoReply(bool enabled)
    {
        lock (_gate)
            _autoReplyOverride = enabled;
    }

    private bool AutoReplyOn => _autoReplyOverride ?? _autoReply();

    public bool IsCqMessage =>
        Ft4MessageCodec.TryParse(CurrentTxMessage, out var callTo, out _, out _)
        && Ft4MessageCodec.IsCq(callTo);

    public Ft4QsoPhase Phase { get; private set; } = Ft4QsoPhase.Idle;
    public string? TheirCall { get; private set; }
    public string? TheirGrid { get; private set; }
    public string? ReportSent { get; private set; }
    public string? ReportReceived { get; private set; }
    public string CurrentTxMessage { get; private set; } = "";
    public bool TransmitEnabled { get; private set; }

    /// <summary>Audio frequency, in Hz, of the station we are working.</summary>
    public double TheirAudioHz { get; private set; }

    /// <summary>
    /// UTC when the last contact reached 73. Decodes at or before this belong to that
    /// contact, so a new CQ must not answer them again.
    /// </summary>
    public DateTime? QsoCompletedUtc { get; private set; }
    public bool PreferEvenSlot { get; set; }
    public double TxAudioHz { get; set; } = 1500;

    public void Reset()
    {
        lock (_gate)
            ResetCore();
    }

    private void ResetCore()
    {
        Phase = Ft4QsoPhase.Idle;
        TheirCall = null;
        TheirGrid = null;
        ReportSent = null;
        ReportReceived = null;
        CurrentTxMessage = "";
        TransmitEnabled = false;
        QsoCompletedUtc = null;
        _weSent73 = false;
        _theySent73 = false;
        TheirAudioHz = 0;
        _heardLocators.Clear();
    }

    /// <summary>True when <paramref name="slotUtc"/> is part of the contact that just finished.</summary>
    public bool IsHistoricDecode(DateTime slotUtc) =>
        QsoCompletedUtc is not null && slotUtc <= QsoCompletedUtc.Value;

    public void StartCq(bool evenSlot)
    {
        // Decode and TX-complete run off the UI thread. Without the lock a 73 that
        // already passed its phase check can write the old contact back over this CQ.
        lock (_gate)
        {
            PreferEvenSlot = evenSlot;
            CurrentTxMessage = BuildOwnCq();
            BeginFreshCq(keepMessage: true);
        }
    }

    /// <summary>Operator clicked a decode to answer.</summary>
    public void StartAnswer(Ft4DecodedMessage decode, bool oppositeEvenSlot)
    {
        lock (_gate)
            StartAnswerCore(decode, oppositeEvenSlot);
    }

    private void StartAnswerCore(Ft4DecodedMessage decode, bool oppositeEvenSlot)
    {
        if (!Ft4MessageCodec.TryParse(decode.Text, out var callTo, out var callDe, out var extra)
            || string.IsNullOrWhiteSpace(callDe))
            return;

        var my = _myCall();
        // Ignore own echoes / own TX lines in the decode list.
        if (callDe.Equals(my, StringComparison.OrdinalIgnoreCase))
            return;

        PreferEvenSlot = oppositeEvenSlot;
        if (!_holdTxFrequency())
            TxAudioHz = decode.FreqHz;
        _weSent73 = false;
        _theySent73 = false;
        AssignTheirStation(callDe, extra);
        NoteTheirAudio(decode.FreqHz);
        Phase = Ft4QsoPhase.InQso;
        TransmitEnabled = true;
        ReportSent = null;
        ReportReceived = null;

        if (Ft4MessageCodec.IsCq(callTo))
        {
            // Standard first reply to a CQ is our grid, not a report.
            CurrentTxMessage = Ft4MessageCodec.BuildGridReply(TheirCall, my, _myGrid());
            return;
        }

        if (Ft4MessageCodec.IsAddressedTo(callTo, my))
        {
            if (Ft4MessageCodec.IsReport(extra))
            {
                ReportReceived = Ft4MessageCodec.NormalizeSnrReport(extra);
                ReportSent = Ft4MessageCodec.FormatSnrReport(decode.SnrDb);
                // A plain report still needs ours back (R+NN); only an R+NN is ready for RR73.
                CurrentTxMessage = !Ft4MessageCodec.IsRogerReport(extra)
                    ? Ft4MessageCodec.BuildReport(TheirCall, my, Ft4MessageCodec.FormatRogerReport(decode.SnrDb))
                    : _skipRrr()
                        ? Ft4MessageCodec.BuildRr73(TheirCall, my)
                        : Ft4MessageCodec.BuildRrr(TheirCall, my);
                return;
            }

            // No grid: the reply to a CQ from a non-standard call carries the full call instead.
            if (Ft4MessageCodec.IsGrid(extra) || string.IsNullOrWhiteSpace(extra))
            {
                if (Ft4MessageCodec.IsGrid(extra))
                    TheirGrid = extra;
                ReportSent = Ft4MessageCodec.FormatSnrReport(decode.SnrDb);
                CurrentTxMessage = Ft4MessageCodec.BuildReport(TheirCall, my, ReportSent);
                return;
            }

            if (Ft4MessageCodec.IsClosing(extra))
            {
                if (Ft4MessageCodec.Is73(extra) || Ft4MessageCodec.IsRr73(extra))
                    _theySent73 = true;
                CurrentTxMessage = Ft4MessageCodec.Build73(TheirCall, my);
                return;
            }
        }

        CurrentTxMessage = Ft4MessageCodec.BuildGridReply(TheirCall, my, _myGrid());
    }

    public void HaltTx()
    {
        lock (_gate)
        {
            TransmitEnabled = false;
            if (Phase == Ft4QsoPhase.CallingCq)
                Phase = Ft4QsoPhase.Idle;
        }
    }

    public void EnableTx()
    {
        lock (_gate)
            EnableTxCore();
    }

    private void EnableTxCore()
    {
        if (string.IsNullOrWhiteSpace(CurrentTxMessage))
            CurrentTxMessage = BuildOwnCq();

        // A CQ in the box is a new call. Leaving the previous contact in place
        // meant auto reply ignored the station that answered this CQ.
        if (IsCqMessage)
        {
            BeginFreshCq(keepMessage: true);
            return;
        }

        if (Phase == Ft4QsoPhase.Idle || Phase == Ft4QsoPhase.Finished)
            Phase = Ft4QsoPhase.CallingCq;
        TransmitEnabled = true;
    }

    /// <summary>
    /// Auto reply was turned on while a CQ is going out. Drop any previous contact
    /// so the next caller is answered. Returns false when this is not a CQ.
    /// </summary>
    public bool PrepareAutoReply()
    {
        lock (_gate)
            return PrepareAutoReplyCore();
    }

    private bool PrepareAutoReplyCore()
    {
        if (!TransmitEnabled || !AutoReplyOn || !IsCqMessage)
            return false;

        if (Phase != Ft4QsoPhase.CallingCq || TheirCall is not null || ReportSent is not null)
            BeginFreshCq(keepMessage: true);
        return true;
    }

    private void BeginFreshCq(bool keepMessage)
    {
        Phase = Ft4QsoPhase.CallingCq;
        TheirCall = null;
        TheirGrid = null;
        TheirAudioHz = 0;
        ReportSent = null;
        ReportReceived = null;
        _weSent73 = false;
        _theySent73 = false;
        if (!keepMessage || string.IsNullOrWhiteSpace(CurrentTxMessage))
            CurrentTxMessage = BuildOwnCq();
        TransmitEnabled = true;
    }

    /// <summary>
    /// Newline-separated messages to try when the first decode misses a weak copy.
    /// Only while a contact is open and both calls are known: their grid, a report, and,
    /// once a report has been exchanged, RR73 or 73.
    /// RRR is not guessed. A decoded RRR queues our 73, and satellite contacts usually skip that step.
    /// 73 and RR73 are not guessed while we are still sending the opening grid. That guess
    /// can win on the other station's CQ (the two messages share his callsign) and the
    /// sequencer would then send 73 before any report existed.
    /// Once our TX is already 73 or RR73, only those two are tried. A fresh report guess
    /// at that point would look like another huge report after the contact had moved on.
    /// </summary>
    public bool TryGetApHints(out string hints, out double audioHz)
    {
        lock (_gate)
            return TryGetApHintsCore(out hints, out audioHz);
    }

    private bool TryGetApHintsCore(out string hints, out double audioHz)
    {
        hints = "";
        audioHz = TheirAudioHz;
        if (Phase != Ft4QsoPhase.InQso)
            return false;
        if (string.IsNullOrWhiteSpace(TheirCall))
            return false;
        if (TheirAudioHz is < 200 or > 3000)
            return false;

        var my = Ft4MessageCodec.NormalizeCall(_myCall());
        var them = TheirCall;
        if (string.IsNullOrWhiteSpace(my))
            return false;

        // A standard call working our non-standard one sends its full call where it can
        // (WSJT-X): "<MYCALL> THEM" in reply to our CQ, "MYCALL <THEM> RR73" to close.
        var fullFromThem = Ft4MessageCodec.IsHashedCall(my) && !Ft4MessageCodec.IsHashedCall(them);

        var lines = new List<string>(164);
        if (!IsOutgoingClosing(CurrentTxMessage))
        {
            if (!string.IsNullOrWhiteSpace(TheirGrid))
                lines.Add(Ft4MessageCodec.BuildGridReply(my, them, TheirGrid));
            if (fullFromThem)
                lines.Add($"<{my}> {them}");

            for (var snr = -30; snr <= Ft4DecodeDepth.MaxHintedReportDb; snr++)
            {
                var report = Ft4MessageCodec.FormatSnrReport(snr);
                lines.Add(Ft4MessageCodec.BuildReport(my, them, report));
                lines.Add(Ft4MessageCodec.BuildReport(my, them, Ft4MessageCodec.FormatRogerReport(snr)));
            }
        }

        if (IsOutgoingClosing(CurrentTxMessage) || ReportSent is not null || ReportReceived is not null)
        {
            lines.Add(Ft4MessageCodec.BuildRr73(my, them));
            lines.Add(Ft4MessageCodec.Build73(my, them));
            if (fullFromThem)
            {
                lines.Add($"{my} <{them}> RR73");
                lines.Add($"{my} <{them}> 73");
            }
        }

        hints = string.Join('\n', lines);
        return true;
    }

    private void NoteTheirAudio(double hz)
    {
        if (hz is >= 200 and <= 3000)
            TheirAudioHz = hz;
    }

    /// <summary>Operator-edited TX text from the FT4 window.</summary>
    public void SetTxMessage(string? message)
    {
        lock (_gate)
            SetTxMessageCore(message);
    }

    private void SetTxMessageCore(string? message)
    {
        var text = (message ?? "").Trim()
            .Replace('\u2215', '/')
            .Replace('\u2044', '/')
            .ToUpperInvariant();
        CurrentTxMessage = text;
    }

    /// <summary>
    /// Process a decode during our receive period. Returns true when the contact is complete
    /// and ready to log.
    /// </summary>
    public bool OnDecoded(Ft4DecodedMessage decode)
    {
        lock (_gate)
        {
            // A guess that failed the CRC stays on screen. It must not move the contact:
            // the closest hint can be his message to someone else with our call written in.
            if (decode.IsRejected)
                return false;

            var before = decode.IsApriori ? Capture() : null;
            var finished = OnDecodedCore(decode);
            if (before is not null)
            {
                var after = Capture();
                _aprioriUndo = !finished
                    && after != before
                    && Ft4MessageCodec.TryParse(decode.Text, out _, out var callDe, out _)
                    && !string.IsNullOrWhiteSpace(callDe)
                        ? new AprioriUndo(decode.SlotUtc, Ft4MessageCodec.NormalizeCall(callDe), before, after)
                        : null;
            }

            return finished;
        }
    }

    /// <summary>
    /// A later pass decoded <paramref name="callDe"/> sending something else in the slot of the
    /// last hinted reply, so that reply was false. Put the contact back as it was before it.
    /// Returns false when nothing was undone: another hint, a different slot, or the contact
    /// has moved on since (operator action, another decode, or it finished and was logged).
    /// </summary>
    public bool RevertApriori(DateTime slotUtc, string callDe)
    {
        lock (_gate)
        {
            if (_aprioriUndo is not { } undo
                || undo.SlotUtc != slotUtc
                || !undo.CallDe.Equals(callDe, StringComparison.OrdinalIgnoreCase))
                return false;

            _aprioriUndo = null;
            if (Capture() != undo.After)
                return false;

            Restore(undo.Before);
            return true;
        }
    }

    private sealed record State(
        Ft4QsoPhase Phase,
        string? TheirCall,
        string? TheirGrid,
        string? ReportSent,
        string? ReportReceived,
        string CurrentTxMessage,
        bool TransmitEnabled,
        double TheirAudioHz,
        DateTime? QsoCompletedUtc,
        bool WeSent73,
        bool TheySent73);

    private sealed record AprioriUndo(DateTime SlotUtc, string CallDe, State Before, State After);

    private State Capture() => new(
        Phase,
        TheirCall,
        TheirGrid,
        ReportSent,
        ReportReceived,
        CurrentTxMessage,
        TransmitEnabled,
        TheirAudioHz,
        QsoCompletedUtc,
        _weSent73,
        _theySent73);

    private void Restore(State s)
    {
        Phase = s.Phase;
        TheirCall = s.TheirCall;
        TheirGrid = s.TheirGrid;
        ReportSent = s.ReportSent;
        ReportReceived = s.ReportReceived;
        CurrentTxMessage = s.CurrentTxMessage;
        TransmitEnabled = s.TransmitEnabled;
        TheirAudioHz = s.TheirAudioHz;
        QsoCompletedUtc = s.QsoCompletedUtc;
        _weSent73 = s.WeSent73;
        _theySent73 = s.TheySent73;
    }

    private bool OnDecodedCore(Ft4DecodedMessage decode)
    {
        if (!Ft4MessageCodec.TryParse(decode.Text, out var callTo, out var callDe, out var extra)
            || string.IsNullOrWhiteSpace(callDe))
            return false;

        // Remember locators even while idle, so a later contact can use this station's
        // grid instead of one left over from a call that was not finished.
        RememberLocator(callDe, extra);

        if (!TransmitEnabled
            && Phase is not Ft4QsoPhase.InQso and not Ft4QsoPhase.CallingCq and not Ft4QsoPhase.Finished)
            return false;

        var my = _myCall();

        if (Phase == Ft4QsoPhase.Finished)
        {
            // They missed RR73 and are still sending a report. Send it again.
            ResumeClosingIfTheyRepeatReport(callTo, callDe, extra, my);
            return false;
        }

        if (Phase == Ft4QsoPhase.CallingCq
            && Ft4MessageCodec.IsAddressedTo(callTo, my)
            && !callDe.Equals(my, StringComparison.OrdinalIgnoreCase))
        {
            // Auto reply off: keep calling CQ until the operator clicks a station.
            if (!AutoReplyOn)
                return false;

            // A 73 or RR73 is the end of a contact, not a station calling this CQ.
            if (Ft4MessageCodec.IsClosing(extra))
                return false;

            // A blocked callsign stays on the decode list. CQ continues until another
            // station calls, or the operator clicks this one.
            if (_isBlockedCall(callDe))
                return false;

            AssignTheirStation(callDe, extra);
            NoteTheirAudio(decode.FreqHz);
            // Stay on our CQ frequency (WSJT-X). Only answering a decode moves TX.
            // Keep our CQ slot parity. The caller answered on the opposite slot; flipping
            // would put both stations on the same TX slots (WSJT-X then cannot decode us).
            Phase = Ft4QsoPhase.InQso;

            if (Ft4MessageCodec.IsGrid(extra) || string.IsNullOrWhiteSpace(extra))
            {
                ReportSent = Ft4MessageCodec.FormatSnrReport(decode.SnrDb);
                CurrentTxMessage = Ft4MessageCodec.BuildReport(TheirCall, my, ReportSent);
            }
            else if (Ft4MessageCodec.IsReport(extra))
            {
                // A plain +NN while we are calling CQ is their first report to us, often
                // because they are answering a call we did not take. Reply R+NN. Only an
                // R+NN from them is ready for RR73.
                ReplyToReport(TheirCall, my, extra, decode.SnrDb);
            }
            return false;
        }

        if (Phase != Ft4QsoPhase.InQso || TheirCall is null)
            return false;

        if (!callDe.Equals(TheirCall, StringComparison.OrdinalIgnoreCase))
            return false;
        NoteTheirAudio(decode.FreqHz);
        if (!Ft4MessageCodec.IsAddressedTo(callTo, my) && !Ft4MessageCodec.IsCq(callTo))
            return false;

        // Still calling CQ: remember their grid, but do not skip our pending grid reply.
        if (Ft4MessageCodec.IsCq(callTo))
        {
            if (Ft4MessageCodec.IsGrid(extra))
                TheirGrid = extra;
            return false;
        }

        if (Ft4MessageCodec.Is73(extra) || Ft4MessageCodec.IsRr73(extra))
        {
            // A hinted 73 before any report is the opening-grid false decode: his CQ
            // shares the callsign, and 73 can be the best of a long hint list.
            if (decode.IsApriori && ReportSent is null && ReportReceived is null)
                return false;

            // RR73 is that station's 73. The contact ends only after we have sent one too.
            _theySent73 = true;
            if (!IsOutgoingClosing(CurrentTxMessage))
                CurrentTxMessage = Ft4MessageCodec.Build73(TheirCall, my);
            return TryCompleteContact();
        }

        if (Ft4MessageCodec.IsRrr(extra))
        {
            // RRR is not a sign-off. Send 73 unless one is already queued.
            if (!IsOutgoingClosing(CurrentTxMessage))
                CurrentTxMessage = Ft4MessageCodec.Build73(TheirCall, my);
            return false;
        }

        if (Ft4MessageCodec.IsGrid(extra))
        {
            TheirGrid = extra;
            // We called them on our TX slot and still owe the opening grid. On the
            // next slot they may call us (call, call, grid) instead of sending a
            // report. That is the start of the contact, so send a report and stay
            // on our slot. A hinted grid is left alone: their CQ shares the callsign
            // and can score as that call.
            if (decode.IsApriori && IsPendingGridReply(my))
                return false;

            ReportSent = Ft4MessageCodec.FormatSnrReport(decode.SnrDb);
            CurrentTxMessage = Ft4MessageCodec.BuildReport(TheirCall, my, ReportSent);
            return false;
        }

        if (Ft4MessageCodec.IsReport(extra))
        {
            ReplyToReport(TheirCall, my, extra, decode.SnrDb);
            return false;
        }

        return false;
    }

    /// <summary>
    /// Plain +NN gets R+NN back. An R+NN, or a plain report after we already sent our own
    /// plain report, advances to RR73 or RRR. A repeated +NN while R+NN is already queued
    /// stays on R+NN.
    /// </summary>
    private void ReplyToReport(string? theirCall, string my, string? extra, float snrDb)
    {
        if (string.IsNullOrWhiteSpace(theirCall))
            return;

        ReportReceived = Ft4MessageCodec.NormalizeSnrReport(extra);
        var plain = !Ft4MessageCodec.IsRogerReport(extra);
        if (plain && (ReportSent is null || IsOutgoingRogerReport(CurrentTxMessage)))
        {
            ReportSent = Ft4MessageCodec.FormatSnrReport(snrDb);
            CurrentTxMessage = Ft4MessageCodec.BuildReport(
                theirCall,
                my,
                Ft4MessageCodec.FormatRogerReport(snrDb));
            return;
        }

        ReportSent ??= Ft4MessageCodec.FormatSnrReport(snrDb);
        CurrentTxMessage = _skipRrr()
            ? Ft4MessageCodec.BuildRr73(theirCall, my)
            : Ft4MessageCodec.BuildRrr(theirCall, my);
    }

    private static bool IsOutgoingRogerReport(string message) =>
        Ft4MessageCodec.TryParse(message, out _, out _, out var extra)
        && Ft4MessageCodec.IsRogerReport(extra);

    /// <summary>
    /// The contact is already finished. A repeated report means they did not copy the
    /// closing message, so send RR73 (or RRR) again. Their 73 leaves the contact finished.
    /// </summary>
    private void ResumeClosingIfTheyRepeatReport(string? callTo, string callDe, string? extra, string my)
    {
        if (TheirCall is null || string.IsNullOrWhiteSpace(my))
            return;
        if (!callDe.Equals(TheirCall, StringComparison.OrdinalIgnoreCase))
            return;
        if (!Ft4MessageCodec.IsAddressedTo(callTo, my))
            return;
        if (!Ft4MessageCodec.IsReport(extra))
            return;

        ReportReceived ??= Ft4MessageCodec.NormalizeSnrReport(extra);
        CurrentTxMessage = _skipRrr()
            ? Ft4MessageCodec.BuildRr73(TheirCall, my)
            : Ft4MessageCodec.BuildRrr(TheirCall, my);
        Phase = Ft4QsoPhase.InQso;
        TransmitEnabled = true;
        QsoCompletedUtc = null;
        _weSent73 = false;
        _theySent73 = false;
    }

    /// <summary>
    /// Bind the contact to <paramref name="callDe"/>. A locator in this message wins.
    /// Otherwise keep this station's locator, or the last one heard from them.
    /// A different station never inherits the previous contact's grid.
    /// </summary>
    [MemberNotNull(nameof(TheirCall))]
    private void AssignTheirStation(string callDe, string? extra)
    {
        var call = Ft4MessageCodec.NormalizeCall(callDe);
        RememberLocator(call, extra);
        var sameStation = call.Equals(TheirCall, StringComparison.OrdinalIgnoreCase);
        TheirCall = call;

        if (IsLocator(extra))
        {
            TheirGrid = extra;
            return;
        }

        if (sameStation && !string.IsNullOrEmpty(TheirGrid))
            return;

        TheirGrid = _heardLocators.TryGetValue(call, out var heard) ? heard : null;
    }

    private void RememberLocator(string call, string? extra)
    {
        if (string.IsNullOrEmpty(call) || !IsLocator(extra))
            return;

        _heardLocators[call] = extra!;
    }

    /// <summary>RR73 matches the grid shape, but it is a sign-off, not a locator.</summary>
    private static bool IsLocator(string? extra) =>
        Ft4MessageCodec.IsGrid(extra) && !Ft4MessageCodec.IsRr73(extra);

    /// <summary>True while our next TX is still the first grid reply to their CQ.</summary>
    private bool IsPendingGridReply(string my)
    {
        if (TheirCall is null || ReportSent is not null)
            return false;

        return CurrentTxMessage.Equals(
            Ft4MessageCodec.BuildGridReply(TheirCall, my, _myGrid()),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Operator asked to send a signal report again. Reuses the report already sent,
    /// or <paramref name="snrDb"/> when none has been sent yet.
    /// </summary>
    public bool ForceReport(float? snrDb)
    {
        lock (_gate)
            return ForceReportCore(snrDb);
    }

    private bool ForceReportCore(float? snrDb)
    {
        if (string.IsNullOrWhiteSpace(TheirCall))
            return false;

        var my = _myCall();
        if (string.IsNullOrWhiteSpace(my))
            return false;

        var report = ReportSent ?? Ft4MessageCodec.FormatSnrReport(snrDb ?? 0f);
        ReportSent = report;
        CurrentTxMessage = Ft4MessageCodec.BuildReport(TheirCall, my, report);
        Phase = Ft4QsoPhase.InQso;
        TransmitEnabled = true;
        return true;
    }

    /// <summary>Operator asked to send 73 again to the station in the current QSO.</summary>
    public bool Force73()
    {
        lock (_gate)
            return Force73Core();
    }

    private bool Force73Core()
    {
        if (string.IsNullOrWhiteSpace(TheirCall))
            return false;

        var my = _myCall();
        if (string.IsNullOrWhiteSpace(my))
            return false;

        CurrentTxMessage = Ft4MessageCodec.Build73(TheirCall, my);
        Phase = Ft4QsoPhase.InQso;
        TransmitEnabled = true;
        return true;
    }

    /// <summary>
    /// Called after a TX message was fully sent. Our 73 or RR73 counts as sent.
    /// The contact finishes only when the other station has sent 73 or RR73 as well.
    /// </summary>
    public bool OnTxCompleted()
    {
        lock (_gate)
        {
            if (Phase != Ft4QsoPhase.InQso)
                return false;

            if (!IsOutgoingClosing(CurrentTxMessage))
                return false;

            _weSent73 = true;
            return TryCompleteContact();
        }
    }

    /// <summary>Stop and log only after both stations have sent 73 (RR73 counts).</summary>
    private bool TryCompleteContact()
    {
        if (!_weSent73 || !_theySent73)
            return false;

        FinishContact();
        return CanLog();
    }

    /// <summary>True for our 73 and RR73. RRR is not a sign-off.</summary>
    private static bool IsOutgoingClosing(string msg) =>
        msg.EndsWith(" RR73", StringComparison.Ordinal)
        || (msg.EndsWith(" 73", StringComparison.Ordinal)
            && !msg.EndsWith(" RR73", StringComparison.Ordinal));

    private void FinishContact()
    {
        TransmitEnabled = false;
        Phase = Ft4QsoPhase.Finished;
        QsoCompletedUtc = DateTime.UtcNow;
        // Leave the box on CQ so the next enable does not send the old 73.
        CurrentTxMessage = BuildOwnCq();
    }

    public bool CanLog() =>
        TheirCall is not null
        && ReportSent is not null
        && ReportReceived is not null;
}
