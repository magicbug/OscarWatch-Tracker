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

    public Ft4QsoSequencer(
        Func<string> myCall,
        Func<string> myGrid,
        Func<bool> skipRrr,
        Func<bool>? holdTxFrequency = null)
    {
        _myCall = myCall;
        _myGrid = myGrid;
        _skipRrr = skipRrr;
        _holdTxFrequency = holdTxFrequency ?? (() => true);
    }

    public Ft4QsoPhase Phase { get; private set; } = Ft4QsoPhase.Idle;
    public string? TheirCall { get; private set; }
    public string? TheirGrid { get; private set; }
    public string? ReportSent { get; private set; }
    public string? ReportReceived { get; private set; }
    public string CurrentTxMessage { get; private set; } = "";
    public bool TransmitEnabled { get; private set; }
    public bool PreferEvenSlot { get; set; }
    public double TxAudioHz { get; set; } = 1500;

    public void Reset()
    {
        Phase = Ft4QsoPhase.Idle;
        TheirCall = null;
        TheirGrid = null;
        ReportSent = null;
        ReportReceived = null;
        CurrentTxMessage = "";
        TransmitEnabled = false;
    }

    public void StartCq(bool evenSlot)
    {
        PreferEvenSlot = evenSlot;
        Phase = Ft4QsoPhase.CallingCq;
        TheirCall = null;
        TheirGrid = null;
        ReportSent = null;
        ReportReceived = null;
        CurrentTxMessage = Ft4MessageCodec.BuildCq(_myCall(), _myGrid());
        TransmitEnabled = true;
    }

    /// <summary>Operator clicked a decode to answer.</summary>
    public void StartAnswer(Ft4DecodedMessage decode, bool oppositeEvenSlot)
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
        TheirCall = Ft4MessageCodec.NormalizeCall(callDe);
        TheirGrid = Ft4MessageCodec.IsGrid(extra) ? extra : TheirGrid;
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

            if (Ft4MessageCodec.IsGrid(extra))
            {
                TheirGrid = extra;
                ReportSent = Ft4MessageCodec.FormatSnrReport(decode.SnrDb);
                CurrentTxMessage = Ft4MessageCodec.BuildReport(TheirCall, my, ReportSent);
                return;
            }

            if (Ft4MessageCodec.IsClosing(extra))
            {
                CurrentTxMessage = Ft4MessageCodec.Build73(TheirCall, my);
                return;
            }
        }

        CurrentTxMessage = Ft4MessageCodec.BuildGridReply(TheirCall, my, _myGrid());
    }

    public void HaltTx()
    {
        TransmitEnabled = false;
        if (Phase == Ft4QsoPhase.CallingCq)
            Phase = Ft4QsoPhase.Idle;
    }

    public void EnableTx()
    {
        if (string.IsNullOrWhiteSpace(CurrentTxMessage))
            CurrentTxMessage = Ft4MessageCodec.BuildCq(_myCall(), _myGrid());
        if (Phase == Ft4QsoPhase.Idle || Phase == Ft4QsoPhase.Finished)
            Phase = Ft4QsoPhase.CallingCq;
        TransmitEnabled = true;
    }

    /// <summary>Operator-edited TX text from the FT4 window.</summary>
    public void SetTxMessage(string? message)
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
        if (!TransmitEnabled && Phase is not Ft4QsoPhase.InQso and not Ft4QsoPhase.CallingCq)
            return false;

        if (!Ft4MessageCodec.TryParse(decode.Text, out var callTo, out var callDe, out var extra)
            || string.IsNullOrWhiteSpace(callDe))
            return false;

        var my = _myCall();

        if (Phase == Ft4QsoPhase.CallingCq
            && Ft4MessageCodec.IsAddressedTo(callTo, my)
            && !callDe.Equals(my, StringComparison.OrdinalIgnoreCase))
        {
            TheirCall = Ft4MessageCodec.NormalizeCall(callDe);
            TheirGrid = Ft4MessageCodec.IsGrid(extra) ? extra : TheirGrid;
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
                ReportReceived = Ft4MessageCodec.NormalizeSnrReport(extra);
                ReportSent ??= Ft4MessageCodec.FormatSnrReport(decode.SnrDb);
                CurrentTxMessage = _skipRrr()
                    ? Ft4MessageCodec.BuildRr73(TheirCall, my)
                    : Ft4MessageCodec.BuildRrr(TheirCall, my);
            }
            return false;
        }

        if (Phase != Ft4QsoPhase.InQso || TheirCall is null)
            return false;

        if (!callDe.Equals(TheirCall, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!Ft4MessageCodec.IsAddressedTo(callTo, my) && !Ft4MessageCodec.IsCq(callTo))
            return false;

        // Still calling CQ: remember their grid, but do not skip our pending grid reply.
        if (Ft4MessageCodec.IsCq(callTo))
        {
            if (Ft4MessageCodec.IsGrid(extra))
                TheirGrid = extra;
            return false;
        }

        if (_skipRrr() && Ft4MessageCodec.Is73(extra))
        {
            // Their 73 after our RR73: contact done, no further TX.
            TransmitEnabled = false;
            Phase = Ft4QsoPhase.Finished;
            return CanLog();
        }

        if (Ft4MessageCodec.IsRrr(extra) || Ft4MessageCodec.IsRr73(extra) || Ft4MessageCodec.Is73(extra))
        {
            if (CurrentTxMessage.EndsWith(" RR73", StringComparison.Ordinal)
                || CurrentTxMessage.EndsWith(" 73", StringComparison.Ordinal))
            {
                // We already sent closing; their ack finishes the QSO.
                TransmitEnabled = false;
                Phase = Ft4QsoPhase.Finished;
                return CanLog();
            }

            CurrentTxMessage = Ft4MessageCodec.Build73(TheirCall, my);
            return false;
        }

        if (Ft4MessageCodec.IsGrid(extra))
        {
            TheirGrid = extra;
            // We answered their CQ and still owe our grid: do not jump to a report.
            if (IsPendingGridReply(my))
                return false;

            ReportSent = Ft4MessageCodec.FormatSnrReport(decode.SnrDb);
            CurrentTxMessage = Ft4MessageCodec.BuildReport(TheirCall, my, ReportSent);
            return false;
        }

        if (Ft4MessageCodec.IsReport(extra))
        {
            ReportReceived = Ft4MessageCodec.NormalizeSnrReport(extra);

            // After our grid reply to their CQ, a plain +NN means send R+NN next.
            // R+NN (or a second report after we already sent one) advances to RR73/RRR.
            if (ReportSent is null && !Ft4MessageCodec.IsRogerReport(extra))
            {
                ReportSent = Ft4MessageCodec.FormatSnrReport(decode.SnrDb);
                CurrentTxMessage = Ft4MessageCodec.BuildReport(
                    TheirCall,
                    my,
                    Ft4MessageCodec.FormatRogerReport(decode.SnrDb));
                return false;
            }

            ReportSent ??= Ft4MessageCodec.FormatSnrReport(decode.SnrDb);
            CurrentTxMessage = _skipRrr()
                ? Ft4MessageCodec.BuildRr73(TheirCall, my)
                : Ft4MessageCodec.BuildRrr(TheirCall, my);
            return false;
        }

        return false;
    }

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

    /// <summary>Called after a TX message was fully sent.</summary>
    public bool OnTxCompleted()
    {
        if (Phase != Ft4QsoPhase.InQso)
            return false;

        var msg = CurrentTxMessage;
        if (_skipRrr() && msg.EndsWith(" RR73", StringComparison.Ordinal))
        {
            TransmitEnabled = false;
            Phase = Ft4QsoPhase.Finished;
            return CanLog();
        }

        if (msg.EndsWith(" 73", StringComparison.Ordinal)
            && !msg.EndsWith(" RR73", StringComparison.Ordinal))
        {
            TransmitEnabled = false;
            Phase = Ft4QsoPhase.Finished;
            return CanLog();
        }

        return false;
    }

    public bool CanLog() =>
        TheirCall is not null
        && ReportSent is not null
        && ReportReceived is not null;
}
