namespace OscarWatch.Core.Models;

public sealed class SatelliteCatalogEntry
{
    public required string Name { get; init; }
    public required string NoradId { get; init; }
    public required string Line1 { get; init; }
    public required string Line2 { get; init; }
    public DateTime? EpochUtc { get; init; }
}
