using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

public class SatelliteDatabaseMergerTests
{
    [Fact]
    public void BuildPlan_adds_new_satellite_and_mode()
    {
        var local = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "SO-50",
                Modes =
                [
                    new SatelliteTransponderMode
                    {
                        Type = "FM VOICE",
                        DownlinkKHz = 436_795,
                        UplinkKHz = 145_850,
                        DownlinkMode = "FMN",
                        UplinkMode = "FMN",
                        Doppler = "NOR"
                    }
                ]
            }
        };

        var remote = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "SO-50",
                Modes =
                [
                    new SatelliteTransponderMode
                    {
                        Type = "FM VOICE",
                        DownlinkKHz = 436_795,
                        UplinkKHz = 145_850,
                        DownlinkMode = "FMN",
                        UplinkMode = "FMN",
                        Doppler = "NOR"
                    },
                    new SatelliteTransponderMode
                    {
                        Type = "Telemetry",
                        DownlinkKHz = 435_000,
                        UplinkKHz = 0,
                        DownlinkMode = "FM",
                        UplinkMode = "FM",
                        Doppler = "NOR"
                    }
                ]
            },
            new()
            {
                Name = "BOTAN",
                Modes =
                [
                    new SatelliteTransponderMode
                    {
                        Type = "Packet",
                        DownlinkKHz = 145_825,
                        UplinkKHz = 145_825,
                        DownlinkMode = "FM",
                        UplinkMode = "FM",
                        Doppler = "NOR"
                    }
                ]
            }
        };

        var plan = SatelliteDatabaseMerger.BuildPlan(local, remote);

        Assert.Single(plan.NewSatellites);
        Assert.Equal("BOTAN", plan.NewSatellites[0].Entry.Name);
        Assert.Single(plan.NewModes);
        Assert.Equal("SO-50", plan.NewModes[0].SatelliteName);
        Assert.Equal("Telemetry", plan.NewModes[0].Mode.Type);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void BuildPlan_detects_conflicting_mode_fields()
    {
        var local = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "ISS",
                Modes =
                [
                    new SatelliteTransponderMode
                    {
                        Type = "Packet",
                        DownlinkKHz = 145_825,
                        UplinkKHz = 145_825,
                        DownlinkMode = "FM",
                        UplinkMode = "FM",
                        Doppler = "NOR"
                    }
                ]
            }
        };

        var remote = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "ISS",
                Modes =
                [
                    new SatelliteTransponderMode
                    {
                        Type = "Packet",
                        DownlinkKHz = 145_825,
                        UplinkKHz = 145_825,
                        DownlinkMode = "FMN",
                        UplinkMode = "FMN",
                        Doppler = "NOR"
                    }
                ]
            }
        };

        var plan = SatelliteDatabaseMerger.BuildPlan(local, remote);

        Assert.Empty(plan.NewSatellites);
        Assert.Empty(plan.NewModes);
        Assert.Single(plan.Conflicts);
        Assert.Equal("ISS", plan.Conflicts[0].SatelliteName);
        Assert.Equal("Packet", plan.Conflicts[0].ModeType);
    }

    [Fact]
    public void Apply_adds_selected_entries_and_resolves_conflicts()
    {
        var local = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "ISS",
                Modes =
                [
                    new SatelliteTransponderMode
                    {
                        Type = "Packet",
                        DownlinkKHz = 145_825,
                        UplinkKHz = 145_825,
                        DownlinkMode = "FM",
                        UplinkMode = "FM",
                        Doppler = "NOR"
                    }
                ]
            }
        };

        var remote = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "ISS",
                Modes =
                [
                    new SatelliteTransponderMode
                    {
                        Type = "Packet",
                        DownlinkKHz = 145_825,
                        UplinkKHz = 145_825,
                        DownlinkMode = "FMN",
                        UplinkMode = "FMN",
                        Doppler = "NOR"
                    }
                ]
            },
            new()
            {
                Name = "BOTAN",
                Modes =
                [
                    new SatelliteTransponderMode
                    {
                        Type = "Packet",
                        DownlinkKHz = 145_825,
                        UplinkKHz = 145_825,
                        DownlinkMode = "FM",
                        UplinkMode = "FM",
                        Doppler = "NOR"
                    }
                ]
            }
        };

        var plan = SatelliteDatabaseMerger.BuildPlan(local, remote);
        var selection = new SatelliteDatabaseMergeSelection
        {
            AcceptedNewSatelliteKeys = { "BOTAN" },
            AcceptRemoteConflictKeys = { plan.Conflicts[0].Key }
        };

        var merged = SatelliteDatabaseMerger.Apply(local, plan, selection);

        Assert.Equal(2, merged.Count);
        var iss = merged.First(e => e.Name == "ISS");
        Assert.Equal("FMN", iss.Modes[0].DownlinkMode);
        Assert.Contains(merged, e => e.Name == "BOTAN");
    }

    [Fact]
    public void BuildPlan_backfills_missing_norad_id_from_remote()
    {
        var local = new List<SatelliteRadioEntry>
        {
            Entry("SO-50", Mode("FM VOICE", 436_795, 145_850, downlinkMode: "FM", uplinkMode: "FM"))
        };

        var remote = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "SO-50",
                NoradId = "27607",
                Modes = [Mode("FM VOICE", 436_795, 145_850, downlinkMode: "FM", uplinkMode: "FM")]
            }
        };

        var plan = SatelliteDatabaseMerger.BuildPlan(local, remote);

        Assert.Single(plan.NoradIdBackfills);
        Assert.Equal("27607", plan.NoradIdBackfills[0].NoradId);

        var merged = SatelliteDatabaseMerger.Apply(local, plan, new SatelliteDatabaseMergeSelection());
        Assert.Equal("27607", merged.Single(e => e.Name == "SO-50").NoradId);
    }

    [Fact]
    public void Apply_new_satellite_preserves_norad_id()
    {
        var remoteEntry = new SatelliteRadioEntry
        {
            Name = "BOTAN",
            NoradId = "65942",
            Modes = [Mode("Packet", 145_825, 145_825, downlinkMode: "FM", uplinkMode: "FM")]
        };

        var plan = SatelliteDatabaseMerger.BuildPlan([], [remoteEntry]);
        var merged = SatelliteDatabaseMerger.Apply(
            [],
            plan,
            new SatelliteDatabaseMergeSelection { AcceptedNewSatelliteKeys = { "BOTAN" } });

        Assert.Equal("65942", merged.Single().NoradId);
    }

    [Fact]
    public void WithoutAcknowledgedConflicts_hides_matching_conflict()
    {
        var local = Entry("RS-44", Mode("FT4", downlink: 435_611.6, uplink: 145_993.61));
        var remote = Entry("RS-44", Mode("FT4", downlink: 435_611, uplink: 145_993.61));
        var plan = SatelliteDatabaseMerger.BuildPlan([local], [remote]);
        var conflict = plan.Conflicts.Single();

        var acknowledgments = SatelliteDatabaseMerger.BuildLocalAcknowledgments(
            plan,
            new SatelliteDatabaseMergeSelection { AcceptLocalConflictKeys = { conflict.Key } });

        var filtered = SatelliteDatabaseMerger.WithoutAcknowledgedConflicts(plan, acknowledgments);

        Assert.Empty(filtered.Conflicts);
        Assert.False(filtered.HasChanges);
    }

    [Fact]
    public void WithoutAcknowledgedConflicts_shows_conflict_when_local_changes()
    {
        var local = Entry("RS-44", Mode("FT4", downlink: 435_611.6, uplink: 145_993.61));
        var remote = Entry("RS-44", Mode("FT4", downlink: 435_611, uplink: 145_993.61));
        var plan = SatelliteDatabaseMerger.BuildPlan([local], [remote]);
        var conflict = plan.Conflicts.Single();

        var acknowledgments = SatelliteDatabaseMerger.BuildLocalAcknowledgments(
            plan,
            new SatelliteDatabaseMergeSelection { AcceptLocalConflictKeys = { conflict.Key } });

        local.Modes[0].DownlinkKHz = 435_611.7;
        var updatedPlan = SatelliteDatabaseMerger.BuildPlan([local], [remote]);
        var filtered = SatelliteDatabaseMerger.WithoutAcknowledgedConflicts(updatedPlan, acknowledgments);

        Assert.Single(filtered.Conflicts);
    }

    [Fact]
    public void ParseJson_accepts_string_frequencies()
    {
        const string json = """
            [
              {
                "name": "TEST",
                "modes": [
                  {
                    "type": "SSTV",
                    "downlink": "437350",
                    "uplink": 0,
                    "downlink_mode": "FM",
                    "uplink_mode": "FM",
                    "doppler": "NOR"
                  }
                ]
              }
            ]
            """;

        var entries = SatelliteDatabaseFile.ParseJson(json);

        Assert.Single(entries);
        Assert.Equal(437_350, entries[0].Modes[0].DownlinkKHz);
    }

    [Fact]
    public void BuildPlan_matches_renamed_local_by_norad_id()
    {
        var local = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "UmKA-1 (RS40-S)",
                NoradId = "57172",
                AlternativeNames = ["UmKA-1"],
                Modes = [Mode("FM VOICE", 436_795, 145_850, downlinkMode: "FM", uplinkMode: "FM")]
            }
        };

        var remote = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "UmKA-1",
                NoradId = "57172",
                Modes =
                [
                    Mode("FM VOICE", 436_795, 145_850, downlinkMode: "FM", uplinkMode: "FM"),
                    Mode("Telemetry", 435_000, 0, downlinkMode: "FM", uplinkMode: "FM")
                ]
            }
        };

        var plan = SatelliteDatabaseMerger.BuildPlan(local, remote);

        Assert.Empty(plan.NewSatellites);
        Assert.Single(plan.NewModes);
        Assert.Equal("UmKA-1 (RS40-S)", plan.NewModes[0].SatelliteName);
        Assert.Equal("Telemetry", plan.NewModes[0].Mode.Type);

        var merged = SatelliteDatabaseMerger.Apply(
            local,
            plan,
            new SatelliteDatabaseMergeSelection { AcceptedNewModeKeys = { plan.NewModes[0].Key } });

        var entry = Assert.Single(merged);
        Assert.Equal("UmKA-1 (RS40-S)", entry.Name);
        Assert.Equal(2, entry.Modes.Count);
        Assert.Contains(entry.AlternativeNames ?? [], a => a == "UmKA-1");
    }

    [Fact]
    public void BuildPlan_matches_renamed_local_by_alternative_name()
    {
        var local = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "UmKA-1 (RS40-S)",
                AlternativeNames = ["UmKA-1"],
                Modes = [Mode("FM VOICE", 436_795, 145_850, downlinkMode: "FM", uplinkMode: "FM")]
            }
        };

        var remote = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "UmKA-1",
                Modes = [Mode("FM VOICE", 436_795, 145_850, downlinkMode: "FM", uplinkMode: "FM")]
            }
        };

        var plan = SatelliteDatabaseMerger.BuildPlan(local, remote);

        Assert.Empty(plan.NewSatellites);
        Assert.Empty(plan.NewModes);
        Assert.Empty(plan.Conflicts);
    }

    private static SatelliteRadioEntry Entry(string name, params SatelliteTransponderMode[] modes) =>
        new() { Name = name, Modes = modes.ToList() };

    private static SatelliteTransponderMode Mode(
        string type,
        double downlink,
        double uplink,
        string downlinkMode = "DATA-USB",
        string uplinkMode = "DATA-LSB",
        string doppler = "REV") =>
        new()
        {
            Type = type,
            DownlinkKHz = downlink,
            UplinkKHz = uplink,
            DownlinkMode = downlinkMode,
            UplinkMode = uplinkMode,
            Doppler = doppler
        };
}
