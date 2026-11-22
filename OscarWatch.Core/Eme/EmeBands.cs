namespace OscarWatch.Core.Eme;

/// <summary>An amateur band used for EME, with the frequency a new setup starts from.</summary>
/// <param name="Id">Stable key used in settings.</param>
/// <param name="NominalMHz">The frequency used until the operator enters their own.</param>
/// <param name="DefaultCircular">True where dish stations normally use circular polarisation.</param>
public sealed record EmeBand(string Id, double NominalMHz, bool DefaultCircular)
{
    /// <summary>The label for the band, such as "144 MHz" or "10 GHz".</summary>
    public string Label => NominalMHz >= 10_000 ? $"{NominalMHz / 1000.0:0.###} GHz" : $"{NominalMHz:0.###} MHz";
}

/// <summary>The EME bands from 50 MHz to 76 GHz.</summary>
public static class EmeBands
{
    /// <summary>
    /// The bands. Linear polarisation is the default below 1 GHz and circular from 1296 MHz up, where dishes are
    /// the usual antenna. Each start frequency is the band number, and the operator enters the frequency they
    /// actually use.
    /// </summary>
    public static IReadOnlyList<EmeBand> All { get; } =
    [
        new("50", 50, false),
        new("144", 144, false),
        new("222", 222, false),
        new("432", 432, false),
        new("902", 902, false),
        new("1296", 1296, true),
        new("2320", 2320, true),
        new("3400", 3400, true),
        new("5760", 5760, true),
        new("10368", 10368, true),
        new("24000", 24000, true),
        new("47000", 47000, true),
        new("76000", 76000, true)
    ];

    public static EmeBand? Find(string? id) =>
        All.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.Ordinal));

    public static EmeBand Default => All[1];
}
