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

    [Fact]
    public void BuildPlan_updates_stale_norad_id_and_official_oscar_rename()
    {
        // Local still has the pre-designation name and the old Transporter-18 catalogue ID.
        var local = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "UNNE-1B",
                NoradId = "100948",
                Modes =
                [
                    Mode("FM Repeater", 436_888, 145_925, downlinkMode: "FM", uplinkMode: "FM"),
                    Mode("CW", 436_888, 0, downlinkMode: "CW", uplinkMode: "CW")
                ]
            }
        };

        var remote = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "SO-131",
                NoradId = "101101",
                AlternativeNames = ["UNNE-1B"],
                Modes =
                [
                    Mode("FM Repeater", 436_888, 145_925, downlinkMode: "FM", uplinkMode: "FM"),
                    Mode("CW", 436_888, 0, downlinkMode: "CW", uplinkMode: "CW")
                ]
            }
        };

        var plan = SatelliteDatabaseMerger.BuildPlan(local, remote);

        Assert.Empty(plan.NewSatellites);
        Assert.Empty(plan.NewModes);
        Assert.Empty(plan.Conflicts);
        Assert.Single(plan.PreferredNameUpdates);
        Assert.Equal("UNNE-1B", plan.PreferredNameUpdates[0].CurrentName);
        Assert.Equal("SO-131", plan.PreferredNameUpdates[0].NewPreferredName);
        Assert.Single(plan.NoradIdBackfills);
        Assert.Equal("UNNE-1B", plan.NoradIdBackfills[0].SatelliteName);
        Assert.Equal("A1101", plan.NoradIdBackfills[0].NoradId);

        var merged = SatelliteDatabaseMerger.Apply(local, plan, new SatelliteDatabaseMergeSelection());
        var entry = Assert.Single(merged);
        Assert.Equal("SO-131", entry.Name);
        Assert.Equal("A1101", entry.NoradId);
        Assert.Contains(entry.AlternativeNames ?? [], a => a == "UNNE-1B");
        Assert.Equal(2, entry.Modes.Count);

        var tempPath = Path.Combine(Path.GetTempPath(), $"oscarwatch-so131-{Guid.NewGuid():N}.json");
        try
        {
            SatelliteDatabaseFile.Save(tempPath, merged);
            var database = new SatelliteDatabaseService(tempPath, tempPath);
            Assert.NotNull(database.TryGetEntry("SO-131", "A1101"));
            Assert.NotNull(database.TryGetEntry("UNNE-1B", "A1101"));
            Assert.Equal(2, database.TryGetEntry("SO-131", "A1101")!.Modes.Count);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void BuildPlan_updates_stale_norad_id_for_hades_l_to_so132()
    {
        var local = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "HADES-L",
                NoradId = "100909",
                Modes = [Mode("Voice A/U", 436_888, 145_925, downlinkMode: "FM", uplinkMode: "FM")]
            }
        };

        var remote = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "SO-132",
                NoradId = "101102",
                AlternativeNames = ["HADES-L"],
                Modes = [Mode("Voice A/U", 436_888, 145_925, downlinkMode: "FM", uplinkMode: "FM")]
            }
        };

        var plan = SatelliteDatabaseMerger.BuildPlan(local, remote);
        Assert.True(plan.HasChanges);
        Assert.Single(plan.PreferredNameUpdates);
        Assert.Single(plan.NoradIdBackfills);

        var merged = SatelliteDatabaseMerger.Apply(local, plan, new SatelliteDatabaseMergeSelection());
        var entry = Assert.Single(merged);
        Assert.Equal("SO-132", entry.Name);
        Assert.Equal("A1102", entry.NoradId);
        Assert.Contains(entry.AlternativeNames ?? [], a => a == "HADES-L");
    }

    [Fact]
    public void BuildPlan_and_Apply_sync_operating_notices()
    {
        var local = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "SO-131",
                NoradId = "101101",
                Modes = [Mode("FM Repeater", 436_888, 145_925, downlinkMode: "FM", uplinkMode: "FM")]
            }
        };

        var remote = new List<SatelliteRadioEntry>
        {
            new()
            {
                Name = "SO-131",
                NoradId = "101101",
                Modes = [Mode("FM Repeater", 436_888, 145_925, downlinkMode: "FM", uplinkMode: "FM")],
                Notices =
                [
                    new SatelliteOperatingNotice
                    {
                        Message = "Wednesdays (UTC): UNNE-1B repeater is dedicated to FSK digital modes (AMSAT-EA).",
                        DaysOfWeekUtc = ["Wednesday"]
                    }
                ]
            }
        };

        var plan = SatelliteDatabaseMerger.BuildPlan(local, remote);
        Assert.Empty(plan.NewSatellites);
        Assert.Empty(plan.NewModes);
        Assert.Empty(plan.Conflicts);
        Assert.Single(plan.NoticeUpdates);
        Assert.Equal("SO-131", plan.NoticeUpdates[0].SatelliteName);
        Assert.Single(plan.NoticeUpdates[0].Notices);

        var merged = SatelliteDatabaseMerger.Apply(local, plan, new SatelliteDatabaseMergeSelection());
        var entry = Assert.Single(merged);
        var notice = Assert.Single(entry.Notices!);
        Assert.Contains("FSK", notice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["Wednesday"], notice.DaysOfWeekUtc);
    }

    [Fact]
    public void Applied_SO131_notice_is_not_offered_again_on_the_next_startup_check()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ow-notices-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var userPath = Path.Combine(directory, "user.json");
        var bundledPath = Path.Combine(directory, "bundled.json");

        try
        {
            var remote = new List<SatelliteRadioEntry>
            {
                new()
                {
                    Name = "SO-131",
                    NoradId = "101101",
                    Modes = [Mode("FM Repeater", 436_888, 145_925, downlinkMode: "FM", uplinkMode: "FM")],
                    Notices =
                    [
                        new SatelliteOperatingNotice
                        {
                            Message = "Wednesdays (UTC): SO-131 repeater is dedicated to FSK digital modes (AMSAT-EA).",
                            DaysOfWeekUtc = ["Wednesday"]
                        }
                    ]
                }
            };

            SatelliteDatabaseFile.Save(bundledPath, []);
            SatelliteDatabaseFile.Save(userPath, remote.Select(e => new SatelliteRadioEntry
            {
                Name = e.Name,
                NoradId = e.NoradId,
                Modes = e.Modes
            }).ToList());

            var database = new SatelliteDatabaseService(bundledPath, userPath);
            var editor = new SatelliteDatabaseEditor(database, bundledPath, userPath);
            var firstPlan = SatelliteDatabaseMerger.BuildPlan(editor.LoadForEditing(), remote);
            Assert.Single(firstPlan.NoticeUpdates);

            editor.Save(SatelliteDatabaseMerger.Apply(editor.LoadForEditing(), firstPlan, new SatelliteDatabaseMergeSelection()));

            var secondPlan = SatelliteDatabaseMerger.BuildPlan(editor.LoadForEditing(), remote);
            Assert.Empty(secondPlan.NoticeUpdates);
            Assert.False(secondPlan.HasChanges);

            var saved = Assert.Single(SatelliteDatabaseFile.Load(userPath));
            Assert.Contains("FSK", Assert.Single(saved.Notices!).Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
