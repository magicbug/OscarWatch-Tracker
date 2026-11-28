namespace OscarWatch.Core.Logbook;

/// <summary>
/// Maps uplink and downlink frequencies to an ADIF <c>SAT_MODE</c> string
/// (for example <c>V/U</c>, <c>U/V</c>, or <c>V</c> for same-band simplex).
/// Letter bands follow Cloudlog's satellite mode designators.
/// </summary>
public static class AdifSatModeHelper
{
    /// <summary>
    /// Builds <c>SAT_MODE</c> as uplink/downlink letters. Returns empty when either
    /// frequency cannot be mapped. Same-band contacts use a single letter.
    /// </summary>
    public static string FromUplinkDownlinkHz(long uplinkHz, long downlinkHz)
    {
        var uplink = LetterFromHz(uplinkHz);
        var downlink = LetterFromHz(downlinkHz);
        if (uplink.Length == 0 || downlink.Length == 0)
            return "";

        return uplink == downlink ? uplink : uplink + "/" + downlink;
    }

    /// <summary>
    /// AMSAT / Cloudlog band letter for <paramref name="hz"/>, or empty when outside
    /// the usual satellite bands.
    /// </summary>
    public static string LetterFromHz(long hz)
    {
        if (hz <= 0)
            return "";

        // Match Cloudlog Radio::get_mode_designator, with V and U widened to the
        // ADIF 2m and 70cm band edges so Doppler-shifted edges still map.
        return hz switch
        {
            > 21_000_000 and < 22_000_000 => "H",
            > 28_000_000 and < 30_000_000 => "A",
            > 144_000_000 and < 148_000_000 => "V",
            > 420_000_000 and < 450_000_000 => "U",
            > 1_240_000_000 and < 1_300_000_000 => "L",
            > 2_320_000_000 and < 2_450_000_000 => "S",
            > 3_400_000_000 and < 3_475_000_000 => "S2",
            > 5_650_000_000 and < 5_850_000_000 => "C",
            > 10_000_000_000 and < 10_500_000_000 => "X",
            > 24_000_000_000 and < 24_250_000_000 => "K",
            > 47_000_000_000 and < 47_200_000_000 => "R",
            _ => ""
        };
    }
}
