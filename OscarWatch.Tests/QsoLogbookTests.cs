using Microsoft.Data.Sqlite;
using OscarWatch.Core.Logbook;
using OscarWatch.Core.Models;

namespace OscarWatch.Tests;

public class AdifBandHelperTests
{
    [Theory]
    [InlineData(145_850_000, "2m")]
    [InlineData(435_400_000, "70cm")]
    [InlineData(2400_100_000, "13cm")]
    [InlineData(0, "")]
    public void FromHz_maps_common_satellite_bands(long hz, string expected) =>
        Assert.Equal(expected, AdifBandHelper.FromHz(hz));
}

public class AdifExporterTests
{
    [Fact]
    public void ExportLogbook_writes_satellite_fields()
    {
        var logbook = new QsoLogbook
        {
            Id = 1,
            Name = "Portable",
            CreatedUtc = DateTime.UtcNow,
            MyCallsign = "2M0SQL",
            MyGridSquare = "IO87IP"
        };
        var qso = new QsoRecord
        {
            Id = 1,
            LogbookId = 1,
            QsoUtc = new DateTime(2020, 2, 12, 17, 10, 0, DateTimeKind.Utc),
            Call = "G0ABC",
            RstSent = "59",
            RstRcvd = "59",
            GridSquare = "IO91WM",
            SatName = "SO-50",
            Mode = "FM",
            ModeRx = "FM",
            FreqHz = 145_850_000,
            FreqRxHz = 436_795_000,
            Band = "2m",
            BandRx = "70cm",
            PropMode = "SAT",
            CreatedUtc = DateTime.UtcNow
        };

        var adif = AdifExporter.ExportLogbook(logbook, [qso]);

        Assert.Contains("<CALL:5>G0ABC", adif, StringComparison.Ordinal);
        Assert.Contains("<QSO_DATE:8>20200212", adif, StringComparison.Ordinal);
        Assert.Contains("<TIME_ON:4>1710", adif, StringComparison.Ordinal);
        Assert.Contains("<SAT_NAME:5>SO-50", adif, StringComparison.Ordinal);
        Assert.Contains("<PROP_MODE:3>SAT", adif, StringComparison.Ordinal);
        Assert.Contains("<STATION_CALLSIGN:6>2M0SQL", adif, StringComparison.Ordinal);
        Assert.Contains("<MY_GRIDSQUARE:6>IO87IP", adif, StringComparison.Ordinal);
        Assert.Contains("<EOR>", adif, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportLogbook_writes_dxcc_and_country()
    {
        var logbook = new QsoLogbook
        {
            Id = 1,
            Name = "Portable",
            CreatedUtc = DateTime.UtcNow,
            MyCallsign = "MM9SQL",
            MyGridSquare = "IO87IP"
        };
        var qso = new QsoRecord
        {
            Id = 1,
            LogbookId = 1,
            QsoUtc = new DateTime(2020, 2, 12, 17, 10, 0, DateTimeKind.Utc),
            Call = "G0ABC",
            Dxcc = 223,
            Country = "England",
            SatName = "SO-50",
            Mode = "FM",
            ModeRx = "FM",
            PropMode = "SAT",
            CreatedUtc = DateTime.UtcNow
        };

        var adif = AdifExporter.ExportLogbook(logbook, [qso]);

        Assert.Contains("<DXCC:3>223", adif, StringComparison.Ordinal);
        Assert.Contains("<COUNTRY:7>England", adif, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportLogbook_omits_dxcc_when_unresolved()
    {
        var logbook = new QsoLogbook
        {
            Id = 1,
            Name = "Portable",
            CreatedUtc = DateTime.UtcNow
        };
        var qso = new QsoRecord
        {
            Id = 1,
            LogbookId = 1,
            QsoUtc = DateTime.UtcNow,
            Call = "ZZ9ZZ",
            SatName = "SO-50",
            Mode = "FM",
            ModeRx = "FM",
            PropMode = "SAT",
            CreatedUtc = DateTime.UtcNow
        };

        var adif = AdifExporter.ExportLogbook(logbook, [qso]);

        Assert.DoesNotContain("<DXCC:", adif, StringComparison.Ordinal);
        Assert.DoesNotContain("<COUNTRY:", adif, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportLogbook_writes_comma_separated_grids()
    {
        var logbook = new QsoLogbook
        {
            Id = 1,
            Name = "Line",
            CreatedUtc = DateTime.UtcNow,
            MyCallsign = "2M0SQL",
            MyGridSquare = "IO77,IO87"
        };
        var qso = new QsoRecord
        {
            Id = 1,
            LogbookId = 1,
            QsoUtc = new DateTime(2020, 2, 12, 17, 10, 0, DateTimeKind.Utc),
            Call = "G0ABC",
            GridSquare = "IO91,IO92",
            SatName = "SO-50",
            Mode = "FM",
            ModeRx = "FM",
            FreqHz = 145_850_000,
            FreqRxHz = 436_795_000,
            PropMode = "SAT",
            CreatedUtc = DateTime.UtcNow
        };

        var adif = AdifExporter.ExportLogbook(logbook, [qso]);

        Assert.Contains("<GRIDSQUARE:9>IO91,IO92", adif, StringComparison.Ordinal);
        Assert.Contains("<MY_GRIDSQUARE:9>IO77,IO87", adif, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportLogbook_header_uses_program_metadata_only()
    {
        var logbook = new QsoLogbook
        {
            Id = 1,
            Name = "Main logbook",
            CreatedUtc = DateTime.UtcNow,
            MyCallsign = "2M0SQL",
            MyGridSquare = "IO87IP"
        };
        var qso = new QsoRecord
        {
            Id = 1,
            LogbookId = 1,
            QsoUtc = new DateTime(2020, 2, 12, 17, 10, 0, DateTimeKind.Utc),
            Call = "G0ABC",
            PropMode = "SAT",
            CreatedUtc = DateTime.UtcNow
        };

        var adif = AdifExporter.ExportLogbook(logbook, [qso]);
        var eohIndex = adif.IndexOf("<EOH>", StringComparison.Ordinal);
        Assert.True(eohIndex > 0);

        var header = adif[..eohIndex];
        Assert.Contains("<PROGRAMID:10>OscarWatch", header, StringComparison.Ordinal);
        Assert.DoesNotContain("<COMMENT:", header, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportLogbook_escapes_special_characters_in_field_data()
    {
        var logbook = new QsoLogbook
        {
            Id = 1,
            Name = "Portable",
            CreatedUtc = DateTime.UtcNow,
            MyCallsign = "2M0SQL",
            MyGridSquare = "IO87IP"
        };
        var qso = new QsoRecord
        {
            Id = 1,
            LogbookId = 1,
            QsoUtc = new DateTime(2020, 2, 12, 17, 10, 0, DateTimeKind.Utc),
            Call = "G0ABC",
            Comment = "test<CALL:3>ABC",
            SatName = "SO-50",
            Mode = "FM",
            ModeRx = "FM",
            PropMode = "SAT",
            CreatedUtc = DateTime.UtcNow
        };

        var adif = AdifExporter.ExportLogbook(logbook, [qso]);

        Assert.Contains("<COMMENT:16>test\\<CALL:3>ABC", adif, StringComparison.Ordinal);
        Assert.DoesNotContain("<CALL:3>ABC<EOR>", adif, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportLogbook_remaps_satellite_names_for_lotw()
    {
        var logbook = new QsoLogbook
        {
            Id = 1,
            Name = "Portable",
            CreatedUtc = DateTime.UtcNow,
            MyCallsign = "2M0SQL",
            MyGridSquare = "IO87IP"
        };
        var qso = new QsoRecord
        {
            Id = 1,
            LogbookId = 1,
            QsoUtc = new DateTime(2020, 2, 12, 17, 10, 0, DateTimeKind.Utc),
            Call = "G0ABC",
            SatName = "ISS",
            PropMode = "SAT",
            CreatedUtc = DateTime.UtcNow
        };

        var standard = AdifExporter.ExportLogbook(logbook, [qso], forLotw: false);
        var lotw = AdifExporter.ExportLogbook(logbook, [qso], forLotw: true);

        Assert.Contains("<SAT_NAME:3>ISS", standard, StringComparison.Ordinal);
        Assert.Contains("<SAT_NAME:5>ARISS", lotw, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ISS", "ARISS")]
    [InlineData("GREENCUBE", "IO-117")]
    [InlineData("TEVEL2-4", "TEV2-4")]
    [InlineData("SO-50", "SO-50")]
    public void LotwSatelliteNameMapper_maps_known_names_and_preserves_unknown(string source, string expected) =>
        Assert.Equal(expected, LotwSatelliteNameMapper.MapForExport(source, forLotw: true));

    [Fact]
    public void LotwSatelliteNameMapper_leaves_name_unchanged_when_not_for_lotw() =>
        Assert.Equal("ISS", LotwSatelliteNameMapper.MapForExport("ISS", forLotw: false));

    [Theory]
    [InlineData(@"back\slash", @"back\\slash")]
    [InlineData("line\nbreak", "line\\nbreak")]
    [InlineData("angle<bracket", "angle\\<bracket")]
    public void EscapeAdifValue_escapes_adif_special_characters(string input, string expected) =>
        Assert.Equal(expected, AdifExporter.EscapeAdifValue(input));

    [Fact]
    public void ExportLogbook_maps_ssb_submodes_and_omits_mode_rx()
    {
        var logbook = new QsoLogbook
        {
            Id = 1,
            Name = "Portable",
            CreatedUtc = DateTime.UtcNow,
            MyCallsign = "2M0SQL",
            MyGridSquare = "IO87IP"
        };
        var qso = new QsoRecord
        {
            Id = 1,
            LogbookId = 1,
            QsoUtc = new DateTime(2020, 2, 12, 17, 10, 0, DateTimeKind.Utc),
            Call = "G0ABC",
            SatName = "AO-07",
            Mode = "LSB",
            ModeRx = "USB",
            FreqHz = 145_950_000,
            FreqRxHz = 435_850_000,
            Band = "2m",
            BandRx = "70cm",
            PropMode = "SAT",
            CreatedUtc = DateTime.UtcNow
        };

        var adif = AdifExporter.ExportLogbook(logbook, [qso]);

        Assert.Contains("<MODE:3>SSB", adif, StringComparison.Ordinal);
        Assert.Contains("<SUBMODE:3>LSB", adif, StringComparison.Ordinal);
        Assert.Contains("<COMMENT:11>RX mode USB", adif, StringComparison.Ordinal);
        Assert.DoesNotContain("MODE_RX", adif, StringComparison.Ordinal);
        Assert.DoesNotContain("<MODE:3>LSB", adif, StringComparison.Ordinal);
        Assert.DoesNotContain("<MODE:3>USB", adif, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportLogbook_merges_user_comment_with_rx_mode_note()
    {
        var logbook = new QsoLogbook
        {
            Id = 1,
            Name = "Portable",
            CreatedUtc = DateTime.UtcNow
        };
        var qso = new QsoRecord
        {
            Id = 1,
            LogbookId = 1,
            QsoUtc = new DateTime(2020, 2, 12, 17, 10, 0, DateTimeKind.Utc),
            Call = "G0ABC",
            Comment = "Portable",
            SatName = "RS-44",
            Mode = "LSB",
            ModeRx = "USB",
            FreqHz = 145_960_000,
            FreqRxHz = 435_610_000,
            PropMode = "SAT",
            CreatedUtc = DateTime.UtcNow
        };

        var adif = AdifExporter.ExportLogbook(logbook, [qso]);

        Assert.Contains("<COMMENT:", adif, StringComparison.Ordinal);
        Assert.Contains("Portable · RX mode USB", adif, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LSB", "SSB", "LSB")]
    [InlineData("USB", "SSB", "USB")]
    [InlineData("FM", "FM", null)]
    [InlineData("FMN", "FM", null)]
    [InlineData("CW", "CW", null)]
    [InlineData("FT4", "MFSK", "FT4")]
    [InlineData("DATA-USB", "SSB", "USB")]
    public void AdifModeHelper_maps_operating_modes(string source, string mode, string? submode)
    {
        var mapped = AdifModeHelper.FromOperatingMode(source);
        Assert.Equal(mode, mapped.Mode);
        Assert.Equal(submode, mapped.Submode);
    }
}

public class QsoLogbookRepositoryTests : IDisposable
{
    private readonly string _path;
    private readonly QsoLogbookRepository _repository;

    public QsoLogbookRepositoryTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"oscarwatch-logbook-{Guid.NewGuid():N}.db");
        _repository = new QsoLogbookRepository(_path);
    }

    [Fact]
    public async Task Create_logbook_and_add_qso_round_trips()
    {
        await _repository.InitializeAsync();

        var logbook = await _repository.CreateLogbookAsync(new QsoLogbookCreateRequest
        {
            Name = "Test activation",
            MyCallsign = "2M0SQL",
            MyGridSquare = "IO87IP"
        });

        var qso = await _repository.AddQsoAsync(new QsoRecordCreateRequest
        {
            LogbookId = logbook.Id,
            QsoUtc = new DateTime(2020, 2, 12, 17, 10, 0, DateTimeKind.Utc),
            Call = "g0abc",
            RstSent = "59",
            RstRcvd = "59",
            GridSquare = "io91wm",
            SatName = "AO-91",
            Mode = "FM",
            ModeRx = "FM",
            FreqHz = 145_960_000,
            FreqRxHz = 435_400_000,
            Band = "2m",
            BandRx = "70cm"
        });

        Assert.Equal("G0ABC", qso.Call);
        Assert.Equal("IO91WM", qso.GridSquare);

        var rows = await _repository.ListQsosAsync(logbook.Id);
        Assert.Single(rows);

        var filtered = await _repository.SearchQsosByCallAsync(logbook.Id, "G0");
        Assert.Single(filtered);

        var previous = await _repository.FindLatestQsoForCallAsync(logbook.Id, "G0ABC");
        Assert.NotNull(previous);
        Assert.Equal("IO91WM", previous!.GridSquare);

        var (calls, grids) = await _repository.LoadWorkedCallAndGridFieldsAsync();
        Assert.Contains("G0ABC", calls);
        Assert.Contains("IO91", grids);
    }

    [Fact]
    public async Task Add_qso_normalizes_multi_grid_locators()
    {
        await _repository.InitializeAsync();

        var logbook = await _repository.CreateLogbookAsync(new QsoLogbookCreateRequest
        {
            Name = "Grid line",
            MyCallsign = "2m0sql",
            MyGridSquare = "io77 / io87"
        });

        Assert.Equal("2M0SQL", logbook.MyCallsign);
        Assert.Equal("IO77,IO87", logbook.MyGridSquare);

        var qso = await _repository.AddQsoAsync(new QsoRecordCreateRequest
        {
            LogbookId = logbook.Id,
            QsoUtc = DateTime.UtcNow,
            Call = "g0abc",
            GridSquare = "io91, io92"
        });

        Assert.Equal("IO91,IO92", qso.GridSquare);
    }

    [Fact]
    public async Task UpdateQsoAsync_updates_entry_fields_and_preserves_time_and_satellite()
    {
        await _repository.InitializeAsync();

        var logbook = await _repository.CreateLogbookAsync(new QsoLogbookCreateRequest
        {
            Name = "Edit test",
            MyCallsign = "2M0SQL",
            MyGridSquare = "IO87IP"
        });

        var qsoUtc = new DateTime(2020, 2, 12, 17, 10, 0, DateTimeKind.Utc);
        var created = await _repository.AddQsoAsync(new QsoRecordCreateRequest
        {
            LogbookId = logbook.Id,
            QsoUtc = qsoUtc,
            Call = "G0ABC",
            RstSent = "59",
            RstRcvd = "59",
            GridSquare = "IO91WM",
            Name = "Alice",
            Comment = "First",
            SatName = "SO-50",
            Mode = "FM",
            ModeRx = "FM",
            FreqHz = 145_850_000,
            FreqRxHz = 436_795_000,
            Band = "2m",
            BandRx = "70cm"
        });

        var updated = await _repository.UpdateQsoAsync(new QsoRecordUpdateRequest
        {
            Id = created.Id,
            QsoUtc = qsoUtc,
            Call = "g0xyz",
            RstSent = "599",
            RstRcvd = "599",
            GridSquare = "io92sl",
            Name = "Bob",
            Comment = "Corrected",
            SatName = created.SatName,
            Mode = created.Mode,
            ModeRx = created.ModeRx,
            FreqHz = created.FreqHz,
            FreqRxHz = created.FreqRxHz,
            Band = created.Band,
            BandRx = created.BandRx
        });

        Assert.Equal("G0XYZ", updated.Call);
        Assert.Equal("IO92SL", updated.GridSquare);
        Assert.Equal("Bob", updated.Name);
        Assert.Equal("Corrected", updated.Comment);
        Assert.Equal(qsoUtc, updated.QsoUtc);
        Assert.Equal("SO-50", updated.SatName);
        Assert.Equal(145_850_000, updated.FreqHz);
    }

    [Fact]
    public async Task ListQsosAsync_filters_by_utc_date_range()
    {
        await _repository.InitializeAsync();

        var logbook = await _repository.CreateLogbookAsync(new QsoLogbookCreateRequest
        {
            Name = "Range test",
            MyCallsign = "2M0SQL",
            MyGridSquare = "IO87IP"
        });

        await _repository.AddQsoAsync(new QsoRecordCreateRequest
        {
            LogbookId = logbook.Id,
            QsoUtc = new DateTime(2026, 7, 11, 18, 0, 0, DateTimeKind.Utc),
            Call = "G1AAA"
        });
        await _repository.AddQsoAsync(new QsoRecordCreateRequest
        {
            LogbookId = logbook.Id,
            QsoUtc = new DateTime(2026, 7, 12, 12, 30, 0, DateTimeKind.Utc),
            Call = "G2BBB"
        });
        await _repository.AddQsoAsync(new QsoRecordCreateRequest
        {
            LogbookId = logbook.Id,
            QsoUtc = new DateTime(2026, 7, 13, 9, 15, 0, DateTimeKind.Utc),
            Call = "G3CCC"
        });

        var july12 = await _repository.ListQsosAsync(
            logbook.Id,
            new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc));
        Assert.Single(july12);
        Assert.Equal("G2BBB", july12[0].Call);

        Assert.Equal(3, await _repository.CountQsosAsync(logbook.Id));
        Assert.Equal(1, await _repository.CountQsosAsync(
            logbook.Id,
            new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void QsoLogbookExportRange_normalizes_swapped_dates_and_builds_query_bounds()
    {
        var (from, to) = QsoLogbookExportRange.NormalizeUtcDates(
            new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc), to);

        var (fromInclusive, toExclusive) = QsoLogbookExportRange.ToQueryBounds(new QsoAdifExportOptions
        {
            Scope = QsoAdifExportScope.DateRange,
            FromUtcDate = from,
            ToUtcDate = to
        });
        Assert.Equal(from, fromInclusive);
        Assert.Equal(new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc), toExclusive);
    }

    [Fact]
    public async Task UpdateLogbookAsync_updates_name_callsign_and_grid()
    {
        await _repository.InitializeAsync();

        var logbook = await _repository.CreateLogbookAsync(new QsoLogbookCreateRequest
        {
            Name = "Portable",
            MyCallsign = "2M0SQL",
            MyGridSquare = "IO87IP"
        });

        var updated = await _repository.UpdateLogbookAsync(new QsoLogbookUpdateRequest
        {
            Id = logbook.Id,
            Name = "SOTA day",
            MyCallsign = "m0abc/p",
            MyGridSquare = "io77, io87"
        });

        Assert.Equal("SOTA day", updated.Name);
        Assert.Equal("M0ABC/P", updated.MyCallsign);
        Assert.Equal("IO77,IO87", updated.MyGridSquare);
        Assert.Equal(logbook.CreatedUtc, updated.CreatedUtc);

        var listed = await _repository.ListLogbooksAsync();
        Assert.Contains(listed, item => item.Id == logbook.Id && item.Name == "SOTA day");
    }

    [Fact]
    public async Task DeleteLogbookAsync_removes_qsos_via_foreign_key_cascade()
    {
        await _repository.InitializeAsync();

        var logbook = await _repository.CreateLogbookAsync(new QsoLogbookCreateRequest
        {
            Name = "Cascade test",
            MyCallsign = "2M0SQL",
            MyGridSquare = "IO87IP"
        });

        await _repository.AddQsoAsync(new QsoRecordCreateRequest
        {
            LogbookId = logbook.Id,
            QsoUtc = DateTime.UtcNow,
            Call = "G0ABC"
        });

        await _repository.DeleteLogbookAsync(logbook.Id);

        var rows = await _repository.ListQsosAsync(logbook.Id);
        Assert.Empty(rows);

        await using var connection = new SqliteConnection($"Data Source={_path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM qsos WHERE logbook_id = $id";
        command.Parameters.AddWithValue("$id", logbook.Id);
        var orphanCount = (long)(await command.ExecuteScalarAsync() ?? 0L);
        Assert.Equal(0, orphanCount);
    }

    [Fact]
    public async Task Cloudlog_settings_and_upload_status_round_trip()
    {
        await _repository.InitializeAsync();

        var logbook = await _repository.CreateLogbookAsync(new QsoLogbookCreateRequest
        {
            Name = "Cloudlog test",
            MyCallsign = "2M0SQL",
            MyGridSquare = "IO87IP"
        });

        var updatedLogbook = await _repository.UpdateLogbookCloudlogSettingsAsync(new QsoLogbookCloudlogSettingsRequest
        {
            Id = logbook.Id,
            CloudlogAutoUpload = true,
            CloudlogStationProfileId = 42
        });

        Assert.True(updatedLogbook.CloudlogAutoUpload);
        Assert.Equal(42, updatedLogbook.CloudlogStationProfileId);

        var qso = await _repository.AddQsoAsync(new QsoRecordCreateRequest
        {
            LogbookId = logbook.Id,
            QsoUtc = DateTime.UtcNow,
            Call = "G0ABC",
            CloudlogUploadStatus = CloudlogUploadStatus.Pending
        });

        Assert.Equal(CloudlogUploadStatus.Pending, qso.CloudlogUploadStatus);

        await _repository.UpdateQsoCloudlogUploadStateAsync(
            qso.Id,
            CloudlogUploadStatus.Failed,
            1,
            "Network error",
            null);

        var pending = await _repository.ListQsosPendingCloudlogUploadAsync();
        Assert.Contains(pending, item => item.Id == qso.Id);

        await _repository.ResetFailedCloudlogUploadsAsync(logbook.Id);
        var reloaded = await _repository.GetQsoByIdAsync(qso.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(CloudlogUploadStatus.Pending, reloaded!.CloudlogUploadStatus);
        Assert.Equal(0, reloaded.CloudlogUploadAttempts);
    }

    public void Dispose()
    {
        _repository.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
