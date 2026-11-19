using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4QsoSequencerTests
{
    private static Ft4DecodedMessage Msg(string text, float snr = -8f, float freq = 1200f, bool ap = false) =>
        new(DateTime.UtcNow, text, freq, 0.4f, snr, null, null, null, false, IsApriori: ap);

    [Fact]
    public void Cq_then_grid_reply_then_rr73_when_skip_rrr()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartCq(evenSlot: true);
        Assert.Equal(Ft4QsoPhase.CallingCq, seq.Phase);
        Assert.Equal("CQ MM9SQL IO85", seq.CurrentTxMessage);
        Assert.True(seq.PreferEvenSlot);
        Assert.False(seq.TryGetApHints(out _, out _));

        Assert.False(seq.OnDecoded(Msg("MM9SQL G4ABC IO91")));
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.Equal("G4ABC", seq.TheirCall);
        Assert.Equal(1200, seq.TheirAudioHz);
        Assert.True(seq.TryGetApHints(out var hints, out var hz));
        Assert.Equal(1200, hz);
        Assert.Contains("MM9SQL G4ABC 73", hints, StringComparison.Ordinal);
        Assert.Contains("MM9SQL G4ABC RR73", hints, StringComparison.Ordinal);
        Assert.DoesNotContain("MM9SQL G4ABC RRR", hints, StringComparison.Ordinal);
        Assert.Contains("MM9SQL G4ABC -12", hints, StringComparison.Ordinal);
        Assert.Contains("MM9SQL G4ABC R+20", hints, StringComparison.Ordinal);
        Assert.DoesNotContain("MM9SQL G4ABC R+21", hints, StringComparison.Ordinal);
        Assert.DoesNotContain("MM9SQL G4ABC R+35", hints, StringComparison.Ordinal);
        Assert.DoesNotContain("G4ABC MM9SQL 73", hints, StringComparison.Ordinal);

        Assert.Equal("IO91", seq.TheirGrid);
        Assert.StartsWith("G4ABC MM9SQL", seq.CurrentTxMessage);
        Assert.NotNull(seq.ReportSent);
        // Stay on our CQ slots; the answerer already took the opposite parity.
        Assert.True(seq.PreferEvenSlot);

        Assert.False(seq.OnDecoded(Msg("MM9SQL G4ABC -10", snr: -10f)));
        Assert.Equal("G4ABC MM9SQL RR73", seq.CurrentTxMessage);

        // Our RR73 is sent. Their 73 has not arrived, so TX stays on.
        Assert.False(seq.OnTxCompleted());
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.True(seq.TransmitEnabled);
        Assert.Equal("-10", seq.ReportReceived);

        Assert.True(seq.OnDecoded(Msg("MM9SQL G4ABC 73")));
        Assert.Equal(Ft4QsoPhase.Finished, seq.Phase);
        Assert.False(seq.TransmitEnabled);
        Assert.Equal("CQ MM9SQL IO85", seq.CurrentTxMessage);
        Assert.True(seq.CanLog());
    }

    [Fact]
    public void Directed_cq_word_is_on_every_cq_but_not_on_the_contact()
    {
        var word = "POTA";
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true, cqModifier: () => word);
        seq.StartCq(evenSlot: true);
        Assert.Equal("CQ POTA MM9SQL IO85", seq.CurrentTxMessage);
        Assert.True(seq.IsCqMessage);

        Assert.False(seq.OnDecoded(Msg("MM9SQL G4ABC IO91")));
        Assert.StartsWith("G4ABC MM9SQL ", seq.CurrentTxMessage);
        Assert.DoesNotContain("POTA", seq.CurrentTxMessage, StringComparison.Ordinal);

        Assert.False(seq.OnDecoded(Msg("MM9SQL G4ABC -10", snr: -10f)));
        Assert.Equal("G4ABC MM9SQL RR73", seq.CurrentTxMessage);
        Assert.False(seq.OnTxCompleted());

        word = "DX";
        Assert.True(seq.OnDecoded(Msg("MM9SQL G4ABC 73")));
        Assert.Equal("CQ DX MM9SQL IO85", seq.CurrentTxMessage);
    }

    [Fact]
    public void Answering_a_directed_cq_calls_the_station_not_the_word()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartAnswer(Msg("CQ POTA W1AW FN20"), oppositeEvenSlot: false);
        Assert.Equal("W1AW", seq.TheirCall);
        Assert.Equal("FN20", seq.TheirGrid);
        Assert.Equal("W1AW MM9SQL IO85", seq.CurrentTxMessage);
    }

    [Fact]
    public void Repeated_report_after_rr73_resends_rr73()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartCq(evenSlot: true);
        seq.OnDecoded(Msg("MM9SQL G1YEF IO91"));
        seq.OnDecoded(Msg("MM9SQL G1YEF R-17"));
        Assert.Equal("G1YEF MM9SQL RR73", seq.CurrentTxMessage);
        Assert.False(seq.OnTxCompleted());
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.True(seq.TransmitEnabled);

        Assert.False(seq.OnDecoded(Msg("MM9SQL G1YEF R-17")));
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.True(seq.TransmitEnabled);
        Assert.Equal("G1YEF MM9SQL RR73", seq.CurrentTxMessage);
        Assert.Null(seq.QsoCompletedUtc);

        Assert.True(seq.OnDecoded(Msg("MM9SQL G1YEF 73")));
        Assert.Equal(Ft4QsoPhase.Finished, seq.Phase);
        Assert.False(seq.TransmitEnabled);

        // They missed the sign-off and send the report again: RR73 goes out once more.
        Assert.False(seq.OnDecoded(Msg("MM9SQL G1YEF R-17")));
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.True(seq.TransmitEnabled);
        Assert.Equal("G1YEF MM9SQL RR73", seq.CurrentTxMessage);
        Assert.False(seq.OnTxCompleted());
        Assert.True(seq.OnDecoded(Msg("MM9SQL G1YEF 73")));
        Assert.Equal(Ft4QsoPhase.Finished, seq.Phase);
        Assert.False(seq.TransmitEnabled);
        Assert.Equal("CQ MM9SQL IO85", seq.CurrentTxMessage);
    }

    [Fact]
    public void Closing_tx_does_not_guess_a_fresh_report()
    {
        var seq = new Ft4QsoSequencer(() => "GW4VXE", () => "IO71", () => true);
        seq.StartCq(evenSlot: true);
        seq.OnDecoded(Msg("GW4VXE R8CEL IO91"));
        Assert.False(seq.OnDecoded(Msg("GW4VXE R8CEL RRR")));
        Assert.Equal("R8CEL GW4VXE 73", seq.CurrentTxMessage);

        Assert.True(seq.TryGetApHints(out var hints, out _));
        Assert.Contains("GW4VXE R8CEL 73", hints, StringComparison.Ordinal);
        Assert.Contains("GW4VXE R8CEL RR73", hints, StringComparison.Ordinal);
        Assert.DoesNotContain("R+28", hints, StringComparison.Ordinal);
        Assert.DoesNotContain("+21", hints, StringComparison.Ordinal);
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
        Assert.Equal("CQ MM9SQL IO85", seq.CurrentTxMessage);
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
    public void Answer_cq_does_not_guess_73_before_a_report()
    {
        var seq = new Ft4QsoSequencer(() => "GW4VXE", () => "IO71", () => true);
        seq.StartAnswer(Msg("CQ G8KJJ IO92", freq: 1400f), oppositeEvenSlot: false);
        Assert.Equal("G8KJJ GW4VXE IO71", seq.CurrentTxMessage);
        Assert.Null(seq.ReportSent);
        Assert.Null(seq.ReportReceived);

        Assert.True(seq.TryGetApHints(out var hints, out _));
        Assert.Contains("GW4VXE G8KJJ IO92", hints, StringComparison.Ordinal);
        Assert.Contains("GW4VXE G8KJJ -14", hints, StringComparison.Ordinal);
        Assert.DoesNotContain("73", hints, StringComparison.Ordinal);

        // His CQ can score as a hinted 73. That must not become our sign-off.
        Assert.False(seq.OnDecoded(Msg("GW4VXE G8KJJ 73", snr: -14f, ap: true)));
        Assert.Equal("G8KJJ GW4VXE IO71", seq.CurrentTxMessage);
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.True(seq.TransmitEnabled);

        // A CRC-valid 73 is still his sign-off.
        Assert.False(seq.OnDecoded(Msg("GW4VXE G8KJJ 73", snr: -14f)));
        Assert.Equal("G8KJJ GW4VXE 73", seq.CurrentTxMessage);
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
    public void Cq_reply_keeps_tx_hz_when_hold_disabled()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true, () => false);
        seq.TxAudioHz = 1650;
        seq.StartCq(evenSlot: true);
        seq.OnDecoded(Msg("MM9SQL G4ABC IO91", freq: 1100f));
        Assert.Equal(1650f, seq.TxAudioHz);
    }

    [Fact]
    public void Their_grid_call_on_the_next_slot_starts_the_contact()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        // Their CQ was on an odd slot, so we call them on even.
        seq.StartAnswer(Msg("CQ G4ABC JO01"), oppositeEvenSlot: true);
        Assert.True(seq.PreferEvenSlot);
        Assert.Equal("G4ABC MM9SQL IO85", seq.CurrentTxMessage);
        Assert.Null(seq.ReportSent);

        // Next slot (odd): they call us, call call grid, instead of a report.
        Assert.False(seq.OnDecoded(Msg("MM9SQL G4ABC IO91", snr: -6f)));
        Assert.True(seq.PreferEvenSlot);
        Assert.Equal("G4ABC", seq.TheirCall);
        Assert.Equal("IO91", seq.TheirGrid);
        Assert.Equal("-06", seq.ReportSent);
        Assert.Equal("G4ABC MM9SQL -06", seq.CurrentTxMessage);
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);

        // Both then send plain reports, so the next step is RR73.
        Assert.False(seq.OnDecoded(Msg("MM9SQL G4ABC +10", snr: 8f)));
        Assert.Equal("+10", seq.ReportReceived);
        Assert.Equal("G4ABC MM9SQL RR73", seq.CurrentTxMessage);
    }

    [Fact]
    public void Hinted_grid_while_the_opening_grid_is_queued_does_not_send_a_report()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartAnswer(Msg("CQ G4ABC JO01"), oppositeEvenSlot: false);

        // Their CQ can score as a hinted call to us. Keep sending the grid.
        Assert.False(seq.OnDecoded(Msg("MM9SQL G4ABC JO01", ap: true)));
        Assert.Equal("G4ABC MM9SQL IO85", seq.CurrentTxMessage);
        Assert.Null(seq.ReportSent);
        Assert.Equal("JO01", seq.TheirGrid);
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
    public void Unfinished_call_does_not_lend_its_grid_to_the_next_station()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartAnswer(Msg("CQ M3JFM IO91"), oppositeEvenSlot: false);
        Assert.Equal("M3JFM", seq.TheirCall);
        Assert.Equal("IO91", seq.TheirGrid);

        // 2E0SQL's CQ was on screen while the first call was still open.
        Assert.False(seq.OnDecoded(Msg("CQ 2E0SQL JO01")));
        Assert.Equal("M3JFM", seq.TheirCall);

        // The click that starts the second contact is a report, which has no locator.
        seq.StartAnswer(Msg("MM9SQL 2E0SQL +06", snr: 4f), oppositeEvenSlot: true);
        Assert.Equal("2E0SQL", seq.TheirCall);
        Assert.Equal("JO01", seq.TheirGrid);
        Assert.Equal("2E0SQL MM9SQL R+04", seq.CurrentTxMessage);

        Assert.False(seq.OnDecoded(Msg("MM9SQL 2E0SQL R+06")));
        Assert.False(seq.OnDecoded(Msg("MM9SQL 2E0SQL RR73")));
        Assert.True(seq.OnTxCompleted());
        Assert.True(seq.CanLog());
        Assert.Equal("2E0SQL", seq.TheirCall);
        Assert.Equal("JO01", seq.TheirGrid);
    }

    [Fact]
    public void Next_station_without_a_heard_grid_is_logged_with_a_blank_locator()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartAnswer(Msg("CQ M3JFM IO91"), oppositeEvenSlot: false);

        seq.StartAnswer(Msg("MM9SQL 2E0SQL +06", snr: 4f), oppositeEvenSlot: true);
        Assert.Equal("2E0SQL", seq.TheirCall);
        Assert.Null(seq.TheirGrid);
    }

    [Fact]
    public void New_cq_uses_the_callers_own_grid_after_an_unfinished_contact()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartCq(evenSlot: true);
        seq.OnDecoded(Msg("MM9SQL M3JFM IO91"));
        Assert.Equal("IO91", seq.TheirGrid);

        Assert.False(seq.OnDecoded(Msg("CQ 2E0SQL JO01")));
        seq.StartCq(evenSlot: true);
        seq.OnDecoded(Msg("MM9SQL 2E0SQL +04", snr: 4f));

        Assert.Equal("2E0SQL", seq.TheirCall);
        Assert.Equal("JO01", seq.TheirGrid);
        Assert.Equal("2E0SQL MM9SQL R+04", seq.CurrentTxMessage);
    }

    [Fact]
    public void Answering_another_cq_replaces_the_previous_grid()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartAnswer(Msg("CQ M3JFM IO91"), oppositeEvenSlot: false);
        seq.StartAnswer(Msg("CQ 2E0SQL JO01"), oppositeEvenSlot: false);

        Assert.Equal("2E0SQL", seq.TheirCall);
        Assert.Equal("JO01", seq.TheirGrid);
        Assert.Equal("2E0SQL MM9SQL IO85", seq.CurrentTxMessage);
    }

    [Fact]
    public void Repeating_the_same_station_keeps_their_grid()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartAnswer(Msg("CQ M3JFM IO91"), oppositeEvenSlot: false);
        seq.StartAnswer(Msg("MM9SQL M3JFM +06", snr: 6f), oppositeEvenSlot: true);

        Assert.Equal("M3JFM", seq.TheirCall);
        Assert.Equal("IO91", seq.TheirGrid);
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
        Assert.Equal("CQ MM9SQL IO85", seq.CurrentTxMessage);
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
    public void Plain_report_answering_our_cq_gets_a_roger_report_before_rr73()
    {
        // They called us, we did not answer, they went back to CQ. Our later plain
        // report must be answered with R+NN, not RR73.
        var seq = new Ft4QsoSequencer(() => "GW4VXE", () => "IO81", () => true);
        seq.StartCq(evenSlot: true);

        Assert.False(seq.OnDecoded(Msg("GW4VXE MM9SQL +18", snr: 9f)));
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.Equal("MM9SQL GW4VXE R+09", seq.CurrentTxMessage);

        Assert.False(seq.OnDecoded(Msg("GW4VXE MM9SQL +18", snr: 12f)));
        Assert.Equal("MM9SQL GW4VXE R+12", seq.CurrentTxMessage);

        Assert.False(seq.OnDecoded(Msg("GW4VXE MM9SQL R+12")));
        Assert.Equal("MM9SQL GW4VXE RR73", seq.CurrentTxMessage);
    }

    [Fact]
    public void Answer_plain_report_sends_roger_report_back()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartAnswer(Msg("MM9SQL G4ABC -07", snr: -8f), oppositeEvenSlot: true);
        Assert.Equal("G4ABC MM9SQL R-08", seq.CurrentTxMessage);
        Assert.Equal("-07", seq.ReportReceived);
        Assert.Equal("-08", seq.ReportSent);
    }

    [Fact]
    public void Answer_roger_report_with_skip_rrr_sends_rr73()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartAnswer(Msg("MM9SQL G4ABC R-07"), oppositeEvenSlot: true);
        Assert.Equal("G4ABC MM9SQL RR73", seq.CurrentTxMessage);
    }

    [Fact]
    public void Answering_a_call_and_grid_without_cq_calls_the_station()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO87", () => true);
        seq.StartAnswer(Msg("SM/R1CR KP04"), oppositeEvenSlot: false);

        Assert.Equal("SM/R1CR", seq.TheirCall);
        Assert.Equal("KP04", seq.TheirGrid);
        Assert.Equal("<SM/R1CR> MM9SQL IO87", seq.CurrentTxMessage);
    }

    [Fact]
    public void Their_compound_call_is_sent_as_a_hash_when_ours_is_standard()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO87", () => true);
        seq.StartAnswer(Msg("CQ R0CM/4"), oppositeEvenSlot: false);
        Assert.Equal("<R0CM/4> MM9SQL IO87", seq.CurrentTxMessage);

        // Their report comes back with their call shown hashed.
        Assert.False(seq.OnDecoded(Msg("MM9SQL <R0CM/4> -14", snr: -12f)));
        Assert.Equal("<R0CM/4> MM9SQL R-12", seq.CurrentTxMessage);

        Assert.False(seq.OnDecoded(Msg("MM9SQL <R0CM/4> RR73")));
        Assert.Equal("<R0CM/4> MM9SQL 73", seq.CurrentTxMessage);
        Assert.True(seq.OnTxCompleted());
        Assert.Equal("R0CM/4", seq.TheirCall);
    }

    [Fact]
    public void Clicking_a_hashed_report_answers_that_call()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO87", () => true);
        seq.StartAnswer(Msg("MM9SQL <R0CM/4> -14", snr: -12f), oppositeEvenSlot: false);

        Assert.Equal("R0CM/4", seq.TheirCall);
        Assert.Equal("<R0CM/4> MM9SQL R-12", seq.CurrentTxMessage);
    }

    [Fact]
    public void Own_compound_call_goes_out_in_full_when_calling_cq()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL/M", () => "IO87", () => true, autoReply: () => true);
        seq.StartCq(evenSlot: true);
        Assert.Equal("CQ MM9SQL/M", seq.CurrentTxMessage);

        // A standard call answers with no grid, our call hashed and theirs in full.
        Assert.False(seq.OnDecoded(Msg("<MM9SQL/M> G4ABC", snr: -10f)));
        Assert.Equal("G4ABC", seq.TheirCall);
        Assert.Equal("G4ABC <MM9SQL/M> -10", seq.CurrentTxMessage);

        Assert.False(seq.OnDecoded(Msg("<MM9SQL/M> G4ABC R-08")));
        Assert.Equal("G4ABC MM9SQL/M RR73", seq.CurrentTxMessage);

        Assert.False(seq.OnDecoded(Msg("MM9SQL/M <G4ABC> 73")));
        Assert.True(seq.OnTxCompleted());
    }

    [Fact]
    public void Own_compound_call_answers_a_cq_without_a_grid()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL/M", () => "IO87", () => true);
        seq.StartAnswer(Msg("CQ G4ABC IO91"), oppositeEvenSlot: false);
        Assert.Equal("G4ABC MM9SQL/M", seq.CurrentTxMessage);

        Assert.False(seq.OnDecoded(Msg("<MM9SQL/M> G4ABC -14", snr: -12f)));
        Assert.Equal("G4ABC <MM9SQL/M> R-12", seq.CurrentTxMessage);

        Assert.False(seq.OnDecoded(Msg("<MM9SQL/M> G4ABC RR73")));
        Assert.Equal("G4ABC MM9SQL/M 73", seq.CurrentTxMessage);
        Assert.True(seq.OnTxCompleted());
    }

    [Fact]
    public void Clicking_a_no_grid_call_to_our_cq_sends_a_report()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL/M", () => "IO87", () => true);
        seq.StartAnswer(Msg("<MM9SQL/M> G4ABC", snr: -9f), oppositeEvenSlot: false);
        Assert.Equal("G4ABC <MM9SQL/M> -09", seq.CurrentTxMessage);
    }

    [Fact]
    public void Ap_hints_cover_full_call_replies_to_our_compound_call()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL/M", () => "IO87", () => true);
        seq.StartAnswer(Msg("<MM9SQL/M> G4ABC -14", snr: -12f), oppositeEvenSlot: false);

        Assert.True(seq.TryGetApHints(out var hints, out _));
        Assert.Contains("<MM9SQL/M> G4ABC -12", hints, StringComparison.Ordinal);
        Assert.Contains("MM9SQL/M <G4ABC> RR73", hints, StringComparison.Ordinal);
        Assert.Contains("MM9SQL/M <G4ABC> 73", hints, StringComparison.Ordinal);
    }

    [Fact]
    public void Blocked_callsign_is_not_auto_answered()
    {
        var blocked = new List<string> { "G4ABC" };
        var seq = new Ft4QsoSequencer(
            () => "MM9SQL",
            () => "IO85",
            () => true,
            isBlockedCall: call => Ft4BlockedCallsigns.Matches(blocked, call));
        seq.StartCq(evenSlot: true);

        Assert.False(seq.OnDecoded(Msg("MM9SQL G4ABC IO91", snr: -6f)));
        Assert.Equal(Ft4QsoPhase.CallingCq, seq.Phase);
        Assert.Null(seq.TheirCall);
        Assert.Equal("CQ MM9SQL IO85", seq.CurrentTxMessage);
        Assert.True(seq.TransmitEnabled);

        Assert.False(seq.OnDecoded(Msg("MM9SQL G4ABC/P +02", snr: 2f)));
        Assert.Equal(Ft4QsoPhase.CallingCq, seq.Phase);
        Assert.Null(seq.TheirCall);

        Assert.False(seq.OnDecoded(Msg("MM9SQL M0XYZ IO92", snr: -4f)));
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.Equal("M0XYZ", seq.TheirCall);
        Assert.StartsWith("M0XYZ MM9SQL", seq.CurrentTxMessage);
    }

    [Fact]
    public void Blocked_callsign_can_still_be_answered_by_clicking()
    {
        var seq = new Ft4QsoSequencer(
            () => "MM9SQL",
            () => "IO85",
            () => true,
            isBlockedCall: call => Ft4BlockedCallsigns.Matches(["G4ABC"], call));
        seq.StartCq(evenSlot: true);
        seq.OnDecoded(Msg("MM9SQL G4ABC IO91"));
        Assert.Equal(Ft4QsoPhase.CallingCq, seq.Phase);

        seq.StartAnswer(Msg("MM9SQL G4ABC IO91", snr: -4f), oppositeEvenSlot: false);
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.Equal("G4ABC", seq.TheirCall);
        Assert.StartsWith("G4ABC MM9SQL", seq.CurrentTxMessage);
    }

    [Fact]
    public void Auto_reply_off_keeps_calling_cq_until_a_station_is_clicked()
    {
        var seq = new Ft4QsoSequencer(
            () => "MM9SQL", () => "IO85", () => true, holdTxFrequency: () => true, autoReply: () => false);
        seq.StartCq(evenSlot: true);

        Assert.False(seq.OnDecoded(Msg("MM9SQL G4ABC IO91", snr: -6f)));
        Assert.Equal(Ft4QsoPhase.CallingCq, seq.Phase);
        Assert.Null(seq.TheirCall);
        Assert.Equal("CQ MM9SQL IO85", seq.CurrentTxMessage);
        Assert.True(seq.TransmitEnabled);

        seq.StartAnswer(Msg("MM9SQL M0XYZ IO92", snr: -4f), oppositeEvenSlot: false);
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.Equal("M0XYZ", seq.TheirCall);
        Assert.False(seq.OnDecoded(Msg("MM9SQL M0XYZ +02", snr: 2f)));
        Assert.Equal("M0XYZ MM9SQL RR73", seq.CurrentTxMessage);
    }

    [Fact]
    public void Auto_reply_turned_back_on_answers_the_next_caller()
    {
        var seq = new Ft4QsoSequencer(
            () => "MM9SQL", () => "IO85", () => true, holdTxFrequency: () => true, autoReply: () => false);
        seq.StartCq(evenSlot: true);

        Assert.False(seq.OnDecoded(Msg("MM9SQL G4ABC IO91", snr: -6f)));
        Assert.Equal(Ft4QsoPhase.CallingCq, seq.Phase);
        Assert.Equal("CQ MM9SQL IO85", seq.CurrentTxMessage);

        seq.SetAutoReply(true);
        Assert.True(seq.PrepareAutoReply());
        Assert.False(seq.OnDecoded(Msg("MM9SQL M0XYZ IO92", snr: -4f)));
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.Equal("M0XYZ", seq.TheirCall);
        Assert.StartsWith("M0XYZ MM9SQL", seq.CurrentTxMessage);
    }

    [Fact]
    public void EnableTx_of_a_cq_drops_the_previous_contact()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartCq(evenSlot: true);
        seq.OnDecoded(Msg("MM9SQL G4ABC IO91"));
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.Equal("G4ABC", seq.TheirCall);

        seq.HaltTx();
        seq.SetTxMessage("CQ MM9SQL IO85");
        seq.EnableTx();

        Assert.Equal(Ft4QsoPhase.CallingCq, seq.Phase);
        Assert.Null(seq.TheirCall);
        Assert.True(seq.TransmitEnabled);

        Assert.False(seq.OnDecoded(Msg("MM9SQL M0XYZ IO92", snr: 2f)));
        Assert.Equal("M0XYZ", seq.TheirCall);
        Assert.StartsWith("M0XYZ MM9SQL", seq.CurrentTxMessage);
    }

    [Fact]
    public void Auto_reply_toggled_during_a_report_keeps_the_contact()
    {
        var seq = new Ft4QsoSequencer(
            () => "MM9SQL", () => "IO85", () => true, autoReply: () => true);
        seq.StartCq(evenSlot: true);
        seq.OnDecoded(Msg("MM9SQL G4ABC IO91"));
        var message = seq.CurrentTxMessage;

        seq.SetAutoReply(false);
        seq.SetAutoReply(true);
        Assert.False(seq.PrepareAutoReply());
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.Equal("G4ABC", seq.TheirCall);
        Assert.Equal(message, seq.CurrentTxMessage);
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
        // Heard before we have transmitted RR73: keep TX until our 73 is sent.
        Assert.False(seq.OnDecoded(Msg("MM9SQL G4ABC 73")));
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.True(seq.TransmitEnabled);

        Assert.True(seq.OnTxCompleted());
        Assert.Equal(Ft4QsoPhase.Finished, seq.Phase);
        Assert.False(seq.TransmitEnabled);
        Assert.Equal("CQ MM9SQL IO85", seq.CurrentTxMessage);
    }

    [Fact]
    public void Cq_after_73_keeps_cq_and_marks_the_old_contact_historic()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartCq(evenSlot: true);
        seq.OnDecoded(Msg("MM9SQL G1YEF IO91", snr: 15f));
        Assert.Equal("G1YEF MM9SQL +15", seq.CurrentTxMessage);
        seq.OnDecoded(Msg("MM9SQL G1YEF +05"));
        Assert.False(seq.OnTxCompleted());
        Assert.Null(seq.QsoCompletedUtc);
        Assert.True(seq.OnDecoded(Msg("MM9SQL G1YEF 73")));
        Assert.NotNull(seq.QsoCompletedUtc);

        var completed = seq.QsoCompletedUtc!.Value;
        seq.StartCq(evenSlot: true);
        Assert.Equal("CQ MM9SQL IO85", seq.CurrentTxMessage);
        Assert.Equal(Ft4QsoPhase.CallingCq, seq.Phase);
        Assert.Equal(completed, seq.QsoCompletedUtc);
        Assert.True(seq.IsHistoricDecode(completed.AddSeconds(-20)));
        Assert.False(seq.IsHistoricDecode(completed.AddSeconds(8)));
    }

    [Fact]
    public void Closing_message_while_calling_cq_does_not_start_a_qso()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        seq.StartCq(evenSlot: true);
        Assert.False(seq.OnDecoded(Msg("MM9SQL G1YEF 73")));
        Assert.Equal(Ft4QsoPhase.CallingCq, seq.Phase);
        Assert.Equal("CQ MM9SQL IO85", seq.CurrentTxMessage);
        Assert.Null(seq.TheirCall);
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
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.True(seq.TransmitEnabled);

        Assert.False(seq.OnDecoded(Msg("MM9SQL G4ABC 73")));
        Assert.Equal(Ft4QsoPhase.Finished, seq.Phase);
        Assert.False(seq.TransmitEnabled);
        Assert.Equal("CQ MM9SQL IO85", seq.CurrentTxMessage);

        var fresh = new Ft4QsoSequencer(() => "MM9SQL", () => "IO85", () => true);
        fresh.StartAnswer(Msg("CQ G4ABC JO01"), oppositeEvenSlot: false);
        Assert.Null(fresh.ReportSent);
        Assert.True(fresh.ForceReport(4f));
        Assert.Equal("G4ABC MM9SQL +04", fresh.CurrentTxMessage);
    }

    private static readonly DateTime ApSlot = new(2026, 10, 7, 21, 49, 52, 500, DateTimeKind.Utc);

    private static Ft4QsoSequencer AnsweringKk7mnc()
    {
        var seq = new Ft4QsoSequencer(() => "MM9SQL", () => "IO87", () => true);
        seq.StartAnswer(Msg("CQ KK7MNC CN87", freq: 1990f), oppositeEvenSlot: true);
        Assert.Equal("KK7MNC MM9SQL IO87", seq.CurrentTxMessage);
        return seq;
    }

    private static Ft4DecodedMessage ApAt(DateTime slot, string text) =>
        new(slot, text, 1969f, 0.5f, -16f, null, null, null, false, IsApriori: true);

    [Fact]
    public void Guessed_hint_does_not_move_the_contact()
    {
        var seq = new Ft4QsoSequencer(() => "GM4VXE", () => "IO81", () => false);
        seq.StartAnswer(Msg("CQ JW7XK JQ78", freq: 1400f), oppositeEvenSlot: false);
        Assert.True(seq.ForceReport(-13f));
        Assert.Equal("JW7XK GM4VXE -13", seq.CurrentTxMessage);

        // His -05 to someone else, read as a reply to us because the CRC was not checked.
        var guess = new Ft4DecodedMessage(
            ApSlot,
            "GM4VXE JW7XK -05",
            1400f,
            0.5f,
            -16f,
            null,
            null,
            null,
            false,
            IsApriori: true,
            IsRejected: true);
        Assert.False(seq.OnDecoded(guess));
        Assert.Equal("JW7XK GM4VXE -13", seq.CurrentTxMessage);
        Assert.Null(seq.ReportReceived);

        // The same text with the CRC checked still advances the contact.
        Assert.False(seq.OnDecoded(ApAt(ApSlot.AddSeconds(15), "GM4VXE JW7XK -05")));
        Assert.Equal("JW7XK GM4VXE RRR", seq.CurrentTxMessage);
        Assert.Equal("-05", seq.ReportReceived);
    }

    [Fact]
    public void Contradicted_hint_puts_the_contact_back()
    {
        var seq = AnsweringKk7mnc();
        Assert.False(seq.OnDecoded(ApAt(ApSlot, "MM9SQL KK7MNC R+07")));
        Assert.Equal("KK7MNC MM9SQL RR73", seq.CurrentTxMessage);
        Assert.Equal("+07", seq.ReportReceived);

        Assert.True(seq.RevertApriori(ApSlot, "kk7mnc"));
        Assert.Equal("KK7MNC MM9SQL IO87", seq.CurrentTxMessage);
        Assert.Null(seq.ReportReceived);
        Assert.Null(seq.ReportSent);
        Assert.Equal(Ft4QsoPhase.InQso, seq.Phase);
        Assert.True(seq.TransmitEnabled);

        // Undone once only.
        Assert.False(seq.RevertApriori(ApSlot, "KK7MNC"));
    }

    [Fact]
    public void Hint_is_not_undone_for_another_slot_or_station()
    {
        var seq = AnsweringKk7mnc();
        seq.OnDecoded(ApAt(ApSlot, "MM9SQL KK7MNC R+07"));

        Assert.False(seq.RevertApriori(ApSlot.AddSeconds(15), "KK7MNC"));
        Assert.False(seq.RevertApriori(ApSlot, "KJ4SKO"));
        Assert.Equal("KK7MNC MM9SQL RR73", seq.CurrentTxMessage);
    }

    [Fact]
    public void Hint_is_not_undone_once_the_contact_has_moved_on()
    {
        var seq = AnsweringKk7mnc();
        seq.OnDecoded(ApAt(ApSlot, "MM9SQL KK7MNC R+07"));
        Assert.True(seq.Force73());

        Assert.False(seq.RevertApriori(ApSlot, "KK7MNC"));
        Assert.Equal("KK7MNC MM9SQL 73", seq.CurrentTxMessage);
    }
}
