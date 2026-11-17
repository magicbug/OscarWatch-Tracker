using System.Globalization;
using OscarWatch.Core.Eme;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using OscarWatch.ViewModels;

namespace OscarWatch.Tests;

public sealed class EmeViewModelTests
{
    // Moon at azimuth 163.7, elevation 47.5 from London at this time.
    private static readonly DateTime Now = new(2026, 3, 20, 12, 30, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public FakeTracking Tracking { get; } = new();
        public TestSettingsService Settings { get; } = new();
        public EmeViewModel Vm { get; }

        public Harness()
        {
            Settings.Current.GroundStation.LatitudeDeg = 51.5074;
            Settings.Current.GroundStation.LongitudeDeg = -0.1278;
            Settings.Current.GroundStation.AltitudeMetersAsl = 11;
            Settings.Current.DisplayTimesInUtc = true;
            Settings.Current.Use24HourClock = true;
            Vm = new EmeViewModel(Settings, Tracking, new EchoLocalizer(), () => Now);
        }
    }

    [Fact]
    public void Starts_with_no_frame_and_builds_one_on_the_first_refresh()
    {
        var h = new Harness();
        Assert.False(h.Vm.HasFrame);
        h.Vm.Refresh();
        Assert.True(h.Vm.HasFrame);
        Assert.NotNull(h.Vm.Frame);
        Assert.Equal("", h.Vm.EmptyMessage);
        Assert.NotEmpty(h.Vm.StatusSections);
        Assert.Equal(47.5, h.Vm.Frame!.Sample.ElevationDeg, 0.1);
    }

    [Fact]
    public void The_readouts_follow_the_map_time_and_not_the_clock()
    {
        var h = new Harness();
        h.Tracking.SnapshotUtcOverride = Now.AddHours(6);
        h.Vm.Refresh();
        Assert.Equal(Now.AddHours(6), h.Vm.Frame!.TimeUtc);
        h.Tracking.SnapshotUtcOverride = null;
        h.Tracking.MapTimeOffset = TimeSpan.FromHours(3);
        h.Tracking.SnapshotUtcOverride = DateTime.MinValue; // before the first snapshot: clock plus the map offset
        h.Vm.Refresh();
        Assert.Equal(Now.AddHours(3), h.Vm.Frame!.TimeUtc);
    }

    [Fact]
    public void A_moved_station_is_used_without_a_restart()
    {
        var h = new Harness();
        h.Vm.Refresh();
        var london = h.Vm.Frame!.Sample.AzimuthDeg;
        h.Settings.Current.GroundStation.LatitudeDeg = -33.87;
        h.Settings.Current.GroundStation.LongitudeDeg = 151.21;
        h.Vm.Refresh();
        Assert.NotEqual(london, h.Vm.Frame!.Sample.AzimuthDeg);
        Assert.True(h.Vm.Frame.Sample.ElevationDeg < 0);
        Assert.False(h.Vm.IsUp);
    }

    [Fact]
    public void A_station_that_is_not_a_number_shows_the_empty_message_and_never_throws()
    {
        var h = new Harness();
        h.Vm.Refresh();
        h.Settings.Current.GroundStation.LatitudeDeg = double.NaN;
        h.Vm.Refresh();
        Assert.False(h.Vm.HasFrame);
        Assert.Null(h.Vm.Frame);
        Assert.Equal(EmeTextBuilder.KeyEmptyNoPosition, h.Vm.EmptyMessage);
        Assert.Empty(h.Vm.StatusSections);
        Assert.Equal("", h.Vm.PlainText);
        Assert.False(h.Vm.IsUp);
    }

