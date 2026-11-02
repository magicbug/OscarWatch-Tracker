using System.ComponentModel;
using System.Globalization;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using OscarWatch.ViewModels;

namespace OscarWatch.Tests;

public sealed class EarthViewViewModelTests
{
    private static readonly DateTime Now = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

    private static SatelliteTrackState State(
        string norad = "25544", string name = "ISS",
        double lat = 10, double lon = 20, double altitudeKm = 420,
        double? elevation = 30, double azimuth = 100,
        IReadOnlyList<GeoCoordinate>? track = null) => new()
    {
        Name = name,
        NoradId = norad,
        Subpoint = new GeoCoordinate(lat, lon, altitudeKm),
        AltitudeKm = altitudeKm,
        LookAngles = elevation is { } el ? new LookAngles(azimuth, el, 900) : null,
        GroundTrack = track ?? []
    };

    private sealed class Harness
    {
        public FakeTracking Tracking { get; } = new();
        public TestSettingsService Settings { get; } = new();
        public EarthViewViewModel Vm { get; }

        public Harness()
        {
            Settings.Current.GroundStation.LatitudeDeg = 10;
            Settings.Current.GroundStation.LongitudeDeg = 20; // directly under a satellite at (10, 20)
            Settings.Current.MinimumElevationDeg = 5;
            Vm = new EarthViewViewModel(Settings, Tracking, new EchoLocalizer(), () => Now);
        }
    }

    [Fact]
    public void Starts_with_no_satellite_and_says_so()
    {
        var h = new Harness();

        Assert.False(h.Vm.HasSatellite);
        Assert.Null(h.Vm.Frame);
        Assert.Equal(EarthViewTextBuilder.KeyEmptyNoSatellite, h.Vm.EmptyMessage);
    }

    [Fact]
    public void No_focused_satellite_shows_the_empty_state()
    {
        var h = new Harness();
        h.Tracking.Snapshot = [State()];
        h.Tracking.FocusedNoradId = null;

        h.Vm.Refresh();

        Assert.False(h.Vm.HasSatellite);
        Assert.Null(h.Vm.Frame);
        Assert.Equal(EarthViewTextBuilder.KeyEmptyNoSatellite, h.Vm.EmptyMessage);
        Assert.Equal("", h.Vm.StatusText);
        Assert.Empty(h.Vm.StatusSections);
    }

    [Fact]
    public void Focused_satellite_missing_from_the_snapshot_shows_a_different_message()
    {
        var h = new Harness();
        h.Tracking.Snapshot = [State("25544")];
        h.Tracking.FocusedNoradId = "43700";

        h.Vm.Refresh();

        Assert.False(h.Vm.HasSatellite);
        Assert.Equal(EarthViewTextBuilder.KeyEmptyNoPosition, h.Vm.EmptyMessage);
    }

    [Fact]
    public void A_focused_satellite_gives_a_frame_and_status_text()
    {
        var h = new Harness();
        h.Tracking.Snapshot = [State()];
        h.Tracking.FocusedNoradId = "25544";

        h.Vm.Refresh();

        Assert.True(h.Vm.HasSatellite);
        Assert.NotNull(h.Vm.Frame);
        Assert.Equal("25544", h.Vm.Frame!.NoradId);
        Assert.Equal("", h.Vm.EmptyMessage);
        Assert.Contains("ISS", h.Vm.StatusText);
        Assert.Contains(EarthViewTextBuilder.KeyValueKilometres + "|420", h.Vm.StatusText);
        Assert.Equal(
            [EarthViewTextBuilder.KeySectionSatellite, EarthViewTextBuilder.KeySectionStation, EarthViewTextBuilder.KeySectionCoverage],
            h.Vm.StatusSections.Select(sec => sec.Heading));
        Assert.Contains(EarthViewTextBuilder.KeyStatusSunlit, h.Vm.StatusText);
    }

    [Fact]
    public void Uses_the_home_station_and_minimum_elevation_from_settings()
    {
        var h = new Harness();
        h.Settings.Current.MinimumElevationDeg = 12;
        h.Tracking.Snapshot = [State()];
        h.Tracking.FocusedNoradId = "25544";

        h.Vm.Refresh();

        Assert.Equal(12, h.Vm.Frame!.MinimumElevationDeg);
        Assert.Equal(EarthViewSituation.Usable, h.Vm.Frame.HomeSituation);
        Assert.Equal(10, h.Vm.Frame.Stations[0].LatitudeDeg);
    }

