using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

public sealed class LiveTrackingServiceTests
{
    [Fact]
    public void Background_loop_updates_snapshot()
    {
        var calls = 0;
        var orchestrator = CreateMinimalOrchestrator();
        using var service = new LiveTrackingService(orchestrator, gps: null, _ =>
        {
            calls++;
            return
            [
                new SatelliteTrackState
                {
                    Name = "TEST",
                    NoradId = "1",
                    Subpoint = new GeoCoordinate(0, 0, 400)
                }
            ];
        });

        service.Start();
        service.RefreshSnapshotSynchronously();
        Assert.True(calls >= 1);
        Assert.Single(service.GetSnapshot());
        Assert.True(service.SnapshotUtc > DateTime.MinValue);
    }

    [Fact]
    public void RequestReload_refreshes_snapshot_on_worker()
    {
        var orchestrator = CreateMinimalOrchestrator();
        using var service = new LiveTrackingService(orchestrator, gps: null, _ => []);
        service.Start();
        service.RequestReload();
        service.DrainCommandQueueForTests();
        Assert.True(service.SnapshotUtc >= DateTime.MinValue);
    }

    [Fact]
    public void MapTimeOffset_shifts_snapshot_utc()
    {
        var orchestrator = CreateMinimalOrchestrator();
        using var service = new LiveTrackingService(orchestrator, gps: null, _ => []);

        service.Start();
        var before = DateTime.UtcNow;
        service.MapTimeOffset = TimeSpan.FromMinutes(30);
        service.RefreshSnapshotSynchronously();
        var after = DateTime.UtcNow;

        Assert.InRange(service.SnapshotUtc, before.AddMinutes(30), after.AddMinutes(30).AddSeconds(1));
    }

    [Fact]
    public void GetLiveNowSnapshot_uses_real_utc_when_map_offset_is_set()
    {
        var orchestrator = CreateMinimalOrchestrator();
        using var service = new LiveTrackingService(orchestrator, gps: null, _ => []);

        service.Start();
        var before = DateTime.UtcNow;
        service.MapTimeOffset = TimeSpan.FromMinutes(15);
        service.RefreshSnapshotSynchronously();
        var after = DateTime.UtcNow;

        Assert.InRange(service.SnapshotUtc, before.AddMinutes(15), after.AddMinutes(15).AddSeconds(1));
        Assert.InRange(service.LiveNowSnapshotUtc, before, after.AddSeconds(1));
        Assert.True((service.SnapshotUtc - service.LiveNowSnapshotUtc).TotalMinutes > 14);
    }

    [Fact]
    public void GetLiveNowSnapshot_aliases_display_snapshot_when_offset_is_zero()
    {
        var orchestrator = CreateMinimalOrchestrator();
        using var service = new LiveTrackingService(orchestrator, gps: null, _ =>
        [
            new SatelliteTrackState
            {
                Name = "TEST",
                NoradId = "1",
                Subpoint = new GeoCoordinate(0, 0, 400)
            }
        ]);

        service.Start();
        service.RefreshSnapshotSynchronously();

        var (display, liveNow) = service.GetSnapshotsForTests();
        Assert.Same(display, liveNow);
    }

    [Fact]
    public void TrackingUtc_falls_back_to_system_clock_when_gps_time_runs_too_fast()
    {
        var orchestrator = CreateMinimalOrchestrator();
        var gps = new StubGpsService
        {
            TrackingUtc = DateTime.UtcNow
        };
        using var service = new LiveTrackingService(orchestrator, gps, _ => []);
        service.Start();

        service.RefreshSnapshotSynchronously();
        var firstSnapshotUtc = service.SnapshotUtc;

        gps.TrackingUtc = gps.TrackingUtc!.Value.AddSeconds(20);
        Thread.Sleep(40);
        var beforeSecond = DateTime.UtcNow;
        service.RefreshSnapshotSynchronously();
        var secondSnapshotUtc = service.SnapshotUtc;
        var afterSecond = DateTime.UtcNow;

        Assert.True((secondSnapshotUtc - firstSnapshotUtc) < TimeSpan.FromSeconds(2));
        Assert.InRange(secondSnapshotUtc, beforeSecond.AddSeconds(-1), afterSecond.AddSeconds(1));
    }

