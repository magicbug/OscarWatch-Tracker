using OscarWatch.Core.Display;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using OscarWatch.ViewModels;

namespace OscarWatch.Tests;

public class PassbandIndicatorTests
{
    private static SatelliteRadioEntry CreateRs44Entry() =>
        new()
        {
            Name = "RS-44",
            NoradId = "44909",
            DownlinkMinKHz = 435_610,
            DownlinkMaxKHz = 435_670,
            UplinkMinKHz = 145_935,
            UplinkMaxKHz = 145_995,
            Modes =
            [
                new SatelliteTransponderMode
                {
                    Type = "SSB Transponder",
                    DownlinkKHz = 435_667,
                    UplinkKHz = 145_937.61,
                    DownlinkMode = "USB",
                    UplinkMode = "LSB",
                    Doppler = "REV"
                },
                new SatelliteTransponderMode
                {
                    Type = "FT4",
                    DownlinkKHz = 435_611,
                    UplinkKHz = 145_993.61,
                    DownlinkMode = "DATA-USB",
                    UplinkMode = "DATA-LSB",
                    Doppler = "REV"
                },
                new SatelliteTransponderMode
                {
                    Type = "CW TLM",
                    DownlinkKHz = 435_605,
                    UplinkKHz = 0,
                    DownlinkMode = "CW",
                    UplinkMode = "CW",
                    Doppler = "REV"
                }
            ]
        };

    [Fact]
    public void Rs44_satellite_level_bounds_resolve_spots_for_ft4_and_cw_tlm()
    {
        var rs44 = CreateRs44Entry();
        var ssb = rs44.Modes[0];

        var state = PassbandIndicatorCalculator.Build(rs44, ssb, rs44.Modes, 435_640, 145_964.61);

        Assert.NotNull(state);
        Assert.Equal(60.0, state.WidthKHz, 3);
        Assert.True(state.IsInverting);
        Assert.Equal(PassbandZone.InBand, state.Zone);
        Assert.InRange(state.OffsetFromCentreKHz, -0.001, 0.001);
        Assert.InRange(state.DownlinkMarkerPosition, 0.499, 0.501);

        Assert.Equal(2, state.Spots.Count);
        Assert.Equal("CW TLM", state.Spots[0].Label);
        Assert.True(state.Spots[0].IsBeacon);
        Assert.True(state.Spots[0].NormalizedPosition < PassbandIndicatorCalculator.GutterFraction);

        Assert.Equal("FT4", state.Spots[1].Label);
        Assert.False(state.Spots[1].IsBeacon);
        Assert.NotNull(state.Spots[1].SpanNormalized);
        Assert.True(state.Spots[1].NormalizedPosition > PassbandIndicatorCalculator.GutterFraction);
    }

    [Fact]
    public void Rs44_ft4_mode_inherits_satellite_passband_while_beacon_hides_it()
    {
        var rs44 = CreateRs44Entry();
        var ft4 = rs44.Modes[1];
        var beacon = rs44.Modes[2];

        var ft4State = PassbandIndicatorCalculator.Build(rs44, ft4, rs44.Modes, 435_611, 145_993.61);
        var beaconState = PassbandIndicatorCalculator.Build(rs44, beacon, rs44.Modes, 435_605, null);

        Assert.NotNull(ft4State);
        Assert.Equal(PassbandZone.NearEdge, ft4State.Zone);
        Assert.Null(beaconState);
    }

    [Fact]
    public void Passband_detects_near_edge_and_out_of_band_deltas()
    {
        var rs44 = CreateRs44Entry();
        var ssb = rs44.Modes[0];

        var nearHigh = PassbandIndicatorCalculator.Build(rs44, ssb, rs44.Modes, 435_668, 145_936.61);
        var above = PassbandIndicatorCalculator.Build(rs44, ssb, rs44.Modes, 435_674, 145_930.61);
        var below = PassbandIndicatorCalculator.Build(rs44, ssb, rs44.Modes, 435_606, 145_998.61);

        Assert.NotNull(nearHigh);
        Assert.Equal(PassbandZone.NearEdge, nearHigh.Zone);

        Assert.NotNull(above);
        Assert.Equal(PassbandZone.OutOfBand, above.Zone);
        Assert.Equal(4.0, above.OutOfBandDeltaKHz, 3);

        Assert.NotNull(below);
        Assert.Equal(PassbandZone.OutOfBand, below.Zone);
        Assert.Equal(-4.0, below.OutOfBandDeltaKHz, 3);
    }

