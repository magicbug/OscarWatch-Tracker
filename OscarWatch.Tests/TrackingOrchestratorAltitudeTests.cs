using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

/// <summary>
/// <see cref="SatelliteTrackState.AltitudeKm"/> must reach the UI on every state the
/// orchestrator returns, including states rebuilt to attach a refreshed ground track.
/// </summary>
public sealed class TrackingOrchestratorAltitudeTests
{
    private const string IssNorad = "25544";

    [Fact]
    public void Focused_satellite_state_carries_propagator_altitude()
    {
        var geometry = new CountingGroundGeometry();
        var orchestrator = CreateOrchestrator(geometry);

        var states = orchestrator.GetLiveStates(DateTime.UtcNow, groundTrackNoradId: IssNorad);

        var state = Assert.Single(states);
        Assert.Equal(420.0, state.AltitudeKm, precision: 6);
    }

    [Fact]
    public void Non_focused_satellite_keeps_altitude_when_ground_track_state_is_rebuilt()
    {
        var geometry = new CountingGroundGeometry();
        var orchestrator = CreateOrchestrator(geometry);

        var states = orchestrator.GetLiveStates(DateTime.UtcNow);

        // The non-focused ground track starts stale, so the state is rebuilt with a fresh track.
        // Without this call count the test would not prove the copy path ran.
        Assert.True(geometry.GroundTrackCalls > 0, "Expected the stale-track rebuild path to run.");
        var state = Assert.Single(states);
        Assert.Equal(420.0, state.AltitudeKm, precision: 6);
    }

    [Fact]
    public void Altitude_falls_back_to_tle_when_propagator_reports_none()
    {
        var geometry = new CountingGroundGeometry();
        var orchestrator = CreateOrchestrator(geometry, reportedAltitudeKm: 0);

        var states = orchestrator.GetLiveStates(DateTime.UtcNow, groundTrackNoradId: IssNorad);

        var state = Assert.Single(states);
        Assert.True(state.AltitudeKm > 100, $"Expected a TLE-derived altitude, got {state.AltitudeKm}.");
    }

    private static TrackingOrchestrator CreateOrchestrator(
        CountingGroundGeometry geometry,
        double reportedAltitudeKm = 420.0)
    {
        var orchestrator = new TrackingOrchestrator(
            new TestSettingsService(),
            new StubTleService([SampleSatellite()]),
            new FixedPropagator(reportedAltitudeKm),
            geometry,
            new NullPassPredictor());
        orchestrator.ReloadEnabledSatellites();
        return orchestrator;
    }

    private static SatelliteCatalogEntry SampleSatellite() => new()
    {
        Name = "ISS",
        NoradId = IssNorad,
        Line1 = "1 25544U 98067A   24001.50000000  .00016717  00000-0  10270-3 0  9993",
        Line2 = "2 25544  51.6400 247.4627 0006703 130.5360 325.0288 15.49519779439320"
    };

    private sealed class FixedPropagator(double reportedAltitudeKm) : IOrbitPropagator
    {
        public IReadOnlyCollection<string> LoadedNoradIds { get; } = [IssNorad];

        public void Clear() { }
        public void LoadSatellite(SatelliteCatalogEntry entry) { }
        public void RemoveSatellite(string noradId) { }
        public bool HasSatellite(string noradId) => noradId == IssNorad;

        public GeoCoordinate GetSubpoint(string noradId, DateTime utc) =>
            new(10, 20, reportedAltitudeKm);

        public EciPosition GetEciPosition(string noradId, DateTime utc) =>
            new(6800, 0, 0);

        public LookAngles GetLookAngles(string noradId, GroundStation site, DateTime utc) =>
            new(AzimuthDeg: 90, ElevationDeg: 30, RangeKm: 900);
    }

    private sealed class CountingGroundGeometry : IGroundGeometry
    {
        public int GroundTrackCalls { get; private set; }

        public IReadOnlyList<GeoCoordinate> GetGroundTrack(
            SatelliteCatalogEntry satellite,
            DateTime utcStart,
            DateTime utcEnd,
            TimeSpan step)
        {
            GroundTrackCalls++;
            return [];
        }

        public IReadOnlyList<GeoCoordinate> GetFootprint(
            SatelliteCatalogEntry satellite,
            DateTime utc,
            double minimumElevationDeg) => [];
    }

    private sealed class StubTleService(IReadOnlyList<SatelliteCatalogEntry> satellites) : ITleService
    {
        public IReadOnlyList<SatelliteCatalogEntry> Catalog => satellites;
        public DateTime? LastFetchedUtc => DateTime.UtcNow;
        public string CachePath => Path.Combine(Path.GetTempPath(), "tle-cache-test");
        public TleCatalogLoadDiagnostics? LastLoadDiagnostics => null;
        public string ActiveSourceLabel => "test";
        public IReadOnlyList<SatelliteCatalogEntry> GetEnabledSatellites(AppSettings settings) => satellites;
        public Task EnsureLoadedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void InvalidateCatalog() { }
        public bool IsStale(int staleHours) => false;
    }

    private sealed class NullPassPredictor : IPassPredictor
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

    private sealed class TestSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string? LoadError => null;
        public bool CanPersist => true;
        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "tracking-altitude-test.json");
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
}
