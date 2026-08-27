using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

public sealed class SatelliteDisplayNameTests
{
    [Fact]
    public void Resolve_returns_catalog_name_when_database_missing()
    {
        Assert.Equal("UmKA-1", SatelliteDisplayName.Resolve("UmKA-1", "57172", database: null));
    }

    [Fact]
    public void Resolve_prefers_database_name_when_matched()
    {
        var database = new StubDatabase(
        [
            new SatelliteRadioEntry
            {
                Name = "UmKA-1 (RS40-S)",
                NoradId = "57172",
                AlternativeNames = ["UmKA-1"],
                Modes = [new SatelliteTransponderMode { Type = "FM", DownlinkKHz = 1, UplinkKHz = 1, DownlinkMode = "FM", UplinkMode = "FM" }]
            }
        ]);

        Assert.Equal(
            "UmKA-1 (RS40-S)",
            SatelliteDisplayName.Resolve("UmKA-1", "57172", database));
    }

    [Fact]
    public void Resolve_keeps_catalog_name_when_no_entry()
    {
        var database = new StubDatabase([]);

        Assert.Equal("UmKA-1", SatelliteDisplayName.Resolve("UmKA-1", "57172", database));
    }

    private sealed class StubDatabase(IReadOnlyList<SatelliteRadioEntry> entries) : ISatelliteDatabaseService
    {
        public IReadOnlyList<SatelliteRadioEntry> Entries { get; } = entries;
        public string ActiveDatabasePath { get; } = "stub.json";
        public bool IsUsingUserDatabase => false;

        public SatelliteRadioEntry? TryGetEntry(string satelliteName, string? noradId = null)
        {
            foreach (var entry in Entries)
            {
                if (!string.IsNullOrWhiteSpace(noradId)
                    && string.Equals(entry.NoradId, noradId, StringComparison.OrdinalIgnoreCase))
                    return entry;

                if (entry.Name.Equals(satelliteName, StringComparison.OrdinalIgnoreCase))
                    return entry;

                if (entry.AlternativeNames?.Any(a =>
                        a.Equals(satelliteName, StringComparison.OrdinalIgnoreCase)) == true)
                    return entry;
            }

            return null;
        }

        public void Reload() { }
    }
}
