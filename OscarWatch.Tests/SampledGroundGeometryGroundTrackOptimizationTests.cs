using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Orbit;

namespace OscarWatch.Tests;

/// <summary>
/// Tests for the SampledGroundGeometry.GetGroundTrack optimization.
/// Verifies functional equivalence and correctness of optimized implementation.
/// </summary>
public class SampledGroundGeometryGroundTrackOptimizationTests
{
    [Fact]
    public void GetGroundTrack_produces_correct_point_count_for_various_step_sizes()
    {
        // Arrange
        var propagator = new MinimalPropagator();
        propagator.LoadSatellite(TestSatellites.ISS);
        var geometry = new SampledGroundGeometry(propagator);
        
        var utcStart = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        // Test various step sizes
        var testCases = new[]
        {
            (TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(30)), // 31 points
            (TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10)), // 21 points  
            (TimeSpan.FromSeconds(120), TimeSpan.FromHours(1)),   // 31 points
        };

        foreach (var (step, duration) in testCases)
        {
            var utcEnd = utcStart + duration;
            var expectedPoints = (int)((utcEnd - utcStart).Ticks / step.Ticks) + 1;

            // Act
            var track = geometry.GetGroundTrack(TestSatellites.ISS, utcStart, utcEnd, step);

            // Assert
            Assert.Equal(expectedPoints, track.Count);
            Assert.All(track, point => 
            {
                Assert.InRange(point.LatitudeDeg, -90, 90);
                Assert.InRange(point.LongitudeDeg, -180, 180);
                Assert.True(point.AltitudeKm > 0);
            });
        }
    }

    [Fact]
    public void GetGroundTrack_handles_single_point_case()
    {
        // Arrange
        var propagator = new MinimalPropagator();
        propagator.LoadSatellite(TestSatellites.ISS);
        var geometry = new SampledGroundGeometry(propagator);
        
        var utc = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        // Act - Same start and end time should produce single point
        var track = geometry.GetGroundTrack(TestSatellites.ISS, utc, utc, TimeSpan.FromSeconds(60));

        // Assert
        Assert.Single(track);
        var point = track[0];
        Assert.InRange(point.LatitudeDeg, -90, 90);
        Assert.InRange(point.LongitudeDeg, -180, 180);
        Assert.True(point.AltitudeKm > 0);
    }

    [Fact]
    public void GetGroundTrack_with_propagation_failures_includes_nan_sentinels()
    {
        // Arrange - Use propagator that has the satellite but fails during GetSubpoint
        var propagator = new FailingPropagator(["25544"]); // Fail for ISS
        propagator.LoadSatellite(TestSatellites.ISS); // Load it so HasSatellite returns true
        var geometry = new SampledGroundGeometry(propagator);
        
        var utcStart = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var utcEnd = utcStart.AddMinutes(10);
        var step = TimeSpan.FromMinutes(2);

        // Act
        var track = geometry.GetGroundTrack(TestSatellites.ISS, utcStart, utcEnd, step);

        // Assert - Should have points, but some may be NaN sentinels
        Assert.True(track.Count > 0);
        Assert.Contains(track, p => double.IsNaN(p.LatitudeDeg)); // Should have NaN sentinel(s)
    }

    [Fact]
    public void GetGroundTrack_with_no_failures_produces_all_valid_points()
    {
        // Arrange
        var propagator = new MinimalPropagator();
        propagator.LoadSatellite(TestSatellites.ISS);
        propagator.LoadSatellite(TestSatellites.SO50);
        var geometry = new SampledGroundGeometry(propagator);
        
        var utcStart = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var utcEnd = utcStart.AddMinutes(30);
        var step = TimeSpan.FromMinutes(2);

        // Act
        var issTrack = geometry.GetGroundTrack(TestSatellites.ISS, utcStart, utcEnd, step);
        var so50Track = geometry.GetGroundTrack(TestSatellites.SO50, utcStart, utcEnd, step);

        // Assert
        Assert.True(issTrack.Count > 0);
        Assert.True(so50Track.Count > 0);
        Assert.All(issTrack, p => Assert.False(double.IsNaN(p.LatitudeDeg)));
        Assert.All(so50Track, p => Assert.False(double.IsNaN(p.LatitudeDeg)));
    }

    [Fact] 
    public void GetGroundTrack_with_fallback_orbit_calculation_works()
    {
        // Arrange - Use empty propagator to force fallback to orbit calculation
        var emptyPropagator = new MinimalPropagator();
        var geometry = new SampledGroundGeometry(emptyPropagator);
        
        var utcStart = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var utcEnd = utcStart.AddMinutes(10);
        var step = TimeSpan.FromSeconds(60);

        // Act
        var track = geometry.GetGroundTrack(TestSatellites.ISS, utcStart, utcEnd, step);

        // Assert - Should work via orbit fallback
        Assert.True(track.Count > 0);
        var expectedCount = (int)((utcEnd - utcStart).Ticks / step.Ticks) + 1;
        Assert.Equal(expectedCount, track.Count);
    }

    [Fact]
    public void GetGroundTrack_with_large_step_count_pre_allocates_efficiently()
    {
        // Arrange
        var propagator = new MinimalPropagator();
        propagator.LoadSatellite(TestSatellites.ISS);
        var geometry = new SampledGroundGeometry(propagator);
        
        var utcStart = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var utcEnd = utcStart.AddHours(2); // Longer duration
        var step = TimeSpan.FromSeconds(10); // Smaller step = more points

        // Act
        var track = geometry.GetGroundTrack(TestSatellites.ISS, utcStart, utcEnd, step);

        // Assert - Should handle large number of points efficiently
        var expectedCount = (int)((utcEnd - utcStart).Ticks / step.Ticks) + 1;
        Assert.Equal(expectedCount, track.Count);
        Assert.All(track, p => Assert.False(double.IsNaN(p.LatitudeDeg)));
    }

    [Fact]
    public void GetGroundTrack_with_utcEnd_before_utcStart_returns_empty_list()
    {
        // Arrange
        var propagator = new MinimalPropagator();
        propagator.LoadSatellite(TestSatellites.ISS);
        var geometry = new SampledGroundGeometry(propagator);
        
        var utcStart = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var utcEnd = utcStart.AddMinutes(-30); // End before start
        var step = TimeSpan.FromMinutes(1);

        // Act
        var track = geometry.GetGroundTrack(TestSatellites.ISS, utcStart, utcEnd, step);

        // Assert - Should return empty like original implementation
        Assert.Empty(track);
    }

    [Fact]
    public void GetGroundTrack_with_zero_step_returns_empty_list()
    {
        // Arrange
        var propagator = new MinimalPropagator();
        propagator.LoadSatellite(TestSatellites.ISS);
        var geometry = new SampledGroundGeometry(propagator);
        
        var utcStart = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var utcEnd = utcStart.AddMinutes(10);

        // Act & Assert - Should handle zero and negative steps gracefully
        Assert.Empty(geometry.GetGroundTrack(TestSatellites.ISS, utcStart, utcEnd, TimeSpan.Zero));
        Assert.Empty(geometry.GetGroundTrack(TestSatellites.ISS, utcStart, utcEnd, TimeSpan.FromSeconds(-1)));
    }

    [Fact] 
    public void GetGroundTrack_preserves_original_datetime_kind()
    {
        // Arrange
        var propagator = new DateTimeKindCapturingPropagator();
        propagator.LoadSatellite(TestSatellites.ISS);
        var geometry = new SampledGroundGeometry(propagator);
        
        var utcStart = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Local);
        var utcEnd = utcStart.AddMinutes(5);
        var step = TimeSpan.FromMinutes(1);

        // Act
        var track = geometry.GetGroundTrack(TestSatellites.ISS, utcStart, utcEnd, step);

        // Assert - Should preserve original DateTimeKind
        Assert.True(track.Count > 0);
        Assert.All(propagator.CapturedDateTimes, dt => Assert.Equal(DateTimeKind.Local, dt.Kind));
    }

    private sealed class MinimalPropagator : IOrbitPropagator
    {
        private readonly HashSet<string> _loadedSatellites = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> LoadedNoradIds => _loadedSatellites;
        
        public void LoadSatellite(SatelliteCatalogEntry satellite) => _loadedSatellites.Add(satellite.NoradId);
        public void RemoveSatellite(string noradId) => _loadedSatellites.Remove(noradId);
        public bool HasSatellite(string noradId) => _loadedSatellites.Contains(noradId);
        public void Clear() => _loadedSatellites.Clear();
        
        public LookAngles GetLookAngles(string noradId, GroundStation site, DateTime utc) => new(0, 45, 100, 0);
        
        public GeoCoordinate GetSubpoint(string noradId, DateTime utc)
        {
            // Simulate realistic subpoint movement over time
            var hoursSinceEpoch = (utc - new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalHours;
            var lat = 30.0 * Math.Sin(hoursSinceEpoch * 0.1);
            var lon = (hoursSinceEpoch * 15.0) % 360.0; // 15 degrees per hour
            if (lon > 180) lon -= 360;
            
            return new GeoCoordinate(lat, lon, 400);
        }
        
        public EciPosition GetEciPosition(string noradId, DateTime utc) => new(0, 0, 6800);
    }

    private sealed class FailingPropagator : IOrbitPropagator
    {
        private readonly HashSet<string> _failForNoradIds;
        private readonly HashSet<string> _loadedSatellites = new(StringComparer.Ordinal);

        public FailingPropagator(IEnumerable<string> failForNoradIds)
        {
            _failForNoradIds = failForNoradIds.ToHashSet(StringComparer.Ordinal);
        }

        public IReadOnlyCollection<string> LoadedNoradIds => _loadedSatellites;
        
        public void LoadSatellite(SatelliteCatalogEntry satellite) => _loadedSatellites.Add(satellite.NoradId);
        public void RemoveSatellite(string noradId) => _loadedSatellites.Remove(noradId);
        public bool HasSatellite(string noradId) => _loadedSatellites.Contains(noradId);
        public void Clear() => _loadedSatellites.Clear();
        
        public LookAngles GetLookAngles(string noradId, GroundStation site, DateTime utc)
        {
            if (_failForNoradIds.Contains(noradId))
                throw new InvalidOperationException($"Simulated failure for {noradId}");
            return new LookAngles(0, 45, 100, 0);
        }
        
        public GeoCoordinate GetSubpoint(string noradId, DateTime utc)
        {
            if (_failForNoradIds.Contains(noradId))
                throw new InvalidOperationException($"Simulated failure for {noradId}");
            return new GeoCoordinate(0, 0, 400);
        }
        
        public EciPosition GetEciPosition(string noradId, DateTime utc)
        {
            if (_failForNoradIds.Contains(noradId))
                throw new InvalidOperationException($"Simulated failure for {noradId}");
            return new EciPosition(0, 0, 6800);
        }
    }

    private sealed class DateTimeKindCapturingPropagator : IOrbitPropagator
    {
        private readonly HashSet<string> _loadedSatellites = new(StringComparer.Ordinal);
        
        public IReadOnlyCollection<string> LoadedNoradIds => _loadedSatellites;
        public List<DateTime> CapturedDateTimes { get; } = new();
        
        public void LoadSatellite(SatelliteCatalogEntry satellite) => _loadedSatellites.Add(satellite.NoradId);
        public void RemoveSatellite(string noradId) => _loadedSatellites.Remove(noradId);
        public bool HasSatellite(string noradId) => _loadedSatellites.Contains(noradId);
        public void Clear() => _loadedSatellites.Clear();
        
        public LookAngles GetLookAngles(string noradId, GroundStation site, DateTime utc)
        {
            CapturedDateTimes.Add(utc);
            return new(0, 45, 100, 0);
        }
        
        public GeoCoordinate GetSubpoint(string noradId, DateTime utc)
        {
            CapturedDateTimes.Add(utc);
            
            // Simulate realistic subpoint movement over time
            var hoursSinceEpoch = (utc - new DateTime(2024, 1, 1, 0, 0, 0, utc.Kind)).TotalHours;
            var lat = 30.0 * Math.Sin(hoursSinceEpoch * 0.1);
            var lon = (hoursSinceEpoch * 15.0) % 360.0; // 15 degrees per hour
            if (lon > 180) lon -= 360;
            
            return new GeoCoordinate(lat, lon, 400);
        }
        
        public EciPosition GetEciPosition(string noradId, DateTime utc)
        {
            CapturedDateTimes.Add(utc);
            return new(0, 0, 6800);
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
    }
}