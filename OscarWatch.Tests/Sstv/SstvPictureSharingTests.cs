using System.Net;
using System.Text;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Core.Sstv;

namespace OscarWatch.Tests.Sstv;

public sealed class SstvPictureSharingTests : IDisposable
{
    private const string Norad = "25544";
    private static readonly DateTime Received = new(2026, 10, 10, 15, 19, 43, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ow-sstv-share-" + Guid.NewGuid().ToString("N"));
    private readonly List<SstvPictureUpload> _posted = [];
    private readonly Queue<SstvPictureShareResult> _responses = new();
    private SatelliteStatusSettings _connection = new() { BaseUrl = "https://oscarwatch.org", ApiToken = "test-token" };
    private bool _enabled = true;
    private double? _elevation = 30;
    private DateTime _now = Received;

    public SstvPictureSharingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Good_live_picture_is_queued()
    {
        Assert.True(SstvShareRules.ShouldQueue(true, "token", false, "ISS", Image(lockPercent: 92)));
    }

    [Theory]
    [InlineData(false, "token", false, "ISS", 92.0, true)]
    [InlineData(true, "", false, "ISS", 92.0, true)]
    [InlineData(true, null, false, "ISS", 92.0, true)]
    [InlineData(true, "token", true, "ISS", 92.0, true)]
    [InlineData(true, "token", false, null, 92.0, true)]
    [InlineData(true, "token", false, "  ", 92.0, true)]
    [InlineData(true, "token", false, "ISS", 49.9, true)]
    [InlineData(true, "token", false, "ISS", 92.0, false)]
    public void Picture_is_not_queued_when_a_rule_fails(
        bool enabled, string? token, bool fromRecording, string? satellite, double lockPercent, bool complete)
    {
        Assert.False(SstvShareRules.ShouldQueue(enabled, token, fromRecording, satellite, Image(lockPercent, complete)));
    }

    [Fact]
    public void Share_option_needs_an_api_token()
    {
        Assert.False(SstvShareRules.CanShare(new SatelliteStatusSettings { ApiToken = "" }));
        Assert.False(SstvShareRules.CanShare(new SatelliteStatusSettings { ApiToken = "   " }));
        Assert.True(SstvShareRules.CanShare(new SatelliteStatusSettings { ApiToken = "abc" }));
    }

    [Fact]
    public void Uploader_is_inactive_without_token_or_when_switched_off()
    {
        using var uploader = CreateUploader();
        Assert.True(uploader.IsActive);

        _connection = new SatelliteStatusSettings { BaseUrl = "https://oscarwatch.org", ApiToken = "" };
        Assert.False(uploader.IsActive);

        _connection = new SatelliteStatusSettings { BaseUrl = "https://oscarwatch.org", ApiToken = "test-token" };
        _enabled = false;
        Assert.False(uploader.IsActive);
    }

    [Fact]
    public async Task Picture_is_held_until_the_satellite_has_set()
    {
        using var uploader = CreateUploader();
        uploader.Enqueue(Item());

        foreach (var el in new double[] { 40, 25, 10, 3 })
            await Tick(uploader, el);
        Assert.Empty(_posted);
        Assert.Equal(SstvShareState.WaitingForPass, uploader.Status.State);

        await Tick(uploader, -0.5);
        await Tick(uploader, -1.0);
        Assert.Empty(_posted);

        _responses.Enqueue(new SstvPictureShareResult(true, "ok", 201, "approved"));
        await Tick(uploader, -1.5);

        var posted = Assert.Single(_posted);
        Assert.Equal("ISS", posted.Satellite);
        Assert.Equal("PD 120", posted.Mode);
        Assert.Equal(0, uploader.PendingCount);
        Assert.Equal(SstvShareState.Shared, uploader.Status.State);
    }

    [Fact]
    public async Task Rising_below_the_horizon_does_not_count_as_set()
    {
        using var uploader = CreateUploader();
        uploader.Enqueue(Item());

        foreach (var el in new double[] { -4, -3, -2, -1, -0.5 })
            await Tick(uploader, el);

        Assert.Empty(_posted);
    }

    [Fact]
    public async Task Picture_from_an_unknown_satellite_waits_thirty_minutes()
    {
        using var uploader = CreateUploader();
        uploader.Enqueue(Item(norad: null));

        _now = Received.AddMinutes(29);
        await uploader.ProcessOnceAsync();
        Assert.Empty(_posted);

        _responses.Enqueue(new SstvPictureShareResult(true, "ok", 201));
        _now = Received.AddMinutes(31);
        await uploader.ProcessOnceAsync();
        Assert.Single(_posted);
    }

    [Fact]
    public async Task Rejected_picture_is_dropped()
    {
        using var uploader = CreateUploader();
        uploader.Enqueue(Item());
        _responses.Enqueue(new SstvPictureShareResult(false, "Validation failed: mostly noise", 422));

        await SetPass(uploader);

        Assert.Single(_posted);
        Assert.Equal(0, uploader.PendingCount);
        Assert.Equal(SstvShareState.Rejected, uploader.Status.State);
        Assert.Contains("mostly noise", uploader.Status.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Auth_or_feature_errors_stop_the_queue_until_the_token_changes(int code)
    {
        using var uploader = CreateUploader();
        uploader.Enqueue(Item());
        _responses.Enqueue(new SstvPictureShareResult(false, "Unauthorised", code));

        await SetPass(uploader);
        Assert.Single(_posted);
        Assert.Equal(1, uploader.PendingCount);
        Assert.Equal(SstvShareState.Stopped, uploader.Status.State);

        _now = _now.AddHours(1);
        await uploader.ProcessOnceAsync();
        Assert.Single(_posted);

        _connection = new SatelliteStatusSettings { BaseUrl = "https://oscarwatch.org", ApiToken = "new-token" };
        _responses.Enqueue(new SstvPictureShareResult(true, "ok", 201));
        await uploader.ProcessOnceAsync();
        Assert.Equal(2, _posted.Count);
        Assert.Equal(0, uploader.PendingCount);
    }

    [Fact]
    public async Task Rate_limit_keeps_the_picture_and_waits()
    {
        using var uploader = CreateUploader();
        uploader.Enqueue(Item());
        _responses.Enqueue(new SstvPictureShareResult(false, "Rate limit exceeded", 429, RetryAfter: TimeSpan.FromMinutes(10)));

        await SetPass(uploader);
        Assert.Single(_posted);
        Assert.Equal(1, uploader.PendingCount);
        Assert.Equal(SstvShareState.RateLimited, uploader.Status.State);

        _now = _now.AddMinutes(9);
        await uploader.ProcessOnceAsync();
        Assert.Single(_posted);

        _responses.Enqueue(new SstvPictureShareResult(true, "ok", 201));
        _now = _now.AddMinutes(2);
        await uploader.ProcessOnceAsync();
        Assert.Equal(2, _posted.Count);
        Assert.Equal(0, uploader.PendingCount);
    }

    [Fact]
    public async Task Network_error_keeps_the_picture_for_a_retry()
    {
        using var uploader = CreateUploader();
        uploader.Enqueue(Item());
        _responses.Enqueue(new SstvPictureShareResult(false, "No such host", 0));

        await SetPass(uploader);
        Assert.Equal(1, uploader.PendingCount);
        Assert.Equal(SstvShareState.Retrying, uploader.Status.State);

        _responses.Enqueue(new SstvPictureShareResult(true, "ok", 201));
        _now = _now.AddMinutes(1);
        await uploader.ProcessOnceAsync();
        Assert.Equal(2, _posted.Count);
        Assert.Equal(0, uploader.PendingCount);
    }

    [Fact]
    public async Task Nothing_is_sent_when_sharing_is_switched_off()
    {
        using var uploader = CreateUploader();
        uploader.Enqueue(Item());
        _enabled = false;

        await SetPass(uploader);

        Assert.Empty(_posted);
    }

    [Fact]
    public void Queue_survives_a_restart()
    {
        var queuePath = Path.Combine(_dir, SstvShareQueueStore.FileName);
        using (var first = CreateUploader(queuePath))
            first.Enqueue(Item());

        Assert.True(File.Exists(queuePath));
        using var second = CreateUploader(queuePath);
        var item = Assert.Single(second.Pending);
        Assert.Equal("ISS", item.Satellite);
        Assert.Equal(Norad, item.NoradId);
        Assert.Equal(Received, item.ReceivedUtc);
    }

    [Fact]
    public async Task Temporary_picture_is_deleted_after_sharing()
    {
        using var uploader = CreateUploader();
        var item = Item(temporary: true);
        uploader.Enqueue(item);
        _responses.Enqueue(new SstvPictureShareResult(true, "ok", 201));

        await SetPass(uploader);

        Assert.False(File.Exists(item.PicturePath));
    }

    [Fact]
    public void Clear_drops_the_queue_and_temporary_pictures()
    {
        using var uploader = CreateUploader();
        var kept = Item();
        var temporary = Item(temporary: true);
        uploader.Enqueue(kept);
        uploader.Enqueue(temporary);

        uploader.Clear();

        Assert.Equal(0, uploader.PendingCount);
        Assert.True(File.Exists(kept.PicturePath));
        Assert.False(File.Exists(temporary.PicturePath));
    }

    [Fact]
    public async Task Multipart_uses_api_field_names_and_mode_name()
    {
        var picture = new SstvPictureUpload(
            WritePicture(),
            "ISS",
            SstvModeTable.Pd120.Name,
            Received,
            91.6,
            "OscarWatch-Tracker/2.4.0",
            "25544");
        using var content = SstvPictureShareService.BuildContent(picture, [1, 2, 3]);

        var parts = new Dictionary<string, string>();
        foreach (var part in content)
            parts[part.Headers.ContentDisposition!.Name!.Trim('"')] = await part.ReadAsStringAsync();

        Assert.Equal(["picture", "satellite", "norad_id", "mode", "received_at", "sync_lock", "client"], parts.Keys);
        Assert.Equal("ISS", parts["satellite"]);
        Assert.Equal("25544", parts["norad_id"]);
        Assert.Equal("PD 120", parts["mode"]);
        Assert.Equal("2026-10-10T15:19:43Z", parts["received_at"]);
        Assert.Equal("92", parts["sync_lock"]);
        Assert.Equal("OscarWatch-Tracker/2.4.0", parts["client"]);
        var file = content.First();
        Assert.Equal("image/png", file.Headers.ContentType?.MediaType);
        Assert.EndsWith(".png", file.Headers.ContentDisposition!.FileName!.Trim('"'), StringComparison.Ordinal);
    }

    [Fact]
    public void Mode_names_match_the_gallery_api()
    {
        var names = SstvModeTable.All.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
                 {
                     "Robot 36", "Robot 72", "Scottie S1", "Scottie S2", "Scottie DX", "Martin M1", "Martin M2",
                     "Wraase SC2-180", "PD 50", "PD 90", "PD 120", "PD 160", "PD 180", "PD 240", "PD 290"
                 })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public async Task Service_posts_to_sstv_pictures_with_bearer_token()
    {
        var handler = new StubHandler("""{"id":42,"status":"pending"}""", HttpStatusCode.Created);
        var service = new SstvPictureShareService(new HttpClient(handler));

        var result = await service.UploadAsync(_connection, new SstvPictureUpload(WritePicture(), "ISS", "PD 120", Received, 92, null));

        Assert.True(result.Accepted);
        Assert.Equal(201, result.HttpStatusCode);
        Assert.Equal("pending", result.Status);
        Assert.Equal("https://oscarwatch.org/api/v1/sstv/pictures", handler.LastUri?.ToString());
        Assert.Equal("Bearer", handler.LastAuthScheme);
        Assert.Equal("test-token", handler.LastAuthParameter);
        Assert.Equal("multipart/form-data", handler.LastContentType);
    }

    [Fact]
    public async Task Service_needs_a_token()
    {
        var handler = new StubHandler("{}", HttpStatusCode.Created);
        var service = new SstvPictureShareService(new HttpClient(handler));

        var result = await service.UploadAsync(
            new SatelliteStatusSettings { BaseUrl = "https://oscarwatch.org" },
            new SstvPictureUpload(WritePicture(), "ISS", "PD 120", Received, 92, null));

        Assert.False(result.Accepted);
        Assert.Null(handler.LastUri);
    }

    [Fact]
    public async Task Service_reports_422_picture_reason()
    {
        var handler = new StubHandler(
            """{"message":"The given data was invalid.","errors":{"picture":["The picture is the wrong size for PD 120."]}}""",
            HttpStatusCode.UnprocessableEntity);
        var service = new SstvPictureShareService(new HttpClient(handler));

        var result = await service.UploadAsync(_connection, new SstvPictureUpload(WritePicture(), "ISS", "PD 120", Received, 92, null));

        Assert.False(result.Accepted);
        Assert.Equal(422, result.HttpStatusCode);
        Assert.Contains("wrong size", result.Message, StringComparison.Ordinal);
    }

    private SstvPictureUploader CreateUploader(string? queuePath = null) =>
        new(
            (_, picture, _) =>
            {
                _posted.Add(picture);
                return Task.FromResult(_responses.Count > 0
                    ? _responses.Dequeue()
                    : new SstvPictureShareResult(false, "no response queued", 0));
            },
            () => _connection,
            () => _enabled,
            _ => _elevation,
            "OscarWatch-Tracker/test",
            queuePath,
            () => _now);

    private async Task Tick(SstvPictureUploader uploader, double elevation)
    {
        _elevation = elevation;
        _now = _now.AddSeconds(2);
        await uploader.ProcessOnceAsync();
    }

    private async Task SetPass(SstvPictureUploader uploader)
    {
        foreach (var el in new double[] { 20, -0.5, -1.0, -1.5 })
            await Tick(uploader, el);
    }

    private SstvShareItem Item(string? norad = Norad, bool temporary = false) =>
        new(WritePicture(), "ISS", norad, "PD 120", Received, 92, temporary);

    private string WritePicture()
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        return path;
    }

    private static SstvDecodedImage Image(double lockPercent, bool complete = true) =>
        new()
        {
            Mode = SstvModeTable.Pd120,
            Image = new SstvImage(SstvModeTable.Pd120.Width, SstvModeTable.Pd120.Height),
            LineQuality = [],
            FromVis = true,
            Complete = complete,
            LockPercent = lockPercent,
            OffsetHz = 0,
            ClockPpm = 0,
            StartSeconds = 0,
            EndSeconds = 120
        };

    private sealed class StubHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        public string? LastAuthScheme { get; private set; }
        public string? LastAuthParameter { get; private set; }
        public string? LastContentType { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastAuthScheme = request.Headers.Authorization?.Scheme;
            LastAuthParameter = request.Headers.Authorization?.Parameter;
            LastContentType = request.Content?.Headers.ContentType?.MediaType;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
