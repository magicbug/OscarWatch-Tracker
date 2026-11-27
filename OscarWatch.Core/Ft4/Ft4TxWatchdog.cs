namespace OscarWatch.Core.Ft4;

/// <summary>
/// Stops FT4 transmit when nobody has replied to this station for a set time
/// while transmit is armed. Time with transmit off does not count.
/// Other stations on the passband, and this station's own echo, do not count.
/// </summary>
public static class Ft4TxWatchdog
{
    public const int DefaultMinutes = 3;
    public const int MaxMinutes = 30;

    /// <summary>0 disables the watchdog.</summary>
    public static int ClampMinutes(int minutes) => Math.Clamp(minutes, 0, MaxMinutes);

    public static bool ShouldHalt(
        bool transmitEnabled,
        bool tuning,
        int minutes,
        DateTime utcNow,
        DateTime lastReplyUtc)
    {
        if (tuning || !transmitEnabled || minutes <= 0)
            return false;

        return utcNow - lastReplyUtc > TimeSpan.FromMinutes(minutes);
    }

    /// <summary>True when another station addressed this callsign.</summary>
    public static bool IsReply(string? callTo, string? callDe, string? myCall)
    {
        if (string.IsNullOrWhiteSpace(myCall) || string.IsNullOrWhiteSpace(callDe))
            return false;
        if (callDe.Equals(myCall, StringComparison.OrdinalIgnoreCase))
            return false;
        return Ft4MessageCodec.IsAddressedTo(callTo, myCall);
    }
}

/// <summary>
/// Watchdog clock. Listening, Tune, and a halt do not age it.
/// The sample that first sees transmit armed starts the minutes and does not halt,
/// so turning transmit back on cannot trip on time spent off the air.
/// </summary>
public readonly record struct Ft4TxWatchdogClock(bool WasArmed, DateTime LastReplyUtc)
{
    public static Ft4TxWatchdogClock Idle(DateTime utcNow) => new(false, utcNow);

    public Ft4TxWatchdogClock NoteReply(DateTime utcNow) => this with { LastReplyUtc = utcNow };

    public Ft4TxWatchdogClock Disarm(DateTime utcNow) => new(false, utcNow);

    public (Ft4TxWatchdogClock Clock, bool Halt) Poll(
        bool transmitArmed,
        bool tuning,
        int minutes,
        DateTime utcNow)
    {
        if (tuning || !transmitArmed || minutes <= 0)
            return (new Ft4TxWatchdogClock(false, utcNow), false);

        if (!WasArmed)
            return (new Ft4TxWatchdogClock(true, utcNow), false);

        if (!Ft4TxWatchdog.ShouldHalt(true, false, minutes, utcNow, LastReplyUtc))
            return (this, false);

        // Stay armed in the clock until the caller halts. Clearing it here would make
        // the next sample look like a fresh arm and the halt would never stick.
        return (this, true);
    }
}
