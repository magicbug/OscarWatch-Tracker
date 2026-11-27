using OscarWatch.Core.Ft4;
using OscarWatch.Core.Models;
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

    [Fact]
    public void Bundled_database_SO131_has_FT4_transponder_with_DATA_USB_modes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "satellite_database.json");
        Assert.True(File.Exists(path), $"Bundled database not found at {path}");

        var entries = SatelliteDatabaseFile.Load(path);
        var so131 = entries.Single(e => e.Name == "SO-131");
        var fm = so131.Modes.Single(m => m.Type == "FM Repeater");
        var ft4 = so131.Modes.Single(m => m.Type == "FT4");

        Assert.Equal(fm.DownlinkKHz, ft4.DownlinkKHz);
        Assert.Equal(fm.UplinkKHz, ft4.UplinkKHz);
        Assert.Equal("DATA-USB", ft4.DownlinkMode);
        Assert.Equal("DATA-USB", ft4.UplinkMode);
        Assert.Equal("NOR", ft4.Doppler);
        Assert.True(Ft4SatelliteEligibility.IsAllowed(so131.Name, so131.NoradId, ft4));
    }

    [Fact]
    public void Bundled_database_SO131_has_DSTAR_transponder_with_DV_modes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "satellite_database.json");
        Assert.True(File.Exists(path), $"Bundled database not found at {path}");

        var entries = SatelliteDatabaseFile.Load(path);
        var so131 = entries.Single(e => e.Name == "SO-131");
        var fm = so131.Modes.Single(m => m.Type == "FM Repeater");
        var dstar = so131.Modes.Single(m => m.Type == "D-STAR");

        Assert.Equal(fm.DownlinkKHz, dstar.DownlinkKHz);
        Assert.Equal(fm.UplinkKHz, dstar.UplinkKHz);
        Assert.Equal("DV", dstar.DownlinkMode);
        Assert.Equal("DV", dstar.UplinkMode);
        Assert.Equal("NOR", dstar.Doppler);
        Assert.True(TransponderCatModes.IsDigitalVoice(dstar.DownlinkMode));
    }

    [Fact]
    public void Bundled_database_linear_passbands_match_published_edges()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "satellite_database.json");
        var entries = SatelliteDatabaseFile.Load(path);

        var jo97 = entries.Single(e => e.Name == "JO-97");
        AssertPassband(jo97, 145_855, 145_875, 435_100, 435_120);
        AssertInside(jo97.Modes.Single(m => m.Type == "SSB Transponder").DownlinkKHz, jo97);
        AssertInside(jo97.Modes.Single(m => m.Type == "FT4").DownlinkKHz, jo97);

        var ao73 = entries.Single(e => e.Name == "AO-73");
        AssertPassband(ao73, 145_950, 145_970, 435_130, 435_150);
        AssertInside(ao73.Modes.Single(m => m.Type == "Voice U/V").DownlinkKHz, ao73);

        var fo29 = entries.Single(e => e.Name == "FO-29");
        AssertPassband(fo29, 435_800, 435_900, 145_900, 146_000);
        AssertInside(fo29.Modes.Single(m => m.Type == "SSB Transponder").DownlinkKHz, fo29);

        var ao7 = entries.Single(e => e.Name == "AO-07");
        Assert.False(ao7.HasDownlinkPassband);
        var modeB = ao7.Modes.Single(m => m.Type == "Mode B");
        var modeA = ao7.Modes.Single(m => m.Type == "Mode A");
        AssertModePassband(modeB, 145_925, 145_975, 432_125, 432_175);
        AssertModePassband(modeA, 29_400, 29_500, 145_850, 145_950);
        Assert.False(ao7.Modes.Single(m => m.Type == "CW").HasDownlinkPassband);

        var qo100 = entries.Single(e => e.Name == "QO-100");
        AssertPassband(qo100, 10_489_500, 10_490_000, 2_400_000, 2_400_500);
        AssertInside(qo100.Modes.Single(m => m.Type == "Narrowband Transponder").DownlinkKHz, qo100);
        AssertInside(qo100.Modes.Single(m => m.Type == "FT4").DownlinkKHz, qo100);
        var wideband = qo100.Modes.Single(m => m.Type == "Wideband DATV");
        AssertModePassband(wideband, 10_490_500, 10_499_500, 2_401_000, 2_410_000);
        Assert.True(wideband.DownlinkKHz > qo100.DownlinkMaxKHz);
    }

    private static void AssertPassband(
        SatelliteRadioEntry entry,
        double downlinkMin,
        double downlinkMax,
        double uplinkMin,
        double uplinkMax)
    {
        Assert.Equal(downlinkMin, entry.DownlinkMinKHz);
        Assert.Equal(downlinkMax, entry.DownlinkMaxKHz);
        Assert.Equal(uplinkMin, entry.UplinkMinKHz);
        Assert.Equal(uplinkMax, entry.UplinkMaxKHz);
    }

    private static void AssertInside(double downlinkKHz, SatelliteRadioEntry entry)
    {
        Assert.InRange(downlinkKHz, entry.DownlinkMinKHz!.Value, entry.DownlinkMaxKHz!.Value);
    }

    private static void AssertModePassband(
        SatelliteTransponderMode mode,
        double downlinkMin,
        double downlinkMax,
        double uplinkMin,
        double uplinkMax)
    {
        Assert.Equal(downlinkMin, mode.DownlinkMinKHz);
        Assert.Equal(downlinkMax, mode.DownlinkMaxKHz);
        Assert.Equal(uplinkMin, mode.UplinkMinKHz);
        Assert.Equal(uplinkMax, mode.UplinkMaxKHz);
        Assert.InRange(mode.DownlinkKHz, downlinkMin, downlinkMax);
        Assert.InRange(mode.UplinkKHz, uplinkMin, uplinkMax);
    }
}
