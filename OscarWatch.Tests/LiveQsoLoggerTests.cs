using OscarWatch.Core.Dxcc;
using OscarWatch.Core.Logbook;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using OscarWatch.Services;
using OscarWatch.ViewModels;

namespace OscarWatch.Tests;

public class LiveQsoLoggerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly QsoLogbookRepository _repository;
    private readonly CapturingDxccLookup _dxcc;

    public LiveQsoLoggerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"oscarwatch-live-qso-{Guid.NewGuid():N}.db");
        _repository = new QsoLogbookRepository(_dbPath);
        _dxcc = new CapturingDxccLookup();
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

    [Fact]
    public async Task LogAsync_stores_dxcc_and_country_at_log_time()
    {
        await _repository.InitializeAsync();
        _dxcc.NextMatch = new DxccMatch(291, "United States", "K", "United States");

        var logger = CreateLogger(_repository, _dxcc);
        var qsoUtc = new DateTime(2026, 3, 15, 18, 42, 0, DateTimeKind.Utc);
        var record = await logger.LogAsync(new LiveQsoEntry(
            qsoUtc,
            "K2MO",
            "-12",
            "-14",
            "FN31",
            "FT4",
            "FT4",
            "RS-44 FT4"));

        Assert.Equal(291, record.Dxcc);
        Assert.Equal("United States", record.Country);

        var loaded = await _repository.GetQsoByIdAsync(record.Id);
        Assert.NotNull(loaded);
        Assert.Equal(291, loaded.Dxcc);
        Assert.Equal("United States", loaded.Country);

        var missing = await _repository.ListQsosMissingDxccAsync(record.LogbookId);
        Assert.DoesNotContain(missing, q => q.Id == record.Id);
    }

    [Fact]
    public async Task LogAsync_leaves_dxcc_empty_when_lookup_fails()
    {
        await _repository.InitializeAsync();
        _dxcc.NextMatch = null;

        var logger = CreateLogger(_repository, _dxcc);
        var record = await logger.LogAsync(new LiveQsoEntry(
            DateTime.UtcNow,
            "ZZ9UNKNOWN",
            "59",
            "59",
            "",
            "FT4",
            "FT4",
            ""));

        Assert.Null(record.Dxcc);
        Assert.Equal("", record.Country);
    }

    [Fact]
    public async Task LogAsync_notifies_logbook_listeners()
    {
        await _repository.InitializeAsync();
        _dxcc.NextMatch = new DxccMatch(291, "United States", "K", "United States");

        long? changedLogbookId = null;
        _repository.QsosChanged += id => changedLogbookId = id;

        var logger = CreateLogger(_repository, _dxcc);
        var record = await logger.LogAsync(new LiveQsoEntry(
            DateTime.UtcNow,
            "K2MO",
            "59",
            "59",
            "",
            "FT4",
            "FT4",
            ""));

        Assert.Equal(record.LogbookId, changedLogbookId);
    }

    private static LiveQsoLogger CreateLogger(IQsoLogbookRepository repository, IDxccLookupService dxcc)
    {
        var settings = new MinimalSettings();
        var database = new EmptySatelliteDatabase();
        var frequencies = new FrequencyOverlayViewModel(settings, database, LocalizationService.Instance);
        return new LiveQsoLogger(
            settings,
            repository,
            new NoOpCloudlogUpload(),
            new NoOpSatelliteLink(),
            new NoOpWsjtxUdp(),
            new EmptySnapshotProvider(),
            new StubLiveTracking(),
            frequencies,
            LocalizationService.Instance,
            dxcc);
    }

    private sealed class CapturingDxccLookup : IDxccLookupService
    {
        public DxccMatch? NextMatch { get; set; }

        public string ActiveCountryFilePath => "";
        public DateTime? CountryFileLastWriteUtc => null;

        public bool TryResolve(string? callsign, out DxccMatch match)
        {
            if (NextMatch is { } resolved)
            {
                match = resolved;
                return true;
            }

            match = default;
            return false;
        }

        public void EnsureLoaded() { }
        public void Reload() { }

        public Task<DxccCountryFileUpdateResult> UpdateCountryFileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DxccCountryFileUpdateResult());
    }

    private sealed class MinimalSettings : ISettingsService
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

    private sealed class EmptySatelliteDatabase : ISatelliteDatabaseService
    {
        public IReadOnlyList<SatelliteRadioEntry> Entries => [];
        public string ActiveDatabasePath => "";
        public bool IsUsingUserDatabase => false;
        public SatelliteRadioEntry? TryGetEntry(string satelliteName, string? noradId = null) => null;
        public void Reload() { }
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

        public Task<CloudlogBulkUploadResult> SendAllToCloudlogAsync(
            long logbookId,
            IProgress<(int Done, int Total)>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CloudlogBulkUploadResult.Blocked(CloudlogBulkUploadOutcome.NotConfigured));
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

    private sealed class EmptySnapshotProvider : ILiveTrackerSnapshotProvider
    {
        public string? FocusedNoradId => null;
        public LiveTrackerSnapshot GetCurrent() => LiveTrackerSnapshot.Empty;
    }

    private sealed class StubLiveTracking : ILiveTrackingService
    {
        public DateTime SnapshotUtc => DateTime.MinValue;
        public TimeSpan MapTimeOffset { get; set; }
        public string? FocusedNoradId { get; set; }
        public DateTime LiveNowSnapshotUtc => DateTime.MinValue;

        public IReadOnlyList<SatelliteTrackState> GetSnapshot() => [];
        public IReadOnlyList<SatelliteTrackState> GetLiveNowSnapshot() => [];
        public void Start() { }
        public void RequestReload() { }
        public void Dispose() { }
    }
}
