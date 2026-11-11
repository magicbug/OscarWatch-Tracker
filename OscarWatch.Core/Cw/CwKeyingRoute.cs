using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;

namespace OscarWatch.Core.Cw;

public enum CwKeyingMethod
{
    /// <summary>Kenwood <c>KY</c>: the radio forms the Morse from text.</summary>
    KenwoodText,

    /// <summary>Icom CI-V <c>0x17</c>: the radio forms the Morse from text.</summary>
    IcomText,

    /// <summary>DTR or RTS on the CAT serial port that OscarWatch already has open.</summary>
    CatPortLine,

    /// <summary>DTR or RTS on a COM port the keyer opens for itself.</summary>
    SeparatePortLine
}

/// <summary>Which keying method the uplink radio and the CW keyer settings select.</summary>
public static class CwKeyingRoute
{
    public static RigType UplinkRigType(RigSettings rig)
    {
        if (!rig.Enabled)
            return RigType.None;
        return rig.DualRadioEnabled ? rig.Uplink.Type : rig.Type;
    }

    public static bool IsKenwoodTextRadio(RigType type) => type is
        RigType.KenwoodTs2000 or RigType.KenwoodTs590s or RigType.KenwoodTs590sg
        or RigType.KenwoodTs890s or RigType.KenwoodTs990s;

    public static bool IsIcomTextRadio(RigType type) => IcomCwCodec.SupportsTextKeying(type);

    /// <summary>
    /// Yaesu FT-991, FT-991A and FTX-1 hold RTS on the CAT port for flow control, so only DTR
    /// on that port can be a key line.
    /// </summary>
    public static bool CatPortHoldsRts(RigType type) => type is
        RigType.YaesuFt991 or RigType.YaesuFt991a or RigType.YaesuFtx1;

    public static CwKeyingMethod Resolve(CwKeyerSettings cw, RigSettings rig)
    {
        var type = UplinkRigType(rig);
        if (IsKenwoodTextRadio(type))
            return CwKeyingMethod.KenwoodText;
        if (IsIcomTextRadio(type) && !cw.UseComLineOnTextRadios)
            return CwKeyingMethod.IcomText;
        return cw.UseSeparatePort ? CwKeyingMethod.SeparatePortLine : CwKeyingMethod.CatPortLine;
    }

    public static bool IsTextMethod(CwKeyingMethod method) =>
        method is CwKeyingMethod.KenwoodText or CwKeyingMethod.IcomText;

    /// <summary>True when the CAT-port line choice would toggle RTS that the radio uses for flow control.</summary>
    public static bool CatPortRtsBlocked(CwKeyerSettings cw, RigSettings rig) =>
        Resolve(cw, rig) == CwKeyingMethod.CatPortLine
        && cw.KeyLine == PttLine.Rts
        && CatPortHoldsRts(UplinkRigType(rig));
}