    [Fact]
    public void Database_file_round_trips_satellite_level_passband_bounds()
    {
        var json = SatelliteDatabaseFile.SerializeEntries([CreateRs44Entry()]);
        var loaded = SatelliteDatabaseFile.ParseJson(json);

        var rs44 = Assert.Single(loaded);
        Assert.Equal(435_610, rs44.DownlinkMinKHz);
        Assert.Equal(435_670, rs44.DownlinkMaxKHz);
        Assert.Equal(145_935, rs44.UplinkMinKHz);
        Assert.Equal(145_995, rs44.UplinkMaxKHz);
    }

    [Fact]
    public void Frequency_overlay_viewmodel_tracks_satellite_frame_position_independently_of_doppler()
    {
        var settings = new TestSettingsService();
        var database = new TestSatelliteDatabaseService([CreateRs44Entry()]);
        var vm = new FrequencyOverlayViewModel(settings, database, LocalizationService.Instance);

        vm.Update(new SatelliteTrackState
        {
            Name = "RS-44",
            NoradId = "44909",
            Subpoint = new GeoCoordinate(57, 18),
            LookAngles = new LookAngles(180, 25, 800, -4.5)
        });

        Assert.True(vm.ShowPassbandIndicator);
        Assert.NotNull(vm.PassbandState);
        Assert.Contains("60 kHz", vm.PassbandBadgeText);
        Assert.True(vm.ShowPassbandUplinkScale);
        Assert.Equal("↑ 145.995", vm.PassbandUplinkLeftText);
        Assert.Equal("145.935 ↑", vm.PassbandUplinkRightText);

        var posAtAos = vm.PassbandState!.DownlinkMarkerPosition;

        // Same tuning, different Doppler (LOS) -> marker stays steady in satellite frame
        vm.Update(new SatelliteTrackState
        {
            Name = "RS-44",
            NoradId = "44909",
            Subpoint = new GeoCoordinate(57, 18),
            LookAngles = new LookAngles(180, 25, 800, +4.5)
        });

        Assert.Equal(posAtAos, vm.PassbandState!.DownlinkMarkerPosition, 6);

        // Tuning RX offset down by 10 kHz moves marker left toward centre
        vm.ReceiveOffsetKHz = -10.0;
        Assert.True(vm.PassbandState!.DownlinkMarkerPosition < posAtAos);
        Assert.True(vm.PassbandIsInBand);
    }

    [Fact]
    public void Passband_diagram_can_be_hidden_and_the_choice_is_remembered()
    {
        var settings = new TestSettingsService();
        settings.Current.ShowFrequencyPassband = true;
        var database = new TestSatelliteDatabaseService([CreateRs44Entry()]);
        var vm = new FrequencyOverlayViewModel(settings, database, LocalizationService.Instance);
        vm.Update(Rs44Track());

        Assert.True(vm.ShowPassbandIndicator);
        Assert.False(vm.ShowPassbandReveal);

        vm.HidePassbandCommand.Execute(null);

        Assert.False(settings.Current.ShowFrequencyPassband);
        Assert.False(vm.ShowPassbandIndicator);
        Assert.True(vm.ShowPassbandReveal);
        Assert.NotNull(vm.PassbandState);

        vm.ShowPassbandCommand.Execute(null);

        Assert.True(settings.Current.ShowFrequencyPassband);
        Assert.True(vm.ShowPassbandIndicator);
        Assert.False(vm.ShowPassbandReveal);
    }

    [Fact]
    public void Hidden_passband_stays_hidden_when_the_panel_is_reopened()
    {
        var settings = new TestSettingsService();
        settings.Current.ShowFrequencyPassband = false;
        var database = new TestSatelliteDatabaseService([CreateRs44Entry()]);
        var vm = new FrequencyOverlayViewModel(settings, database, LocalizationService.Instance);
        vm.Update(Rs44Track());

        Assert.False(vm.ShowPassbandIndicator);
        Assert.True(vm.ShowPassbandReveal);
    }

