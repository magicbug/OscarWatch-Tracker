namespace OscarWatch.Core.Models;

/// <summary>Operating modes accepted by hams.at <c>POST /api/alerts</c>.</summary>
public static class HamsAtApiModes
{
    public const string Ssb = "SSB";
    public const string Cw = "CW";
    public const string Data = "Data";
    public const string Fm = "FM";

    public static readonly IReadOnlyList<string> All = [Ssb, Cw, Data, Fm];

    public static readonly IReadOnlyList<string> Linear = [Ssb, Cw, Data];

    public static readonly IReadOnlyList<string> FmOnly = [Fm];

    public static readonly IReadOnlyList<string> DataOnly = [Data];

    public static readonly IReadOnlyList<string> CwOnly = [Cw];
}
