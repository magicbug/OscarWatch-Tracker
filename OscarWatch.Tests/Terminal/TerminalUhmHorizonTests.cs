using System.Net;
using System.Reflection;
using System.Text;
using OscarWatch.Core.Dxcc;
using OscarWatch.Core.Logbook;
using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Services;
using OscarWatch.Core.Terminal;
using OscarWatch.Core.Terminal.Uhm;
using OscarWatch.Localization;
using OscarWatch.Services;
using OscarWatch.Terminal;
using OscarWatch.ViewModels;

namespace OscarWatch.Tests.Terminal;

public sealed class TerminalUhmHorizonTests : IDisposable
{
    private const string NoradId = TerminalSatelliteProfiles.IssNoradId;
    private const string Me = "MM9SQL";
    private static readonly DateTime T0 = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);
    private readonly string _dbPath;
    private readonly QsoLogbookRepository _repository;

    public TerminalUhmHorizonTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"oscarwatch-uhm-horizon-{Guid.NewGuid():N}.db");
        _repository = new QsoLogbookRepository(_dbPath);
    }

    public void Dispose()
    {
        _repository.Dispose();
        try
        {
            File.Delete(_dbPath);
        }
        catch
        {
            // ignore
        }
    }

    [Theory]
    [InlineData(-2.0, false)]
    [InlineData(-1.01, false)]
    [InlineData(-1.0, true)]
    [InlineData(0.0, true)]
    [InlineData(20.0, true)]
    public void UhmSatelliteIsUp_respects_minus_one_degree_slop(double elevationDeg, bool expected)
    {
        var (service, _) = CreateService(elevationDeg, out _);
        Assert.Equal(expected, service.UhmSatelliteIsUp());
    }

    [Fact]
    public void UhmSatelliteIsUp_false_when_look_angles_missing()
    {
        var settings = CreateSettings();
        var tracking = new StubLiveTracking(NoradId, states: []);
        var service = CreateTerminalService(settings, tracking, new CountingHandler());
        service.SetSatelliteContext(IssProfile());
        Assert.False(service.UhmSatelliteIsUp());
    }

    [Fact]
    public async Task UhmTick_makes_no_http_calls_when_below_horizon()
    {
        var handler = new CountingHandler();
        var (service, tracking) = CreateService(-5, out handler);
        service.SetSatelliteContext(IssProfile());

        await InvokeUhmTickAsync(service).ConfigureAwait(true);

        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(TerminalUhmStatus.BelowHorizon, service.UhmStatus);
    }

    [Fact]
    public async Task UhmTick_polls_when_above_horizon()
    {
        var handler = new CountingHandler();
        var (service, tracking) = CreateService(15, out handler);
        service.SetSatelliteContext(IssProfile());

        await InvokeUhmTickAsync(service).ConfigureAwait(true);

        Assert.True(handler.RequestCount >= 2);
        Assert.Equal(TerminalUhmStatus.Active, service.UhmStatus);
    }

    [Fact]
    public async Task PostUhmHeard_skips_http_when_below_horizon()
    {
        var handler = new CountingHandler();
        var (service, _) = CreateService(-5, out handler);
        service.SetSatelliteContext(IssProfile());

        await InvokePostUhmHeardAsync(service).ConfigureAwait(true);

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task PostUhmHeard_posts_when_above_horizon()
    {
        var handler = new CountingHandler();
        var (service, _) = CreateService(10, out handler);
        service.SetSatelliteContext(IssProfile());

        await InvokePostUhmHeardAsync(service).ConfigureAwait(true);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Contains("/uhm/heard", handler.LastUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UhmTick_resumes_polling_after_satellite_rises()
    {
        var handler = new CountingHandler();
        var tracking = new StubLiveTracking(NoradId, [Snapshot(-5)]);
        var settings = CreateSettings();
        var service = CreateTerminalService(settings, tracking, handler);
        service.SetSatelliteContext(IssProfile());

        await InvokeUhmTickAsync(service).ConfigureAwait(true);
        Assert.Equal(0, handler.RequestCount);

        tracking.SetSnapshot(Snapshot(12));
        await InvokeUhmTickAsync(service).ConfigureAwait(true);

        Assert.True(handler.RequestCount >= 2);
        Assert.Equal(TerminalUhmStatus.Active, service.UhmStatus);
    }

    private (TerminalService Service, StubLiveTracking Tracking) CreateService(double elevationDeg, out CountingHandler handler)
    {
        handler = new CountingHandler();
        var settings = CreateSettings();
        var tracking = new StubLiveTracking(NoradId, [Snapshot(elevationDeg)]);
        var service = CreateTerminalService(settings, tracking, handler);
        return (service, tracking);
    }

    private static TerminalSatelliteProfile IssProfile() =>
        TerminalSatelliteProfiles.FindBuiltIn(NoradId)!;

    private static SatelliteTrackState Snapshot(double elevationDeg) =>
        new()
        {
            Name = "ISS",
            NoradId = NoradId,
            Subpoint = new GeoCoordinate(0, 0, 0),
            LookAngles = new LookAngles(90, elevationDeg, 400)
        };

    private TerminalService CreateTerminalService(TestSettingsService settings, StubLiveTracking tracking, CountingHandler handler)
    {
        _ = _repository.InitializeAsync();
        var database = new EmptySatelliteDatabase();
        var frequencies = new FrequencyOverlayViewModel(settings, database, LocalizationService.Instance);
        var qsoLogger = new LiveQsoLogger(
            settings,
            _repository,
            new NoOpCloudlogUpload(),
            new NoOpSatelliteLink(),
            new EmptySnapshotProvider(),
            tracking,
            frequencies,
            LocalizationService.Instance,
            new NoOpDxcc());

        return new TerminalService(
            settings,
            qsoLogger,
            new NullPropagator(),
            tracking,
            new UhmApiClient(new HttpClient(handler)));
    }

    private static TestSettingsService CreateSettings()
    {
        var settings = new TestSettingsService();
        settings.Current.Terminal.Uhm.Enabled = true;
        settings.Current.GroundStation.Callsign = Me;
        settings.Current.SatelliteStatus.ApiToken = "tok";
        settings.Current.SatelliteStatus.BaseUrl = "https://oscarwatch.org/";
        return settings;
    }

    private static async Task InvokeUhmTickAsync(TerminalService service)
    {
        var method = typeof(TerminalService).GetMethod(
            "UhmTickAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var nextActive = DateTime.MinValue;
        var nextStatus = DateTime.MinValue;
        var task = (Task)method.Invoke(
            service,
            [nextActive, nextStatus, CancellationToken.None])!;
        await task.ConfigureAwait(true);
    }

    private static async Task InvokePostUhmHeardAsync(TerminalService service)
    {
        var frame = Ax25Frame.CreateUi(Ax25Address.Parse(Me), Ax25Address.Parse("G0ABC"), [], "JO45");
        var message = TerminalMessageCodec.Decode(frame, TerminalMessageFormat.PlainText, T0);
        var profile = IssProfile();
        var method = typeof(TerminalService).GetMethod(
            "PostUhmHeard",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(service, [message, Me, profile]);
        await Task.Delay(200).ConfigureAwait(true);
    }

    private sealed class TestSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string SettingsPath => "";
        public string? LoadError => null;
        public bool CanPersist => true;
        public string SerializeCurrent() => "";
        public Task ReplaceAndSaveAsync(AppSettings imported, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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

    private sealed class StubLiveTracking(string? focusedNoradId, IReadOnlyList<SatelliteTrackState> states) : ILiveTrackingService
    {
        private IReadOnlyList<SatelliteTrackState> _states = states;

        public string? FocusedNoradId { get; set; } = focusedNoradId;
        public DateTime SnapshotUtc => DateTime.MinValue;
        public TimeSpan MapTimeOffset { get; set; }
        public DateTime LiveNowSnapshotUtc => DateTime.MinValue;

        public void SetSnapshot(SatelliteTrackState state) => _states = [state];

        public IReadOnlyList<SatelliteTrackState> GetSnapshot() => _states;
        public IReadOnlyList<SatelliteTrackState> GetLiveNowSnapshot() => _states;
        public void Start() { }
        public void RequestReload() { }
        public void Dispose() { }
    }

    private sealed class NullPropagator : IOrbitPropagator
    {
        public IReadOnlyCollection<string> LoadedNoradIds { get; } = [];
        public void Clear() { }
        public void LoadSatellite(SatelliteCatalogEntry entry) { }
        public void RemoveSatellite(string noradId) { }
        public bool HasSatellite(string noradId) => false;
        public GeoCoordinate GetSubpoint(string noradId, DateTime utc) => new(0, 0, 0);
        public EciPosition GetEciPosition(string noradId, DateTime utc) => new(0, 0, 0);
        public LookAngles GetLookAngles(string noradId, GroundStation site, DateTime utc) => new(0, 0, 0, 0);
    }

    private sealed class EmptySatelliteDatabase : ISatelliteDatabaseService
    {
        public IReadOnlyList<SatelliteRadioEntry> Entries => [];
        public string ActiveDatabasePath => "";
        public bool IsUsingUserDatabase => false;
        public SatelliteRadioEntry? TryGetEntry(string satelliteName, string? noradId = null) => null;
        public void Reload() { }
    }

    private sealed class EmptySnapshotProvider : ILiveTrackerSnapshotProvider
    {
        public string? FocusedNoradId => null;
        public LiveTrackerSnapshot GetCurrent() => LiveTrackerSnapshot.Empty;
    }

    private sealed class NoOpCloudlogUpload : ICloudlogQsoUploadService
    {
        public event Action<long>? UploadStateChanged { add { } remove { } }
        public Task QueueUploadIfEnabledAsync(long qsoId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ReuploadQsoAsync(long qsoId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ProcessRetryQueueAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetFailedUploadsForRetryAsync(long logbookId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NoOpSatelliteLink : ISatelliteLinkBroadcastService
    {
        public event Action? StateChanged { add { } remove { } }
        public bool IsListening => false;
        public int ClientCount => 0;
        public string? LastError => null;
        public void ApplySettings(SatelliteLinkSettings settings) { }
        public void Publish(SatelliteTrackState? track, RigTrackingContext? context, bool force = false) { }
        public void PublishQso(QsoRecord record, QsoLogbook logbook, SatelliteLinkQsoEventKind kind, string? noradId = null) { }
        public void PublishPassAlert(PassInfo pass, DateTime utcNow) { }
        public Task<bool> TestBindAsync(SatelliteLinkSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class NoOpDxcc : IDxccLookupService
    {
        public string ActiveCountryFilePath => "";
        public DateTime? CountryFileLastWriteUtc => null;
        public bool TryResolve(string? callsign, out DxccMatch match)
        {
            match = default;
            return false;
        }

        public void EnsureLoaded() { }
        public void Reload() { }
        public Task<DxccCountryFileUpdateResult> UpdateCountryFileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DxccCountryFileUpdateResult());
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastMethod = request.Method;
            LastUri = request.RequestUri;

            var path = request.RequestUri!.AbsolutePath;
            var body = path.Contains("/heard", StringComparison.Ordinal)
                ? """{"stored":true}"""
                : path.Contains("/active", StringComparison.Ordinal)
                    ? """{"callsigns":[]}"""
                    : path.Contains("/status", StringComparison.Ordinal)
                        ? """{"message":"ok"}"""
                        : """{"matched":false,"mutual":false}""";

            var status = request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
