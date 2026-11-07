using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Services;
using OscarWatch.Core.Tle;

namespace OscarWatch.Tests;

public sealed class TrackingOrchestratorPassesTests
{
    [Fact]
    public async Task GetPassesAsync_returns_partial_results_when_one_satellite_fails()
    {
        var aos = DateTime.UtcNow.AddHours(1);
        var goodPass = new PassInfo
        {
            NoradId = "25544",
            SatelliteName = "ISS",
            AosUtc = aos,
            LosUtc = aos.AddMinutes(10),
            MaxElevationUtc = aos.AddMinutes(5),
            MaxElevationDeg = 45
        };

        var enabled = new[]
        {
            new SatelliteCatalogEntry { NoradId = "25544", Name = "ISS", Line1 = "", Line2 = "" },
            new SatelliteCatalogEntry { NoradId = "99999", Name = "BAD", Line1 = "", Line2 = "" }
        };

        var orchestrator = new TrackingOrchestrator(
            new StubSettingsService(),
            new StubTleService(enabled),
            new NoOpPropagator(),
            new NoOpGroundGeometry(),
            new SelectivePassPredictor(
                goodNoradId: "25544",
                goodPasses: [goodPass],
                failingNoradId: "99999"));

        var passes = await orchestrator.GetPassesAsync(
            new GroundStation(),
            minimumElevationDeg: 5,
            predictionHours: 24,
            minimumDurationMinutes: 0);

        Assert.Single(passes);
        Assert.Equal("25544", passes[0].NoradId);
        Assert.Equal(0, passes[0].PaletteIndex);
    }

    [Fact]
    public async Task GetPassesAsync_uses_one_palette_index_per_enabled_satellite()
    {
        var aos = DateTime.UtcNow.AddHours(1);
        PassInfo Make(string id, string name, int hours) => new()
        {
            NoradId = id,
            SatelliteName = name,
            AosUtc = aos.AddHours(hours),
            LosUtc = aos.AddHours(hours).AddMinutes(10),
            MaxElevationUtc = aos.AddHours(hours).AddMinutes(5),
            MaxElevationDeg = 40
        };

        var enabled = new[]
        {
            new SatelliteCatalogEntry { NoradId = "25544", Name = "ISS", Line1 = "", Line2 = "" },
            new SatelliteCatalogEntry { NoradId = "44909", Name = "RS-44", Line1 = "", Line2 = "" }
        };

        var orchestrator = new TrackingOrchestrator(
            new StubSettingsService(),
            new StubTleService(enabled),
            new NoOpPropagator(),
            new NoOpGroundGeometry(),
            new MappedPassPredictor(new Dictionary<string, PassInfo[]>(StringComparer.Ordinal)
            {
                ["25544"] = [Make("25544", "ISS", 2), Make("25544", "ISS", 5)],
                ["44909"] = [Make("44909", "RS-44", 1)]
            }));

        var passes = await orchestrator.GetPassesAsync(
            new GroundStation(),
            minimumElevationDeg: 5,
            predictionHours: 24,
            minimumDurationMinutes: 0);

        Assert.Equal(["44909", "25544", "25544"], passes.Select(p => p.NoradId).ToArray());
        Assert.Equal(1, passes[0].PaletteIndex);
        Assert.Equal(0, passes[1].PaletteIndex);
        Assert.Equal(0, passes[2].PaletteIndex);
    }

    [Fact]
    public async Task GetUpcomingPassesAsync_uses_sidebar_duration_not_planner_duration()
    {
        var aos = DateTime.UtcNow.AddHours(2);
        var shortPass = new PassInfo
        {
            NoradId = "25544",
            SatelliteName = "ISS",
            AosUtc = aos,
            LosUtc = aos.AddMinutes(1.5),
            MaxElevationUtc = aos.AddMinutes(0.75),
            MaxElevationDeg = 70
        };
        var settings = new StubSettingsService();
        settings.Current.UpcomingPassesMinDurationMinutes = 0;
        settings.Current.PassFilterMinDurationMinutes = 10;
        var orchestrator = new TrackingOrchestrator(
            settings,
            new StubTleService([new SatelliteCatalogEntry { NoradId = "25544", Name = "ISS", Line1 = "", Line2 = "" }]),
            new NoOpPropagator(),
            new NoOpGroundGeometry(),
            new MappedPassPredictor(new Dictionary<string, PassInfo[]>(StringComparer.Ordinal)
            {
                ["25544"] = [shortPass]
            }));

        var upcoming = await orchestrator.GetUpcomingPassesAsync();
        Assert.Single(upcoming);

        var planner = await orchestrator.GetPassesAsync(
            new GroundStation(),
            minimumElevationDeg: 5,
            predictionHours: 48,
            minimumDurationMinutes: 10);
        Assert.Empty(planner);
    }

