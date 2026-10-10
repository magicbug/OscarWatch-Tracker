using OscarWatch.Core.Models;

namespace OscarWatch.Core.Radio;

/// <summary>CI-V model bytes returned by command 0x19 0x00. These match the factory CI-V address.</summary>
public static class IcomCivModelIds
{
    public static bool TryGet(RigType rigType, out byte modelId)
    {
        modelId = rigType switch
        {
            RigType.IcomIc706 => 0x48,
            RigType.IcomIc706Mkii => 0x4E,
            RigType.IcomIc706MkiiG => 0x58,
            RigType.IcomIc821h => 0x4C,
            RigType.IcomIc910 => 0x60,
            RigType.IcomIc9100 => 0x7C,
            RigType.IcomIc7100 => 0x88,
            RigType.IcomIc7300 => 0x94,
            RigType.IcomIc9700 => 0xA2,
            RigType.IcomIc705 => 0xA4,
            RigType.IcomIc905 => 0xAC,
            _ => 0
        };
        return modelId != 0;
    }
}
