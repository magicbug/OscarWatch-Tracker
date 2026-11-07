using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Core.Tle;
using OscarWatch.Localization;
using OscarWatch.ViewModels;

namespace OscarWatch.Tests;

public sealed class PassPlannerSettingsPersistenceTests
{
    [Fact]
    public async Task SaveFilters_does_not_overwrite_tracking_minimum_elevation_or_pass_window()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ow-planner-{Guid.NewGuid():N}", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var settings = new SettingsService(path);
        settings.Load();
        settings.Current.MinimumElevationDeg = 12;
        settings.Current.PassPredictionHours = 36;
        settings.Current.PassFilterMinDurationMinutes = 10;
        settings.Current.PassPlannerPredictionHours = 72;
        settings.Current.PassPlannerMinElevationDeg = 25;
        settings.EnsureSavedStations();
        await settings.SaveAsync();

        var vm = new PassPlanningViewModel(
            settings,
            new StubTleServiceForPlanner(),
            null!,
            null!,
            LocalizationService.Instance);
        vm.Initialize();
        vm.FilterMinElevationDeg = 45;
        vm.FilterMinDurationMinutes = 15;
        vm.FilterPredictionHours = 96;

        await vm.SaveFiltersAndStationsAsync();

        Assert.Equal(12, settings.Current.MinimumElevationDeg);
        Assert.Equal(36, settings.Current.PassPredictionHours);
        Assert.Equal(45, settings.Current.PassPlannerMinElevationDeg);
        Assert.Equal(15, settings.Current.PassFilterMinDurationMinutes);
        Assert.Equal(96, settings.Current.PassPlannerPredictionHours);
    }

    private sealed class StubTleServiceForPlanner : ITleService
    {
        public IReadOnlyList<SatelliteCatalogEntry> Catalog => [];
        public DateTime? LastFetchedUtc => null;
        public string CachePath => "";
        public TleCatalogLoadDiagnostics? LastLoadDiagnostics => null;
        public bool IsStale(int staleHours) => false;
        public Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureLoadedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void InvalidateCatalog() { }
        public string ActiveSourceLabel => "Test";
        public IReadOnlyList<SatelliteCatalogEntry> GetEnabledSatellites(AppSettings settings) => [];
    }
}
