using System.Globalization;
using System.Text;
using OscarWatch.Core.Geo;

namespace OscarWatch.Core.Logbook;

/// <summary>Result of parsing an ADI (ADIF text) file.</summary>
public sealed class AdifImportParseResult
{
    public required IReadOnlyList<AdifImportedQso> Records { get; init; }
    public int UnparsedCount { get; init; }
}

/// <summary>Parses ADIF 3.1 ADI text into <see cref="AdifImportedQso"/> records.</summary>
public static class AdifImporter
{
    public static AdifImportParseResult Parse(string? adifText)
    {
        if (string.IsNullOrWhiteSpace(adifText))
            return new AdifImportParseResult { Records = [], UnparsedCount = 0 };

        var start = FindBodyStart(adifText);
        var records = new List<AdifImportedQso>();
        var unparsed = 0;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = start;

        while (i < adifText.Length)
        {
            var open = adifText.IndexOf('<', i);
            if (open < 0)
                break;

            if (!TryReadTag(adifText, open, out var tagName, out var data, out var next))
            {
                i = open + 1;
                continue;
            }

            i = next;

            if (string.Equals(tagName, "EOR", StringComparison.OrdinalIgnoreCase))
            {
                if (TryMapRecord(fields, out var qso))
                    records.Add(qso);
                else
                    unparsed++;
                fields.Clear();
                continue;
            }

            if (string.Equals(tagName, "EOH", StringComparison.OrdinalIgnoreCase))
                continue;

            if (tagName.StartsWith("APP_", StringComparison.OrdinalIgnoreCase))
                continue;

            fields[tagName] = data;
        }

        // A trailing partial record without <EOR> counts as unparsed when it has any fields.
        if (fields.Count > 0)
            unparsed++;

        return new AdifImportParseResult { Records = records, UnparsedCount = unparsed };
    }

    /// <summary>UTC instant truncated to the minute for duplicate matching.</summary>
    public static DateTime ToDuplicateMinute(DateTime utc)
    {
        var n = QsoLogbookTime.NormalizeToUtc(utc);
        return new DateTime(n.Year, n.Month, n.Day, n.Hour, n.Minute, 0, DateTimeKind.Utc);
    }

    public static string FormatDuplicateKey(string call, DateTime qsoUtc)
    {
        var minute = ToDuplicateMinute(qsoUtc);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{MaidenheadLocator.NormalizeCallsign(call)}|{minute:yyyyMMddHHmm}");
    }

    private static int FindBodyStart(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('<', i);
            if (open < 0)
                return 0;

            if (!TryReadTag(text, open, out var tagName, out _, out var next))
            {
                i = open + 1;
                continue;
            }

            if (string.Equals(tagName, "EOH", StringComparison.OrdinalIgnoreCase))
                return next;

            // An <EOR> before <EOH> means there is no header marker; parse from the start.
            if (string.Equals(tagName, "EOR", StringComparison.OrdinalIgnoreCase))
                return 0;

            i = next;
        }

