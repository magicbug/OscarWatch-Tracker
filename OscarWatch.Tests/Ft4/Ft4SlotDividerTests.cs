using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4SlotDividerTests
{
    private static readonly DateTime EvenSlot = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OddSlot = EvenSlot.AddSeconds(7.5);

    [Fact]
    public void First_row_starts_a_group()
    {
        Assert.True(Ft4SlotDivider.StartsGroup(EvenSlot, previousSlotUtc: null));
    }

    [Fact]
    public void Same_slot_does_not_start_a_group()
    {
        Assert.False(Ft4SlotDivider.StartsGroup(EvenSlot, EvenSlot));
    }

    [Fact]
    public void Different_slot_starts_a_group()
    {
        Assert.True(Ft4SlotDivider.StartsGroup(OddSlot, EvenSlot));
    }

    [Fact]
    public void Tx_and_echo_with_the_same_slot_stay_in_the_group()
    {
        Assert.False(Ft4SlotDivider.StartsGroup(EvenSlot, EvenSlot));
        Assert.False(Ft4SlotDivider.StartsGroup(EvenSlot, EvenSlot));
    }

    [Fact]
    public void Calling_me_uses_its_own_neighbours_when_other_stations_are_skipped()
    {
        // Band activity: even CQ, even call to me, odd CQ, odd call to me.
        // Calling me keeps only the calls addressed to this station.
        var band = new[] { EvenSlot, EvenSlot, OddSlot, OddSlot };
        var callingMe = new[] { EvenSlot, OddSlot };

        Assert.True(Ft4SlotDivider.StartsGroup(band[0], null));
        Assert.False(Ft4SlotDivider.StartsGroup(band[1], band[0]));
        Assert.True(Ft4SlotDivider.StartsGroup(band[2], band[1]));
        Assert.False(Ft4SlotDivider.StartsGroup(band[3], band[2]));

        Assert.True(Ft4SlotDivider.StartsGroup(callingMe[0], null));
        Assert.True(Ft4SlotDivider.StartsGroup(callingMe[1], callingMe[0]));
    }
}
