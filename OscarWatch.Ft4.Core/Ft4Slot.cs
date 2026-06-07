using OscarWatch.Ft4.Core.Native;

namespace OscarWatch.Ft4.Core;

public sealed class Ft4Slot
{
    public DateTime Utc { get; set; } = DateTime.UtcNow;

    public long SlotNumber => (long)(Utc.TimeOfDay.TotalSeconds / Ft4Constants.TimeSlotSeconds);

    public bool IsOdd => SlotNumber % 2 == 1;

    public double SecondsIntoSlot =>
        Utc.TimeOfDay.TotalSeconds - SlotNumber * Ft4Constants.TimeSlotSeconds;

    public int SamplesIntoSlot =>
        (int)(SecondsIntoSlot * Ft4Constants.SamplingRateHz);

    public DateTime CurrentSlotStart =>
        Utc.Date.AddSeconds(SlotNumber * Ft4Constants.TimeSlotSeconds);

    public double SlotProgress =>
        Math.Clamp(SecondsIntoSlot / Ft4Constants.TimeSlotSeconds, 0, 1);

    public double TxWindowProgress =>
        Math.Clamp(SecondsIntoSlot / Ft4Constants.EncodeSeconds, 0, 1);

    public DateTime GetTxStartTime(bool txOdd)
    {
        var slot = SlotNumber;
        var wantOdd = txOdd;
        if (IsOdd != wantOdd)
            slot++;

        if (Utc >= Utc.Date.AddSeconds(slot * Ft4Constants.TimeSlotSeconds))
            slot++;

        return Utc.Date.AddSeconds(slot * Ft4Constants.TimeSlotSeconds);
    }
}
