using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using OscarWatch.ViewModels;
using static OscarWatch.Localization.LocalizationCulture;
namespace OscarWatch.Tests;
public class ElementAgeWarningTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
    [Fact]
    public void Whole_days_are_counted_from_the_element_epoch()
    {
        // 2026-10-26 12:00 to 2026-11-16 12:00 is 21 days (31 - 26 + 16), counted by hand, not by the helper.
        var epoch = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(21, ElementAgeWarning.WholeDaysOld(epoch, Now));
        Assert.Equal(20, ElementAgeWarning.WholeDaysOld(epoch.AddHours(1), Now));
    }
    [Fact]
    public void An_unknown_epoch_has_no_age_and_never_warns()
    {
        Assert.Null(ElementAgeWarning.WholeDaysOld(null, Now));
        Assert.False(ElementAgeWarning.IsOldEnough(null, Now, 1));
    }
    [Fact]
    public void An_epoch_in_the_future_counts_as_zero_days()
    {
        Assert.Equal(0, ElementAgeWarning.WholeDaysOld(Now.AddDays(3), Now));
        Assert.False(ElementAgeWarning.IsOldEnough(Now.AddDays(3), Now, 1));
    }
    [Fact]
    public void Warns_from_the_configured_number_of_whole_days()
    {
        var exactly14 = Now.AddDays(-14);
        Assert.True(ElementAgeWarning.IsOldEnough(exactly14, Now, 14));
        Assert.False(ElementAgeWarning.IsOldEnough(exactly14.AddMinutes(1), Now, 14));
        Assert.True(ElementAgeWarning.IsOldEnough(exactly14, Now, 7));
        Assert.False(ElementAgeWarning.IsOldEnough(exactly14, Now, 15));
    }
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    [InlineData(365, 365)]
    [InlineData(9999, 365)]
    public void The_warning_period_is_kept_within_range(int configured, int expected)
    {
        Assert.Equal(expected, ElementAgeWarning.ClampDays(configured));
    }
    [Fact]
    public void An_out_of_range_period_is_applied_as_the_nearest_limit()
    {
        var sevenDaysOld = Now.AddDays(-7);
        Assert.True(ElementAgeWarning.IsOldEnough(sevenDaysOld, Now, 0));
        Assert.False(ElementAgeWarning.IsOldEnough(sevenDaysOld, Now, 9999));
    }
    [Fact]
    public void The_default_period_is_fourteen_days()
    {
        Assert.Equal(14, new AppSettings().ElementAgeWarningDays);
    }
    [Fact]
    public void Settings_without_the_field_get_the_default()
    {
        Assert.True(SettingsService.TryParse("{ \"minimumElevationDeg\": 5 }", out var parsed, out _));
        Assert.Equal(14, parsed.ElementAgeWarningDays);
    }
    [Theory]
    [InlineData(30, 30)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(5000, 365)]
    public void A_saved_period_is_read_back_within_range(int saved, int expected)
    {
        Assert.True(SettingsService.TryParse($"{{ \"elementAgeWarningDays\": {saved} }}", out var parsed, out _));
        Assert.Equal(expected, parsed.ElementAgeWarningDays);
    }
    [Fact]
    public void The_picker_marks_a_satellite_stale_by_the_configured_period()
    {
        using var _ = TestUiCulture.Apply(DefaultLanguage);
        var settings = new PickerSettings();
        var tle = new PickerTle([
            Entry("OLD-SAT", "11111", DateTime.UtcNow.AddDays(-20)),
            Entry("FRESH-SAT", "22222", DateTime.UtcNow.AddDays(-2)),
            Entry("NO-EPOCH", "33333", null)
        ]);
        var vm = new SatellitePickerViewModel(settings, tle, LocalizationService.Instance);
        Assert.True(Assert.Single(vm.Satellites, s => s.Name == "OLD-SAT").IsStale);
        Assert.False(Assert.Single(vm.Satellites, s => s.Name == "FRESH-SAT").IsStale);
        Assert.False(Assert.Single(vm.Satellites, s => s.Name == "NO-EPOCH").IsStale);
        settings.Current.ElementAgeWarningDays = 30;
        vm = new SatellitePickerViewModel(settings, tle, LocalizationService.Instance);
        Assert.False(Assert.Single(vm.Satellites, s => s.Name == "OLD-SAT").IsStale);
        settings.Current.ElementAgeWarningDays = 1;
        vm = new SatellitePickerViewModel(settings, tle, LocalizationService.Instance);
        Assert.True(Assert.Single(vm.Satellites, s => s.Name == "FRESH-SAT").IsStale);
    }
    [Fact]
    public void A_pass_row_shows_the_age_and_clears_it_again()
    {
        using var _ = TestUiCulture.Apply(DefaultLanguage);
        var row = new PassRowViewModel { SatelliteName = "AO-91", NoradId = "43017" };
        Assert.False(row.ShowElementAge);
        row.ApplyElementAge(21, new DateTime(2026, 10, 26, 8, 30, 0, DateTimeKind.Utc));
        Assert.True(row.ShowElementAge);
        Assert.Equal("⚠ 21 d", row.ElementAgeText);
        Assert.Contains("AO-91", row.ElementAgeToolTip);
        Assert.Contains("21", row.ElementAgeToolTip);
        Assert.Contains("2026-10-26", row.ElementAgeToolTip);
        Assert.Contains("AO-91", row.ElementAgeAutomationName);
        row.ClearElementAge();
        Assert.False(row.ShowElementAge);
        Assert.Equal("", row.ElementAgeText);
        Assert.Equal("", row.ElementAgeToolTip);
        Assert.Equal("", row.ElementAgeAutomationName);
    }
    private static SatelliteCatalogEntry Entry(string name, string noradId, DateTime? epoch) => new()
    {
        Name = name,
        NoradId = noradId,
        Line1 = $"1 {noradId}U 00000A   00001.00000000  .00000000  00000-0  00000-0 0  0000",
        Line2 = $"2 {noradId}  00.0000 000.0000 0000000 000.0000 000.0000 15.00000000 00000",
        EpochUtc = epoch
    };
    private sealed class PickerSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string? LoadError => null;
        public bool CanPersist => true;
        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "oscarwatch-element-age-test-settings.json");
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
    private sealed class PickerTle(IReadOnlyList<SatelliteCatalogEntry> catalog) : ITleService
    {
        public IReadOnlyList<SatelliteCatalogEntry> Catalog { get; } = catalog;
        public DateTime? LastFetchedUtc => null;
        public string CachePath => "";
        public TleCatalogLoadDiagnostics? LastLoadDiagnostics => null;
        public string ActiveSourceLabel => "test";
        public bool IsStale(int staleHours) => false;
        public Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureLoadedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void InvalidateCatalog() { }
        public IReadOnlyList<SatelliteCatalogEntry> GetEnabledSatellites(AppSettings settings) => [];
    }
}
