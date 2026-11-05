using System.Text.Json.Nodes;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Troubleshoot;

namespace OscarWatch.Tests.Troubleshoot;

public sealed class TroubleshootSettingsFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"oscarwatch-troubleshoot-{Guid.NewGuid():N}");

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public TroubleshootSettingsFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // temp folder
        }
    }

    [Fact]
    public async Task DisableHardware_turns_off_rig_rotator_and_gps_and_tracker_keeps_everything_else()
    {
        var tracker = new SettingsService(SettingsPath);
        tracker.Load();
        tracker.Current.GroundStation.Callsign = "MM9SQL";
        tracker.Current.Rig.Enabled = true;
        tracker.Current.Rig.Port = "COM7";
        tracker.Current.Rig.BaudRate = 9600;
        tracker.Current.Rotator.Enabled = true;
        tracker.Current.Rotator.Port = "COM9";
        tracker.Current.Gps.Enabled = true;
        tracker.Current.Gps.ConnectionKind = GpsConnectionKind.Gpsd;
        tracker.Current.Gps.GpsdHost = "pi.local";
        tracker.Current.Cloudlog.ApiKey = "secret-key";
        await tracker.SaveAsync();

        Assert.Equal(DisableHardwareResult.Disabled, SettingsFile.DisableHardware(SettingsPath));

        var reloaded = new SettingsService(SettingsPath);
        reloaded.Load();
        Assert.Null(reloaded.LoadError);
        Assert.False(reloaded.Current.Rig.Enabled);
        Assert.False(reloaded.Current.Rotator.Enabled);
        Assert.False(reloaded.Current.Gps.Enabled);
        Assert.Equal("COM7", reloaded.Current.Rig.Port);
        Assert.Equal(9600, reloaded.Current.Rig.BaudRate);
        Assert.Equal("COM9", reloaded.Current.Rotator.Port);
        Assert.Equal(GpsConnectionKind.Gpsd, reloaded.Current.Gps.ConnectionKind);
        Assert.Equal("pi.local", reloaded.Current.Gps.GpsdHost);
        Assert.Equal("MM9SQL", reloaded.Current.GroundStation.Callsign);
        Assert.Equal("secret-key", reloaded.Current.Cloudlog.ApiKey);
    }

    [Fact]
    public void DisableHardware_keeps_properties_the_model_does_not_know()
    {
        File.WriteAllText(SettingsPath, """
            { "rig": { "enabled": true, "port": "COM3", "futureRigOption": 42 }, "futureSection": { "a": [1, 2] } }
            """);

        Assert.Equal(DisableHardwareResult.Disabled, SettingsFile.DisableHardware(SettingsPath));

        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.False(root["rig"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(42, root["rig"]!["futureRigOption"]!.GetValue<int>());
        Assert.Equal(2, root["futureSection"]!["a"]!.AsArray().Count);
        Assert.Single(Directory.GetFiles(_dir, "settings.json.bak-*"));
    }

    [Fact]
    public void DisableHardware_reports_already_off_and_leaves_file_untouched()
    {
        const string json = """{ "rig": { "enabled": false }, "rotator": { "enabled": false } }""";
        File.WriteAllText(SettingsPath, json);

        Assert.Equal(DisableHardwareResult.AlreadyDisabled, SettingsFile.DisableHardware(SettingsPath));
        Assert.Equal(json, File.ReadAllText(SettingsPath));
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.bak-*"));
    }

    [Fact]
    public void DisableHardware_does_not_rewrite_unreadable_settings()
    {
        const string broken = """{ "rig": { "enabled": true, """;
        File.WriteAllText(SettingsPath, broken);

        Assert.Equal(DisableHardwareResult.Unreadable, SettingsFile.DisableHardware(SettingsPath));
        Assert.Equal(broken, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void DisableHardware_reports_missing_file()
    {
        Assert.Equal(DisableHardwareResult.Missing, SettingsFile.DisableHardware(SettingsPath));
    }

    [Fact]
    public async Task ClearEchoCalibrations_empties_every_trim_and_tracker_keeps_other_ft4_settings()
    {
        var tracker = new SettingsService(SettingsPath);
        tracker.Load();
        tracker.Current.Ft4.UplinkCalibrationKHzBySatellite["RS-44"] = 0.12;
        tracker.Current.Ft4.UplinkCalibrationKHzBySatellite["RS-44|FT4 Asia"] = -0.05;
        tracker.Current.Ft4.WaterfallRangeDb = 30;
        tracker.Current.Ft4.WaterfallFlatten = false;
        tracker.Current.Ft4.WaterfallSlotLines = false;
        tracker.Current.Rig.Enabled = true;
        await tracker.SaveAsync();

        Assert.Equal(2, SettingsFile.Read(SettingsPath).EchoCalibrationCount);
        var backupsBefore = Directory.GetFiles(_dir, "settings.json.bak-*").Length;
        Assert.Equal(ClearEchoCalibrationsResult.Cleared, SettingsFile.ClearEchoCalibrations(SettingsPath));
        Assert.Equal(0, SettingsFile.Read(SettingsPath).EchoCalibrationCount);
        Assert.Equal(backupsBefore + 1, Directory.GetFiles(_dir, "settings.json.bak-*").Length);

        var reloaded = new SettingsService(SettingsPath);
        reloaded.Load();
        Assert.Null(reloaded.LoadError);
        Assert.Empty(reloaded.Current.Ft4.UplinkCalibrationKHzBySatellite);
        Assert.Equal(30, reloaded.Current.Ft4.WaterfallRangeDb);
        Assert.False(reloaded.Current.Ft4.WaterfallFlatten);
        Assert.False(reloaded.Current.Ft4.WaterfallSlotLines);
        Assert.True(reloaded.Current.Rig.Enabled);
    }

    [Theory]
    [InlineData("""{ "ft4": { "uplinkCalibrationKHzBySatellite": {} } }""")]
    [InlineData("""{ "ft4": { "waterfallRangeDb": 40 } }""")]
    [InlineData("""{ "rig": { "enabled": true } }""")]
    public void ClearEchoCalibrations_reports_none_saved_and_leaves_file_untouched(string json)
    {
        File.WriteAllText(SettingsPath, json);

        Assert.Equal(ClearEchoCalibrationsResult.NoneSaved, SettingsFile.ClearEchoCalibrations(SettingsPath));
        Assert.Equal(json, File.ReadAllText(SettingsPath));
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.bak-*"));
    }

    [Fact]
    public void ClearEchoCalibrations_does_not_rewrite_unreadable_settings()
    {
        const string broken = """{ "ft4": { "uplinkCalibrationKHzBySatellite": { "RS-44": 0.1 }, """;
        File.WriteAllText(SettingsPath, broken);

        Assert.Equal(ClearEchoCalibrationsResult.Unreadable, SettingsFile.ClearEchoCalibrations(SettingsPath));
        Assert.Equal(broken, File.ReadAllText(SettingsPath));
        Assert.Equal(ClearEchoCalibrationsResult.Missing, SettingsFile.ClearEchoCalibrations(Path.Combine(_dir, "none.json")));
    }

    [Fact]
    public void Read_describes_devices_and_targets()
    {
        File.WriteAllText(SettingsPath, """
            {
              "rig": { "enabled": true, "dualRadioEnabled": true, "downlink": { "port": "COM4" }, "uplink": { "port": "COM5" } },
              "rotator": { "enabled": true, "type": "spidMd01", "networkHost": "10.0.0.5", "networkPort": 23 },
              "gps": { "enabled": false, "connectionKind": "serial", "port": "/dev/ttyACM0" }
            }
            """);

        var snapshot = SettingsFile.Read(SettingsPath);

        Assert.Equal(SettingsFileState.Readable, snapshot.State);
        Assert.Equal(new DeviceState(true, "COM4 / COM5"), snapshot.Radio);
        Assert.Equal(new DeviceState(true, "10.0.0.5:23"), snapshot.Rotator);
        Assert.Equal(new DeviceState(false, "/dev/ttyACM0"), snapshot.Gps);
    }

    [Fact]
    public void Read_reports_unreadable_file()
    {
        File.WriteAllText(SettingsPath, "not json");

        var snapshot = SettingsFile.Read(SettingsPath);

        Assert.Equal(SettingsFileState.Unreadable, snapshot.State);
        Assert.False(string.IsNullOrEmpty(snapshot.Error));
    }

    [Fact]
    public void PutAside_moves_settings_and_every_recovery_candidate()
    {
        foreach (var name in new[]
                 {
                     "settings.json", "settings.json.bak", "settings.json.bak-20260101-120000",
                     "settings.json.restored-from-bak-20260101", "settings.json.corrupt-20260101"
                 })
            File.WriteAllText(Path.Combine(_dir, name), "{}");

        var folder = SettingsFile.PutAside(SettingsPath, new DateTime(2026, 11, 5, 23, 0, 0, DateTimeKind.Utc));

        Assert.NotNull(folder);
        Assert.Equal("settings-aside-20261105-230000", Path.GetFileName(folder));
        Assert.Equal(
            ["settings.json", "settings.json.bak", "settings.json.bak-20260101-120000", "settings.json.restored-from-bak-20260101"],
            Directory.GetFiles(folder).Select(path => Path.GetFileName(path)).Order().ToArray());
        Assert.False(File.Exists(SettingsPath));
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json.corrupt-20260101")));
    }

    [Fact]
    public void PutAside_returns_null_when_nothing_to_move()
    {
        Assert.Null(SettingsFile.PutAside(SettingsPath, DateTime.UtcNow));
    }

    [Fact]
    public void Redacted_hides_passwords_keys_and_tokens()
    {
        File.WriteAllText(SettingsPath, """
            {
              "cloudlog": { "apiKey": "abc", "url": "https://log.example" },
              "qrz": { "password": "pw", "username": "MM9SQL" },
              "satelliteStatus": { "apiToken": "tok" },
              "hamQth": { "password": "" }
            }
            """);

        var root = JsonNode.Parse(SettingsFile.Redacted(SettingsPath))!;

        Assert.Equal("***", root["cloudlog"]!["apiKey"]!.GetValue<string>());
        Assert.Equal("https://log.example", root["cloudlog"]!["url"]!.GetValue<string>());
        Assert.Equal("***", root["qrz"]!["password"]!.GetValue<string>());
        Assert.Equal("MM9SQL", root["qrz"]!["username"]!.GetValue<string>());
        Assert.Equal("***", root["satelliteStatus"]!["apiToken"]!.GetValue<string>());
        Assert.Equal("", root["hamQth"]!["password"]!.GetValue<string>());
    }
}
