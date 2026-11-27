using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4TxWatchdogTests
{
    private static readonly DateTime Start = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Halts_after_the_configured_minutes_with_no_reply()
    {
        Assert.False(Ft4TxWatchdog.ShouldHalt(true, false, 3, Start.AddMinutes(3), Start));
        Assert.True(Ft4TxWatchdog.ShouldHalt(true, false, 3, Start.AddMinutes(3).AddSeconds(1), Start));
    }

    [Fact]
    public void Does_not_halt_while_idle_tuning_or_disabled()
    {
        var late = Start.AddMinutes(10);
        Assert.False(Ft4TxWatchdog.ShouldHalt(false, false, 3, late, Start));
        Assert.False(Ft4TxWatchdog.ShouldHalt(true, true, 3, late, Start));
        Assert.False(Ft4TxWatchdog.ShouldHalt(true, false, 0, late, Start));
    }

    [Fact]
    public void Minutes_clamp_to_zero_through_thirty()
    {
        Assert.Equal(0, Ft4TxWatchdog.ClampMinutes(-1));
        Assert.Equal(3, Ft4TxWatchdog.ClampMinutes(3));
        Assert.Equal(30, Ft4TxWatchdog.ClampMinutes(90));
    }

    [Fact]
    public void Listening_does_not_trip_the_watchdog_when_transmit_is_armed()
    {
        var clock = Ft4TxWatchdogClock.Idle(Start);
        var listened = clock.Poll(false, false, 3, Start.AddMinutes(10));
        Assert.False(listened.Halt);

        var armed = listened.Clock.Poll(true, false, 3, Start.AddMinutes(10));
        Assert.False(armed.Halt);
        Assert.True(armed.Clock.WasArmed);

        var stillInside = armed.Clock.Poll(true, false, 3, Start.AddMinutes(13));
        Assert.False(stillInside.Halt);
        Assert.True(armed.Clock.Poll(true, false, 3, Start.AddMinutes(13).AddSeconds(1)).Halt);
    }

    [Fact]
    public void Re_arming_after_a_halt_starts_a_fresh_timer()
    {
        var armed = Ft4TxWatchdogClock.Idle(Start).Poll(true, false, 3, Start).Clock;
        var expired = armed.Poll(true, false, 3, Start.AddMinutes(3).AddSeconds(1));
        Assert.True(expired.Halt);

        var halted = expired.Clock.Disarm(Start.AddMinutes(3).AddSeconds(1));
        var again = halted.Poll(true, false, 3, Start.AddMinutes(3).AddSeconds(2));
        Assert.False(again.Halt);

        var reply = again.Clock.NoteReply(Start.AddMinutes(4));
        Assert.False(reply.Poll(true, false, 3, Start.AddMinutes(7)).Halt);
        Assert.True(reply.Poll(true, false, 3, Start.AddMinutes(7).AddSeconds(1)).Halt);
    }

    [Theory]
    [InlineData("MM9SQL", "G4ABC", "MM9SQL", true)]
    [InlineData("CQ", "G4ABC", "MM9SQL", false)]
    [InlineData("G4ABC", "MM9SQL", "MM9SQL", false)]
    [InlineData("MM9SQL", "G4ABC", "", false)]
    public void Reply_is_another_station_calling_us(string callTo, string callDe, string myCall, bool reply)
    {
        Assert.Equal(reply, Ft4TxWatchdog.IsReply(callTo, callDe, myCall));
    }
}
