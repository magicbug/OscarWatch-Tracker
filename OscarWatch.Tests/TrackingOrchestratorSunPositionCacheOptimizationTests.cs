using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

/// <summary>
/// Tests for the TrackingOrchestrator sun position caching optimization.
/// Verifies functional equivalence and performance improvement.
/// </summary>
public class TrackingOrchestratorSunPositionCacheOptimizationTests
{
    [Fact]
    public void GetLiveStates_with_same_time_reuses_cached_sun_position()
    {
        // Arrange
        var settings = new TestSettingsService();
        var satellites = new[] { TestSatellites.ISS };
        var sunCalculatorSpy = new SunPositionCalculatorSpy();
        var orchestrator = new TrackingOrchestrator(
            settings,
            new StubTleService(satellites),
            new MinimalPropagator(satellites),
            new TestGroundGeometry(),
            new NullPassPredictor(),
            new NullTrackingDiagnostics(),
            null, // No satellite database
            sunCalculatorSpy);

        orchestrator.ReloadEnabledSatellites();
        var utc = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        // Act - Call GetLiveStates multiple times with the same time
        var states1 = orchestrator.GetLiveStates(utc);
        var states2 = orchestrator.GetLiveStates(utc);
        var states3 = orchestrator.GetLiveStates(utc);

        // Assert - Should get consistent results AND verify cache behavior
        Assert.Single(states1);
        Assert.Single(states2); 
        Assert.Single(states3);
        Assert.Equal(states1[0].IsSunlit, states2[0].IsSunlit);
        Assert.Equal(states2[0].IsSunlit, states3[0].IsSunlit);

        // Most importantly: sun position calculator should only be called once due to caching
        Assert.Equal(1, sunCalculatorSpy.CallCount);
        Assert.Single(sunCalculatorSpy.CalledWithTimes);
        Assert.Equal(utc, sunCalculatorSpy.CalledWithTimes[0]);
    }

    [Fact]
    public void GetLiveStates_with_time_within_cache_duration_reuses_sun_position()
    {
        // Arrange
        var settings = new TestSettingsService();
        var satellites = new[] { TestSatellites.ISS };
        var sunCalculatorSpy = new SunPositionCalculatorSpy();
        var orchestrator = new TrackingOrchestrator(
            settings,
            new StubTleService(satellites),
            new MinimalPropagator(satellites),
            new TestGroundGeometry(),
            new NullPassPredictor(),
            new NullTrackingDiagnostics(),
            null,
            sunCalculatorSpy);

        orchestrator.ReloadEnabledSatellites();
        var utc1 = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var utc2 = utc1.AddSeconds(15); // Within 30-second cache window
        var utc3 = utc1.AddSeconds(29); // Still within cache window

        // Act
        var states1 = orchestrator.GetLiveStates(utc1);
        var states2 = orchestrator.GetLiveStates(utc2);  
        var states3 = orchestrator.GetLiveStates(utc3);

        // Assert - All should work and give consistent illumination for such small time differences
        Assert.Single(states1);
        Assert.Single(states2);
        Assert.Single(states3);
        // Sun position changes so slowly (0.004°/min) that illumination should be identical
        Assert.Equal(states1[0].IsSunlit, states2[0].IsSunlit);
        Assert.Equal(states2[0].IsSunlit, states3[0].IsSunlit);

        // Verify cache behavior: only the first call should compute sun position
        Assert.Equal(1, sunCalculatorSpy.CallCount);
        Assert.Single(sunCalculatorSpy.CalledWithTimes);
        Assert.Equal(utc1, sunCalculatorSpy.CalledWithTimes[0]);
    }

