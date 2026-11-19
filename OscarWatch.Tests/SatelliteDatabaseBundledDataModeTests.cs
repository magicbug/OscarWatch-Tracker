using OscarWatch.Core.Radio;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

public sealed class SatelliteDatabaseBundledDataModeTests
{
    [Fact]
    public void Bundled_database_SONATE2_APRS_uses_DATA_FM_for_CAT_data_mode()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "satellite_database.json");
        Assert.True(File.Exists(path), $"Bundled database not found at {path}");

        var entries = SatelliteDatabaseFile.Load(path);
        var sonate = entries.Single(e => e.Name == "SONATE-2");
        var aprs = sonate.Modes.Single(m => m.Type == "APRS");

        Assert.True(TransponderCatModes.IsData(aprs.DownlinkMode));
        Assert.True(TransponderCatModes.IsData(aprs.UplinkMode));
        Assert.Equal("DATA-FM", aprs.DownlinkMode);
        Assert.Equal("DATA-FM", aprs.UplinkMode);
    }

    [Fact]
    public void Bundled_database_SO131_has_wednesday_utc_fsk_notice()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "satellite_database.json");
        Assert.True(File.Exists(path), $"Bundled database not found at {path}");

        var entries = SatelliteDatabaseFile.Load(path);
        var so131 = entries.Single(e => e.Name == "SO-131");
        var notice = Assert.Single(so131.Notices!);
        Assert.Contains("FSK", notice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["Wednesday"], notice.DaysOfWeekUtc);

        var wednesday = new DateTime(2026, 11, 18, 15, 0, 0, DateTimeKind.Utc);
        var thursday = new DateTime(2026, 11, 19, 15, 0, 0, DateTimeKind.Utc);
        Assert.True(SatelliteNoticeEvaluator.HasActiveNotice(so131, wednesday));
        Assert.False(SatelliteNoticeEvaluator.HasActiveNotice(so131, thursday));
    }
}
