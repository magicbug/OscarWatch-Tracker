using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OscarWatch.Core.Models;
using OscarWatch.Core.Net;

namespace OscarWatch.Core.Services;

/// <summary>One decoded SSTV picture to share to the OscarWatch.org gallery.</summary>
public sealed record SstvPictureUpload(
    string PicturePath,
    string Satellite,
    string Mode,
    DateTime ReceivedUtc,
    double? SyncLock,
    string? Client,
    string? NoradId = null);

/// <summary>
/// Result of a gallery upload. <see cref="Status"/> is "approved" or "pending" when stored;
/// <see cref="RetryAfter"/> comes from the server on 429 when it sends one.
/// </summary>
public sealed record SstvPictureShareResult(
    bool Accepted,
    string Message,
    int HttpStatusCode,
    string? Status = null,
    TimeSpan? RetryAfter = null);

public interface ISstvPictureShareService
{
    Task<SstvPictureShareResult> UploadAsync(
        SatelliteStatusSettings settings,
        SstvPictureUpload picture,
        CancellationToken cancellationToken = default);
}

/// <summary>Posts decoded SSTV pictures to <c>POST /api/v1/sstv/pictures</c> as multipart form data.</summary>
public sealed class SstvPictureShareService : ISstvPictureShareService
{
    public const string Path = "/api/v1/sstv/pictures";

    private readonly HttpClient _httpClient;

    public SstvPictureShareService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? OscarWatchHttpClients.Create(TimeSpan.FromSeconds(60));
    }

    public async Task<SstvPictureShareResult> UploadAsync(
        SatelliteStatusSettings settings,
        SstvPictureUpload picture,
        CancellationToken cancellationToken = default)
    {
        var token = settings.ApiToken?.Trim() ?? "";
        if (string.IsNullOrEmpty(token))
            return new SstvPictureShareResult(false, "API token is required.", 0);

        if (!SatelliteSpotService.TryBuildUri(settings.BaseUrl, Path, out var uri, out var urlError))
            return new SstvPictureShareResult(false, urlError, 0);

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(picture.PicturePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SstvPictureShareResult(false, $"Picture file could not be read: {ex.Message}", 0);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = BuildContent(picture, bytes);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var code = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.Created)
            {
                var status = TryReadStatus(body);
                return new SstvPictureShareResult(true, "Picture shared.", code, status);
            }

            return new SstvPictureShareResult(
                false,
                SatelliteSpotService.FormatError(response.StatusCode, body),
                code,
                RetryAfter: ReadRetryAfter(response));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
            return new SstvPictureShareResult(false, ex.Message, 0);
        }
    }

    public static MultipartFormDataContent BuildContent(SstvPictureUpload picture, byte[] bytes)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(ContentTypeFor(picture.PicturePath));
        content.Add(file, "picture", System.IO.Path.GetFileName(picture.PicturePath));
        content.Add(new StringContent(picture.Satellite.Trim()), "satellite");
        if (!string.IsNullOrWhiteSpace(picture.NoradId))
            content.Add(new StringContent(picture.NoradId.Trim()), "norad_id");
        content.Add(new StringContent(picture.Mode.Trim()), "mode");
        content.Add(
            new StringContent(picture.ReceivedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
            "received_at");
        if (picture.SyncLock is { } sync && double.IsFinite(sync))
        {
            var percent = (int)Math.Round(Math.Clamp(sync, 0, 100));
            content.Add(new StringContent(percent.ToString(CultureInfo.InvariantCulture)), "sync_lock");
        }

        if (!string.IsNullOrWhiteSpace(picture.Client))
        {
            var client = picture.Client.Trim();
            content.Add(new StringContent(client.Length <= 128 ? client : client[..128]), "client");
        }

        return content;
    }

    private static string ContentTypeFor(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "image/png"
        };

    private static string? TryReadStatus(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                ? status.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;
        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                return wait;
        }

        return null;
    }
}
