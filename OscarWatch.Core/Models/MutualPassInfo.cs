namespace OscarWatch.Core.Models;

/// <summary>
/// Time window when a satellite is above minimum elevation at two ground sites simultaneously.
/// </summary>
public sealed class MutualPassInfo
{
    public required string SatelliteName { get; set; }
    public required string NoradId { get; init; }
    public DateTime MutualStartUtc { get; init; }
    public DateTime MutualEndUtc { get; init; }
    public required PassInfo LocalPass { get; init; }
    public required PassInfo RemotePass { get; init; }

    public TimeSpan Duration => MutualEndUtc - MutualStartUtc;
}
