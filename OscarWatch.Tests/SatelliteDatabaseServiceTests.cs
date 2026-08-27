using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

public sealed class SatelliteDatabaseServiceTests
{
    [Fact]
    public void TryGetEntry_resolves_by_norad_id_when_celestrak_name_differs()
    {
        using var fixture = CreateService(
        [
            new SatelliteRadioEntry
            {
                Name = "SO-50",
                NoradId = "27607",
                Modes =
                [
                    new SatelliteTransponderMode
                    {
                        Type = "FM VOICE",
                        DownlinkKHz = 436_795,
                        UplinkKHz = 145_850,
                        DownlinkMode = "FM",
                        UplinkMode = "FM",
                        Doppler = "NOR"
                    }
                ]
            }
        ]);

        var entry = fixture.Service.TryGetEntry("SAUDISAT 1C", noradId: "27607");

        Assert.NotNull(entry);
        Assert.Equal("SO-50", entry!.Name);
    }

    [Fact]
    public void TryGetEntry_zero_pads_norad_id_from_tle_catalog()
    {
        using var fixture = CreateService(
        [
            new SatelliteRadioEntry
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
                        DownlinkMode = "USB",
                        UplinkMode = "LSB",
                        Doppler = "REV"
                    }
                ]
            }
        ]);

        var entry = fixture.Service.TryGetEntry("OSCAR 7", noradId: "7530");

        Assert.NotNull(entry);
        Assert.Equal("AO-07", entry!.Name);
    }

    [Fact]
    public void TryGetEntry_prefers_name_match_before_norad_id()
    {
        using var fixture = CreateService(
        [
            new SatelliteRadioEntry
            {
                Name = "SO-50",
                NoradId = "27607",
                Modes = [Mode("FM")]
            }
        ]);

        var entry = fixture.Service.TryGetEntry("SO-50", noradId: "99999");

        Assert.NotNull(entry);
        Assert.Equal("SO-50", entry!.Name);
    }

    [Fact]
    public void TryGetEntry_uses_static_alias_for_common_celestrak_object_name()
    {
        using var fixture = CreateService(
        [
            new SatelliteRadioEntry
            {
                Name = "SO-50",
                Modes = [Mode("FM")]
            }
        ]);

        Assert.NotNull(fixture.Service.TryGetEntry("SAUDISAT 1C"));
    }

    [Fact]
    public void TryGetEntry_resolves_by_alternative_name()
    {
        using var fixture = CreateService(
        [
            new SatelliteRadioEntry
            {
                Name = "UmKA-1 (RS40-S)",
                NoradId = "57172",
                AlternativeNames = ["UmKA-1", "RS40-S"],
                Modes = [Mode("FM")]
            }
        ]);

        var byCatalog = fixture.Service.TryGetEntry("UmKA-1");
        var byNickname = fixture.Service.TryGetEntry("RS40-S");

        Assert.NotNull(byCatalog);
        Assert.Equal("UmKA-1 (RS40-S)", byCatalog!.Name);
        Assert.NotNull(byNickname);
        Assert.Equal("UmKA-1 (RS40-S)", byNickname!.Name);
    }

    [Fact]
    public void TryGetEntry_resolves_renamed_entry_by_norad_id()
    {
        using var fixture = CreateService(
        [
            new SatelliteRadioEntry
            {
                Name = "UmKA-1 (RS40-S)",
                NoradId = "57172",
                AlternativeNames = ["UmKA-1"],
                Modes = [Mode("FM")]
            }
        ]);

        var entry = fixture.Service.TryGetEntry("SOME OTHER TLE NAME", noradId: "57172");

        Assert.NotNull(entry);
        Assert.Equal("UmKA-1 (RS40-S)", entry!.Name);
    }

    private static SatelliteTransponderMode Mode(string type) => new()
    {
        Type = type,
        DownlinkKHz = 436_795,
        UplinkKHz = 145_850,
        DownlinkMode = "FM",
        UplinkMode = "FM",
        Doppler = "NOR"
    };

    private static ServiceFixture CreateService(IReadOnlyList<SatelliteRadioEntry> entries)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ow-db-{Guid.NewGuid():N}.json");
        var missingUserPath = Path.Combine(Path.GetTempPath(), $"ow-db-missing-{Guid.NewGuid():N}.json");
        SatelliteDatabaseFile.Save(path, entries);
        return new ServiceFixture(path, missingUserPath);
    }

    private sealed class ServiceFixture : IDisposable
    {
        public ServiceFixture(string bundledPath, string userPath)
        {
            Path = bundledPath;
            Service = new SatelliteDatabaseService(bundledPath, userPath);
        }

        public string Path { get; }
        public SatelliteDatabaseService Service { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