    [Fact]
    public void Non_inverting_passband_keeps_uplink_edges_in_frequency_order()
    {
        var entry = CreateRs44Entry();
        entry.Modes[0].Doppler = "NOR";
        var settings = new TestSettingsService();
        var vm = new FrequencyOverlayViewModel(settings, new TestSatelliteDatabaseService([entry]), LocalizationService.Instance);
        vm.Update(Rs44Track());

        Assert.False(vm.PassbandState!.IsInverting);
        Assert.Equal("↑ 145.935", vm.PassbandUplinkLeftText);
        Assert.Equal("145.995 ↑", vm.PassbandUplinkRightText);
    }

    [Fact]
    public void Passband_spots_mark_ft4_and_ft8_but_not_every_data_mode()
    {
        var entry = CreateRs44Entry();
        entry.Modes.Add(new SatelliteTransponderMode
        {
            Type = "Wideband DATV",
            DownlinkKHz = 435_640,
            UplinkKHz = 145_965,
            DownlinkMode = "DATA-USB",
            UplinkMode = "DATA-USB",
            Doppler = "REV"
        });
        entry.Modes.Add(new SatelliteTransponderMode
        {
            Type = "FT8",
            DownlinkKHz = 435_650,
            UplinkKHz = 145_955,
            DownlinkMode = "DATA-USB",
            UplinkMode = "DATA-LSB",
            Doppler = "REV"
        });

        var state = PassbandIndicatorCalculator.Build(entry, entry.Modes[0], entry.Modes, 435_640, 145_965);

        Assert.NotNull(state);
        Assert.Contains(state.Spots, spot => spot.Label == "FT4" && spot.SpanNormalized is > 0);
        Assert.Contains(state.Spots, spot => spot.Label == "FT8" && spot.SpanNormalized is > 0);
        Assert.DoesNotContain(state.Spots, spot => spot.Label == "Wideband DATV");
        Assert.Contains(state.Spots, spot => spot.Label == "CW TLM" && spot.IsBeacon);
    }

    private static SatelliteTrackState Rs44Track() =>
        new()
        {
            Name = "RS-44",
            NoradId = "44909",
            Subpoint = new GeoCoordinate(57, 18),
            LookAngles = new LookAngles(180, 25, 800, -4.5)
        };

    [Fact]
    public void User_database_without_passband_bounds_inherits_bounds_from_bundled_database()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ow-passband-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var bundledPath = Path.Combine(tempDir, "bundled.json");
            var userPath = Path.Combine(tempDir, "user.json");

            SatelliteDatabaseFile.Save(bundledPath, [CreateRs44Entry()]);

            var olderUserEntry = CreateRs44Entry();
            olderUserEntry.DownlinkMinKHz = null;
            olderUserEntry.DownlinkMaxKHz = null;
            olderUserEntry.UplinkMinKHz = null;
            olderUserEntry.UplinkMaxKHz = null;
            SatelliteDatabaseFile.Save(userPath, [olderUserEntry]);

            var service = new SatelliteDatabaseService(bundledPath, userPath);
            var rs44 = service.TryGetEntry("RS-44", "44909");

            Assert.True(service.IsUsingUserDatabase);
            Assert.NotNull(rs44);
            Assert.Equal(435_610, rs44.DownlinkMinKHz);
            Assert.Equal(435_670, rs44.DownlinkMaxKHz);
            Assert.Equal(145_935, rs44.UplinkMinKHz);
            Assert.Equal(145_995, rs44.UplinkMaxKHz);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Ao07_each_transponder_keeps_its_own_passband()
    {
        var ao7 = CreateAo7Entry();
        var modeB = PassbandIndicatorCalculator.Build(ao7, ao7.Modes[0], ao7.Modes, 145_950, 432_146);
        var modeA = PassbandIndicatorCalculator.Build(ao7, ao7.Modes[1], ao7.Modes, 29_450, 145_900);

        Assert.NotNull(modeB);
        Assert.True(modeB.IsInverting);
        Assert.Equal(50, modeB.WidthKHz);
        Assert.Equal(145_925, modeB.DownlinkMinKHz);
        Assert.Equal(145_975, modeB.DownlinkMaxKHz);

        Assert.NotNull(modeA);
        Assert.False(modeA.IsInverting);
        Assert.Equal(100, modeA.WidthKHz);
        Assert.Equal(29_400, modeA.DownlinkMinKHz);
        Assert.Equal(29_500, modeA.DownlinkMaxKHz);
        Assert.Equal(145_850, modeA.UplinkMinKHz);
        Assert.Equal(145_950, modeA.UplinkMaxKHz);
    }

