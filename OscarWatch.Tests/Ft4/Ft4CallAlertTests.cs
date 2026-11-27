using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4CallAlertTests
{
    private static readonly DateTime Slot = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    private readonly Ft4CallAlert _alert = new();

    [Fact]
    public void A_grid_call_to_us_plays_once_until_the_quiet_period_ends()
    {
        Assert.True(_alert.ShouldPlay(Rx("MM9SQL", "G4ABC", "IO91", Slot), "MM9SQL", null));
        Assert.False(_alert.ShouldPlay(Rx("MM9SQL", "G4ABC", "IO91", Slot.AddSeconds(7.5)), "MM9SQL", null));
        Assert.True(_alert.ShouldPlay(
            Rx("MM9SQL", "G4ABC", "IO91", Slot.Add(Ft4CallAlert.RepeatQuietFor)),
            "MM9SQL",
            null));
    }

    [Fact]
    public void The_station_already_being_worked_does_not_play_again()
    {
        Assert.True(_alert.ShouldPlay(Rx("MM9SQL", "G4ABC", "IO91", Slot), "MM9SQL", null));
        Assert.False(_alert.ShouldPlay(Rx("MM9SQL", "G4ABC", "+05", Slot.AddSeconds(15)), "MM9SQL", "G4ABC"));
        Assert.False(_alert.ShouldPlay(
            Rx("MM9SQL", "G4ABC", "R-12", Slot.Add(Ft4CallAlert.RepeatQuietFor).AddSeconds(15)),
            "MM9SQL",
            "G4ABC"));
    }

    [Fact]
    public void A_new_call_after_that_contact_plays_straight_away()
    {
        Assert.True(_alert.ShouldPlay(Rx("MM9SQL", "G4ABC", "IO91", Slot), "MM9SQL", null));
        _alert.NotePartnerChanged("G4ABC", null);

        Assert.True(_alert.ShouldPlay(Rx("MM9SQL", "G4ABC", "IO91", Slot.AddSeconds(15)), "MM9SQL", null));
    }

    [Fact]
    public void An_earlier_line_in_the_slot_does_not_silence_the_caller()
    {
        // The sequencer has already chosen G4ABC, but the partner passed in stays the
        // one from before this slot until every line has been considered.
        Assert.False(_alert.ShouldPlay(Rx("M0XYZ", "G1AAA", "IO91", Slot), "MM9SQL", null));
        Assert.True(_alert.ShouldPlay(Rx("MM9SQL", "G4ABC", "JN48", Slot), "MM9SQL", null));
    }

    [Fact]
    public void A_second_station_plays_as_well()
    {
        Assert.True(_alert.ShouldPlay(Rx("MM9SQL", "G4ABC", "IO91", Slot), "MM9SQL", null));
        Assert.True(_alert.ShouldPlay(Rx("MM9SQL", "M0ABC", "JO01", Slot), "MM9SQL", "G4ABC"));
    }

    [Fact]
    public void A_missed_grid_still_plays_when_the_first_copy_is_a_report()
    {
        Assert.True(_alert.ShouldPlay(Rx("MM9SQL", "G4ABC", "+05", Slot), "MM9SQL", null));
    }

    [Theory]
    [InlineData("CQ", "G4ABC", "IO91")]
    [InlineData("M0ABC", "G4ABC", "IO91")]
    [InlineData("MM9SQL", "G4ABC", "RR73")]
    [InlineData("MM9SQL", "G4ABC", "73")]
    [InlineData("MM9SQL", "G4ABC", "RRR")]
    [InlineData("MM9SQL", "MM9SQL", "IO91")]
    public void These_lines_are_not_the_start_of_a_contact(string to, string de, string extra)
    {
        Assert.False(_alert.ShouldPlay(Rx(to, de, extra, Slot), "MM9SQL", null));
    }

    [Fact]
    public void A_rejected_hint_and_our_own_transmission_stay_quiet()
    {
        Assert.False(_alert.ShouldPlay(Rx("MM9SQL", "G4ABC", "IO91", Slot, rejected: true), "MM9SQL", null));
        Assert.False(_alert.ShouldPlay(
            new Ft4DecodedMessage(Slot, "CQ MM9SQL IO85", 1500, 0, 0, "CQ", "MM9SQL", "IO85", false, IsTransmitted: true),
            "MM9SQL",
            null));
    }

    [Fact]
    public void No_callsign_means_no_sound()
    {
        Assert.False(_alert.ShouldPlay(Rx("MM9SQL", "G4ABC", "IO91", Slot), "", null));
        Assert.False(_alert.ShouldPlay(Rx("MM9SQL", "G4ABC", "IO91", Slot), null, null));
    }

    [Fact]
    public void An_unresolved_call_plays_once()
    {
        Assert.True(_alert.ShouldPlay(Rx("MM9SQL", "", "IO91", Slot), "MM9SQL", null));
        Assert.False(_alert.ShouldPlay(Rx("MM9SQL", "", "+02", Slot.AddSeconds(7.5)), "MM9SQL", null));
    }

    private static Ft4DecodedMessage Rx(string to, string de, string extra, DateTime slot, bool rejected = false) =>
        new(slot, $"{to} {de} {extra}".Trim(), 1500, 0.4f, -8, to, de, extra, false, IsRejected: rejected);
}
