using OscarWatch.Core.Models;

namespace OscarWatch.Core.Radio;

/// <summary>
/// Maps ICOM CI-V RF power level (0–255) to approximate watts using each radio’s
/// band maximum. The CI-V value is relative, not a direct watt reading.
/// </summary>
public static class IcomRfPowerEstimator
{
    /// <summary>
    /// Estimate set RF power in watts from a CI-V level and uplink frequency.
    /// Returns false when the band maximum is unknown for this radio/frequency.
    /// </summary>
    public static bool TryEstimateWatts(
        RigType rigType,
        long frequencyHz,
        int level0To255,
        out double watts,
        Ic910PowerClass ic910PowerClass = Ic910PowerClass.H,
        Ic9700PowerClass ic9700PowerClass = Ic9700PowerClass.Export)
    {
        watts = 0;
        if (level0To255 is < 0 or > 255)
            return false;

        var max = MaxPowerWatts(rigType, frequencyHz, ic910PowerClass, ic9700PowerClass);
        if (max is null or <= 0)
            return false;

        watts = level0To255 / 255.0 * max.Value;
        return true;
    }

    /// <summary>
    /// CI-V level (0–255) for a target wattage on this radio and frequency.
    /// Uses the floor so the set point does not land above the requested watts.
    /// </summary>
    public static bool TryLevelForWatts(
        RigType rigType,
        long frequencyHz,
        double watts,
        out int level0To255,
        Ic910PowerClass ic910PowerClass = Ic910PowerClass.H,
        Ic9700PowerClass ic9700PowerClass = Ic9700PowerClass.Export)
    {
        level0To255 = 0;
        if (watts < 0 || double.IsNaN(watts))
            return false;

        var max = MaxPowerWatts(rigType, frequencyHz, ic910PowerClass, ic9700PowerClass);
        if (max is null or <= 0)
            return false;

        if (watts >= max.Value)
        {
            level0To255 = 255;
            return true;
        }

        level0To255 = (int)Math.Floor(watts / max.Value * 255.0);
        return true;
    }

    /// <summary>Catalogue maximum RF power (W) for the given radio and frequency band.</summary>
    public static int? MaxPowerWatts(
        RigType rigType,
        long frequencyHz,
        Ic910PowerClass ic910PowerClass = Ic910PowerClass.H,
        Ic9700PowerClass ic9700PowerClass = Ic9700PowerClass.Export)
    {
        var band = ClassifyBand(frequencyHz);
        if (band == RfBand.Unknown)
            return null;

        return rigType switch
        {
            RigType.IcomIc9700 => Ic9700MaxPowerWatts(ic9700PowerClass, band),
            RigType.IcomIc9100 => band switch
            {
                RfBand.Hf or RfBand.SixMetres => 100,
                RfBand.Vhf2m => 100,
                RfBand.Uhf70cm => 75,
                RfBand.Shf23cm => 10,
                _ => null
            },
            RigType.IcomIc910 => Ic910MaxPowerWatts(ic910PowerClass, band),
            RigType.IcomIc705 => 10,
            RigType.IcomIc905 => band switch
            {
                RfBand.Vhf2m or RfBand.Uhf70cm or RfBand.Shf23cm
                    or RfBand.Shf13cm or RfBand.Shf6cm => 10,
                RfBand.Shf3cm => 1,
                _ => null
            },
            RigType.IcomIc7300 => band is RfBand.Hf or RfBand.SixMetres ? 100 : null,
            RigType.IcomIc7100 => band switch
            {
                RfBand.Hf or RfBand.SixMetres => 100,
                RfBand.Vhf2m => 50,
                RfBand.Uhf70cm => 35,
                _ => null
            },
            RigType.IcomIc706 or RigType.IcomIc706Mkii => band switch
            {
                RfBand.Hf or RfBand.SixMetres => 100,
                RfBand.Vhf2m => 20,
                _ => null
            },
            RigType.IcomIc706MkiiG => band switch
            {
                RfBand.Hf or RfBand.SixMetres => 100,
                RfBand.Vhf2m => 50,
                RfBand.Uhf70cm => 20,
                _ => null
            },
            // Generic ICOM CI-V path: conservative satellite-band maxima.
            RigType.IcomIc821h => band switch
            {
                RfBand.Vhf2m => 45,
                RfBand.Uhf70cm => 40,
                _ => null
            },
            _ => null
        };
    }

    /// <summary>
    /// IC-9700 maxima. 1200 MHz is 10 W on the export set, the Japanese IC-9700, and the IC-9700S.
    /// </summary>
    private static int? Ic9700MaxPowerWatts(Ic9700PowerClass powerClass, RfBand band) =>
        powerClass switch
        {
            Ic9700PowerClass.Japan => band switch
            {
                RfBand.Vhf2m or RfBand.Uhf70cm => 50,
                RfBand.Shf23cm => 10,
                _ => null
            },
            Ic9700PowerClass.JapanS => band switch
            {
                RfBand.Vhf2m or RfBand.Uhf70cm => 20,
                RfBand.Shf23cm => 10,
                _ => null
            },
            _ => band switch
            {
                RfBand.Vhf2m => 100,
                RfBand.Uhf70cm => 75,
                RfBand.Shf23cm => 10,
                _ => null
            }
        };

    /// <summary>
    /// IC-910 family maxima. 23 cm is the UX-910 option (10 W) on every variant.
    /// </summary>
    private static int? Ic910MaxPowerWatts(Ic910PowerClass powerClass, RfBand band) =>
        powerClass switch
        {
            Ic910PowerClass.D => band switch
            {
                RfBand.Vhf2m or RfBand.Uhf70cm => 50,
                RfBand.Shf23cm => 10,
                _ => null
            },
            Ic910PowerClass.Base => band switch
            {
                RfBand.Vhf2m or RfBand.Uhf70cm => 20,
                RfBand.Shf23cm => 10,
                _ => null
            },
            _ => band switch
            {
                RfBand.Vhf2m => 100,
                RfBand.Uhf70cm => 75,
                RfBand.Shf23cm => 10,
                _ => null
            }
        };

    private static RfBand ClassifyBand(long frequencyHz) => frequencyHz switch
    {
        >= 1_800_000 and < 30_000_000 => RfBand.Hf,
        >= 50_000_000 and < 54_000_000 => RfBand.SixMetres,
        >= 144_000_000 and < 148_000_000 => RfBand.Vhf2m,
        >= 430_000_000 and < 450_000_000 => RfBand.Uhf70cm,
        >= 1_200_000_000 and < 1_300_000_000 => RfBand.Shf23cm,
        >= 2_300_000_000 and < 2_450_000_000 => RfBand.Shf13cm,
        >= 5_650_000_000 and < 5_850_000_000 => RfBand.Shf6cm,
        >= 10_000_000_000 and < 10_500_000_000 => RfBand.Shf3cm,
        _ => RfBand.Unknown
    };

    private enum RfBand
    {
        Unknown,
        Hf,
        SixMetres,
        Vhf2m,
        Uhf70cm,
        Shf23cm,
        Shf13cm,
        Shf6cm,
        Shf3cm
    }
}