    [Fact]
    public void Follows_the_focused_satellite_when_it_changes()
    {
        var h = new Harness();
        h.Tracking.Snapshot = [State("25544", "ISS"), State("43700", "QO-100", lat: 0, lon: 25.5, altitudeKm: 35_786, elevation: 40)];
        h.Tracking.FocusedNoradId = "25544";
        h.Vm.Refresh();
        Assert.Equal("ISS", h.Vm.Frame!.SatelliteName);

        h.Tracking.FocusedNoradId = "43700";
        h.Vm.Refresh();

        Assert.Equal("QO-100", h.Vm.Frame!.SatelliteName);
        Assert.Equal(35_786, h.Vm.Frame.Camera.AltitudeKm);
    }

    [Fact]
    public void Display_time_follows_the_map_time_offset()
    {
        var h = new Harness();
        h.Tracking.Snapshot = [State()];
        h.Tracking.FocusedNoradId = "25544";
        h.Tracking.MapTimeOffset = TimeSpan.FromHours(3);

        h.Vm.Refresh();

        Assert.Equal(Now.AddHours(3), h.Vm.Frame!.DisplayUtc);
        Assert.Contains("2026-06-21 15:00:00", h.Vm.StatusText);
    }

    [Fact]
    public void Tiny_movement_does_not_replace_the_frame_but_real_movement_does()
    {
        var h = new Harness();
        h.Tracking.FocusedNoradId = "25544";
        var track = new List<GeoCoordinate> { new(0, 0), new(0, 1) };
        h.Tracking.Snapshot = [State(track: track)];
        h.Vm.Refresh();
        var first = h.Vm.Frame;

        h.Tracking.Snapshot = [State(lat: 10.001, track: track)];
        h.Vm.Refresh();
        Assert.Same(first, h.Vm.Frame);

        h.Tracking.Snapshot = [State(lat: 10.05, track: track)];
        h.Vm.Refresh();
        Assert.NotSame(first, h.Vm.Frame);
    }

    [Fact]
    public void A_geostationary_satellite_gives_a_steady_frame_and_needs_no_pass_data()
    {
        var h = new Harness();
        h.Settings.Current.GroundStation.LatitudeDeg = 51.5;
        h.Settings.Current.GroundStation.LongitudeDeg = -0.1;
        h.Tracking.FocusedNoradId = "43700";
        var track = new List<GeoCoordinate>();
        h.Tracking.Snapshot = [State("43700", "QO-100", lat: 0, lon: 25.5, altitudeKm: 35_786, elevation: 28, track: track)];
        h.Vm.Refresh();
        var first = h.Vm.Frame;

        // The propagated position drifts by a few thousandths of a degree between ticks.
        h.Tracking.Snapshot = [State("43700", "QO-100", lat: 0.002, lon: 25.5004, altitudeKm: 35_786.1, elevation: 28, track: track)];
        h.Vm.Refresh();

        Assert.Same(first, h.Vm.Frame);
        Assert.Equal(EarthViewSituation.Usable, h.Vm.Frame!.HomeSituation);
    }

    [Fact]
    public void An_unusable_altitude_with_no_earlier_frame_shows_the_empty_state()
    {
        var h = new Harness();
        h.Tracking.FocusedNoradId = "25544";
        h.Tracking.Snapshot = [State(altitudeKm: 0)];

        h.Vm.Refresh();

        Assert.False(h.Vm.HasSatellite);
        Assert.Equal(EarthViewTextBuilder.KeyEmptyNoPosition, h.Vm.EmptyMessage);
    }

    [Fact]
    public void An_unusable_altitude_after_a_good_frame_keeps_that_frame()
    {
        var h = new Harness();
        h.Tracking.FocusedNoradId = "25544";
        h.Tracking.Snapshot = [State()];
        h.Vm.Refresh();
        var good = h.Vm.Frame;

        h.Tracking.Snapshot = [State(altitudeKm: 0)];
        h.Vm.Refresh();

        Assert.Same(good, h.Vm.Frame);
        Assert.True(h.Vm.HasSatellite);
    }

