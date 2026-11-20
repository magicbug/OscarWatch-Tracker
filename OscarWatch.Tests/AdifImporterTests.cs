using OscarWatch.Core.Logbook;

namespace OscarWatch.Tests;

public class AdifImporterTests
{
    [Fact]
    public void Parse_reads_header_and_wsjtx_style_records()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "wsjtx_log.adi");
        var text = File.ReadAllText(path);

        var parsed = AdifImporter.Parse(text);

        Assert.Equal(3, parsed.Records.Count);
        Assert.Equal(0, parsed.UnparsedCount);

        var sat = parsed.Records[0];
        Assert.Equal("G0ABC", sat.Call);
        Assert.Equal(new DateTime(2025, 11, 18, 14, 30, 15, DateTimeKind.Utc), sat.QsoUtc);
        Assert.Equal("FT4", sat.Mode);
        Assert.Equal("QO-100", sat.SatName);
        Assert.Equal("SAT", sat.PropMode);
        Assert.Equal("IO91WM", sat.GridSquare);
        Assert.Equal(435_600_000, sat.FreqHz);
        Assert.Equal("70cm", sat.Band);
        Assert.True(sat.IsSatelliteContact);

        var hf = parsed.Records[1];
        Assert.Equal("PA3EK", hf.Call);
        Assert.Equal(new DateTime(2025, 11, 18, 15, 0, 0, DateTimeKind.Utc), hf.QsoUtc);
        Assert.Equal("FT8", hf.Mode);
        Assert.Equal("", hf.PropMode);
        Assert.Equal("", hf.SatName);
        Assert.False(hf.IsSatelliteContact);
        Assert.Equal(14_074_000, hf.FreqHz);
        Assert.Equal("20m", hf.Band);
    }

    [Fact]
    public void Parse_works_without_eoh_header()
    {
        const string adi = """
            <CALL:5>G0ABC<QSO_DATE:8>20200212<TIME_ON:4>1710<MODE:2>FM<SAT_NAME:5>SO-50<PROP_MODE:3>SAT<EOR>
            """;

        var parsed = AdifImporter.Parse(adi);

        Assert.Single(parsed.Records);
        Assert.Equal("G0ABC", parsed.Records[0].Call);
        Assert.Equal(new DateTime(2020, 2, 12, 17, 10, 0, DateTimeKind.Utc), parsed.Records[0].QsoUtc);
    }

    [Theory]
    [InlineData("SSB", "LSB", "LSB")]
    [InlineData("SSB", "USB", "USB")]
    [InlineData("MFSK", "FT4", "FT4")]
    [InlineData("FT8", null, "FT8")]
    [InlineData("FM", null, "FM")]
    [InlineData("CW", "", "CW")]
    public void AdifModeHelper_ToOperatingMode_reverses_export(string mode, string? submode, string expected) =>
        Assert.Equal(expected, AdifModeHelper.ToOperatingMode(mode, submode));

    [Fact]
    public void Parse_maps_frequency_mhz_to_hz_and_fills_band()
    {
        const string adi = """
            <CALL:5>G0ABC<QSO_DATE:8>20200212<TIME_ON:4>1710<FREQ:10>145.850000<MODE:2>FM<EOR>
            """;

        var parsed = AdifImporter.Parse(adi);
        var qso = Assert.Single(parsed.Records);
        Assert.Equal(145_850_000, qso.FreqHz);
        Assert.Equal("2m", qso.Band);
    }

    [Fact]
    public void Parse_preserves_blank_prop_mode_and_sat_name()
    {
        const string adi = """
            <CALL:5>G0ABC<QSO_DATE:8>20200212<TIME_ON:4>1710<MODE:2>FM<SAT_NAME:5>SO-50<EOR>
            """;

        var qso = Assert.Single(AdifImporter.Parse(adi).Records);
        Assert.Equal("", qso.PropMode);
        Assert.Equal("SO-50", qso.SatName);
        Assert.True(qso.IsSatelliteContact);
    }

    [Fact]
    public void Parse_uses_time_off_when_time_on_missing()
    {
        const string adi = """
            <CALL:5>G0ABC<QSO_DATE:8>20200212<TIME_OFF:6>171045<MODE:2>FM<EOR>
            """;

        var qso = Assert.Single(AdifImporter.Parse(adi).Records);
        Assert.Equal(new DateTime(2020, 2, 12, 17, 10, 45, DateTimeKind.Utc), qso.QsoUtc);
    }

    [Fact]
    public void Parse_unescapes_special_characters()
    {
        const string adi = """
            <CALL:5>G0ABC<QSO_DATE:8>20200212<TIME_ON:4>1710<COMMENT:16>test\<CALL:3>ABC<EOR>
            """;

        var qso = Assert.Single(AdifImporter.Parse(adi).Records);
        Assert.Equal("test<CALL:3>ABC", qso.Comment);
    }

    [Fact]
    public void Parse_counts_malformed_record_and_continues()
    {
        const string adi = """
            <CALL:5>G0ABC<QSO_DATE:8>20200212<TIME_ON:4>1710<MODE:2>FM<EOR>
            <QSO_DATE:8>20200212<TIME_ON:4>1711<MODE:2>FM<EOR>
            <CALL:5>G0DEF<QSO_DATE:8>20200212<TIME_ON:4>1712<MODE:2>FM<EOR>
            """;

        var parsed = AdifImporter.Parse(adi);
        Assert.Equal(2, parsed.Records.Count);
        Assert.Equal(1, parsed.UnparsedCount);
        Assert.Equal("G0ABC", parsed.Records[0].Call);
        Assert.Equal("G0DEF", parsed.Records[1].Call);
    }

    [Fact]
    public void Duplicate_key_matches_hhmm_and_hhmmss_to_the_minute()
    {
        var withSeconds = new DateTime(2020, 2, 12, 17, 10, 45, DateTimeKind.Utc);
        var toMinute = new DateTime(2020, 2, 12, 17, 10, 0, DateTimeKind.Utc);

        Assert.Equal(
            AdifImporter.FormatDuplicateKey("g0abc", withSeconds),
            AdifImporter.FormatDuplicateKey("G0ABC", toMinute));
    }

    [Fact]
    public void Classifier_marks_logbook_and_in_file_duplicates()
    {
        const string adi = """
            <CALL:5>G0ABC<QSO_DATE:8>20200212<TIME_ON:4>1710<MODE:2>FM<SAT_NAME:5>SO-50<PROP_MODE:3>SAT<EOR>
            <CALL:5>G0DEF<QSO_DATE:8>20200212<TIME_ON:6>171100<MODE:2>FM<EOR>
            <CALL:5>G0DEF<QSO_DATE:8>20200212<TIME_ON:4>1711<MODE:2>FM<EOR>
            <CALL:5>G0XYZ<QSO_DATE:8>20200212<TIME_ON:4>1800<MODE:3>FT8<EOR>
            """;

        var parsed = AdifImporter.Parse(adi);
        var existing = AdifImportClassifier.BuildDuplicateKeySet(
        [
            ("G0ABC", new DateTime(2020, 2, 12, 17, 10, 0, DateTimeKind.Utc))
        ]);

        var preview = AdifImportClassifier.BuildPreview(parsed, existing, satelliteOnly: false);

        Assert.Equal(2, preview.NewCount);
        Assert.Equal(2, preview.DuplicateCount);
        Assert.Contains(preview.Duplicates, d =>
            d.Qso.Call == "G0ABC" && d.DuplicateSource == AdifImportDuplicateSource.Logbook);
        Assert.Contains(preview.Duplicates, d =>
            d.Qso.Call == "G0DEF" && d.DuplicateSource == AdifImportDuplicateSource.EarlierInFile);
        Assert.Contains(preview.NewQsos, n => n.Qso.Call == "G0XYZ");
    }

    [Fact]
    public void Classifier_satellite_only_filters_terrestrial()
    {
        const string adi = """
            <CALL:5>G0ABC<QSO_DATE:8>20200212<TIME_ON:4>1710<MODE:2>FM<SAT_NAME:5>SO-50<PROP_MODE:3>SAT<EOR>
            <CALL:5>G0DEF<QSO_DATE:8>20200212<TIME_ON:4>1800<MODE:3>FT8<EOR>
            """;

        var parsed = AdifImporter.Parse(adi);
        var preview = AdifImportClassifier.BuildPreview(
            parsed,
            new HashSet<string>(StringComparer.Ordinal),
            satelliteOnly: true);

        Assert.Equal(1, preview.NewCount);
        Assert.Equal(1, preview.FilteredOutCount);
        Assert.Equal("G0ABC", Assert.Single(preview.NewQsos).Qso.Call);
    }

    [Fact]
    public void Roundtrip_export_then_import_preserves_core_fields()
    {
        var logbook = new OscarWatch.Core.Models.QsoLogbook
        {
            Id = 1,
            Name = "Portable",
            CreatedUtc = DateTime.UtcNow,
            MyCallsign = "MM9SQL",
            MyGridSquare = "IO87IP"
        };
        var qso = new OscarWatch.Core.Models.QsoRecord
        {
            Id = 1,
            LogbookId = 1,
            QsoUtc = new DateTime(2020, 2, 12, 17, 10, 0, DateTimeKind.Utc),
            Call = "G0ABC",
            RstSent = "59",
            RstRcvd = "59",
            GridSquare = "IO91WM",
            SatName = "SO-50",
            Mode = "FT4",
            ModeRx = "FT4",
            FreqHz = 435_600_000,
            FreqRxHz = 435_600_000,
            Band = "70cm",
            BandRx = "70cm",
            PropMode = "SAT",
            Dxcc = 223,
            Country = "England",
            CreatedUtc = DateTime.UtcNow
        };

        var adif = AdifExporter.ExportLogbook(logbook, [qso]);
        var imported = Assert.Single(AdifImporter.Parse(adif).Records);

        Assert.Equal("G0ABC", imported.Call);
        Assert.Equal(qso.QsoUtc, imported.QsoUtc);
        Assert.Equal("FT4", imported.Mode);
        Assert.Equal("SO-50", imported.SatName);
        Assert.Equal("SAT", imported.PropMode);
        Assert.Equal(435_600_000, imported.FreqHz);
        Assert.Equal("70cm", imported.Band);
        Assert.Equal(223, imported.Dxcc);
        Assert.Equal("England", imported.Country);
        Assert.DoesNotContain("STATION_CALLSIGN", imported.Comment, StringComparison.Ordinal);
    }
}
