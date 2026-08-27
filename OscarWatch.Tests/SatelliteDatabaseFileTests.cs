using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

public class SatelliteDatabaseFileTests
{
    [Fact]
    public void Save_and_load_round_trip_preserves_mode_fields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ow-db-{Guid.NewGuid():N}.json");
        try
        {
            var entries = new List<SatelliteRadioEntry>
            {
                new()
                {
                    Name = "TEST-SAT",
                    Modes =
                    [
                        new SatelliteTransponderMode
                        {
                            Type = "FM VOICE",
                            DownlinkKHz = 436_795,
                            UplinkKHz = 145_850,
                            DownlinkMode = "FMN",
                            UplinkMode = "FMN",
                            Doppler = "NOR",
                            CtcssHz = 67.0,
                            CtcssArmHz = 74.4
                        }
                    ]
                }
            };

            SatelliteDatabaseFile.Save(path, entries);
            var loaded = SatelliteDatabaseFile.Load(path);

            Assert.Single(loaded);
            Assert.Equal("TEST-SAT", loaded[0].Name);
            var mode = loaded[0].Modes[0];
            Assert.Equal(436_795, mode.DownlinkKHz);
            Assert.Equal(67.0, mode.CtcssHz);
            Assert.Equal(74.4, mode.CtcssArmHz);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ValidateEntries_rejects_duplicate_names()
    {
        var entries = new List<SatelliteRadioEntry>
        {
            new() { Name = "ISS", Modes = [new SatelliteTransponderMode { Type = "A", DownlinkKHz = 1, UplinkKHz = 1, DownlinkMode = "FM", UplinkMode = "FM" }] },
            new() { Name = "iss", Modes = [new SatelliteTransponderMode { Type = "B", DownlinkKHz = 1, UplinkKHz = 1, DownlinkMode = "FM", UplinkMode = "FM" }] }
        };

        Assert.NotNull(SatelliteDatabaseFile.ValidateEntries(entries));
    }

    [Fact]
    public void NormalizeEntry_zero_pads_norad_id()
    {
        var entry = new SatelliteRadioEntry
        {
            Name = "AO-07",
            NoradId = "7530",
            Modes = [new SatelliteTransponderMode { Type = "FM", DownlinkKHz = 1, UplinkKHz = 1, DownlinkMode = "FM", UplinkMode = "FM" }]
        };

        SatelliteDatabaseFile.NormalizeEntry(entry);

        Assert.Equal("07530", entry.NoradId);
    }

    [Fact]
    public void NormalizeEntry_cleans_alternative_names()
    {
        var entry = new SatelliteRadioEntry
        {
            Name = "UmKA-1 (RS40-S)",
            AlternativeNames = ["  UmKA-1  ", "RS40-S", "umka-1", "UmKA-1 (RS40-S)", ""],
            Modes = [new SatelliteTransponderMode { Type = "FM", DownlinkKHz = 1, UplinkKHz = 1, DownlinkMode = "FM", UplinkMode = "FM" }]
        };

        SatelliteDatabaseFile.NormalizeEntry(entry);

        Assert.Equal(["UmKA-1", "RS40-S"], entry.AlternativeNames);
    }

    [Fact]
    public void ValidateEntries_rejects_duplicate_alternative_name()
    {
        var entries = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "UmKA-1 (RS40-S)",
                AlternativeNames = ["SO-50"],
                Modes = [new SatelliteTransponderMode { Type = "FM", DownlinkKHz = 1, UplinkKHz = 1, DownlinkMode = "FM", UplinkMode = "FM" }]
            },
            new()
            {
                Name = "SO-50",
                Modes = [new SatelliteTransponderMode { Type = "FM", DownlinkKHz = 1, UplinkKHz = 1, DownlinkMode = "FM", UplinkMode = "FM" }]
            }
        };

        Assert.Contains("Duplicate", SatelliteDatabaseFile.ValidateEntries(entries), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateEntries_rejects_invalid_norad_id()
    {
        var entries = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "TEST",
                NoradId = "abc",
                Modes = [new SatelliteTransponderMode { Type = "FM", DownlinkKHz = 1, UplinkKHz = 1, DownlinkMode = "FM", UplinkMode = "FM" }]
            }
        };

        var error = SatelliteDatabaseFile.ValidateEntries(entries);

        Assert.Contains("NORAD ID", error, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeEntry_encodes_alpha5_norad_id()
    {
        var entry = new SatelliteRadioEntry
        {
            Name = "ALPHA5-TEST",
            NoradId = "100000",
            Modes = [new SatelliteTransponderMode { Type = "FM", DownlinkKHz = 1, UplinkKHz = 1, DownlinkMode = "FM", UplinkMode = "FM" }]
        };

        SatelliteDatabaseFile.NormalizeEntry(entry);

        Assert.Equal("A0000", entry.NoradId);
        Assert.Null(SatelliteDatabaseFile.ValidateEntries([entry]));
    }

    [Fact]
    public void NormalizeEntry_accepts_alpha5_field()
    {
        var entry = new SatelliteRadioEntry
        {
            Name = "ALPHA5-TEST",
            NoradId = "a0000",
            Modes = [new SatelliteTransponderMode { Type = "FM", DownlinkKHz = 1, UplinkKHz = 1, DownlinkMode = "FM", UplinkMode = "FM" }]
        };

        SatelliteDatabaseFile.NormalizeEntry(entry);

        Assert.Equal("A0000", entry.NoradId);
        Assert.True(SatelliteDatabaseFile.IsValidNoradId(entry.NoradId!));
    }

    [Fact]
    public void ValidateEntries_rejects_alpha5_letters_i_and_o()
    {
        var entries = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "BAD-I",
                NoradId = "I0000",
                Modes = [new SatelliteTransponderMode { Type = "FM", DownlinkKHz = 1, UplinkKHz = 1, DownlinkMode = "FM", UplinkMode = "FM" }]
            }
        };

        Assert.Contains("NORAD ID", SatelliteDatabaseFile.ValidateEntries(entries), StringComparison.Ordinal);
    }
}
