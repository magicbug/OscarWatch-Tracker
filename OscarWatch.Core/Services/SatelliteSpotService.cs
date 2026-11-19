using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OscarWatch.Core.Models;
using OscarWatch.Core.Net;

namespace OscarWatch.Core.Services;

public sealed class SatelliteSpotService : ISatelliteSpotService
{
    private readonly HttpClient _httpClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SatelliteSpotService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateDefaultClient();
    }

    public async Task<SatelliteSpotResult> SubmitAsync(
        SatelliteStatusSettings settings,
        OscarWatchSatelliteSpot spot,
        CancellationToken cancellationToken = default)
    {
        var token = settings.ApiToken?.Trim() ?? "";
        if (string.IsNullOrEmpty(token))
            return new SatelliteSpotResult(false, "API token is required.", 0);

        if (!TryBuildUri(settings.BaseUrl, "/api/v1/spots", out var uri, out var urlError))
            return new SatelliteSpotResult(false, urlError, 0);

        var payload = new SpotBody
        {
            Satellite = spot.Satellite.Trim(),
            Mode = string.IsNullOrWhiteSpace(spot.Mode) ? "FT4" : spot.Mode.Trim().ToUpperInvariant(),
            HeardCallsign = spot.HeardCallsign.Trim(),
            HeardGrid = string.IsNullOrWhiteSpace(spot.HeardGrid) ? null : spot.HeardGrid.Trim(),
            SnrDb = Math.Clamp(spot.SnrDb, -50, 50),
            UplinkHz = spot.UplinkHz is > 0 ? spot.UplinkHz : null,
            DownlinkHz = spot.DownlinkHz is > 0 ? spot.DownlinkHz : null,
            HeardAt = spot.HeardAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            Message = TrimToNull(spot.Message, 64),
            Client = TrimToNull(spot.Client, 128)
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var code = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.Accepted)
                return new SatelliteSpotResult(true, "Spot accepted.", code);

            return new SatelliteSpotResult(false, FormatError(response.StatusCode, body), code);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return new SatelliteSpotResult(false, ex.Message, 0);
        }
    }

    private static string? TrimToNull(string? value, int max)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0)
            return null;
        return text.Length <= max ? text : text[..max];
    }

    internal static bool TryBuildUri(string? baseUrl, string path, out Uri uri, out string error)
    {
        uri = null!;
        error = "";
        var root = (baseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(root))
        {
            error = "Base URL is required.";
            return false;
        }

        if (!Uri.TryCreate(root + path, UriKind.Absolute, out var built) ||
            (built.Scheme != Uri.UriSchemeHttps && built.Scheme != Uri.UriSchemeHttp))
        {
            error = "Base URL is not a valid http(s) address.";
            return false;
        }

        uri = built;
        return true;
    }

    internal static string FormatError(HttpStatusCode status, string body)
    {
        var detail = TryExtractMessage(body);
        var prefix = status switch
        {
            HttpStatusCode.Unauthorized => "Unauthorised",
            HttpStatusCode.Forbidden => "Forbidden",
            HttpStatusCode.NotFound => "Not found (feature may be inactive)",
            HttpStatusCode.UnprocessableEntity => "Validation failed",
            (HttpStatusCode)429 => "Rate limit exceeded",
            _ => $"HTTP {(int)status}"
        };

        return string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix}: {detail}";
    }

    private static string TryExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in errors.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.GetArrayLength() > 0)
                    {
                        var first = prop.Value[0];
                        if (first.ValueKind == JsonValueKind.String)
                            return first.GetString() ?? "";
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                return message.GetString() ?? "";
        }
        catch (JsonException)
        {
            // fall through
        }

        return Truncate(body, 160);
    }

    private static string Truncate(string text, int max)
    {
        text = text.Trim();
        if (text.Length <= max)
            return text;
        return text[..max] + "…";
    }

    private static HttpClient CreateDefaultClient() =>
        OscarWatchHttpClients.Create(TimeSpan.FromSeconds(30));

    private sealed class SpotBody
    {
        public string Satellite { get; set; } = "";
        public string Mode { get; set; } = "FT4";
        public string HeardCallsign { get; set; } = "";
        public string? HeardGrid { get; set; }
        public int SnrDb { get; set; }
        public long? UplinkHz { get; set; }
        public long? DownlinkHz { get; set; }
        public string HeardAt { get; set; } = "";
        public string? Message { get; set; }
        public string? Client { get; set; }
    }
}