    [Fact]
    public void GetLiveStates_with_time_outside_cache_duration_recalculates_sun_position()
    {
        // Arrange
        var settings = new TestSettingsService();
        var satellites = new[] { TestSatellites.ISS };
        var sunCalculatorSpy = new SunPositionCalculatorSpy();
        var orchestrator = new TrackingOrchestrator(
            settings,
            new StubTleService(satellites),
            new MinimalPropagator(satellites),
            new TestGroundGeometry(),
            new NullPassPredictor(),
            new NullTrackingDiagnostics(),
            null,
            sunCalculatorSpy);

        orchestrator.ReloadEnabledSatellites();
        var utc1 = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var utc2 = utc1.AddSeconds(31); // Outside 30-second cache window

        // Act
        var states1 = orchestrator.GetLiveStates(utc1);
        var states2 = orchestrator.GetLiveStates(utc2);

        // Assert - Both should work (cache invalidation doesn't break functionality)
        Assert.Single(states1);
        Assert.Single(states2);

        // Verify cache invalidation: should be called twice (once for each time outside cache window)
        Assert.Equal(2, sunCalculatorSpy.CallCount);
        Assert.Equal(2, sunCalculatorSpy.CalledWithTimes.Count);
        Assert.Equal(utc1, sunCalculatorSpy.CalledWithTimes[0]);
        Assert.Equal(utc2, sunCalculatorSpy.CalledWithTimes[1]);
    }

    [Fact]
    public void GetLiveStates_handles_multiple_satellites_with_cached_sun_position()
    {
        // Arrange
        var settings = new TestSettingsService();
        var satellites = new[] { TestSatellites.ISS, TestSatellites.SO50, TestSatellites.AO91 };
        var sunCalculatorSpy = new SunPositionCalculatorSpy();
        var orchestrator = new TrackingOrchestrator(
            settings,
            new StubTleService(satellites),
            new MinimalPropagator(satellites),
            new TestGroundGeometry(),
            new NullPassPredictor(),
            new NullTrackingDiagnostics(),
            null,
            sunCalculatorSpy);

        orchestrator.ReloadEnabledSatellites();
        var utc = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var states = orchestrator.GetLiveStates(utc);

        // Assert - All satellites should get illumination calculated with the same cached sun position
        Assert.Equal(3, states.Count);
        Assert.All(states, s => Assert.NotNull(s.Name));
        // All satellites should have illumination calculated (IsSunlit is a bool, never null)

        // Verify cache efficiency: only one sun calculation for multiple satellites
        Assert.Equal(1, sunCalculatorSpy.CallCount);
        Assert.Single(sunCalculatorSpy.CalledWithTimes);
        Assert.Equal(utc, sunCalculatorSpy.CalledWithTimes[0]);
    }

    [Fact]
    public void GetLiveStates_cache_survives_across_different_method_calls()
    {
        // Arrange
        var settings = new TestSettingsService();
        var satellites = new[] { TestSatellites.ISS };
        var sunCalculatorSpy = new SunPositionCalculatorSpy();
        var orchestrator = new TrackingOrchestrator(
            settings,
            new StubTleService(satellites),
            new MinimalPropagator(satellites),
            new TestGroundGeometry(),
            new NullPassPredictor(),
            new NullTrackingDiagnostics(),
            null,
            sunCalculatorSpy);

        orchestrator.ReloadEnabledSatellites();
        var utc = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        // Act - Call other methods between GetLiveStates calls to ensure cache persists
        var states1 = orchestrator.GetLiveStates(utc);
        orchestrator.InvalidateVisualCache(); // This shouldn't clear sun cache
        var states2 = orchestrator.GetLiveStates(utc);

        // Assert - Should still get consistent results
        Assert.Single(states1);
        Assert.Single(states2);
        Assert.Equal(states1[0].IsSunlit, states2[0].IsSunlit);

        // Verify cache persistence: only one calculation despite method call in between
        Assert.Equal(1, sunCalculatorSpy.CallCount);
        Assert.Single(sunCalculatorSpy.CalledWithTimes);
        Assert.Equal(utc, sunCalculatorSpy.CalledWithTimes[0]);
    }

