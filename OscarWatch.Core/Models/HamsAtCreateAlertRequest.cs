namespace OscarWatch.Core.Models;

using OscarWatch.Core.Services;

public sealed class HamsAtCreateAlertRequest
{
    public required int SatelliteNumber { get; init; }
    public required double ObserverLat { get; init; }
    public required double ObserverLon { get; init; }
    public required DateTime MaxAtUtc { get; init; }
    public required string Callsign { get; init; }
    public required IReadOnlyList<string> Grids { get; init; }
    public string? Mode { get; init; }
    public string? Comment { get; init; }
    public double? Mhz { get; init; }
    public string? MhzDirection { get; init; }
    public bool? ChatEnabled { get; init; }
}

public sealed class HamsAtCreateAlertResult
{
    public required bool Ok { get; init; }
    public string? AlertUrl { get; init; }
    public string? ErrorMessage { get; init; }
    public int HttpStatusCode { get; init; }
    public HamsAtFetchErrorKind ErrorKind { get; init; }
    public string? RequestJson { get; init; }

    public static HamsAtCreateAlertResult Success(string alertUrl, int httpStatusCode) =>
        new()
        {
            Ok = true,
            AlertUrl = alertUrl,
            HttpStatusCode = httpStatusCode,
            ErrorKind = HamsAtFetchErrorKind.None
        };

    public static HamsAtCreateAlertResult Failure(
        string message,
        int httpStatusCode,
        HamsAtFetchErrorKind kind = HamsAtFetchErrorKind.Generic,
        string? requestJson = null) =>
        new()
        {
            Ok = false,
            ErrorMessage = message,
            HttpStatusCode = httpStatusCode,
            ErrorKind = kind,
            RequestJson = requestJson
        };
}
