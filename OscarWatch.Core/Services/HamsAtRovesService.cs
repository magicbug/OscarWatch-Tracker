using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OscarWatch.Core.Net;
using System.Text.Json.Serialization;
using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

public sealed class HamsAtRovesService : IHamsAtRovesService
{
    public const string UpcomingAlertsUrl = "https://hams.at/api/alerts/upcoming";
    public const string CreateAlertUrl = "https://hams.at/api/alerts";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;
    private readonly object _cacheLock = new();
    private string? _cachedApiKey;
    private IReadOnlyList<HamsAtUpcomingAlert>? _cachedAlerts;
    private DateTime _cachedAtUtc;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public HamsAtRovesService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateDefaultClient();
    }

    public async Task<HamsAtFetchResult> FetchUpcomingAsync(
        HamsAtSettings settings,
        bool bypassCache = false,
        CancellationToken cancellationToken = default)
    {
        var apiKey = settings.ApiKey?.Trim() ?? "";
        if (string.IsNullOrEmpty(apiKey))
            return HamsAtFetchResult.Failed(HamsAtFetchErrorKind.MissingApiKey);

        if (!bypassCache && TryGetCached(apiKey, out var cached))
            return HamsAtFetchResult.Success(cached);

        try
        {
            var alerts = await FetchFromApiAsync(apiKey, cancellationToken).ConfigureAwait(false);
            StoreCache(apiKey, alerts);
            return HamsAtFetchResult.Success(alerts);
        }
        catch (HttpRequestException ex)
        {
            return HamsAtFetchResult.Failed(HamsAtErrorHelper.FromHttpException(ex));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HamsAtFetchResult.Failed(HamsAtFetchErrorKind.Timeout);
        }
        catch (JsonException)
        {
            return HamsAtFetchResult.Failed(HamsAtFetchErrorKind.UnexpectedResponse);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HamsAtFetchResult.Failed(HamsAtFetchErrorKind.Generic);
        }
    }

    public async Task<(bool Ok, string Message)> TestConnectionAsync(
        HamsAtSettings settings,
        CancellationToken cancellationToken = default)
    {
        var result = await FetchUpcomingAsync(settings, bypassCache: true, cancellationToken)
            .ConfigureAwait(false);
        if (result.Ok)
        {
            // Optimized: Replace LINQ Count() with direct loop to avoid delegate allocation
            var workableCount = 0;
            for (int i = 0; i < result.Alerts.Count; i++)
            {
                if (result.Alerts[i].IsWorkable)
                    workableCount++;
            }
            
            return (true, $"{workableCount} workable alert(s) returned.");
        }

        return (false, result.ErrorMessage ?? HamsAtErrorHelper.ToEnglish(HamsAtFetchErrorKind.Generic));
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedAlerts = null;
            _cachedApiKey = null;
        }
    }

    public async Task<HamsAtCreateAlertResult> CreateAlertAsync(
        HamsAtSettings settings,
        HamsAtCreateAlertRequest request,
        CancellationToken cancellationToken = default)
    {
        var apiKey = settings.ApiKey?.Trim() ?? "";
        if (string.IsNullOrEmpty(apiKey))
        {
            return HamsAtCreateAlertResult.Failure(
                HamsAtErrorHelper.ToEnglish(HamsAtFetchErrorKind.MissingApiKey),
                0,
                HamsAtFetchErrorKind.MissingApiKey);
        }

        try
        {
            var requestJson = SerializeCreateAlertPayload(request);
            using var httpRequest = BuildCreateAlertRequest(apiKey, requestJson);
            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                return HamsAtCreateAlertResult.Failure(
                    ResolveFailureMessage(body, HamsAtFetchErrorKind.InvalidApiKey),
                    (int)response.StatusCode,
                    HamsAtFetchErrorKind.InvalidApiKey,
                    requestJson);
            }

            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                return HamsAtCreateAlertResult.Failure(
                    ResolveFailureMessage(body, HamsAtFetchErrorKind.Generic),
                    (int)response.StatusCode,
                    HamsAtFetchErrorKind.Generic,
                    requestJson);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return HamsAtCreateAlertResult.Failure(
                    ResolveFailureMessage(body, HamsAtFetchErrorKind.RateLimited),
                    (int)response.StatusCode,
                    HamsAtFetchErrorKind.RateLimited,
                    requestJson);
            }

            if ((int)response.StatusCode >= 500)
            {
                return HamsAtCreateAlertResult.Failure(
                    ResolveFailureMessage(body, HamsAtFetchErrorKind.Unavailable),
                    (int)response.StatusCode,
                    HamsAtFetchErrorKind.Unavailable,
                    requestJson);
            }

            if (!response.IsSuccessStatusCode)
            {
                return HamsAtCreateAlertResult.Failure(
                    ResolveFailureMessage(body, HamsAtFetchErrorKind.UnexpectedResponse),
                    (int)response.StatusCode,
                    HamsAtFetchErrorKind.UnexpectedResponse,
                    requestJson);
            }

            var alertUrl = ParseAlertUrl(body);
            if (string.IsNullOrWhiteSpace(alertUrl))
            {
                return HamsAtCreateAlertResult.Failure(
                    HamsAtErrorHelper.ToEnglish(HamsAtFetchErrorKind.UnexpectedResponse),
                    (int)response.StatusCode,
                    HamsAtFetchErrorKind.UnexpectedResponse,
                    requestJson);
            }

            InvalidateCache();
            return HamsAtCreateAlertResult.Success(alertUrl, (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            var kind = HamsAtErrorHelper.FromHttpException(ex);
            return HamsAtCreateAlertResult.Failure(
                HamsAtErrorHelper.ToEnglish(kind),
                0,
                kind,
                SerializeCreateAlertPayload(request));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HamsAtCreateAlertResult.Failure(
                HamsAtErrorHelper.ToEnglish(HamsAtFetchErrorKind.Timeout),
                0,
                HamsAtFetchErrorKind.Timeout,
                SerializeCreateAlertPayload(request));
        }
        catch (JsonException)
        {
            return HamsAtCreateAlertResult.Failure(
                HamsAtErrorHelper.ToEnglish(HamsAtFetchErrorKind.UnexpectedResponse),
                0,
                HamsAtFetchErrorKind.UnexpectedResponse,
                SerializeCreateAlertPayload(request));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return HamsAtCreateAlertResult.Failure(
                HamsAtErrorHelper.ToEnglish(HamsAtFetchErrorKind.Generic),
                0,
                HamsAtFetchErrorKind.Generic,
                SerializeCreateAlertPayload(request));
        }
    }

    public static string SerializeCreateAlertPayload(HamsAtCreateAlertRequest request)
    {
        var payload = BuildCreateAlertPayload(request);
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static Dictionary<string, object?> BuildCreateAlertPayload(HamsAtCreateAlertRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["satellite_number"] = request.SatelliteNumber,
            ["observer_lat"] = request.ObserverLat,
            ["observer_lon"] = request.ObserverLon,
            ["max_at"] = request.MaxAtUtc.ToUniversalTime().ToString("O"),
            ["callsign"] = request.Callsign,
            ["grids"] = request.Grids // Already IReadOnlyList, no need to convert to array
        };

        if (!string.IsNullOrWhiteSpace(request.Mode))
            payload["mode"] = request.Mode.Trim();

        if (!string.IsNullOrWhiteSpace(request.Comment))
            payload["comment"] = request.Comment.Trim();

        if (request.Mhz is > 0)
            payload["mhz"] = request.Mhz.Value;

        if (!string.IsNullOrWhiteSpace(request.MhzDirection))
            payload["mhz_direction"] = request.MhzDirection.Trim();

        if (request.ChatEnabled is not null)
            payload["chat_enabled"] = request.ChatEnabled.Value;

        return payload;
    }

    private static HttpRequestMessage BuildCreateAlertRequest(string apiKey, string requestJson)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, CreateAlertUrl)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return httpRequest;
    }

    internal static IReadOnlyList<string> ParseErrorMessages(string body)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<HamsAtErrorResponseDto>(body, JsonOptions);
            if (payload?.Errors is not { Count: > 0 } errors)
                return [];

            // Optimized: Replace LINQ chain with direct loop to avoid allocations
            var validErrors = new List<string>(errors.Count);
            for (int i = 0; i < errors.Count; i++)
            {
                var error = errors[i];
                if (!string.IsNullOrWhiteSpace(error))
                    validErrors.Add(error.Trim());
            }

            return validErrors;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static string FormatErrorMessages(IReadOnlyList<string> errors) =>
        errors.Count == 0 ? "" : string.Join("; ", errors);

    private static string ResolveFailureMessage(string body, HamsAtFetchErrorKind fallbackKind)
    {
        var errors = ParseErrorMessages(body);
        if (errors.Count > 0)
            return FormatErrorMessages(errors);

        return HamsAtErrorHelper.ToEnglish(fallbackKind);
    }

    private static string? ParseAlertUrl(string body)
    {
        var payload = JsonSerializer.Deserialize<HamsAtAlertResponseDto>(body, JsonOptions);
        return payload?.Data?.Url;
    }

    private bool TryGetCached(string apiKey, out IReadOnlyList<HamsAtUpcomingAlert> alerts)
    {
        lock (_cacheLock)
        {
            if (_cachedAlerts is not null
                && _cachedApiKey == apiKey
                && DateTime.UtcNow - _cachedAtUtc < CacheTtl)
            {
                alerts = _cachedAlerts;
                return true;
            }
        }

        alerts = [];
        return false;
    }

    private void StoreCache(string apiKey, IReadOnlyList<HamsAtUpcomingAlert> alerts)
    {
        lock (_cacheLock)
        {
            _cachedApiKey = apiKey;
            _cachedAlerts = alerts;
            _cachedAtUtc = DateTime.UtcNow;
        }
    }

    private async Task<IReadOnlyList<HamsAtUpcomingAlert>> FetchFromApiAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UpcomingAlertsUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            throw new HttpRequestException("Invalid API key.", null, response.StatusCode);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<HamsAtUpcomingResponseDto>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (payload?.Data is null)
            return [];

        // Optimized: Replace LINQ Select().ToArray() with direct loop
        var alerts = new HamsAtUpcomingAlert[payload.Data.Count];
        for (int i = 0; i < payload.Data.Count; i++)
        {
            alerts[i] = MapAlert(payload.Data[i]);
        }

        return alerts;
    }

    private static HamsAtUpcomingAlert MapAlert(HamsAtUpcomingAlertDto dto) => new()
    {
        Id = dto.Id ?? "",
        Callsign = dto.Callsign ?? "",
        Comment = dto.Comment ?? "",
        Url = dto.Url ?? "",
        Mode = dto.Mode ?? "",
        AosUtc = ParseUtc(dto.AosAt),
        LosUtc = ParseUtc(dto.LosAt),
        Grids = dto.Grids ?? [],
        Mhz = dto.Mhz,
        IsWorkable = dto.IsWorkable,
        Satellite = dto.Satellite is null
            ? null
            : new HamsAtSatelliteInfo
            {
                Name = dto.Satellite.Name ?? "",
                Number = dto.Satellite.Number
            }
    };

    private static DateTime ParseUtc(string? value) =>
        DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var utc)
            ? utc.ToUniversalTime()
            : DateTime.MinValue;

    private static HttpClient CreateDefaultClient() =>
        OscarWatchHttpClients.Create(TimeSpan.FromSeconds(30));

    private sealed class HamsAtUpcomingResponseDto
    {
        [JsonPropertyName("data")]
        public List<HamsAtUpcomingAlertDto>? Data { get; init; }
    }

    private sealed class HamsAtUpcomingAlertDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("callsign")]
        public string? Callsign { get; init; }

        [JsonPropertyName("comment")]
        public string? Comment { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("mode")]
        public string? Mode { get; init; }

        [JsonPropertyName("aos_at")]
        public string? AosAt { get; init; }

        [JsonPropertyName("los_at")]
        public string? LosAt { get; init; }

        [JsonPropertyName("grids")]
        public List<string>? Grids { get; init; }

        [JsonPropertyName("mhz")]
        public double? Mhz { get; init; }

        [JsonPropertyName("is_workable")]
        public bool IsWorkable { get; init; }

        [JsonPropertyName("satellite")]
        public HamsAtSatelliteDto? Satellite { get; init; }
    }

    private sealed class HamsAtSatelliteDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("number")]
        public int Number { get; init; }
    }

    private sealed class HamsAtAlertResponseDto
    {
        [JsonPropertyName("data")]
        public HamsAtAlertDto? Data { get; init; }
    }

    private sealed class HamsAtAlertDto
    {
        [JsonPropertyName("url")]
        public string? Url { get; init; }
    }

    private sealed class HamsAtErrorResponseDto
    {
        [JsonPropertyName("errors")]
        public List<string>? Errors { get; init; }
    }
}