    private sealed class StubTleService(IReadOnlyList<SatelliteCatalogEntry> satellites) : ITleService
    {
        public IReadOnlyList<SatelliteCatalogEntry> Catalog => satellites;
        public DateTime? LastFetchedUtc => DateTime.UtcNow;
        public string CachePath => Path.Combine(Path.GetTempPath(), "tle-sun-cache-test");
        public TleCatalogLoadDiagnostics? LastLoadDiagnostics => null;
        public string ActiveSourceLabel => "test";
        public IReadOnlyList<SatelliteCatalogEntry> GetEnabledSatellites(AppSettings settings) => satellites;
        public Task EnsureLoadedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void InvalidateCatalog() { }
        public bool IsStale(int staleHours) => false;
    }

    private sealed class MinimalPropagator(IReadOnlyList<SatelliteCatalogEntry> satellites) : IOrbitPropagator
    {
        private readonly HashSet<string> _ids = satellites.Select(s => s.NoradId).ToHashSet(StringComparer.Ordinal);

        public IReadOnlyCollection<string> LoadedNoradIds => _ids;
        public void LoadSatellite(SatelliteCatalogEntry satellite) => _ids.Add(satellite.NoradId);
        public void RemoveSatellite(string noradId) => _ids.Remove(noradId);
        public bool HasSatellite(string noradId) => _ids.Contains(noradId);
        public void Clear() => _ids.Clear();
        public LookAngles GetLookAngles(string noradId, GroundStation site, DateTime utc) => new(0, 45, 100, 0);
        public GeoCoordinate GetSubpoint(string noradId, DateTime utc) => new(0, 0, 400);
        public EciPosition GetEciPosition(string noradId, DateTime utc) => new(0, 0, 6800);
    }

    private sealed class TestSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string? LoadError => null;
        public bool CanPersist => true;
        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "sun-cache-test-settings.json");
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

    private sealed class TestGroundGeometry : IGroundGeometry
    {
        public IReadOnlyList<GeoCoordinate> GetGroundTrack(SatelliteCatalogEntry satellite, DateTime utcStart, DateTime utcEnd, TimeSpan step) => [];
        public IReadOnlyList<GeoCoordinate> GetFootprint(SatelliteCatalogEntry satellite, DateTime utc, double minimumElevationDeg = 0) => [];
    }

    private sealed class NullTrackingDiagnostics : ITrackingDiagnostics
    {
        public void LookAnglesSkipped(string noradId, DateTime utc, Exception exception) { }
        public void SatelliteStateSkipped(string noradId, DateTime utc, Exception exception) { }
    }

    private sealed class SunPositionCalculatorSpy : ISunPositionCalculator
    {
        public int CallCount { get; private set; }
        public List<DateTime> CalledWithTimes { get; } = new();

        public EciPosition GetPosition(DateTime utc)
        {
            CallCount++;
            CalledWithTimes.Add(utc);
            return SunPositionCalculator.GetPosition(utc);
        }
    }

    /// <summary>Test satellites for use in tests.</summary>
    private static class TestSatellites
    {
        public static readonly SatelliteCatalogEntry ISS = new()
        {
            NoradId = "25544",
            Name = "ISS (ZARYA)",
            Line1 = "1 25544U 98067A   21001.00000000  .00002182  00000-0  40768-4 0  9990",
            Line2 = "2 25544  51.6461 339.2971 0002829  85.6998 274.4999 15.48919893123456"
        };

        public static readonly SatelliteCatalogEntry SO50 = new()
        {
            NoradId = "27607",
            Name = "SO-50",
            Line1 = "1 27607U 02058C   21001.00000000  .00000123  00000-0  12345-4 0  9998",
            Line2 = "2 27607  64.5551 123.4567 0012345  67.8901 292.1234 14.76543210123456"
        };

        public static readonly SatelliteCatalogEntry AO91 = new()
        {
            NoradId = "43017",
            Name = "AO-91",
            Line1 = "1 43017U 17073E   21001.00000000  .00000456  00000-0  23456-4 0  9997",
            Line2 = "2 43017  97.4567 234.5678 0001234 123.4567  78.9012 15.23456789123456"
        };
    }
}