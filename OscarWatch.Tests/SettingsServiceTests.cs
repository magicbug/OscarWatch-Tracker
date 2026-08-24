using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void TryParse_round_trips_satellite_status_settings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oscarwatch-sat-status-{Guid.NewGuid():N}.json");
        var service = new SettingsService(path);
        service.Current.SatelliteStatus = new SatelliteStatusSettings
        {
            Enabled = true,
            BaseUrl = "https://oscarwatch.org",
            ApiToken = "pat-token-xyz",
            AutoReportOnQso = true
        };

        var json = service.SerializeCurrent();
        Assert.True(SettingsService.TryParse(json, out var parsed, out var error));
        Assert.Null(error);
        Assert.True(parsed.SatelliteStatus.Enabled);
        Assert.Equal("https://oscarwatch.org", parsed.SatelliteStatus.BaseUrl);
        Assert.Equal("pat-token-xyz", parsed.SatelliteStatus.ApiToken);
        Assert.True(parsed.SatelliteStatus.AutoReportOnQso);
    }

    [Fact]
    public void Load_persists_ground_station_callsign_via_saved_stations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oscarwatch-callsign-{Guid.NewGuid():N}.json");
        try
        {
            var service = new SettingsService(path);
            service.Current.GroundStation.Callsign = "mm9sql";
            service.Current.SavedStations =
            [
                StationProfile.FromGroundStation(service.Current.GroundStation, "home1")
            ];
            service.Current.ActiveStationId = "home1";
            service.SaveAsync().GetAwaiter().GetResult();

            var reloaded = new SettingsService(path);
            reloaded.Load();

            Assert.Equal("MM9SQL", reloaded.Current.GroundStation.Callsign);
            Assert.Equal("MM9SQL", reloaded.Current.SavedStations[0].Callsign);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void SyncActiveStationFromGroundStation_copies_callsign()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oscarwatch-callsign-sync-{Guid.NewGuid():N}.json");
        var service = new SettingsService(path);
        service.Current.GroundStation.Callsign = "G0ABC";
        service.EnsureSavedStations();
        service.Current.GroundStation.Callsign = "MM9SQL";

        service.SyncActiveStationFromGroundStation();

        var profile = service.Current.SavedStations.First(s => s.Id == service.Current.ActiveStationId);
        Assert.Equal("MM9SQL", profile.Callsign);
    }

    [Fact]
    public void TryParse_round_trips_serialized_settings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oscarwatch-parse-{Guid.NewGuid():N}.json");
        var service = new SettingsService(path);
        service.Current.GroundStation.DisplayName = "Portable";
        service.Current.EnabledSatelliteNames = ["ISS", "SO-50"];
        service.Current.EnabledSatelliteNoradIds = ["25544", "27607"];
        service.Current.Rig.Enabled = true;

        var json = service.SerializeCurrent();

        Assert.True(SettingsService.TryParse(json, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal("Portable", parsed.GroundStation.DisplayName);
        Assert.Equal(["ISS", "SO-50"], parsed.EnabledSatelliteNames);
        Assert.Equal(["25544", "27607"], parsed.EnabledSatelliteNoradIds);
        Assert.True(parsed.Rig.Enabled);
    }

    [Fact]
    public void TryParse_round_trips_horizon_mask_on_ground_station()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oscarwatch-mask-{Guid.NewGuid():N}.json");
        var service = new SettingsService(path);
        service.Current.GroundStation.HorizonMask = new HorizonMask
        {
            Points =
            [
                new HorizonMaskPoint(0, 8),
                new HorizonMaskPoint(120, 22)
            ]
        };
        service.Current.SavedStations =
        [
            StationProfile.FromGroundStation(service.Current.GroundStation, "home1")
        ];

        var json = service.SerializeCurrent();
        Assert.True(SettingsService.TryParse(json, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal(2, parsed.GroundStation.HorizonMask.Points.Count);
        Assert.Equal(22, parsed.GroundStation.HorizonMask.ElevationAt(120), 3);
        Assert.Single(parsed.SavedStations);
        Assert.Equal(2, parsed.SavedStations[0].HorizonMask.Points.Count);
    }

    [Fact]
    public void TryParse_missing_horizon_mask_defaults_to_empty()
    {
        const string json = """
            {
              "groundStation": { "displayName": "Home", "latitudeDeg": 51.5, "longitudeDeg": -0.1 }
            }
            """;

        Assert.True(SettingsService.TryParse(json, out var parsed, out var error));
        Assert.Null(error);
        Assert.NotNull(parsed.GroundStation.HorizonMask);
        Assert.Empty(parsed.GroundStation.HorizonMask.Points);
    }

    [Fact]
    public void TryParse_missing_norad_ids_defaults_to_empty_list()
    {
        const string json = """
            {
              "enabledSatelliteNames": ["ISS"],
              "groundStation": { "displayName": "Home", "latitudeDeg": 51.5, "longitudeDeg": -0.1 }
            }
            """;

        Assert.True(SettingsService.TryParse(json, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal(["ISS"], parsed.EnabledSatelliteNames);
        Assert.NotNull(parsed.EnabledSatelliteNoradIds);
        Assert.Empty(parsed.EnabledSatelliteNoradIds);
    }

    [Fact]
    public void TryParse_migrates_legacy_passPlannerUseUtcTime_to_displayTimesInUtc()
    {
        const string json = """
            {
              "passPlannerUseUtcTime": true,
              "groundStation": { "displayName": "Home", "latitudeDeg": 51.5, "longitudeDeg": -0.1 }
            }
            """;

        Assert.True(SettingsService.TryParse(json, out var parsed, out var error));
        Assert.Null(error);
        Assert.True(parsed.DisplayTimesInUtc);
        Assert.True(parsed.PassPlannerUseUtcTime);
    }

    [Fact]
    public void TryParse_displayTimesInUtc_is_independent_of_passPlannerUseUtcTime()
    {
        const string json = """
            {
              "displayTimesInUtc": false,
              "passPlannerUseUtcTime": true,
              "groundStation": { "displayName": "Home", "latitudeDeg": 51.5, "longitudeDeg": -0.1 }
            }
            """;

        Assert.True(SettingsService.TryParse(json, out var parsed, out var error));
        Assert.Null(error);
        Assert.False(parsed.DisplayTimesInUtc);
        Assert.True(parsed.PassPlannerUseUtcTime);
    }

    [Fact]
    public async Task ReplaceAndSaveAsync_persists_imported_settings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oscarwatch-import-{Guid.NewGuid():N}.json");
        var service = new SettingsService(path);
        service.Current.GroundStation.DisplayName = "Before";

        var imported = new AppSettings
        {
            GroundStation = new GroundStation { DisplayName = "After", LatitudeDeg = 51.5, LongitudeDeg = -0.1 }
        };

        try
        {
            await service.ReplaceAndSaveAsync(imported);

            Assert.Equal("After", service.Current.GroundStation.DisplayName);
            var reloaded = new SettingsService(path);
            reloaded.Load();
            Assert.Equal("After", reloaded.Current.GroundStation.DisplayName);
        }
        finally
        {
            CleanupSettingsArtifacts(path);
        }
    }

    [Fact]
    public async Task SaveAsync_serializes_concurrent_writes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oscarwatch-settings-{Guid.NewGuid():N}.json");
        var service = new SettingsService(path);
        service.Current.GroundStation.DisplayName = "Home";

        try
        {
            var tasks = Enumerable.Range(0, 20)
                .Select(i =>
                {
                    service.Current.GroundStation.DisplayName = $"Station-{i}";
                    return service.SaveAsync();
                })
                .ToArray();

            await Task.WhenAll(tasks);

            Assert.True(File.Exists(path));
            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("Station-19", json);
        }
        finally
        {
            CleanupSettingsArtifacts(path);
        }
    }

    [Fact]
    public void WriteAtomic_replaces_existing_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oscarwatch-atomic-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, "{ \"old\": true }");
            SettingsService.WriteAtomic(path, "{ \"new\": true, \"pad\": \"xxxxxxxx\" }");

            var json = File.ReadAllText(path);
            Assert.Contains("\"new\": true", json);
            Assert.DoesNotContain("\"old\": true", json);
            Assert.True(File.Exists(path + ".bak"));
        }
        finally
        {
            CleanupSettingsArtifacts(path);
        }
    }

    [Fact]
    public void WriteAtomic_creates_timestamped_backup_before_replace()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oscarwatch-atomic-bak-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, """{ "gridSquare": "IO87jp", "pad": "xxxxxxxx" }""");
            SettingsService.WriteAtomic(path, """{ "gridSquare": "IO91wm", "pad": "yyyyyyyy" }""");

            var dated = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".bak-*");
            Assert.NotEmpty(dated);
            Assert.Contains("IO87jp", File.ReadAllText(dated[0]), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("IO91wm", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupSettingsArtifacts(path);
        }
    }

    [Fact]
    public void WriteAtomic_rejects_empty_payload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oscarwatch-atomic-empty-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "gridSquare": "IO87jp", "pad": "xxxxxxxx" }""");

        try
        {
            Assert.Throws<InvalidOperationException>(() => SettingsService.WriteAtomic(path, " "));
            Assert.Contains("IO87jp", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupSettingsArtifacts(path);
        }
    }

    [Fact]
    public async Task Load_corrupt_file_preserves_disk_and_blocks_save()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oscarwatch-corrupt-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{ this is not json");

        using var service = new SettingsService(path);
        try
        {
            service.Load();

            Assert.False(service.CanPersist);
            Assert.False(string.IsNullOrWhiteSpace(service.LoadError));
            Assert.Equal("{ this is not json", await File.ReadAllTextAsync(path));

            service.Current.GroundStation.GridSquare = "IO91wm";
            service.RequestSave();
            await Task.Delay(800);
            Assert.Equal("{ this is not json", await File.ReadAllTextAsync(path));

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync());
            Assert.Equal("{ this is not json", await File.ReadAllTextAsync(path));

            var corruptSnapshots = Directory.GetFiles(
                Path.GetDirectoryName(path)!,
                Path.GetFileName(path) + ".corrupt-*");
            Assert.NotEmpty(corruptSnapshots);
        }
        finally
        {
            CleanupSettingsArtifacts(path);
        }
    }

    [Fact]
    public async Task ReplaceAndSaveAsync_re_enables_persist_after_corrupt_load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oscarwatch-repair-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{ broken");

        using var service = new SettingsService(path);
        try
        {
            service.Load();
            Assert.False(service.CanPersist);

            await service.ReplaceAndSaveAsync(new AppSettings
            {
                GroundStation = new GroundStation
                {
                    DisplayName = "Home",
                    LatitudeDeg = 57.1,
                    LongitudeDeg = -2.1,
                    GridSquare = "IO87jp"
                }
            });

            Assert.True(service.CanPersist);
            Assert.Null(service.LoadError);
            Assert.Contains("IO87jp", await File.ReadAllTextAsync(path), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupSettingsArtifacts(path);
        }
    }

    [Fact]
    public async Task SaveAsync_refuses_factory_defaults_over_personalized_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oscarwatch-guard-{Guid.NewGuid():N}.json");
        using var service = new SettingsService(path);
        try
        {
            service.Current.GroundStation = new GroundStation
            {
                DisplayName = "Home",
                LatitudeDeg = 57.64583,
                LongitudeDeg = -3.20833,
                GridSquare = "IO87jp"
            };
            await service.SaveAsync();

            service.Current.GroundStation = new GroundStation(); // factory IO91wm
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync());
            Assert.Contains("IO87jp", await File.ReadAllTextAsync(path), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupSettingsArtifacts(path);
        }
    }

    [Fact]
    public async Task Load_restores_personalized_bak_when_live_file_is_factory_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oscarwatch-autobak-{Guid.NewGuid():N}.json");
        using var service = new SettingsService(path);
        try
        {
            service.Current.GroundStation = new GroundStation
            {
                DisplayName = "Home",
                LatitudeDeg = 57.64583,
                LongitudeDeg = -3.20833,
                GridSquare = "IO87jp"
            };
            await service.SaveAsync();

            // Live file becomes factory defaults; .bak keeps the real QTH (from WriteAtomic).
            await File.WriteAllTextAsync(path, new SettingsService(path + ".factory-src").SerializeCurrent());
            await File.WriteAllTextAsync(path + ".bak", """
                {
                  "groundStation": {
                    "displayName": "Home",
                    "latitudeDeg": 57.64583,
                    "longitudeDeg": -3.20833,
                    "altitudeMetersAsl": 50,
                    "gridSquare": "IO87jp"
                  },
                  "savedStations": [],
                  "enabledSatelliteNames": ["ISS"],
                  "enabledSatelliteNoradIds": []
                }
                """);

            using var reader = new SettingsService(path);
            reader.Load();
            Assert.Equal("IO87jp", reader.Current.GroundStation.GridSquare, ignoreCase: true);
            Assert.Contains("IO87jp", await File.ReadAllTextAsync(path), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupSettingsArtifacts(path);
            CleanupSettingsArtifacts(path + ".factory-src");
        }
    }

    [Fact]
    public async Task SaveAsync_reports_failure_when_settings_directory_is_blocked()
    {
        var settingsPath = CreateSettingsPathWithBlockedParentDirectory(out var blockerPath);
        Exception? reported = null;
        void Handler(Exception ex) => reported = ex;
        SettingsService.SaveFailed += Handler;

        try
        {
            var service = new SettingsService(settingsPath);
            await Assert.ThrowsAnyAsync<Exception>(() => service.SaveAsync());
            Assert.NotNull(reported);
        }
        finally
        {
            SettingsService.SaveFailed -= Handler;
            DeleteIfExists(blockerPath);
        }
    }

    [Fact]
    public async Task RequestSave_reports_failure_when_settings_directory_is_blocked()
    {
        var settingsPath = CreateSettingsPathWithBlockedParentDirectory(out var blockerPath);
        Exception? reported = null;
        void Handler(Exception ex) => reported = ex;
        SettingsService.SaveFailed += Handler;

        try
        {
            var service = new SettingsService(settingsPath);
            service.RequestSave();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (reported is null && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            Assert.NotNull(reported);
        }
        finally
        {
            SettingsService.SaveFailed -= Handler;
            DeleteIfExists(blockerPath);
        }
    }

    /// <summary>
    /// Parent path is an ordinary file, so <see cref="SettingsService"/> cannot create the settings directory.
    /// Reliable on Windows and Linux (unlike exclusive file locks or read-only targets).
    /// </summary>
    private static string CreateSettingsPathWithBlockedParentDirectory(out string blockerPath)
    {
        blockerPath = Path.Combine(Path.GetTempPath(), $"oscarwatch-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blockerPath, "blocks directory creation");
        return Path.Combine(blockerPath, "settings.json");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void CleanupSettingsArtifacts(string path)
    {
        DeleteIfExists(path);
        DeleteIfExists(path + ".tmp");
        DeleteIfExists(path + ".bak");
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            return;

        foreach (var extra in Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".*"))
            DeleteIfExists(extra);
    }
}
