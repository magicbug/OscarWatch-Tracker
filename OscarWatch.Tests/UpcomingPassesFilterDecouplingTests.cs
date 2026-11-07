using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Services;
using OscarWatch.Core.Tle;

namespace OscarWatch.Tests;

public sealed class UpcomingPassesFilterDecouplingTests
{
    private static readonly SatelliteCatalogEntry Iss = new()
    {
        Name = "ISS (ZARYA)",
        NoradId = "25544",
        Line1 = "1 25544U 98067A   25205.51782528  .00016717  00000+0  10270-3 0  9993",
        Line2 = "2 25544  51.6416 247.4627 0006703 130.5360 325.0288 15.50415322908603"
    };

    [Fact]
    public async Task GetUpcomingPassesAsync_uses_upcoming_min_duration_not_planner_duration()
    {
        var shortPass = MakePass(durationMinutes: 1.5, maxEl: 72);
        var orchestrator = CreateOrchestrator(
            upcomingMinDurationMinutes: 0,
            plannerMinDurationMinutes: 10,
            passesByNorad: new Dictionary<string, PassInfo[]>(StringComparer.Ordinal)
            {
                [Iss.NoradId] = [shortPass]
            });

        var upcoming = await orchestrator.GetUpcomingPassesAsync();

        Assert.Single(upcoming);
        Assert.Equal(Iss.NoradId, upcoming[0].NoradId);

        var plannerFiltered = await orchestrator.GetPassesAsync(
            new GroundStation { LatitudeDeg = 44.12, LongitudeDeg = 27.26 },
            minimumElevationDeg: 5,
            predictionHours: 48,
            minimumDurationMinutes: 10);

        Assert.Empty(plannerFiltered);
    }

    [Fact]
    public async Task GetUpcomingPassesAsync_honours_upcoming_min_duration_when_planner_is_lenient()
    {
        var shortPass = MakePass(durationMinutes: 1.5, maxEl: 72);
        var orchestrator = CreateOrchestrator(
            upcomingMinDurationMinutes: 2,
            plannerMinDurationMinutes: 0,
            passesByNorad: new Dictionary<string, PassInfo[]>(StringComparer.Ordinal)
            {
                [Iss.NoradId] = [shortPass]
            });

        var upcoming = await orchestrator.GetUpcomingPassesAsync();
        Assert.Empty(upcoming);

        var plannerPasses = await orchestrator.GetPassesAsync(
            new GroundStation(),
            minimumElevationDeg: 5,
            predictionHours: 48,
            minimumDurationMinutes: 0);
        Assert.Single(plannerPasses);
    }

    /// <summary>
    /// When tracking minimum elevation is high, predicted passes can be short above the horizon even with a
    /// high peak (e.g. a few minutes above 45° while TCA is still high relative to an open horizon).
    /// Sidebar duration is controlled independently via <see cref="AppSettings.UpcomingPassesMinDurationMinutes"/>.
    /// </summary>
    [Fact]
    public async Task Sidebar_duration_can_hide_short_high_peak_pass_while_zero_duration_shows_it()
    {
        var shortHighPeak = MakePass(durationMinutes: 1.4, maxEl: 68);
        var lenientSidebar = CreateOrchestrator(
            upcomingMinDurationMinutes: 0,
            plannerMinDurationMinutes: 2,
            passesByNorad: new Dictionary<string, PassInfo[]>(StringComparer.Ordinal)
            {
                [Iss.NoradId] = [shortHighPeak]
            });

        var strictSidebar = CreateOrchestrator(
            upcomingMinDurationMinutes: 2,
            plannerMinDurationMinutes: 2,
            passesByNorad: new Dictionary<string, PassInfo[]>(StringComparer.Ordinal)
            {
                [Iss.NoradId] = [shortHighPeak]
            });

        Assert.Single(await lenientSidebar.GetUpcomingPassesAsync());
        Assert.Empty(await strictSidebar.GetUpcomingPassesAsync());
    }

