using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using OscarWatch.Orbit;
using OscarWatch.ViewModels;
using static OscarWatch.Localization.LocalizationCulture;

namespace OscarWatch.Tests;

public class DxStationOverlayViewModelTests
{
    private static readonly SatelliteCatalogEntry IssEntry = new()
    {
        Name = "ISS (ZARYA)",
        NoradId = "25544",
        Line1 = "1 25544U 98067A   25205.51782528  .00016717  00000+0  10270-3 0  9993",
        Line2 = "2 25544  51.6416 247.4627 0006703 130.5360 325.0288 15.50415322908603"
    };

    [Fact]
    public void Valid_grid_shows_active_overlay_and_collapsed_summary()
    {
        using var _ = TestUiCulture.Apply(DefaultLanguage);

        var settings = new TestSettingsService();
        var inner = new PublicOrbitToolsPropagator();
        inner.LoadSatellite(IssEntry);
        var propagator = new DelegatingOrbitPropagator(inner)
        {
            GetLookAnglesHandler = (_, _, _) => new LookAngles(180, 45, 800, 0)
        };

        var vm = new DxStationOverlayViewModel(settings, propagator, LocalizationService.Instance);
        vm.GridSquare = "JO22";

        Assert.True(vm.IsActive);
        Assert.True(vm.IsPanelOpen);
        Assert.NotNull(vm.RemoteCoordinate);
        vm.Update(new SatelliteTrackState
        {
            Name = "ISS (ZARYA)",
            NoradId = IssEntry.NoradId,
            Subpoint = new GeoCoordinate(0, 0),
            LookAngles = new LookAngles(180, 45, 800, 0)
        });

        Assert.Equal("180.0°", vm.AzimuthText);
        Assert.Equal("45.0°", vm.ElevationText);
        var expectedSummary = LocalizationService.Instance.Get("Dx.Collapsed.AzEl", "JO22", vm.AzimuthText, vm.ElevationText);
        Assert.Equal(expectedSummary, vm.CollapsedSummaryText);
    }

    [Fact]
    public void Clear_target_hides_overlay()
    {
        var settings = new TestSettingsService();
        var vm = new DxStationOverlayViewModel(settings, new PublicOrbitToolsPropagator(), LocalizationService.Instance);
        vm.GridSquare = "FN20";
        Assert.True(vm.IsActive);

        vm.ClearTargetCommand.Execute(null);

        Assert.False(vm.IsActive);
        Assert.False(vm.IsPanelOpen);
        Assert.Equal("", settings.Current.RemoteStationGridSquare);
    }

    [Fact]
    public void OpenFromMapIcon_shows_entry_form_when_no_grid()
    {
        var settings = new TestSettingsService();
        var vm = new DxStationOverlayViewModel(settings, new PublicOrbitToolsPropagator(), LocalizationService.Instance);

        Assert.False(vm.IsPanelOpen);
        vm.OpenFromMapIconCommand.Execute(null);

        Assert.True(vm.IsPanelOpen);
        Assert.False(vm.IsCollapsed);
    }

    [Fact]
    public void OpenFromMapIcon_shows_collapsed_monitor_when_grid_saved()
    {
        var settings = new TestSettingsService { Current = { RemoteStationGridSquare = "FN20" } };
        var vm = new DxStationOverlayViewModel(settings, new PublicOrbitToolsPropagator(), LocalizationService.Instance);

        Assert.True(vm.IsActive);
        Assert.False(vm.IsPanelOpen);
        vm.OpenFromMapIconCommand.Execute(null);

        Assert.True(vm.IsPanelOpen);
        Assert.True(vm.IsCollapsed);
    }

    [Fact]
    public void OpenFromMapIcon_toggles_panel_without_clearing_grid()
    {
        using var _ = TestUiCulture.Apply(DefaultLanguage);

        var settings = new TestSettingsService { Current = { RemoteStationGridSquare = "JO22" } };
        var vm = new DxStationOverlayViewModel(settings, new PublicOrbitToolsPropagator(), LocalizationService.Instance);

        vm.OpenFromMapIconCommand.Execute(null);
        Assert.True(vm.IsPanelOpen);
        Assert.Equal(
            LocalizationService.Instance.Get("Dx.MapLauncher.Hide"),
            vm.MapLauncherToolTip);

        vm.OpenFromMapIconCommand.Execute(null);
        Assert.False(vm.IsPanelOpen);
        Assert.True(vm.IsActive);
        Assert.Equal("JO22", vm.GridSquare);
        Assert.Equal("JO22", settings.Current.RemoteStationGridSquare);
        Assert.Equal(
            LocalizationService.Instance.Get("Dx.MapLauncher.Active"),
            vm.MapLauncherToolTip);

        vm.OpenFromMapIconCommand.Execute(null);
        Assert.True(vm.IsPanelOpen);
        Assert.True(vm.IsActive);
        Assert.Equal("JO22", vm.GridSquare);
    }

    [Fact]
    public void ToggleCollapse_persists_dx_overlay_state()
    {
        var settings = new TestSettingsService();
        var vm = new DxStationOverlayViewModel(settings, new PublicOrbitToolsPropagator(), LocalizationService.Instance)
        {
            GridSquare = "IO91"
        };

        Assert.True(vm.IsCollapsed);
        vm.ToggleCollapseCommand.Execute(null);
        Assert.False(vm.IsCollapsed);
        Assert.False(settings.Current.DxOverlayCollapsed);

        vm.ToggleCollapseCommand.Execute(null);
        Assert.True(settings.Current.DxOverlayCollapsed);
    }

    [Fact]
    public void Remote_site_look_angles_differ_from_local_site()
    {
        var propagator = new PublicOrbitToolsPropagator();
        propagator.LoadSatellite(IssEntry);

        var local = new GroundStation
        {
            LatitudeDeg = 51.5,
            LongitudeDeg = -0.1,
            AltitudeMetersAsl = 50
        };
        var remote = new GroundStation
        {
            LatitudeDeg = 40.7,
            LongitudeDeg = -74.0,
            AltitudeMetersAsl = 50
        };

        var utc = FindIssPassUtc(propagator, local);
        var localLook = propagator.GetLookAngles(IssEntry.NoradId, local, utc);
        var remoteLook = propagator.GetLookAngles(IssEntry.NoradId, remote, utc);

        Assert.True(localLook.ElevationDeg > 10);
        Assert.NotEqual(localLook.AzimuthDeg, remoteLook.AzimuthDeg, 1);
    }

    private static DateTime FindIssPassUtc(PublicOrbitToolsPropagator propagator, GroundStation site)
    {
        var start = new DateTime(2026, 5, 23, 0, 0, 0, DateTimeKind.Utc);
        var bestEl = double.MinValue;
        var bestUtc = start;

        for (var i = 0; i < 86_400; i += 15)
        {
            var t = start.AddSeconds(i);
            var look = propagator.GetLookAngles(IssEntry.NoradId, site, t);
            if (look.ElevationDeg > bestEl)
            {
                bestEl = look.ElevationDeg;
                bestUtc = t;
            }
        }

        Assert.True(bestEl >= 15, $"Expected a usable ISS pass over the test site (best el {bestEl:F1}°).");
        return bestUtc;
    }

    private sealed class TestSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string? LoadError => null;
        public bool CanPersist => true;
        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "oscarwatch-dx-test-settings.json");
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
