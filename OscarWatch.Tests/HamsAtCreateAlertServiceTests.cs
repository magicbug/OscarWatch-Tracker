using System.Net;
using System.Text;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

public sealed class HamsAtCreateAlertServiceTests
{
    private static HamsAtSettings ValidSettings => new() { ApiKey = "test-key" };

    private static HamsAtCreateAlertRequest SampleRequest => new()
    {
        SatelliteNumber = 25544,
        ObserverLat = 51.5,
        ObserverLon = -0.1,
        MaxAtUtc = new DateTime(2026, 8, 24, 12, 30, 0, DateTimeKind.Utc),
        Callsign = "MM9SQL",
        Grids = ["IO91wm"],
        Mode = "FM",
        Comment = "Portable",
        Mhz = 145.950,
        MhzDirection = "up"
    };

    [Fact]
    public async Task CreateAlertAsync_returns_url_on_201()
    {
        const string body = """
            {
              "data": {
                "id": "9c465415-5b3d-4951-a5b7-93bb9346abef",
                "url": "https://hams.at/alerts/9c465415-5b3d-4951-a5b7-93bb9346abef",
                "callsign": "MM9SQL"
              }
            }
            """;
        var handler = new StubHandler(body, HttpStatusCode.Created);
        var service = new HamsAtRovesService(new HttpClient(handler));

        var result = await service.CreateAlertAsync(ValidSettings, SampleRequest);

        Assert.True(result.Ok);
        Assert.Equal(201, result.HttpStatusCode);
        Assert.Equal("https://hams.at/alerts/9c465415-5b3d-4951-a5b7-93bb9346abef", result.AlertUrl);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal(HamsAtRovesService.CreateAlertUrl, handler.LastUri?.ToString());
        Assert.Equal("Bearer", handler.LastAuthScheme);
        Assert.Equal("test-key", handler.LastAuthParameter);
        Assert.Contains("\"satellite_number\":25544", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"observer_lat\":51.5", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"observer_lon\":-0.1", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"max_at\":\"2026-08-24T12:30:00.0000000Z\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"callsign\":\"MM9SQL\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"grids\":[\"IO91wm\"]", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"FM\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"comment\":\"Portable\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"mhz\":145.95", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"mhz_direction\":\"up\"", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAlertAsync_requires_api_key()
    {
        var service = new HamsAtRovesService(new HttpClient(new StubHandler("{}")));
        var result = await service.CreateAlertAsync(new HamsAtSettings(), SampleRequest);

        Assert.False(result.Ok);
        Assert.Equal(HamsAtFetchErrorKind.MissingApiKey, result.ErrorKind);
    }

    [Fact]
    public async Task CreateAlertAsync_fails_on_401()
    {
        var handler = new StubHandler("""{"errors":["Invalid API key"]}""", HttpStatusCode.Unauthorized);
        var service = new HamsAtRovesService(new HttpClient(handler));

        var result = await service.CreateAlertAsync(ValidSettings, SampleRequest);

        Assert.False(result.Ok);
        Assert.Equal(401, result.HttpStatusCode);
        Assert.Equal(HamsAtFetchErrorKind.InvalidApiKey, result.ErrorKind);
    }

    [Fact]
    public async Task CreateAlertAsync_surfaces_422_validation_errors()
    {
        const string body = """{"errors":["Callsign is too short","Grid is invalid"]}""";
        var handler = new StubHandler(body, HttpStatusCode.UnprocessableEntity);
        var service = new HamsAtRovesService(new HttpClient(handler));

        var result = await service.CreateAlertAsync(ValidSettings, SampleRequest);

        Assert.False(result.Ok);
        Assert.Equal(422, result.HttpStatusCode);
        Assert.Equal("Callsign is too short; Grid is invalid", result.ErrorMessage);
        Assert.Contains("\"callsign\":\"MM9SQL\"", result.RequestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAlertAsync_surfaces_500_error_messages()
    {
        const string body = """{"errors":["Sorry, an internal error occurred. Please take a screenshot and contact WW1X."]}""";
        var handler = new StubHandler(body, HttpStatusCode.InternalServerError);
        var service = new HamsAtRovesService(new HttpClient(handler));

        var result = await service.CreateAlertAsync(ValidSettings, SampleRequest);

        Assert.False(result.Ok);
        Assert.Equal(500, result.HttpStatusCode);
        Assert.Contains("internal error", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseErrorMessages_joins_all_validation_errors()
    {
        const string body = """{"errors":["Callsign is too short","Grid is invalid"]}""";

        var errors = HamsAtRovesService.ParseErrorMessages(body);

        Assert.Equal(["Callsign is too short", "Grid is invalid"], errors);
        Assert.Equal("Callsign is too short; Grid is invalid", HamsAtRovesService.FormatErrorMessages(errors));
    }

    [Fact]
    public void InvalidateCache_clears_cached_alerts()
    {
        var service = new HamsAtRovesService(new HttpClient(new StubHandler("""{"data":[]}""")));
        service.InvalidateCache();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _statusCode;

        public HttpMethod? LastMethod { get; private set; }
        public Uri? LastUri { get; private set; }
        public string? LastAuthScheme { get; private set; }
        public string? LastAuthParameter { get; private set; }
        public string LastBody { get; private set; } = "";

        public StubHandler(string body, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _body = body;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastUri = request.RequestUri;
            LastAuthScheme = request.Headers.Authorization?.Scheme;
            LastAuthParameter = request.Headers.Authorization?.Parameter;
            LastBody = request.Content is null
                ? ""
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();

            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }
}
