namespace OscarWatch.Core.Ft4;

/// <summary>UTC slot clock for FT4 (7.5 s) and FT8 (15 s).</summary>
public static class Ft4SlotClock
{
    public const double Ft4SlotSeconds = 7.5;
    /// <summary>FT4 on-air burst length (105 symbols × 0.048 s), used for TX pre-comp slope.</summary>
    public const double Ft4SymbolBurstSeconds = 5.04;

    /// <summary>
    /// Seconds into an RX slot before attempting decode. Burst is ~5.04 s plus lead-in;
    /// waiting for the full 7.5 s slot only adds silence and delays the UI.
    /// </summary>
    public const double Ft4EarlyDecodeSeconds = 6.0;

    /// <summary>
    /// How long before a transmit slot to start the CAT Doppler write.
    /// That write shares the radio port with CAT PTT. On a slow CI-V radio
    /// (IC-9700 with a long CAT delay, Main and Sub) it can take a couple of
    /// seconds, which used to hold PTT until part-way through the burst.
    /// The lead starts after the receive burst (0.5 s silence plus the 5.04 s
    /// waveform) and still leaves a short gap before the next slot.
    /// </summary>
    public static readonly TimeSpan PreTransmitCatLead =
        TimeSpan.FromSeconds(Ft4SlotSeconds - (0.5 + Ft4SymbolBurstSeconds) - 0.16);

    public const double Ft8SlotSeconds = 15.0;

    public static DateTime SlotStartUtc(DateTime utc, double slotSeconds)
    {
        var epoch = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        var totalSeconds = epoch.TimeOfDay.TotalSeconds;
        var slotIndex = Math.Floor(totalSeconds / slotSeconds);
        var start = TimeSpan.FromSeconds(slotIndex * slotSeconds);
        return DateTime.SpecifyKind(epoch.Date + start, DateTimeKind.Utc);
    }

    public static bool IsEvenSlot(DateTime slotStartUtc, double slotSeconds)
    {
        var index = (int)Math.Floor(slotStartUtc.TimeOfDay.TotalSeconds / slotSeconds + 0.001);
        return (index & 1) == 0;
    }

    public static double SecondsIntoSlot(DateTime utc, double slotSeconds)
    {
        var start = SlotStartUtc(utc, slotSeconds);
        return (utc.ToUniversalTime() - start).TotalSeconds;
    }

    public static DateTime NextTransmitSlotStart(DateTime utc, double slotSeconds, bool preferEven)
    {
        var now = utc.ToUniversalTime();
        var start = SlotStartUtc(now, slotSeconds);
        // If we are within the first 0.5 s of this slot, we can still use it; otherwise next matching.
        var into = (now - start).TotalSeconds;
        var candidate = into < 0.5 ? start : start.AddSeconds(slotSeconds);
        while (IsEvenSlot(candidate, slotSeconds) != preferEven)
            candidate = candidate.AddSeconds(slotSeconds);
        return candidate;
    }

    /// <summary>
    /// True when this period will be transmitted, or transmission for it has already started.
    /// A matching period more than 0.5 s old is left as receive unless it was already kicked.
    /// </summary>
    public static bool IsLiveTransmitSlot(
        DateTime utc,
        double slotSeconds,
        bool transmitEnabled,
        bool preferEven,
        DateTime? transmittingSlotStartUtc)
    {
        if (!transmitEnabled)
            return false;

        var now = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        var start = SlotStartUtc(now, slotSeconds);
        if (IsEvenSlot(start, slotSeconds) != preferEven)
            return false;

        if (transmittingSlotStartUtc is { } kicked && kicked.ToUniversalTime() == start)
            return true;

        return NextTransmitSlotStart(now, slotSeconds, preferEven) == start;
    }
}