    [Fact]
    public void User_database_inherits_per_mode_passband_bounds_and_keeps_operator_edges()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ow-passband-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var bundled = CreateAo7Entry();
            var user = CreateAo7Entry();
            foreach (var mode in user.Modes)
            {
                mode.DownlinkMinKHz = null;
                mode.DownlinkMaxKHz = null;
                mode.UplinkMinKHz = null;
                mode.UplinkMaxKHz = null;
            }

            user.Modes[0].DownlinkMinKHz = 145_930;
            user.Modes[0].DownlinkMaxKHz = 145_970;

            SatelliteDatabaseFile.Save(Path.Combine(tempDir, "bundled.json"), [bundled]);
            SatelliteDatabaseFile.Save(Path.Combine(tempDir, "user.json"), [user]);

            var service = new SatelliteDatabaseService(
                Path.Combine(tempDir, "bundled.json"),
                Path.Combine(tempDir, "user.json"));
            var loaded = service.TryGetEntry("AO-07", "07530");

            Assert.NotNull(loaded);
            Assert.False(loaded.HasDownlinkPassband);
            var modeB = loaded.Modes.Single(m => m.Type == "Mode B");
            var modeA = loaded.Modes.Single(m => m.Type == "Mode A");
            Assert.Equal(145_930, modeB.DownlinkMinKHz);
            Assert.Equal(145_970, modeB.DownlinkMaxKHz);
            Assert.Equal(432_125, modeB.UplinkMinKHz);
            Assert.Equal(432_175, modeB.UplinkMaxKHz);
            Assert.Equal(29_400, modeA.DownlinkMinKHz);
            Assert.Equal(29_500, modeA.DownlinkMaxKHz);
            Assert.Equal(145_850, modeA.UplinkMinKHz);
            Assert.Equal(145_950, modeA.UplinkMaxKHz);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static SatelliteRadioEntry CreateAo7Entry() =>
        new()
        {
            Name = "AO-07",
            NoradId = "07530",
            Modes =
            [
                new SatelliteTransponderMode
                {
                    Type = "Mode B",
                    DownlinkKHz = 145_950,
                    UplinkKHz = 432_146,
                    DownlinkMinKHz = 145_925,
                    DownlinkMaxKHz = 145_975,
                    UplinkMinKHz = 432_125,
                    UplinkMaxKHz = 432_175,
                    DownlinkMode = "USB",
                    UplinkMode = "LSB",
                    Doppler = "REV"
                },
                new SatelliteTransponderMode
                {
                    Type = "Mode A",
                    DownlinkKHz = 29_450,
                    UplinkKHz = 145_900,
                    DownlinkMinKHz = 29_400,
                    DownlinkMaxKHz = 29_500,
                    UplinkMinKHz = 145_850,
                    UplinkMaxKHz = 145_950,
                    DownlinkMode = "USB",
                    UplinkMode = "USB",
                    Doppler = "NOR"
                }
            ]
        };

    private sealed class TestSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string? LoadError => null;
        public bool CanPersist => true;
        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "oscarwatch-passband-test-settings.json");
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

    private sealed class TestSatelliteDatabaseService(IReadOnlyList<SatelliteRadioEntry> entries) : ISatelliteDatabaseService
    {
        public IReadOnlyList<SatelliteRadioEntry> Entries { get; } = entries;
        public string ActiveDatabasePath { get; } = "test";
        public bool IsUsingUserDatabase => false;
        public SatelliteRadioEntry? TryGetEntry(string satelliteName, string? noradId = null) =>
            Entries.FirstOrDefault(e => e.Name.Equals(satelliteName, StringComparison.OrdinalIgnoreCase));
        public void Reload() { }
    }
}