    [Fact]
    public void Published_snapshot_survives_orchestrator_buffer_reuse()
    {
        var satellites = new[]
        {
            new SatelliteCatalogEntry
            {
                Name = "ISS",
                NoradId = "25544",
                Line1 = "1 25544U 98067A   24001.50000000  .00016717  00000-0  10270-3 0  9993",
                Line2 = "2 25544  51.6400 247.4627 0006703 130.5360 325.0288 15.49519779439320"
            }
        };

        var orchestrator = new TrackingOrchestrator(
            new TestSettingsService(),
            new StubTleService(satellites),
            new MinimalPropagator(satellites),
            new StubGroundGeometry(),
            new NullPassPredictor());
        orchestrator.ReloadEnabledSatellites();

        using var service = new LiveTrackingService(orchestrator);
        service.Start();
        service.RefreshSnapshotSynchronously();

        var first = service.GetSnapshot();
        Assert.Single(first);
        var firstNorad = first[0].NoradId;

        // Orchestrator reuses and clears the same list reference on the next tick.
        service.RefreshSnapshotSynchronously();

        Assert.Equal(firstNorad, first[0].NoradId);
        Assert.Single(first);
    }

    [Fact]
    public void GetSnapshot_is_safe_while_worker_updates()
    {
        var orchestrator = CreateMinimalOrchestrator();
        using var service = new LiveTrackingService(orchestrator, gps: null, utc =>
        [
            new SatelliteTrackState
            {
                Name = "TEST",
                NoradId = utc.Second.ToString(),
                Subpoint = new GeoCoordinate(0, 0, 400)
            }
        ]);

        service.Start();
        for (var i = 0; i < 20; i++)
        {
            _ = service.GetSnapshot();
            Thread.Sleep(15);
        }

        service.DrainCommandQueueForTests();
        service.Dispose();
    }

