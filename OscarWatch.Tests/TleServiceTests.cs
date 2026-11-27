using System.Net;
using System.Text;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Core.Tle;

namespace OscarWatch.Tests;

public sealed class TleServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _pathsToDelete = [];

    public TleServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "oscarwatch-tle-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (var path in _pathsToDelete)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // best effort
            }
        }

        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void IsEnabled_matches_parenthetical_tle_name()
    {
        var catalog = TleParser.ParseCatalog("""
            SAUDISAT 1C (SO-50)
            1 27607U 02058C   26141.24923057  .00000576  00000-0  85866-4 0  9998
            2 27607  64.5520 212.3264 0075596 267.4106  91.8345 14.82983020260469
            """);

        var enabled = new HashSet<string>(["SO-50"], StringComparer.OrdinalIgnoreCase);

        Assert.True(SatelliteCatalogMatching.IsEnabled(catalog[0], enabled));
    }

    [Fact]
    public async Task EnsureLoadedAsync_discards_invalid_cache_and_refetches_from_network()
    {
        const string validJson = """
            [{"AMSAT_NAME":"AO-07","OBJECT_NAME":"OSCAR 7","OBJECT_ID":"1974-089B","INCLINATION":101.9901,"ECCENTRICITY":0.00126647,"RA_OF_ASC_NODE":201.9731,"ARG_OF_PERICENTER":92.559,"MEAN_ANOMALY":74.3678,"MEAN_MOTION":12.53698425,"EPOCH":"2026-07-07T12:21:17.710848","NORAD_CAT_ID":7530,"REV_AT_EPOCH":36306,"BSTAR":4.948808e-06,"EPHEMERIS_TYPE":0,"CLASSIFICATION_TYPE":"U","ELEMENT_SET_NO":999,"MEAN_MOTION_DDOT":0.0,"MEAN_MOTION_DOT":-4.6e-07}]
            """;

        var cachePath = CreateCachePath();
        await File.WriteAllTextAsync(cachePath, "[{not valid json");

        var settings = new TestSettingsService();
        var service = new TleService(settings, new HttpClient(new StubHandler(validJson)), cachePath);

        await service.EnsureLoadedAsync();

        Assert.Single(service.Catalog);
        Assert.Equal("AO-07", service.Catalog[0].Name);
        Assert.Equal(validJson.Trim(), (await File.ReadAllTextAsync(cachePath)).Trim());
    }

    [Fact]
    public async Task EnsureLoadedAsync_discards_cache_with_no_usable_satellites_and_refetches()
    {
        const string placeholderOnly = """
            [{"AMSAT_NAME":"HYDRA-W","OBJECT_NAME":"","INCLINATION":null,"ECCENTRICITY":null,"MEAN_MOTION":null,"NORAD_CAT_ID":null,"EPOCH":null}]
            """;
        const string validJson = """
            [{"AMSAT_NAME":"AO-07","OBJECT_NAME":"OSCAR 7","OBJECT_ID":"1974-089B","INCLINATION":101.9901,"ECCENTRICITY":0.00126647,"RA_OF_ASC_NODE":201.9731,"ARG_OF_PERICENTER":92.559,"MEAN_ANOMALY":74.3678,"MEAN_MOTION":12.53698425,"EPOCH":"2026-07-07T12:21:17.710848","NORAD_CAT_ID":7530,"REV_AT_EPOCH":36306,"BSTAR":4.948808e-06,"EPHEMERIS_TYPE":0,"CLASSIFICATION_TYPE":"U","ELEMENT_SET_NO":999,"MEAN_MOTION_DDOT":0.0,"MEAN_MOTION_DOT":-4.6e-07}]
            """;

        var cachePath = CreateCachePath();
        await File.WriteAllTextAsync(cachePath, placeholderOnly);

        var settings = new TestSettingsService();
        var service = new TleService(settings, new HttpClient(new StubHandler(validJson)), cachePath);

        await service.EnsureLoadedAsync();

        Assert.Single(service.Catalog);
        Assert.Equal("AO-07", service.Catalog[0].Name);
        Assert.NotNull(service.LastLoadDiagnostics);
        Assert.Equal(TleLoadOrigin.Network, service.LastLoadDiagnostics!.Origin);
    }

    [Fact]
    public async Task EnsureLoadedAsync_discards_cache_when_only_implausible_orbits_and_refetches()
    {
        const string insaneOnly = """
            [{"AMSAT_NAME":"CORRUPT","INCLINATION":51.6,"ECCENTRICITY":0.001,"RA_OF_ASC_NODE":0,"ARG_OF_PERICENTER":0,"MEAN_ANOMALY":0,"MEAN_MOTION":150.0,"EPOCH":"2026-07-07T12:21:17.710848","NORAD_CAT_ID":99999}]
            """;
        const string validJson = """
            [{"AMSAT_NAME":"AO-07","OBJECT_NAME":"OSCAR 7","OBJECT_ID":"1974-089B","INCLINATION":101.9901,"ECCENTRICITY":0.00126647,"RA_OF_ASC_NODE":201.9731,"ARG_OF_PERICENTER":92.559,"MEAN_ANOMALY":74.3678,"MEAN_MOTION":12.53698425,"EPOCH":"2026-07-07T12:21:17.710848","NORAD_CAT_ID":7530,"REV_AT_EPOCH":36306,"BSTAR":4.948808e-06,"EPHEMERIS_TYPE":0,"CLASSIFICATION_TYPE":"U","ELEMENT_SET_NO":999,"MEAN_MOTION_DDOT":0.0,"MEAN_MOTION_DOT":-4.6e-07}]
            """;

        var cachePath = CreateCachePath();
        await File.WriteAllTextAsync(cachePath, insaneOnly);

        var settings = new TestSettingsService();
        var service = new TleService(settings, new HttpClient(new StubHandler(validJson)), cachePath);

        await service.EnsureLoadedAsync();

        Assert.Single(service.Catalog);
        Assert.Equal(TleLoadOrigin.Network, service.LastLoadDiagnostics?.Origin);
    }

    [Fact]
    public async Task EnsureLoadedAsync_discards_cache_from_different_source_and_refetches()
    {
        const string validJson = """
            [{"AMSAT_NAME":"AO-07","OBJECT_NAME":"OSCAR 7","OBJECT_ID":"1974-089B","INCLINATION":101.9901,"ECCENTRICITY":0.00126647,"RA_OF_ASC_NODE":201.9731,"ARG_OF_PERICENTER":92.559,"MEAN_ANOMALY":74.3678,"MEAN_MOTION":12.53698425,"EPOCH":"2026-07-07T12:21:17.710848","NORAD_CAT_ID":7530,"REV_AT_EPOCH":36306,"BSTAR":4.948808e-06,"EPHEMERIS_TYPE":0,"CLASSIFICATION_TYPE":"U","ELEMENT_SET_NO":999,"MEAN_MOTION_DDOT":0.0,"MEAN_MOTION_DOT":-4.6e-07}]
            """;

        var cachePath = CreateCachePath();
        await File.WriteAllTextAsync(cachePath, validJson);
        await File.WriteAllTextAsync(cachePath + ".meta", "AmsatOrg||");

        var settings = new TestSettingsService();
        var service = new TleService(settings, new HttpClient(new StubHandler(validJson)), cachePath);

        await service.EnsureLoadedAsync();

        Assert.Single(service.Catalog);
        Assert.Equal("OscarWatch||", (await File.ReadAllTextAsync(cachePath + ".meta")).Trim());
    }

    [Fact]
    public async Task RefreshAsync_does_not_write_cache_when_downloaded_catalog_is_unparseable()
    {
        var cachePath = CreateCachePath();
        await File.WriteAllTextAsync(cachePath, "existing cache");

        var settings = new TestSettingsService();
        var service = new TleService(settings, new HttpClient(new StubHandler("[{not valid json")), cachePath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshAsync());
        Assert.Equal("existing cache", await File.ReadAllTextAsync(cachePath));
        Assert.Empty(service.Catalog);
    }

    [Fact]
    public async Task EnsureLoadedAsync_falls_back_when_download_times_out()
    {
        var cachePath = CreateCachePath();
        await File.WriteAllTextAsync(cachePath, Ao07Json);
        await File.WriteAllTextAsync(cachePath + ".meta", "OscarWatch||");

        var settings = new TestSettingsService();
        settings.Current.TleSource = CelestrakSource();
        var service = new TleService(settings, new HttpClient(new TimeoutHandler()), cachePath);

        await service.EnsureLoadedAsync();

        Assert.Equal("AO-07", Assert.Single(service.Catalog).Name);
        Assert.Equal(TleLoadOrigin.Cache, service.LastLoadDiagnostics?.Origin);
        Assert.Equal(Ao07Json.Trim(), (await File.ReadAllTextAsync(cachePath)).Trim());
    }

    [Fact]
    public async Task EnsureLoadedAsync_does_not_swallow_caller_cancellation()
    {
        var cachePath = CreateCachePath();
        var settings = new TestSettingsService();
        var service = new TleService(settings, new HttpClient(new TimeoutHandler()), cachePath);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EnsureLoadedAsync(cts.Token));
        Assert.Empty(service.Catalog);
    }

    [Fact]
    public async Task EnsureLoadedAsync_falls_back_to_previous_source_cache_when_new_source_is_forbidden()
    {
        var cachePath = CreateCachePath();
        await File.WriteAllTextAsync(cachePath, Ao07Json);
        await File.WriteAllTextAsync(cachePath + ".meta", "OscarWatch||");

        var settings = new TestSettingsService();
        settings.Current.TleSource = CelestrakSource();
        var handler = new SwitchableHandler { StatusCode = HttpStatusCode.Forbidden };
        var service = new TleService(settings, new HttpClient(handler), cachePath);

        await service.EnsureLoadedAsync();

        Assert.Equal("AO-07", Assert.Single(service.Catalog).Name);
        Assert.Equal(TleLoadOrigin.Cache, service.LastLoadDiagnostics?.Origin);
        Assert.True(File.Exists(cachePath));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task EnsureLoadedAsync_keeps_catalogue_and_does_not_retry_when_source_change_download_fails()
    {
        var cachePath = CreateCachePath();
        var settings = new TestSettingsService();
        var handler = new SwitchableHandler { Body = Ao07Json };
        var service = new TleService(settings, new HttpClient(handler), cachePath);
        await service.EnsureLoadedAsync();
        Assert.Single(service.Catalog);

        settings.Current.TleSource = CelestrakSource();
        handler.StatusCode = HttpStatusCode.Forbidden;
        await service.EnsureLoadedAsync();
        await service.EnsureLoadedAsync();

        Assert.Equal("AO-07", Assert.Single(service.Catalog).Name);
        Assert.True(File.Exists(cachePath));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task EnsureLoadedAsync_does_not_download_again_when_source_is_unchanged()
    {
        var cachePath = CreateCachePath();
        var settings = new TestSettingsService();
        var handler = new SwitchableHandler { Body = Ao07Json };
        var service = new TleService(settings, new HttpClient(handler), cachePath);

        await service.EnsureLoadedAsync();
        await service.EnsureLoadedAsync();

        Assert.Equal(1, handler.RequestCount);
    }

    private const string Ao07Json = """
        [{"AMSAT_NAME":"AO-07","OBJECT_NAME":"OSCAR 7","OBJECT_ID":"1974-089B","INCLINATION":101.9901,"ECCENTRICITY":0.00126647,"RA_OF_ASC_NODE":201.9731,"ARG_OF_PERICENTER":92.559,"MEAN_ANOMALY":74.3678,"MEAN_MOTION":12.53698425,"EPOCH":"2026-07-07T12:21:17.710848","NORAD_CAT_ID":7530,"REV_AT_EPOCH":36306,"BSTAR":4.948808e-06,"EPHEMERIS_TYPE":0,"CLASSIFICATION_TYPE":"U","ELEMENT_SET_NO":999,"MEAN_MOTION_DDOT":0.0,"MEAN_MOTION_DOT":-4.6e-07}]
        """;

    private static TleSourceSettings CelestrakSource() => new()
    {
        Mode = TleSourceMode.CustomUrl,
        CustomUrl = TleSourceResolver.CelestrakAmsatJsonExampleUrl
    };

    private string CreateCachePath()
    {
        var path = Path.Combine(_tempDir, $"tle-cache-{Guid.NewGuid():N}.txt");
        _pathsToDelete.Add(path);
        return path;
    }

    private sealed class TestSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new()
        {
            TleSource = new TleSourceSettings { Mode = TleSourceMode.OscarWatch }
        };

        public string? LoadError => null;
        public bool CanPersist => true;
        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "oscarwatch-tle-service-test.json");
        public string SerializeCurrent() => "{}";
        public Task ReplaceAndSaveAsync(AppSettings imported, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Load() { }
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void RequestSave() { }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void SyncGridFromLatLon() { }
        public void SyncLatLonFromGrid() { }
        public void EnsureSavedStations() { }
        public void ApplyActiveStation() { }
        public void SyncActiveStationFromGroundStation() { }
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 00:00:30 elapsing.",
                new TimeoutException("The operation has timed out."));
        }
    }

    private sealed class SwitchableHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "";
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json")
            });
        }
    }
}
