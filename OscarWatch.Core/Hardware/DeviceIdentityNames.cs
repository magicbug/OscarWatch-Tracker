using OscarWatch.Core.Models;

namespace OscarWatch.Core.Hardware;

/// <summary>Short names used when a COM port does not answer as the selected device.</summary>
public static class DeviceIdentityNames
{
    public static string Radio(RigType type) => type switch
    {
        RigType.IcomIc706 => "IC-706",
        RigType.IcomIc706Mkii => "IC-706MkII",
        RigType.IcomIc706MkiiG => "IC-706MkIIG",
        RigType.IcomIc7100 => "IC-7100",
        RigType.IcomIc7300 => "IC-7300",
        RigType.IcomIc705 => "IC-705",
        RigType.IcomIc821h => "IC-821H",
        RigType.IcomIc905 => "IC-905",
        RigType.IcomIc910 => "IC-910",
        RigType.IcomIc9100 => "IC-9100",
        RigType.IcomIc9700 => "IC-9700",
        RigType.YaesuFt817 => "FT-817",
        RigType.YaesuFt818 => "FT-818",
        RigType.YaesuFt857 => "FT-857",
        RigType.YaesuFt847 => "FT-847",
        RigType.YaesuFt991 => "FT-991",
        RigType.YaesuFt991a => "FT-991A",
        RigType.YaesuFtx1 => "FTX-1",
        RigType.KenwoodTs2000 => "TS-2000",
        RigType.KenwoodThD74 => "TH-D74",
        RigType.KenwoodThD75 => "TH-D75",
        _ => type.ToString()
    };

    public static string Rotator(RotatorType type) => type switch
    {
        RotatorType.YaesuGs232 => "GS-232 rotator",
        RotatorType.EasyComm => "EasyComm rotator",
        RotatorType.Spid => "SPID rotator",
        RotatorType.SpidMd01 => "SPID MD-01",
        RotatorType.Saebrt => "SAEBRTrack rotator",
        RotatorType.UrcTcp => "URC rotator",
        RotatorType.GreenHeronRt21 => "RT-21 rotator",
        _ => type.ToString()
    };
}