    [Fact]
    public void RematchAndPrune_retains_short_scheduled_pass_when_upcoming_includes_it()
    {
        var aos = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
        var los = aos.AddMinutes(1.5);
        var shortPass = new PassInfo
        {
            NoradId = Iss.NoradId,
            SatelliteName = Iss.Name,
            AosUtc = aos,
            LosUtc = los,
            MaxElevationUtc = aos.AddMinutes(0.75),
            MaxElevationDeg = 70
        };

        var rematched = ScheduledPassReminder.RematchAndPrune(
            [new ScheduledPassEntry { NoradId = Iss.NoradId, AosUtc = aos }],
            [shortPass],
            utcNow: aos.AddMinutes(-30));

        Assert.Single(rematched);
        Assert.Equal(aos, rematched[0].AosUtc);
    }

    private static PassInfo MakePass(double durationMinutes, double maxEl)
    {
        var aos = DateTime.UtcNow.AddHours(2);
        var los = aos.AddMinutes(durationMinutes);
        return new PassInfo
        {
            NoradId = Iss.NoradId,
            SatelliteName = Iss.Name,
            AosUtc = aos,
            LosUtc = los,
            MaxElevationUtc = aos.AddMinutes(durationMinutes / 2),
            MaxElevationDeg = maxEl
        };
    }

    private static TrackingOrchestrator CreateOrchestrator(
        int upcomingMinDurationMinutes,
        int plannerMinDurationMinutes,
        Dictionary<string, PassInfo[]> passesByNorad)
    {
        var settings = new AppSettings
        {
            UpcomingPassesMinDurationMinutes = upcomingMinDurationMinutes,
            PassFilterMinDurationMinutes = plannerMinDurationMinutes,
            EnabledSatelliteNoradIds = [Iss.NoradId],
            EnabledSatelliteNames = [Iss.Name]
        };

        return new TrackingOrchestrator(
            new StubSettings(settings),
            new StubTleService([Iss]),
            new NoOpPropagator(),
            new NoOpGroundGeometry(),
            new MappedPassPredictor(passesByNorad));
    }

    private sealed class StubSettings(AppSettings current) : ISettingsService
    {
        public AppSettings Current { get; } = current;
        public string? LoadError => null;
        public bool CanPersist => true;
        public string SettingsPath { get; } = "";
        public string SerializeCurrent() => "{}";
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
        public Task ReplaceAndSaveAsync(AppSettings imported, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubTleService(IReadOnlyList<SatelliteCatalogEntry> enabled) : ITleService
    {
        public IReadOnlyList<SatelliteCatalogEntry> Catalog => enabled;
        public DateTime? LastFetchedUtc => DateTime.UtcNow;
        public string CachePath => "";
        public TleCatalogLoadDiagnostics? LastLoadDiagnostics => null;
        public bool IsStale(int staleHours) => false;
        public Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureLoadedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void InvalidateCatalog() { }
        public string ActiveSourceLabel => "Test";
        public IReadOnlyList<SatelliteCatalogEntry> GetEnabledSatellites(AppSettings settings) => enabled;
    }

    private sealed class NoOpPropagator : IOrbitPropagator
    {
        public IReadOnlyCollection<string> LoadedNoradIds => [];
        public void LoadSatellite(SatelliteCatalogEntry entry) { }
        public void RemoveSatellite(string noradId) { }
        public void Clear() { }
        public bool HasSatellite(string noradId) => true;
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

    private sealed class MappedPassPredictor(Dictionary<string, PassInfo[]> passes) : IPassPredictor
    {
        public Task<IReadOnlyList<PassInfo>> GetPassesAsync(
            SatelliteCatalogEntry satellite,
            GroundStation site,
            DateTime utcStart,
            DateTime utcEnd,
            double minimumElevationDeg,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PassInfo>>(
                passes.TryGetValue(satellite.NoradId, out var list) ? list : []);
    }
}
