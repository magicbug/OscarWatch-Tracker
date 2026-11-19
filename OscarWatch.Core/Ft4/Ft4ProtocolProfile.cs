using OscarWatch.Core.Models;

namespace OscarWatch.Core.Ft4;

/// <summary>
/// Slot timing and native protocol for one digital mode the FT4 window can run.
/// The selected transponder decides which profile is used.
/// </summary>
public sealed record Ft4ProtocolProfile(
    string Mode,
    bool IsFt4,
    double SlotSeconds,
    double BurstSeconds,
    double EarlyDecodeSeconds)
{
    /// <summary>FT4: 7.5 s slots, 105 symbols at 0.048 s.</summary>
    public static readonly Ft4ProtocolProfile Ft4 = new(
        Ft4SlotGate.TransponderType,
        IsFt4: true,
        SlotSeconds: Ft4SlotClock.Ft4SlotSeconds,
        BurstSeconds: Ft4SlotClock.Ft4SymbolBurstSeconds,
        EarlyDecodeSeconds: Ft4SlotClock.Ft4EarlyDecodeSeconds);

    /// <summary>FT8: 15 s slots, 79 symbols at 0.160 s.</summary>
    public static readonly Ft4ProtocolProfile Ft8 = new(
        Ft4SlotGate.Ft8TransponderType,
        IsFt4: false,
        SlotSeconds: Ft4SlotClock.Ft8SlotSeconds,
        BurstSeconds: Ft4SlotClock.Ft8SymbolBurstSeconds,
        EarlyDecodeSeconds: Ft4SlotClock.Ft8EarlyDecodeSeconds);

    /// <summary>Profile for the selected transponder. Anything that is not FT8 uses FT4.</summary>
    public static Ft4ProtocolProfile For(SatelliteTransponderMode? mode) =>
        Ft4SlotGate.IsFt8Transponder(mode) ? Ft8 : Ft4;

    /// <summary>
    /// Lead before a transmit slot to start the CAT Doppler write. Same rule as
    /// <see cref="Ft4SlotClock.PreTransmitCatLead"/>, with this profile's slot and burst.
    /// </summary>
    public TimeSpan PreTransmitCatLead =>
        TimeSpan.FromSeconds(SlotSeconds - (0.5 + BurstSeconds) - 0.16);

    /// <summary>Largest encoded burst plus silence, in samples, for one slot at this rate.</summary>
    public int EncodeCapacity(int sampleRate) =>
        Math.Max(sampleRate * (int)Math.Ceiling(SlotSeconds) + 256, 16);

    /// <summary>Samples in half a slot. Used as the minimum before a slot is kept for re-decode.</summary>
    public int HalfSlotSamples(int sampleRate) =>
        (int)(sampleRate * SlotSeconds / 2);

    /// <summary>Samples in one full slot at this rate.</summary>
    public int SlotSamples(int sampleRate) =>
        (int)(sampleRate * SlotSeconds);
}
