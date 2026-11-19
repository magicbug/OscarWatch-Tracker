using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

/// <summary>One FT4 or FT8 decode to post to OscarWatch.org satellite spots.</summary>
public sealed record OscarWatchSatelliteSpot(
    string Satellite,
    string HeardCallsign,
    string? HeardGrid,
    int SnrDb,
    long? UplinkHz,
    long? DownlinkHz,
    DateTime HeardAtUtc,
    string? Message,
    string? Client,
    string Mode = "FT4");

public sealed record SatelliteSpotResult(bool Accepted, string Message, int HttpStatusCode);

public interface ISatelliteSpotService
{
    Task<SatelliteSpotResult> SubmitAsync(
        SatelliteStatusSettings settings,
        OscarWatchSatelliteSpot spot,
        CancellationToken cancellationToken = default);
}