        return 0;
    }

    private static bool TryReadTag(
        string text,
        int open,
        out string tagName,
        out string data,
        out int next)
    {
        tagName = "";
        data = "";
        next = open + 1;

        var close = text.IndexOf('>', open + 1);
        if (close < 0)
            return false;

        var header = text.AsSpan(open + 1, close - open - 1).Trim();
        if (header.Length == 0)
            return false;

        // <EOR> / <EOH> have no length.
        var colon = header.IndexOf(':');
        if (colon < 0)
        {
            tagName = header.ToString();
            data = "";
            next = close + 1;
            return true;
        }

        tagName = header[..colon].Trim().ToString();
        if (tagName.Length == 0)
            return false;

        var lengthPart = header[(colon + 1)..];
        var typeColon = lengthPart.IndexOf(':');
        if (typeColon >= 0)
            lengthPart = lengthPart[..typeColon];

        if (!int.TryParse(lengthPart.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
            || length < 0)
            return false;

        var dataStart = close + 1;
        if (dataStart + length > text.Length)
            return false;

        var raw = text.Substring(dataStart, length);
        data = UnescapeAdifValue(raw);
        next = dataStart + length;
        return true;
    }

    internal static string UnescapeAdifValue(string value)
    {
        if (value.IndexOf('\\') < 0)
            return value;

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                sb.Append(c);
                continue;
            }

            var next = value[++i];
            sb.Append(next switch
            {
                '\\' => '\\',
                'r' => '\r',
                'n' => '\n',
                '<' => '<',
                _ => next
            });
        }

        return sb.ToString();
    }

    private static bool TryMapRecord(Dictionary<string, string> fields, out AdifImportedQso qso)
    {
        qso = null!;

        if (!fields.TryGetValue("CALL", out var callRaw) || string.IsNullOrWhiteSpace(callRaw))
            return false;

        var call = MaidenheadLocator.NormalizeCallsign(callRaw);
        if (call.Length == 0)
            return false;

        if (!fields.TryGetValue("QSO_DATE", out var dateRaw) || !TryParseQsoUtc(dateRaw, fields, out var qsoUtc))
            return false;

        fields.TryGetValue("RST_SENT", out var rstSent);
        fields.TryGetValue("RST_RCVD", out var rstRcvd);
        fields.TryGetValue("GRIDSQUARE", out var grid);
        fields.TryGetValue("NAME", out var name);
        fields.TryGetValue("COMMENT", out var comment);
        fields.TryGetValue("SAT_NAME", out var satName);
        fields.TryGetValue("PROP_MODE", out var propMode);
        fields.TryGetValue("MODE", out var mode);
        fields.TryGetValue("SUBMODE", out var submode);
        fields.TryGetValue("BAND", out var band);
        fields.TryGetValue("BAND_RX", out var bandRx);
        fields.TryGetValue("COUNTRY", out var country);

        var freqHz = ParseFreqMhzToHz(fields.GetValueOrDefault("FREQ"));
        var freqRxHz = ParseFreqMhzToHz(fields.GetValueOrDefault("FREQ_RX"));
        band = string.IsNullOrWhiteSpace(band) && freqHz > 0
            ? AdifBandHelper.FromHz(freqHz)
            : (band?.Trim() ?? "");
        bandRx = string.IsNullOrWhiteSpace(bandRx) && freqRxHz > 0
            ? AdifBandHelper.FromHz(freqRxHz)
            : (bandRx?.Trim() ?? "");

        int? dxcc = null;
        if (fields.TryGetValue("DXCC", out var dxccRaw)
            && int.TryParse(dxccRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dxccCode)
            && dxccCode > 0)
        {
            dxcc = dxccCode;
        }

        qso = new AdifImportedQso
        {
            Call = call,
            QsoUtc = qsoUtc,
            RstSent = rstSent?.Trim() ?? "",
            RstRcvd = rstRcvd?.Trim() ?? "",
            GridSquare = MaidenheadLocator.NormalizeGrids(grid),
            Name = name?.Trim() ?? "",
            Comment = comment?.Trim() ?? "",
            SatName = satName?.Trim() ?? "",
            Mode = AdifModeHelper.ToOperatingMode(mode, submode),
            ModeRx = "",
            FreqHz = freqHz,
            FreqRxHz = freqRxHz,
            Band = band,
            BandRx = bandRx,
            PropMode = propMode?.Trim() ?? "",
            Dxcc = dxcc,
            Country = country?.Trim() ?? ""
        };
        return true;
    }

    private static bool TryParseQsoUtc(
        string dateRaw,
        Dictionary<string, string> fields,
        out DateTime qsoUtc)
    {
        qsoUtc = default;
        var date = dateRaw.Trim();
        if (date.Length != 8
            || !int.TryParse(date.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(date.AsSpan(4, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(date.AsSpan(6, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var day))
        {
            return false;
        }

        string? timeRaw = null;
        if (fields.TryGetValue("TIME_ON", out var timeOn) && !string.IsNullOrWhiteSpace(timeOn))
            timeRaw = timeOn;
        else if (fields.TryGetValue("TIME_OFF", out var timeOff) && !string.IsNullOrWhiteSpace(timeOff))
            timeRaw = timeOff;

        if (timeRaw is null || !TryParseAdifTime(timeRaw, out var hour, out var minute, out var second))
            return false;

        try
        {
            qsoUtc = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryParseAdifTime(string raw, out int hour, out int minute, out int second)
    {
        hour = 0;
        minute = 0;
        second = 0;
        var t = raw.Trim();
        if (t.Length is not (4 or 6))
            return false;

        if (!int.TryParse(t.AsSpan(0, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)
            || !int.TryParse(t.AsSpan(2, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out minute))
        {
            return false;
        }

        if (t.Length == 6
            && !int.TryParse(t.AsSpan(4, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out second))
        {
            return false;
        }

        return hour is >= 0 and <= 23 && minute is >= 0 and <= 59 && second is >= 0 and <= 59;
    }

    private static long ParseFreqMhzToHz(string? mhzText)
    {
        if (string.IsNullOrWhiteSpace(mhzText))
            return 0;

        if (!decimal.TryParse(
                mhzText.Trim(),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var mhz)
            || mhz <= 0)
        {
            return 0;
        }

        var hz = decimal.Round(mhz * 1_000_000m, 0, MidpointRounding.AwayFromZero);
        if (hz > long.MaxValue)
            return 0;

        return (long)hz;
    }
}