    [Fact]
    public void GetSnapshotWithUtc_pairs_the_states_with_the_time_they_were_propagated_for()
    {
        // Review finding: reading the states and the time separately could pair one tick's states with the next
        // tick's time. Each state here carries the time it was computed for, so a mismatch shows up.
        var orchestrator = CreateMinimalOrchestrator();
        using var service = new LiveTrackingService(orchestrator, gps: null, utc =>
        [
            new SatelliteTrackState
            {
                Name = "TEST",
                NoradId = utc.Ticks.ToString(),
                Subpoint = new GeoCoordinate(0, 0, 400)
            }
        ]);
        service.Start();
        service.RefreshSnapshotSynchronously(); // so the reader has something to read from its first look

        var stop = false;
        long mismatches = 0;
        long reads = 0;
        var reader = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var (states, utc) = service.GetSnapshotWithUtc();
                if (states.Count == 0)
                    continue;

                Interlocked.Increment(ref reads);
                if (states[0].NoradId != utc.Ticks.ToString())
                    Interlocked.Increment(ref mismatches);
            }
        }) { IsBackground = true };
        reader.Start();

        // Keep publishing until the reader has really been running alongside: a busy machine may start it late,
        // so this waits for it rather than assuming it overlaps. Bounded, so a stuck reader fails rather than hangs.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var refreshes = 0;
        while ((refreshes < 400 || Interlocked.Read(ref reads) < 5_000) && DateTime.UtcNow < deadline)
        {
            service.RefreshSnapshotSynchronously();
            service.MapTimeOffset = TimeSpan.FromSeconds(refreshes++); // a changing offset makes every tick's time distinct
        }

        Volatile.Write(ref stop, true);
        Assert.True(reader.Join(TimeSpan.FromSeconds(5)), "the reader did not stop");

        Assert.True(Interlocked.Read(ref reads) >= 5_000, "the reader did not run alongside the refreshes");
        Assert.Equal(0, Interlocked.Read(ref mismatches));
    }

    private static TrackingOrchestrator CreateMinimalOrchestrator()
    {
        var settings = new TestSettingsService();
        var tle = new TleService();
        tle.EnsureLoadedAsync().GetAwaiter().GetResult();
        return new TrackingOrchestrator(
            settings,
            tle,
            new NullOrbitPropagator(),
            new NullGroundGeometry(),
            new NullPassPredictor());
    }

    private sealed class StubTleService(IReadOnlyList<SatelliteCatalogEntry> satellites) : ITleService
    {
        public IReadOnlyList<SatelliteCatalogEntry> Catalog => satellites;
        public DateTime? LastFetchedUtc => DateTime.UtcNow;
        public string CachePath => Path.Combine(Path.GetTempPath(), "live-tracking-tle-test");
        public TleCatalogLoadDiagnostics? LastLoadDiagnostics => null;
        public string ActiveSourceLabel => "test";
        public IReadOnlyList<SatelliteCatalogEntry> GetEnabledSatellites(AppSettings settings) => satellites;
        public Task EnsureLoadedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void InvalidateCatalog() { }
        public bool IsStale(int staleHours) => false;
    }

    private sealed class MinimalPropagator(IReadOnlyList<SatelliteCatalogEntry> satellites) : Core.Orbit.IOrbitPropagator
    {
        private readonly HashSet<string> _ids = satellites.Select(s => s.NoradId).ToHashSet(StringComparer.Ordinal);

        public IReadOnlyCollection<string> LoadedNoradIds => _ids;
        public void Clear() => _ids.Clear();
        public void LoadSatellite(SatelliteCatalogEntry entry) => _ids.Add(entry.NoradId);
        public void RemoveSatellite(string noradId) => _ids.Remove(noradId);
        public bool HasSatellite(string noradId) => _ids.Contains(noradId);
        public GeoCoordinate GetSubpoint(string noradId, DateTime utc) =>
            new(utc.Second * 0.01, utc.Second * 0.01, 400);
        public EciPosition GetEciPosition(string noradId, DateTime utc) => new(7000, 0, 0);
        public LookAngles GetLookAngles(string noradId, GroundStation site, DateTime utc) =>
            new(180, 45, 1000, 0);
    }

    private sealed class StubGroundGeometry : Core.Orbit.IGroundGeometry
    {
        public IReadOnlyList<GeoCoordinate> GetGroundTrack(
            SatelliteCatalogEntry satellite,
            DateTime utcStart,
            DateTime utcEnd,
            TimeSpan step) => [];

        public IReadOnlyList<GeoCoordinate> GetFootprint(
            SatelliteCatalogEntry satellite,
            DateTime utc,
            double minimumElevationDeg) =>
        [
            new(0, 0, 0),
            new(1, 0, 0),
            new(0, 1, 0)
        ];
    }

    private sealed class TestSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string? LoadError => null;
        public bool CanPersist => true;
        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "oscarwatch-live-tracking-test.json");
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

    private sealed class NullOrbitPropagator : Core.Orbit.IOrbitPropagator
    {
        public IReadOnlyCollection<string> LoadedNoradIds { get; } = [];
        public void Clear() { }
        public void LoadSatellite(SatelliteCatalogEntry entry) { }
        public void RemoveSatellite(string noradId) { }
        public bool HasSatellite(string noradId) => false;
        public GeoCoordinate GetSubpoint(string noradId, DateTime utc) => new(0, 0, 0);
        public EciPosition GetEciPosition(string noradId, DateTime utc) => new(0, 0, 0);
        public LookAngles GetLookAngles(string noradId, GroundStation site, DateTime utc) =>
            new(0, 0, 0, 0);
    }

    private sealed class NullGroundGeometry : Core.Orbit.IGroundGeometry
    {
        public IReadOnlyList<GeoCoordinate> GetGroundTrack(
            SatelliteCatalogEntry satellite,
            DateTime utcStart,
            DateTime utcEnd,
            TimeSpan step) => [];

        public IReadOnlyList<GeoCoordinate> GetFootprint(
            SatelliteCatalogEntry satellite,
            DateTime utc,
            double minimumElevationDeg) => [];
    }

    private sealed class NullPassPredictor : Core.Orbit.IPassPredictor
    {
        public Task<IReadOnlyList<PassInfo>> GetPassesAsync(
            SatelliteCatalogEntry satellite,
            GroundStation site,
            DateTime utcStart,
            DateTime utcEnd,
            double minimumElevationDeg,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PassInfo>>([]);
    }

    private sealed class StubGpsService : IGpsService
    {
        public DateTime? TrackingUtc { get; set; }
        public void Update(GpsSettings settings) { }
        public void Disconnect() { }
        public void DisconnectAndWait() { }
        public GpsConnectionStatus GetStatus() => new(false, false, null, null, null, null, null, null);
        public DateTime? GetTrackingUtc() => TrackingUtc;
        public void Dispose() { }
    }
}