    [Fact]
    public void A_typed_frequency_changes_the_echo_shift_and_is_remembered_for_the_band()
    {
        var h = new Harness();
        h.Vm.Refresh();
        var at144 = h.Vm.Frame!.OwnEchoShiftHz!.Value;
        h.Vm.FrequencyText = "432.065";
        Assert.Equal(432.065, h.Vm.Frame!.FrequencyMHz);
        Assert.Equal(at144 * 432.065 / 144.0, h.Vm.Frame.OwnEchoShiftHz!.Value, 1e-6);
        Assert.Equal(432.065, h.Settings.Current.Eme.FrequencyMHzFor(h.Settings.Current.Eme.SelectedBandId));
        Assert.Equal("", h.Vm.FrequencyError);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-144")]
    [InlineData("29")]
    [InlineData("100001")]
    public void A_frequency_that_is_not_valid_is_reported_and_gives_no_doppler_or_loss(string text)
    {
        var h = new Harness();
        h.Vm.Refresh();
        h.Vm.FrequencyText = "144.1";
        h.Vm.FrequencyText = text;
        Assert.Equal(EmeTextBuilder.KeyFrequencyInvalid, h.Vm.FrequencyError);
        // The saved value is not overwritten by the bad entry, but nothing is worked out from it.
        Assert.Equal(144.1, h.Settings.Current.Eme.FrequencyMHzFor("144"));
        Assert.Null(h.Vm.Frame!.FrequencyMHz);
        Assert.Null(h.Vm.Frame.OwnEchoShiftHz);
        Assert.Null(h.Vm.Frame.AbsolutePathLossDb);
        Assert.DoesNotContain(h.Vm.StatusSections.SelectMany(s => s.Rows), r => r.Label == EmeTextBuilder.KeyLabelOwnEcho);
        Assert.DoesNotContain(h.Vm.StatusSections.SelectMany(s => s.Rows), r => r.Label == EmeTextBuilder.KeyLabelAbsoluteLoss);
        // The distance figure does not depend on the frequency and is still shown.
        Assert.Contains(h.Vm.StatusSections.SelectMany(s => s.Rows), r => r.Label == EmeTextBuilder.KeyLabelExtraLoss);
        // A good entry brings the figures back.
        h.Vm.FrequencyText = "144.2";
        Assert.Equal(144.2, h.Vm.Frame!.FrequencyMHz);
        Assert.NotNull(h.Vm.Frame.OwnEchoShiftHz);
    }

    [Fact]
    public void Changing_the_band_loads_that_bands_frequency_polarisation_and_beamwidth()
    {
        var h = new Harness();
        h.Settings.Current.Eme.FrequencyMHzByBand["1296"] = 1296.065;
        h.Settings.Current.Eme.BeamwidthDegByBand["1296"] = 2.4;
        h.Vm.Refresh();
        Assert.False(h.Vm.IsCircular);
        h.Vm.SelectedBand = EmeBands.Find("1296")!;
        Assert.Equal("1296", h.Settings.Current.Eme.SelectedBandId);
        Assert.Equal(1296.065, double.Parse(h.Vm.FrequencyText, CultureInfo.CurrentCulture));
        Assert.Equal(2.4, double.Parse(h.Vm.BeamwidthText, CultureInfo.CurrentCulture));
        Assert.True(h.Vm.IsCircular);
        Assert.Equal("1296", h.Vm.Frame!.BandId);
        // Back to 144: the polarisation and frequency of that band return.
        h.Vm.SelectedBand = EmeBands.Find("144")!;
        Assert.False(h.Vm.IsCircular);
        Assert.Equal("", h.Vm.BeamwidthText);
    }

    [Fact]
    public void Polarisation_and_large_digits_are_remembered()
    {
        var h = new Harness();
        h.Vm.IsCircular = true;
        h.Vm.LargeDigits = true;
        Assert.True(h.Settings.Current.Eme.IsCircular("144"));
        Assert.True(h.Settings.Current.Eme.LargeDigits);
        var again = new EmeViewModel(h.Settings, h.Tracking, new EchoLocalizer(), () => Now);
        Assert.True(again.IsCircular);
        Assert.True(again.LargeDigits);
    }

    [Fact]
    public void A_beamwidth_is_stored_and_clearing_it_removes_it()
    {
        var h = new Harness();
        h.Vm.BeamwidthText = "12";
        Assert.Equal(12, h.Settings.Current.Eme.BeamwidthFor("144"));
        Assert.Equal(12, h.Vm.Frame!.Sky!.SunFlagAngleDeg);
        h.Vm.BeamwidthText = "";
        Assert.Null(h.Settings.Current.Eme.BeamwidthFor("144"));
        Assert.Equal(10, h.Vm.Frame!.Sky!.SunFlagAngleDeg);
    }

    [Fact]
    public void The_large_digits_show_azimuth_and_elevation_to_one_decimal_place()
    {
        var h = new Harness();
        h.Vm.Refresh();
        Assert.Equal(h.Vm.Frame!.Sample.AzimuthDeg.ToString("0.0", CultureInfo.CurrentCulture), h.Vm.LargeAzimuth);
        Assert.Equal(h.Vm.Frame.Sample.ElevationDeg.ToString("0.0", CultureInfo.CurrentCulture), h.Vm.LargeElevation);
        Assert.Equal(EmeTextBuilder.KeyShortUp, h.Vm.ShortStatus);
        h.Settings.Current.Eme.ElevationLimitDeg = 60;
        h.Vm.Refresh();
        Assert.Equal(EmeTextBuilder.KeyShortDown, h.Vm.ShortStatus);
        Assert.StartsWith(EmeTextBuilder.KeyStatusDown, h.Vm.StatusText);
    }

    [Fact]
    public void The_next_rise_or_set_is_found_and_changes_with_the_limit()
    {
        var h = new Harness();
        h.Vm.Refresh();
        var set = h.Vm.Frame!.Next;
        Assert.NotNull(set);
        Assert.True(set!.IsSet);
        Assert.True(set.TimeUtc > Now);
        h.Settings.Current.Eme.ElevationLimitDeg = 60;
        h.Vm.Refresh();
        Assert.False(h.Vm.Frame!.Next!.IsSet);
    }

    [Fact]
    public void The_screen_reader_summary_changes_only_for_a_material_change()
    {
        var h = new Harness();
        h.Vm.Refresh();
        var first = h.Vm.AccessibleSummary;
        Assert.StartsWith(EmeTextBuilder.KeyAccessibleSummaryUp, first);
        h.Tracking.SnapshotUtcOverride = Now.AddSeconds(30);
        h.Vm.Refresh();
        Assert.Equal(first, h.Vm.AccessibleSummary);
        h.Settings.Current.Eme.ElevationLimitDeg = 60;
        h.Vm.Refresh();
        Assert.StartsWith(EmeTextBuilder.KeyAccessibleSummaryDown, h.Vm.AccessibleSummary);
    }

    [Fact]
    public void The_plain_text_has_a_header_and_a_line_for_each_fact()
    {
        var h = new Harness();
        h.Vm.Refresh();
        Assert.StartsWith(EmeTextBuilder.KeyCopyHeader, h.Vm.PlainText);
        Assert.Contains(EmeTextBuilder.KeyLabelAzimuth, h.Vm.PlainText);
        Assert.Contains(EmeTextBuilder.KeyLabelOwnEcho, h.Vm.PlainText);
    }

    [Fact]
    public void Times_follow_the_choice_of_utc_or_local_and_12_or_24_hours()
    {
        var h = new Harness();
        var t = new DateTime(2026, 3, 20, 15, 5, 0, DateTimeKind.Utc);
        Assert.Equal("Moon.Value.TimeUtc|2026-03-20 15:05", h.Vm.FormatTime(t));
        h.Settings.Current.Use24HourClock = false;
        Assert.Contains("3:05", h.Vm.FormatTime(t));
        Assert.Contains("PM", h.Vm.FormatTime(t).ToUpperInvariant());
        h.Settings.Current.DisplayTimesInUtc = false;
        Assert.DoesNotContain("TimeUtc", h.Vm.FormatTime(t));
    }

    [Fact]
    public void Without_a_radio_or_rotator_nothing_else_is_touched()
    {
        // The window needs only settings, the tracking snapshot time and text. It has no rig or rotator input.
        var ctor = typeof(EmeViewModel).GetConstructors().Single().GetParameters().Select(p => p.ParameterType.Name).ToList();
        Assert.Equal(["ISettingsService", "ILiveTrackingService", "ILocalizationService", "Func`1"], ctor);
    }

    [Fact]
    public void A_stopped_or_disposed_model_does_not_refresh()
    {
        var h = new Harness();
        h.Vm.Dispose();
        h.Vm.Refresh();
        Assert.False(h.Vm.HasFrame);
    }

    private sealed class EchoLocalizer : ILocalizationService
    {
        public string Get(string key) => key;

        public string Get(string key, params object[] args) =>
            key + "|" + string.Join("|", args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture)));
    }

    private sealed class FakeTracking : ILiveTrackingService
    {
        public DateTime SnapshotUtc => SnapshotUtcOverride ?? Now + MapTimeOffset;
        public DateTime? SnapshotUtcOverride { get; set; }
        public TimeSpan MapTimeOffset { get; set; }
        public string? FocusedNoradId { get; set; }
        public DateTime LiveNowSnapshotUtc => Now;
        public IReadOnlyList<SatelliteTrackState> GetSnapshot() => [];
        public IReadOnlyList<SatelliteTrackState> GetLiveNowSnapshot() => [];
        public void Start() { }
        public void RequestReload() { }
        public void Dispose() { }
    }

    private sealed class TestSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string? LoadError => null;
        public bool CanPersist => true;
        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "eme-vm-test.json");
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
