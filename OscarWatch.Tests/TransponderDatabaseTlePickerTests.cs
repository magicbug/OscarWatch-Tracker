using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

public class TransponderDatabaseTlePickerTests
{
    [Fact]
    public void ListAvailable_excludes_names_already_in_database()
    {
        var catalog = new List<SatelliteCatalogEntry>
        {
            new() { Name = "AO-07", NoradId = "07530", Line1 = "1", Line2 = "2" },
            new() { Name = "RS-44", NoradId = "43867", Line1 = "1", Line2 = "2" },
            new() { Name = "SO-50", NoradId = "27607", Line1 = "1", Line2 = "2" }
        };

        var available = TransponderDatabaseTlePicker.ListAvailable(catalog, ["RS-44", "JO-97"]);

        Assert.Equal(2, available.Count);
        Assert.Contains(available, e => e.Name == "AO-07");
        Assert.Contains(available, e => e.Name == "SO-50");
        Assert.DoesNotContain(available, e => e.Name == "RS-44");
    }

    [Fact]
    public void ListAvailable_excludes_by_norad_id_and_alternative_name()
    {
        var catalog = new List<SatelliteCatalogEntry>
        {
            new() { Name = "UmKA-1", NoradId = "57172", Line1 = "1", Line2 = "2" },
            new() { Name = "RS40-S", NoradId = "99999", Line1 = "1", Line2 = "2" },
            new() { Name = "AO-07", NoradId = "07530", Line1 = "1", Line2 = "2" }
        };

        var existing = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "UmKA-1 (RS40-S)",
                NoradId = "57172",
                AlternativeNames = ["RS40-S"],
                Modes = [new SatelliteTransponderMode { Type = "FM", DownlinkKHz = 1, UplinkKHz = 1, DownlinkMode = "FM", UplinkMode = "FM" }]
            }
        };

        var available = TransponderDatabaseTlePicker.ListAvailable(catalog, existing);

        Assert.Single(available);
        Assert.Equal("AO-07", available[0].Name);
    }

    [Fact]
    public void ResolveChosenName_prefers_custom_name_when_set()
    {
        Assert.Equal("CUSTOM-1", TransponderDatabaseTlePicker.ResolveChosenName("RS-44", "CUSTOM-1"));
        Assert.Equal("RS-44", TransponderDatabaseTlePicker.ResolveChosenName("RS-44", ""));
        Assert.Null(TransponderDatabaseTlePicker.ResolveChosenName(null, ""));
    }
}