    [Fact]
    public void The_accessible_summary_changes_only_on_material_change()
    {
        var h = new Harness();
        h.Tracking.FocusedNoradId = "25544";
        h.Tracking.Snapshot = [State(elevation: 30, azimuth: 100)];
        h.Vm.Refresh();
        var first = h.Vm.AccessibleSummary;
        Assert.Contains(EarthViewTextBuilder.KeyAccessibleUsable, first);

        var summaryChanges = 0;
        h.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EarthViewViewModel.AccessibleSummary))
                summaryChanges++;
        };

        // Half a degree of elevation and some azimuth movement: not material.
        h.Tracking.Snapshot = [State(elevation: 30.5, azimuth: 130)];
        h.Vm.Refresh();
        Assert.Equal(first, h.Vm.AccessibleSummary);
        Assert.Equal(0, summaryChanges);

        // A full step of elevation: material.
        h.Tracking.Snapshot = [State(elevation: 32, azimuth: 130)];
        h.Vm.Refresh();
        Assert.NotEqual(first, h.Vm.AccessibleSummary);
        Assert.Equal(1, summaryChanges);
    }

    [Fact]
    public void The_summary_changes_when_the_station_falls_below_the_horizon()
    {
        var h = new Harness();
        h.Tracking.FocusedNoradId = "25544";
        h.Tracking.Snapshot = [State()];
        h.Vm.Refresh();

        h.Tracking.Snapshot = [State(lat: -60, lon: 160, elevation: -20)];
        h.Vm.Refresh();

        Assert.Equal(EarthViewSituation.BelowHorizon, h.Vm.Frame!.HomeSituation);
        Assert.Contains(EarthViewTextBuilder.KeyAccessibleBelowHorizon, h.Vm.AccessibleSummary);
        Assert.Contains(EarthViewTextBuilder.KeyStatusBelowHorizon, h.Vm.StatusText);
    }

    [Fact]
    public void Property_changed_is_raised_for_the_frame_only_when_it_is_replaced()
    {
        var h = new Harness();
        h.Tracking.FocusedNoradId = "25544";
        var track = new List<GeoCoordinate> { new(0, 0), new(0, 1) };
        h.Tracking.Snapshot = [State(track: track)];
        var frameChanges = 0;
        h.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EarthViewViewModel.Frame))
                frameChanges++;
        };

        h.Vm.Refresh();
        h.Vm.Refresh();
        h.Vm.Refresh();

        Assert.Equal(1, frameChanges);
    }

    [Fact]
    public void Losing_the_focus_clears_the_frame_again()
    {
        var h = new Harness();
        h.Tracking.FocusedNoradId = "25544";
        h.Tracking.Snapshot = [State()];
        h.Vm.Refresh();
        Assert.NotNull(h.Vm.Frame);

        h.Tracking.FocusedNoradId = null;
        h.Vm.Refresh();

        Assert.Null(h.Vm.Frame);
        Assert.False(h.Vm.HasSatellite);
        Assert.Equal(EarthViewTextBuilder.KeyEmptyNoSatellite, h.Vm.AccessibleSummary);
    }

    [Fact]
    public void A_failing_tracking_service_does_not_throw_or_change_the_state()
    {
        var h = new Harness();
        h.Tracking.FocusedNoradId = "25544";
        h.Tracking.Snapshot = [State()];
        h.Vm.Refresh();
        var good = h.Vm.Frame;

        h.Tracking.ThrowOnSnapshot = true;
        h.Vm.Refresh();

        Assert.Same(good, h.Vm.Frame);
    }

    [Fact]
    public void After_dispose_refresh_and_start_do_nothing()
    {
        var h = new Harness();
        h.Tracking.FocusedNoradId = "25544";
        h.Tracking.Snapshot = [State()];
        h.Vm.Dispose();

        h.Vm.Refresh();
        h.Vm.Start(); // would create a dispatcher timer if it were not disposed
        h.Vm.Dispose();

        Assert.Null(h.Vm.Frame);
    }

    [Fact]
    public void Stop_before_start_is_harmless()
    {
        var h = new Harness();

        h.Vm.Stop();
        h.Vm.Stop();
    }

    [Fact]
    public void Status_text_has_no_look_angle_line_without_look_angles()
    {
        var h = new Harness();
        h.Tracking.FocusedNoradId = "25544";
        h.Tracking.Snapshot = [State(elevation: null)];

        h.Vm.Refresh();

        Assert.DoesNotContain(EarthViewTextBuilder.KeySectionStation, h.Vm.StatusText);
        Assert.DoesNotContain(h.Vm.StatusSections, sec => sec.Heading == EarthViewTextBuilder.KeySectionStation);
        Assert.Contains(EarthViewTextBuilder.KeyAccessibleNoAngles, h.Vm.AccessibleSummary);
    }

    [Fact]
    public void Status_text_reports_the_ring_state_for_each_situation()
    {
        var h = new Harness();
        h.Tracking.FocusedNoradId = "25544";

        h.Tracking.Snapshot = [State()]; // directly overhead
        h.Vm.Refresh();
        Assert.Contains(EarthViewTextBuilder.KeyStatusInsideMinElevation + "|5", h.Vm.StatusText);

        var horizon = GlobeViewMath.FootprintRadiusDeg(420, 0);
        var ring = GlobeViewMath.FootprintRadiusDeg(420, 5);
        var (lat, lon) = GlobeViewMath.Destination(10, 20, (ring + horizon) / 2, 90);
        h.Settings.Current.GroundStation.LatitudeDeg = lat;
        h.Settings.Current.GroundStation.LongitudeDeg = lon;
        h.Vm.Refresh();
        Assert.Contains(EarthViewTextBuilder.KeyStatusOutsideMinElevation + "|5", h.Vm.StatusText);
        Assert.Contains(EarthViewTextBuilder.KeyStatusAboveHorizon, h.Vm.StatusText);
    }

    /// <summary>Returns the key, followed by each argument, so tests can see what was asked for.</summary>
    private sealed class EchoLocalizer : ILocalizationService
    {
        public string Get(string key) => key;

        public string Get(string key, params object[] args) =>
            key + "|" + string.Join("|", args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void The_displayed_time_is_the_time_the_snapshot_was_propagated_for()
    {
        // Review finding: the clock plus the map offset can differ from the snapshot's own time, for
        // example after a GPS time correction, so the label and night shading would disagree with the position.
        var h = new Harness();
        h.Tracking.FocusedNoradId = "25544";
        h.Tracking.Snapshot = [State()];
        var propagatedFor = Now.AddSeconds(-7).AddMinutes(90);
        h.Tracking.SnapshotUtcOverride = propagatedFor;

        h.Vm.Refresh();

        Assert.Equal(propagatedFor, h.Vm.Frame!.DisplayUtc);
    }

    [Fact]
    public void Before_the_first_snapshot_the_time_falls_back_to_the_clock_and_map_offset()
    {
        var h = new Harness();
        h.Tracking.FocusedNoradId = "25544";
        h.Tracking.Snapshot = [State()];
        h.Tracking.MapTimeOffset = TimeSpan.FromHours(2);
        h.Tracking.SnapshotUtcOverride = DateTime.MinValue;

        h.Vm.Refresh();

        Assert.Equal(Now.AddHours(2), h.Vm.Frame!.DisplayUtc);
    }

    private sealed class FakeTracking : ILiveTrackingService
    {
        public IReadOnlyList<SatelliteTrackState> Snapshot { get; set; } = [];
        public bool ThrowOnSnapshot { get; set; }

        /// <summary>Like the real service: the time the snapshot was propagated for, including the map offset.</summary>
        public DateTime SnapshotUtc => SnapshotUtcOverride ?? Now + MapTimeOffset;
        public DateTime? SnapshotUtcOverride { get; set; }
        public TimeSpan MapTimeOffset { get; set; }
        public string? FocusedNoradId { get; set; }
        public DateTime LiveNowSnapshotUtc => Now;

        public IReadOnlyList<SatelliteTrackState> GetSnapshot() =>
            ThrowOnSnapshot ? throw new InvalidOperationException("tracking failed") : Snapshot;

        public IReadOnlyList<SatelliteTrackState> GetLiveNowSnapshot() => Snapshot;
        public void Start() { }
        public void RequestReload() { }
        public void Dispose() { }
    }

    private sealed class TestSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string? LoadError => null;
        public bool CanPersist => true;
        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "earth-view-vm-test.json");
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
