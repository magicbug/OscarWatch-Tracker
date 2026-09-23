using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4QsoSequencerTests
{
    private static Ft4DecodedMessage Msg(string text, float snr = -8f, float freq = 1200f) =>
        new(DateTime.UtcNow, text, freq, 0.4f, snr, null, null, null, false);

    [Fact]
    public void Cq_then_grid_reply_then_rr73_when_skip_rrr()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartCq(evenSlot: true);
        Assert.Equal(Ft4QsoPhase.CallingCq, seq.Phase);
        Assert.Equal("CQ MM9SQL IO85", seq.CurrentTxMessage);
        Assert.True(seq.PreferEvenSlot);

        Assert.False(seq.OnDecoded(Msg("MM9SQL G4ABC IO91")));
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.Equal("G4ABC", seq.TheirCall);
        Assert.Equal("IO91", seq.TheirGrid);
        Assert.StartsWith("G4ABC MM9SQL", seq.CurrentTxMessage);
        Assert.NotNull(seq.ReportSent);
        // Stay on our CQ slots; the answerer already took the opposite parity.
        Assert.True(seq.PreferEvenSlot);

        Assert.False(seq.OnDecoded(Msg("MM9SQL G4ABC -10", snr: -10f)));
        Assert.Equal("G4ABC MM9SQL RR73", seq.CurrentTxMessage);

        Assert.True(seq.OnTxCompleted());
        Assert.Equal(Ft4QsoPhase.Finished, seq.Phase);
        Assert.True(seq.CanLog());
        Assert.Equal("-10", seq.ReportReceived);
    }

    [Fact]
    public void Cq_on_odd_slots_stays_odd_after_reply()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartCq(evenSlot: false);
        Assert.False(seq.PreferEvenSlot);
        seq.OnDecoded(Msg("MM9SQL 2M0SQL IO87"));
        Assert.False(seq.PreferEvenSlot);
        Assert.StartsWith("2M0SQL MM9SQL", seq.CurrentTxMessage);
    }

    [Fact]
    public void Cq_uses_rrr_path_when_skip_disabled()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => false);
        seq.StartCq(evenSlot: true);
        seq.OnDecoded(Msg("MM9SQL G4ABC IO91"));
        seq.OnDecoded(Msg("MM9SQL G4ABC +02"));
        Assert.Equal("G4ABC MM9SQL RRR", seq.CurrentTxMessage);

        Assert.False(seq.OnDecoded(Msg("MM9SQL G4ABC RR73")));
        Assert.Equal("G4ABC MM9SQL 73", seq.CurrentTxMessage);
        Assert.True(seq.OnTxCompleted());
        Assert.Equal(Ft4QsoPhase.Finished, seq.Phase);
    }

    [Fact]
    public void Answer_cq_sends_grid_reply()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartAnswer(Msg("CQ G4ABC JO01", freq: 1200f), oppositeEvenSlot: false);
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.Equal("G4ABC", seq.TheirCall);
        Assert.Equal("G4ABC MM9SQL IO85", seq.CurrentTxMessage);
        // Hold Tx Freq default: keep 1500 Hz rather than jump to their 1200 Hz.
        Assert.Equal(1500f, seq.TxAudioHz);
        Assert.True(seq.TransmitEnabled);
    }

    [Fact]
    public void Answer_cq_jumps_to_decode_hz_when_hold_disabled()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true, () => false);
        seq.TxAudioHz = 1500;
        seq.StartAnswer(Msg("CQ G4ABC JO01", freq: 1200f), oppositeEvenSlot: false);
        Assert.Equal(1200f, seq.TxAudioHz);
        Assert.Equal("G4ABC MM9SQL IO85", seq.CurrentTxMessage);
    }

    [Fact]
    public void Cq_reply_keeps_tx_hz_when_hold_enabled()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true, () => true);
        seq.TxAudioHz = 1650;
        seq.StartCq(evenSlot: true);
        seq.OnDecoded(Msg("MM9SQL G4ABC IO91", freq: 1100f));
        Assert.Equal(1650f, seq.TxAudioHz);
    }

    [Fact]
    public void Cq_reply_jumps_to_their_hz_when_hold_disabled()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true, () => false);
        seq.TxAudioHz = 1650;
        seq.StartCq(evenSlot: true);
        seq.OnDecoded(Msg("MM9SQL G4ABC IO91", freq: 1100f));
        Assert.Equal(1100f, seq.TxAudioHz);
    }

    [Fact]
    public void Answer_cq_keeps_grid_when_their_cq_is_heard_again()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartAnswer(Msg("CQ 2M0SQL IO87", freq: 1031f), oppositeEvenSlot: false);
        Assert.Equal("2M0SQL MM9SQL IO85", seq.CurrentTxMessage);

        // Same station keeps calling CQ; must not skip ahead to a report.
        Assert.False(seq.OnDecoded(Msg("CQ 2M0SQL IO87", snr: 14f, freq: 1031f)));
        Assert.Equal("2M0SQL MM9SQL IO85", seq.CurrentTxMessage);
        Assert.Null(seq.ReportSent);
        Assert.Equal("IO87", seq.TheirGrid);
    }

    [Fact]
    public void Answer_ignores_own_echo()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartAnswer(Msg("2M0SQL MM9SQL +14"), oppositeEvenSlot: false);
        Assert.Equal(Ft4QsoPhase.Idle, seq.Phase);
        Assert.Equal("", seq.CurrentTxMessage);
        Assert.False(seq.TransmitEnabled);
    }

    [Fact]
    public void Answer_cq_then_their_report_then_roger_then_73()
    {
        // Standard answer path (WSJT-X style):
        // they CQ → we grid → they +NN → we R+NN → they RR73 → we 73
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartAnswer(Msg("CQ 2M0SQL IO87"), oppositeEvenSlot: false);
        Assert.Equal("2M0SQL MM9SQL IO85", seq.CurrentTxMessage);

        Assert.False(seq.OnDecoded(Msg("MM9SQL 2M0SQL +12", snr: 10f)));
        Assert.Equal("2M0SQL MM9SQL R+10", seq.CurrentTxMessage);
        Assert.Equal("+12", seq.ReportReceived);
        Assert.Equal("+10", seq.ReportSent);

        Assert.False(seq.OnDecoded(Msg("MM9SQL 2M0SQL RR73")));
        Assert.Equal("2M0SQL MM9SQL 73", seq.CurrentTxMessage);
        Assert.True(seq.OnTxCompleted());
        Assert.Equal(Ft4QsoPhase.Finished, seq.Phase);
    }

    [Fact]
    public void Roger_report_received_is_stored_without_the_R()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartCq(evenSlot: true);
        seq.OnDecoded(Msg("MM9SQL G4ABC IO91", snr: -6f));
        Assert.Equal("-06", seq.ReportSent);

        Assert.False(seq.OnDecoded(Msg("MM9SQL G4ABC R+14")));
        Assert.Equal("+14", seq.ReportReceived);
        Assert.Equal("G4ABC MM9SQL RR73", seq.CurrentTxMessage);
    }

    [Fact]
    public void Answer_directed_grid_sends_report()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartAnswer(Msg("MM9SQL G4ABC JO01", snr: -6f), oppositeEvenSlot: true);
        Assert.Equal("G4ABC", seq.TheirCall);
        Assert.Equal("JO01", seq.TheirGrid);
        Assert.Equal("G4ABC MM9SQL -06", seq.CurrentTxMessage);
        Assert.Equal("-06", seq.ReportSent);
    }

    [Fact]
    public void Answer_report_with_skip_rrr_sends_rr73()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartAnswer(Msg("MM9SQL G4ABC -07"), oppositeEvenSlot: true);
        Assert.Equal("G4ABC MM9SQL RR73", seq.CurrentTxMessage);
        Assert.Equal("-07", seq.ReportReceived);
    }

    [Fact]
    public void Second_caller_ignored_while_in_qso()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartCq(evenSlot: true);
        seq.OnDecoded(Msg("MM9SQL G4ABC IO91"));
        var messageBefore = seq.CurrentTxMessage;
        seq.OnDecoded(Msg("MM9SQL M0XYZ IO92"));
        Assert.Equal("G4ABC", seq.TheirCall);
        Assert.Equal(messageBefore, seq.CurrentTxMessage);
    }

    [Fact]
    public void HaltTx_stops_calling_cq()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartCq(evenSlot: true);
        seq.HaltTx();
        Assert.False(seq.TransmitEnabled);
        Assert.Equal(Ft4QsoPhase.Idle, seq.Phase);
    }

    [Fact]
    public void Their_73_after_rr73_finishes_without_more_tx()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartCq(evenSlot: true);
        seq.OnDecoded(Msg("MM9SQL G4ABC IO91"));
        seq.OnDecoded(Msg("MM9SQL G4ABC -08"));
        Assert.Equal("G4ABC MM9SQL RR73", seq.CurrentTxMessage);
        Assert.True(seq.OnDecoded(Msg("MM9SQL G4ABC 73")));
        Assert.Equal(Ft4QsoPhase.Finished, seq.Phase);
        Assert.False(seq.TransmitEnabled);
    }

    [Fact]
    public void Force_report_and_73_rearm_a_finished_qso()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        Assert.False(seq.ForceReport(-6f));
        Assert.False(seq.Force73());

        seq.StartCq(evenSlot: true);
        seq.OnDecoded(Msg("MM9SQL G4ABC IO91", snr: -6f));
        Assert.Equal("-06", seq.ReportSent);

        seq.HaltTx();
        Assert.True(seq.ForceReport(12f));
        Assert.Equal("G4ABC MM9SQL -06", seq.CurrentTxMessage);
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.True(seq.TransmitEnabled);
        Assert.False(seq.OnTxCompleted());

        Assert.True(seq.Force73());
        Assert.Equal("G4ABC MM9SQL 73", seq.CurrentTxMessage);
        Assert.True(seq.TransmitEnabled);
        Assert.False(seq.OnTxCompleted());
        Assert.Equal(Ft4QsoPhase.Finished, seq.Phase);
        Assert.False(seq.TransmitEnabled);

        var fresh = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        fresh.StartAnswer(Msg("CQ G4ABC JO01"), oppositeEvenSlot: false);
        Assert.Null(fresh.ReportSent);
        Assert.True(fresh.ForceReport(4f));
        Assert.Equal("G4ABC MM9SQL +04", fresh.CurrentTxMessage);
    }
}
