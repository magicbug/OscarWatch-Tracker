using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4SlotClockTests
{
    [Fact]
    public void SlotStartUtc_aligns_to_7_5_seconds()
    {
        var utc = new DateTime(2026, 9, 21, 12, 0, 8, 200, DateTimeKind.Utc);
        var start = Ft4SlotClock.SlotStartUtc(utc, Ft4SlotClock.Ft4SlotSeconds);
        Assert.Equal(new DateTime(2026, 9, 21, 12, 0, 7, 500, DateTimeKind.Utc), start);
    }

    [Fact]
    public void IsEvenSlot_alternates()
    {
        var even = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        var odd = new DateTime(2026, 9, 21, 12, 0, 7, 500, DateTimeKind.Utc);
        Assert.True(Ft4SlotClock.IsEvenSlot(even, Ft4SlotClock.Ft4SlotSeconds));
        Assert.False(Ft4SlotClock.IsEvenSlot(odd, Ft4SlotClock.Ft4SlotSeconds));
    }

    [Fact]
    public void IsLiveTransmitSlot_skips_a_matching_period_that_has_already_started()
    {
        var slot = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        var early = slot.AddSeconds(0.2);
        var stillInWindow = slot.AddSeconds(1.5);
        var late = slot.AddSeconds(2.5);

        Assert.True(Ft4SlotClock.IsLiveTransmitSlot(early, Ft4SlotClock.Ft4SlotSeconds, true, preferEven: true, null));
        Assert.True(Ft4SlotClock.IsLiveTransmitSlot(stillInWindow, Ft4SlotClock.Ft4SlotSeconds, true, preferEven: true, null));
        Assert.False(Ft4SlotClock.IsLiveTransmitSlot(late, Ft4SlotClock.Ft4SlotSeconds, true, preferEven: true, null));
        Assert.True(Ft4SlotClock.IsLiveTransmitSlot(late, Ft4SlotClock.Ft4SlotSeconds, true, preferEven: true, slot));
        Assert.False(Ft4SlotClock.IsLiveTransmitSlot(late, Ft4SlotClock.Ft4SlotSeconds, false, preferEven: true, slot));
        Assert.False(Ft4SlotClock.IsLiveTransmitSlot(late, Ft4SlotClock.Ft4SlotSeconds, true, preferEven: false, slot));
    }

    [Fact]
    public void NextTransmitSlotStart_uses_the_current_period_for_the_first_two_seconds()
    {
        var slot = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        var nextSameParity = slot.AddSeconds(15);

        Assert.Equal(slot, Ft4SlotClock.NextTransmitSlotStart(slot.AddSeconds(1.5), Ft4SlotClock.Ft4SlotSeconds, preferEven: true));
        Assert.Equal(slot, Ft4SlotClock.NextTransmitSlotStart(slot.AddSeconds(2.0), Ft4SlotClock.Ft4SlotSeconds, preferEven: true));
        Assert.Equal(nextSameParity, Ft4SlotClock.NextTransmitSlotStart(slot.AddSeconds(2.5), Ft4SlotClock.Ft4SlotSeconds, preferEven: true));
    }

    [Fact]
    public void NextTransmitSlotStart_from_a_receive_period_is_the_following_period()
    {
        var theirSlot = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        var ourSlot = theirSlot.AddSeconds(7.5);
        var clicked = theirSlot.AddSeconds(6);

        Assert.Equal(ourSlot, Ft4SlotClock.NextTransmitSlotStart(clicked, Ft4SlotClock.Ft4SlotSeconds, preferEven: false));
    }

    [Fact]
    public void PlaybackStartSample_skips_audio_already_past()
    {
        var slot = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(0, Ft4SlotClock.PlaybackStartSample(slot, slot.AddMilliseconds(-20), 48000));
        Assert.Equal(0, Ft4SlotClock.PlaybackStartSample(slot, slot, 48000));
        Assert.Equal(72000, Ft4SlotClock.PlaybackStartSample(slot, slot.AddSeconds(1.5), 48000));
    }

    [Fact]
    public void Early_decode_window_is_after_burst_and_before_slot_end()
    {
        Assert.True(Ft4SlotClock.Ft4EarlyDecodeSeconds > Ft4SlotClock.Ft4SymbolBurstSeconds);
        Assert.True(Ft4SlotClock.Ft4EarlyDecodeSeconds < Ft4SlotClock.Ft4SlotSeconds);
    }

    [Fact]
    public void PreTransmitCatLead_starts_after_the_receive_burst()
    {
        var burstEndSeconds = 0.5 + Ft4SlotClock.Ft4SymbolBurstSeconds;
        var leadSeconds = Ft4SlotClock.PreTransmitCatLead.TotalSeconds;
        Assert.InRange(leadSeconds, 1.0, 2.2);
        Assert.True(Ft4SlotClock.Ft4SlotSeconds - leadSeconds > burstEndSeconds + 0.1);
    }
}
