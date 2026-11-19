using System.Globalization;
using System.Text;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;

namespace OscarWatch.Core.Logbook;

public static class AdifExporter
{
    public static string ExportLogbook(QsoLogbook logbook, IReadOnlyList<QsoRecord> qsos, bool forLotw = false)
    {
        var sb = new StringBuilder();
        AppendHeader(sb);
        foreach (var qso in qsos.OrderBy(q => q.QsoUtc))
            AppendRecord(sb, logbook, qso, forLotw);

        return sb.ToString();
    }

    public static string ExportRecord(QsoLogbook logbook, QsoRecord qso, bool forLotw = false)
    {
        var sb = new StringBuilder();
        AppendRecord(sb, logbook, qso, forLotw);
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb)
    {
        AppendField(sb, "PROGRAMID", "OscarWatch");
        AppendField(sb, "PROGRAMVERSION", typeof(AdifExporter).Assembly.GetName().Version?.ToString(3) ?? "1.0");
        sb.AppendLine("<EOH>");
    }

    private static void AppendRecord(StringBuilder sb, QsoLogbook logbook, QsoRecord qso, bool forLotw)
    {
        AppendField(sb, "CALL", MaidenheadLocator.NormalizeCallsign(qso.Call));
        AppendField(sb, "QSO_DATE", QsoLogbookTime.NormalizeToUtc(qso.QsoUtc).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        AppendField(sb, "TIME_ON", QsoLogbookTime.NormalizeToUtc(qso.QsoUtc).ToString("HHmm", CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(qso.RstSent))
            AppendField(sb, "RST_SENT", qso.RstSent.Trim());
        if (!string.IsNullOrWhiteSpace(qso.RstRcvd))
            AppendField(sb, "RST_RCVD", qso.RstRcvd.Trim());
        if (!string.IsNullOrWhiteSpace(qso.GridSquare))
            AppendField(sb, "GRIDSQUARE", MaidenheadLocator.NormalizeGrids(qso.GridSquare));
        if (!string.IsNullOrWhiteSpace(qso.Name))
            AppendField(sb, "NAME", qso.Name.Trim());

        var comment = AdifModeHelper.MergeComment(
            qso.Comment,
            AdifModeHelper.BuildRxModeComment(qso.Mode, qso.ModeRx));
        if (!string.IsNullOrWhiteSpace(comment))
            AppendField(sb, "COMMENT", comment);

        if (!string.IsNullOrWhiteSpace(logbook.MyCallsign))
            AppendField(sb, "STATION_CALLSIGN", MaidenheadLocator.NormalizeCallsign(logbook.MyCallsign));
        if (!string.IsNullOrWhiteSpace(logbook.MyGridSquare))
            AppendField(sb, "MY_GRIDSQUARE", MaidenheadLocator.NormalizeGrids(logbook.MyGridSquare));
        if (!forLotw)
            AppendActivationRefs(sb, qso);

        if (!string.IsNullOrWhiteSpace(qso.SatName))
        {
            var satName = LotwSatelliteNameMapper.MapForExport(qso.SatName, forLotw);
            if (!string.IsNullOrWhiteSpace(satName))
                AppendField(sb, "SAT_NAME", satName);
        }
        AppendField(sb, "PROP_MODE", string.IsNullOrWhiteSpace(qso.PropMode) ? "SAT" : qso.PropMode.Trim());

        if (qso.FreqHz > 0)
            AppendField(sb, "FREQ", FormatFreqMhz(qso.FreqHz));
        if (!string.IsNullOrWhiteSpace(qso.Band))
            AppendField(sb, "BAND", qso.Band.Trim());

        var txMode = AdifModeHelper.FromOperatingMode(qso.Mode);
        if (!string.IsNullOrWhiteSpace(txMode.Mode))
        {
            AppendField(sb, "MODE", txMode.Mode);
            if (!string.IsNullOrWhiteSpace(txMode.Submode))
                AppendField(sb, "SUBMODE", txMode.Submode);
        }

        if (qso.FreqRxHz > 0 && qso.FreqRxHz != qso.FreqHz)
            AppendField(sb, "FREQ_RX", FormatFreqMhz(qso.FreqRxHz));
        if (!string.IsNullOrWhiteSpace(qso.BandRx) && !string.Equals(qso.Band, qso.BandRx, StringComparison.OrdinalIgnoreCase))
            AppendField(sb, "BAND_RX", qso.BandRx.Trim());

        if (qso.Dxcc is int dxcc and > 0)
            AppendField(sb, "DXCC", dxcc.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(qso.Country))
            AppendField(sb, "COUNTRY", qso.Country.Trim());

        sb.AppendLine("<EOR>");
    }

    /// <summary>
    /// Writes the operator's own POTA, SOTA, and WWFF references. A value that fails the format check is
    /// skipped rather than written. A WWFF reference also sets MY_SIG and MY_SIG_INFO, as WWFF asks for.
    /// </summary>
    private static void AppendActivationRefs(StringBuilder sb, QsoRecord qso)
    {
        var pota = ActivationReferences.ToStored(ActivationProgramme.Pota, qso.PotaRef);
        if (pota.Length > 0)
            AppendField(sb, ActivationReferences.AdifRefTag(ActivationProgramme.Pota), pota);

        var sota = ActivationReferences.ToStored(ActivationProgramme.Sota, qso.SotaRef);
        if (sota.Length > 0)
            AppendField(sb, ActivationReferences.AdifRefTag(ActivationProgramme.Sota), sota);

        var wwff = ActivationReferences.ToStored(ActivationProgramme.Wwff, qso.WwffRef);
        if (wwff.Length > 0)
        {
            AppendField(sb, ActivationReferences.AdifRefTag(ActivationProgramme.Wwff), wwff);
            AppendField(sb, "MY_SIG", "WWFF");
            AppendField(sb, "MY_SIG_INFO", wwff);
        }
    }

    private static string FormatFreqMhz(long hz) =>
        (hz / 1_000_000.0).ToString("0.000000", CultureInfo.InvariantCulture);

    internal static void AppendField(StringBuilder sb, string tag, string value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        var data = EscapeAdifValue(value);
        sb.Append('<').Append(tag).Append(':').Append(data.Length).Append('>').Append(data);
    }

    internal static string EscapeAdifValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("<", "\\<", StringComparison.Ordinal);
}
