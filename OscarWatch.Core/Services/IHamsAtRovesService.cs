using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

public sealed class HamsAtFetchResult
{
    public required bool Ok { get; init; }
    public IReadOnlyList<HamsAtUpcomingAlert> Alerts { get; init; } = [];
    public string? ErrorMessage { get; init; }
    public HamsAtFetchErrorKind ErrorKind { get; init; }

    public static HamsAtFetchResult Success(IReadOnlyList<HamsAtUpcomingAlert> alerts) =>
        new() { Ok = true, Alerts = alerts, ErrorKind = HamsAtFetchErrorKind.None };

    public static HamsAtFetchResult Failed(HamsAtFetchErrorKind kind) =>
        new()
        {
            Ok = false,
            ErrorKind = kind,
            ErrorMessage = HamsAtErrorHelper.ToEnglish(kind)
        };
}

public interface IHamsAtRovesService
{
    Task<HamsAtFetchResult> FetchUpcomingAsync(
        HamsAtSettings settings,
        bool bypassCache = false,
        CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> TestConnectionAsync(
        HamsAtSettings settings,
        CancellationToken cancellationToken = default);

    Task<HamsAtCreateAlertResult> CreateAlertAsync(
        HamsAtSettings settings,
        HamsAtCreateAlertRequest request,
        CancellationToken cancellationToken = default);

    void InvalidateCache();
}