    private sealed class StubSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string? LoadError => null;
        public bool CanPersist => true;
        public string SettingsPath { get; } = "";
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

    private sealed class StubTleService : ITleService
    {
        private readonly IReadOnlyList<SatelliteCatalogEntry> _enabled;

        public StubTleService(IReadOnlyList<SatelliteCatalogEntry> enabled) => _enabled = enabled;

        public IReadOnlyList<SatelliteCatalogEntry> Catalog => _enabled;
        public DateTime? LastFetchedUtc => DateTime.UtcNow;
        public string CachePath => "";
        public TleCatalogLoadDiagnostics? LastLoadDiagnostics => null;
        public bool IsStale(int staleHours) => false;
        public Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureLoadedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void InvalidateCatalog() { }
        public string ActiveSourceLabel => "Test";
        public IReadOnlyList<SatelliteCatalogEntry> GetEnabledSatellites(AppSettings settings) => _enabled;
    }

    private sealed class NoOpPropagator : IOrbitPropagator
    {
        public IReadOnlyCollection<string> LoadedNoradIds => [];
        public void LoadSatellite(SatelliteCatalogEntry entry) { }
        public void RemoveSatellite(string noradId) { }
        public void Clear() { }
        public bool HasSatellite(string noradId) => false;
        public GeoCoordinate GetSubpoint(string noradId, DateTime utc) => new(0, 0, 400);
        public EciPosition GetEciPosition(string noradId, DateTime utc) => new(6778, 0, 0);
        public LookAngles GetLookAngles(string noradId, GroundStation site, DateTime utc) => new(180, 45, 1000, 0);
    }

    private sealed class NoOpGroundGeometry : IGroundGeometry
    {
        public IReadOnlyList<GeoCoordinate> GetGroundTrack(
            SatelliteCatalogEntry satellite, DateTime utcStart, DateTime utcEnd, TimeSpan step) => [];

        public IReadOnlyList<GeoCoordinate> GetFootprint(
            SatelliteCatalogEntry satellite, DateTime utc, double minimumElevationDeg) => [];
    }

    private sealed class SelectivePassPredictor : IPassPredictor
    {
        private readonly string _goodNoradId;
        private readonly IReadOnlyList<PassInfo> _goodPasses;
        private readonly string _failingNoradId;

        public SelectivePassPredictor(
            string goodNoradId,
            IReadOnlyList<PassInfo> goodPasses,
            string failingNoradId)
        {
            _goodNoradId = goodNoradId;
            _goodPasses = goodPasses;
            _failingNoradId = failingNoradId;
        }

        public Task<IReadOnlyList<PassInfo>> GetPassesAsync(
            SatelliteCatalogEntry satellite,
            GroundStation site,
            DateTime utcStart,
            DateTime utcEnd,
            double minimumElevationDeg,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(satellite.NoradId, _failingNoradId, StringComparison.Ordinal))
                return Task.FromException<IReadOnlyList<PassInfo>>(new InvalidOperationException("prediction failed"));

            if (string.Equals(satellite.NoradId, _goodNoradId, StringComparison.Ordinal))
                return Task.FromResult(_goodPasses);

            return Task.FromResult<IReadOnlyList<PassInfo>>([]);
        }
    }

    private sealed class MappedPassPredictor : IPassPredictor
    {
        private readonly Dictionary<string, PassInfo[]> _passes;

        public MappedPassPredictor(Dictionary<string, PassInfo[]> passes) => _passes = passes;

        public Task<IReadOnlyList<PassInfo>> GetPassesAsync(
            SatelliteCatalogEntry satellite,
            GroundStation site,
            DateTime utcStart,
            DateTime utcEnd,
            double minimumElevationDeg,
            CancellationToken cancellationToken = default)
        {
            if (_passes.TryGetValue(satellite.NoradId, out var passes))
                return Task.FromResult<IReadOnlyList<PassInfo>>(passes);

            return Task.FromResult<IReadOnlyList<PassInfo>>([]);
        }
    }
}
