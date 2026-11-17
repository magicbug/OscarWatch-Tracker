namespace OscarWatch.Core.Radio;

public enum RigSatBandRegion
{
    Unknown,
    Hf,
    Vhf,
    UhfAndAbove
}

public static class RigSatModeHelper
{
    /// <summary>HF through 6m (IC-9100); below VHF satellite bands.</summary>
    private const double HfUpperKHz = 50_000;

    /// <summary>VHF vs UHF split used for pass-to-pass Main/Sub band swaps.</summary>
    private const double UhfLowerKHz = 400_000;

    /// <summary>70 cm vs 23 cm (and higher) split for Main/Sub band swaps on 23 cm rigs.</summary>
    private const double LBandLowerKHz = 900_000;

    private enum MainSubBandSlot
    {
        Unknown,
        Hf,
        Vhf,
        Uhf,
        LBandAndAbove
    }

    /// <summary>
    /// True when Main/Sub satellite layout applies (real cross-band pass with both frequencies).
    /// Beacon/downlink-only modes (uplink 0) must not use this — |downlink − 0| would always exceed 10 MHz.
    /// </summary>
    public static bool UseMainSubLayout(double downlinkKHz, double uplinkKHz) =>
        downlinkKHz > 0 && uplinkKHz > 0 && Math.Abs(downlinkKHz - uplinkKHz) > 10_000;

    /// <summary>
    /// Nominal uplink and downlink centre frequencies are equal (e.g. ISS Packet 145.825 MHz).
    /// The radio still needs split — Doppler separates TX and RX during the pass.
    /// </summary>
    public static bool IsSameBandSimplex(double downlinkKHz, double uplinkKHz) =>
        downlinkKHz > 0 && uplinkKHz > 0 && Math.Abs(downlinkKHz - uplinkKHz) < 0.001;

    /// <summary>True when downlink centre is below the UHF satellite band (HF or VHF).</summary>
    public static bool IsVhfCenterKHz(double kHz) => kHz is > 0 and < UhfLowerKHz;

    public static bool IsUhfCenterKHz(double kHz) => kHz >= UhfLowerKHz;

    public static bool IsHfCenterKHz(double kHz) => kHz is > 0 and < HfUpperKHz;

    public static RigSatBandRegion GetSatBandRegion(double kHz) =>
        kHz switch
        {
            <= 0 => RigSatBandRegion.Unknown,
            < HfUpperKHz => RigSatBandRegion.Hf,
            < UhfLowerKHz => RigSatBandRegion.Vhf,
            _ => RigSatBandRegion.UhfAndAbove
        };

    /// <summary>
    /// True when <paramref name="firstKHz"/> and <paramref name="secondKHz"/> belong to different
    /// Main/Sub hardware bands (HF, 2 m, 70 cm, or 23 cm and above).
    /// </summary>
    public static bool AreDifferentMainSubBands(double firstKHz, double secondKHz)
    {
        var first = GetMainSubBandSlot(firstKHz);
        var second = GetMainSubBandSlot(secondKHz);
        return first != MainSubBandSlot.Unknown
            && second != MainSubBandSlot.Unknown
            && first != second;
    }

    /// <summary>
    /// True when Main is on the wrong band for <paramref name="downlinkKHz"/>.
    /// Matches IC-821H/910/9100/9700 Main/Sub band units (HF, VHF, UHF, 23 cm).
    /// </summary>
    public static bool NeedsMainSubBandSwap(long mainFrequencyHz, double downlinkKHz) =>
        mainFrequencyHz > 0 && downlinkKHz > 0 && AreDifferentMainSubBands(mainFrequencyHz / 1000.0, downlinkKHz);

    private static MainSubBandSlot GetMainSubBandSlot(double kHz) =>
        kHz switch
        {
            <= 0 => MainSubBandSlot.Unknown,
            < HfUpperKHz => MainSubBandSlot.Hf,
            < UhfLowerKHz => MainSubBandSlot.Vhf,
            < LBandLowerKHz => MainSubBandSlot.Uhf,
            _ => MainSubBandSlot.LBandAndAbove
        };
}
