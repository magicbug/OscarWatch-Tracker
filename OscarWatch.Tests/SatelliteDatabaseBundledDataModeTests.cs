using OscarWatch.Core.Radio;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

public sealed class SatelliteDatabaseBundledDataModeTests
{
    private static string BundledDatabasePath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "satellite_database.json");

    [Fact]
    public void Bundled_database_UNNE1B_maps_to_norad_101101_and_HADES_E2_alias()
    {
        var path = BundledDatabasePath;
        Assert.True(File.Exists(path), $"Bundled database not found at {path}");

        var entries = SatelliteDatabaseFile.Load(path);
        Assert.Null(SatelliteDatabaseFile.ValidateEntries(entries));

        var unne = entries.Single(e => e.Name == "UNNE-1B");
        Assert.Equal("101101", unne.NoradId);
        Assert.Contains("HADES-E2", unne.AlternativeNames ?? []);

        var userPath = Path.Combine(Path.GetTempPath(), $"ow-db-missing-{Guid.NewGuid():N}.json");
        var service = new SatelliteDatabaseService(path, userPath);
        var byAlias = service.TryGetEntry("HADES-E2");
        Assert.NotNull(byAlias);
        Assert.Equal("UNNE-1B", byAlias!.Name);
        Assert.Equal("101101", byAlias.NoradId);
    }

    [Fact]
    public void Bundled_database_HADES_L_maps_to_norad_101102()
    {
        var path = BundledDatabasePath;
        Assert.True(File.Exists(path), $"Bundled database not found at {path}");

        var entries = SatelliteDatabaseFile.Load(path);
        var hadesL = entries.Single(e => e.Name == "HADES-L");
        Assert.Equal("101102", hadesL.NoradId);
    }
    [Fact]
    public void Bundled_database_SONATE2_APRS_uses_DATA_FM_for_CAT_data_mode()
    {
        var path = BundledDatabasePath;
        Assert.True(File.Exists(path), $"Bundled database not found at {path}");

        var entries = SatelliteDatabaseFile.Load(path);
        var sonate = entries.Single(e => e.Name == "SONATE-2");
        var aprs = sonate.Modes.Single(m => m.Type == "APRS");

        Assert.True(TransponderCatModes.IsData(aprs.DownlinkMode));
        Assert.True(TransponderCatModes.IsData(aprs.UplinkMode));
        Assert.Equal("DATA-FM", aprs.DownlinkMode);
        Assert.Equal("DATA-FM", aprs.UplinkMode);
    }
}
