using System.Globalization;
using System.Text.RegularExpressions;

namespace OscarWatch.Core.Ft4;

/// <summary>Parse standard FT4 QSO messages and build the next transmit text.</summary>
public static partial class Ft4MessageCodec
{
    private static readonly Regex ReportRegex = ReportPattern();

    public static bool TryParse(
        string text,
        out string? callTo,
        out string? callDe,
        out string? extra)
    {
        callTo = null;
        callDe = null;
        extra = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        if (parts[0].Equals("CQ", StringComparison.OrdinalIgnoreCase))
        {
            callTo = "CQ";
            if (parts.Length >= 2)
                callDe = NormalizeCall(parts[1]);
            if (parts.Length >= 3)
                extra = parts[2].ToUpperInvariant();
            return callDe is not null;
        }

        if (parts.Length < 2)
            return false;

        callTo = NormalizeCall(parts[0]);
        callDe = NormalizeCall(parts[1]);
        if (parts.Length >= 3)
            extra = string.Join(' ', parts.Skip(2)).ToUpperInvariant();
        return true;
    }

    public static bool IsAddressedTo(string? callTo, string myCallsign) =>
        !string.IsNullOrWhiteSpace(callTo)
        && callTo.Equals(NormalizeCall(myCallsign), StringComparison.OrdinalIgnoreCase);

    public static bool IsCq(string? callTo) =>
        callTo is not null && callTo.Equals("CQ", StringComparison.OrdinalIgnoreCase);

    public static bool IsGrid(string? extra) =>
        !string.IsNullOrWhiteSpace(extra)
        && extra.Length is >= 4 and <= 6
        && char.IsLetter(extra[0])
        && char.IsLetter(extra[1])
        && char.IsDigit(extra[2])
        && char.IsDigit(extra[3]);

    public static bool IsReport(string? extra) =>
        !string.IsNullOrWhiteSpace(extra)
        && !IsClosing(extra)
        && ReportRegex.IsMatch(extra);

    /// <summary>True for R+NN / R-NN (roger report), not a plain +NN report.</summary>
    public static bool IsRogerReport(string? extra) =>
        IsReport(extra) && extra!.StartsWith('R');

    /// <summary>
    /// SNR for the logbook. Air text may be R+NN; ADIF / Cloudlog want +NN or -NN.
    /// </summary>
    public static string NormalizeSnrReport(string? report)
    {
        if (string.IsNullOrWhiteSpace(report))
            return "";

        var text = report.Trim().ToUpperInvariant();
        if (text.StartsWith('R') && text.Length > 1 && IsReport(text))
            text = text[1..];

        return IsReport(text) ? text : report.Trim();
    }

    public static string FormatRogerReport(float snrDb) =>
        "R" + FormatSnrReport(snrDb);

    public static bool IsRrr(string? extra) =>
        extra is not null && extra.Equals("RRR", StringComparison.OrdinalIgnoreCase);

    public static bool IsRr73(string? extra) =>
        extra is not null && extra.Equals("RR73", StringComparison.OrdinalIgnoreCase);

    public static bool Is73(string? extra) =>
        extra is not null && extra.Equals("73", StringComparison.OrdinalIgnoreCase);

    public static bool IsClosing(string? extra) => IsRrr(extra) || IsRr73(extra) || Is73(extra);

    public static string FormatSnrReport(float snrDb)
    {
        var rounded = (int)Math.Round(snrDb);
        rounded = Math.Clamp(rounded, -50, 49);
        return rounded >= 0
            ? $"+{rounded.ToString("00", CultureInfo.InvariantCulture)}"
            : rounded.ToString("00", CultureInfo.InvariantCulture);
    }

    public static string BuildCq(string myCall, string myGrid) =>
        $"CQ {NormalizeCall(myCall)} {NormalizeGrid(myGrid)}";

    public static string BuildGridReply(string theirCall, string myCall, string myGrid) =>
        $"{NormalizeCall(theirCall)} {NormalizeCall(myCall)} {NormalizeGrid(myGrid)}";

    public static string BuildReport(string theirCall, string myCall, string report) =>
        $"{NormalizeCall(theirCall)} {NormalizeCall(myCall)} {report.Trim().ToUpperInvariant()}";

    public static string BuildRrr(string theirCall, string myCall) =>
        $"{NormalizeCall(theirCall)} {NormalizeCall(myCall)} RRR";

    public static string BuildRr73(string theirCall, string myCall) =>
        $"{NormalizeCall(theirCall)} {NormalizeCall(myCall)} RR73";

    public static string Build73(string theirCall, string myCall) =>
        $"{NormalizeCall(theirCall)} {NormalizeCall(myCall)} 73";

    public static string NormalizeCall(string call)
    {
        if (string.IsNullOrWhiteSpace(call))
            return "";

        // Operators sometimes paste a Unicode slash; ft8_lib only accepts ASCII '/'.
        var normalized = call.Trim()
            .Replace('\u2215', '/') // division slash
            .Replace('\u2044', '/') // fraction slash
            .Replace('\\', '/');
        return normalized.ToUpperInvariant();
    }

    public static string NormalizeGrid(string grid)
    {
        var g = grid.Trim();
        if (g.Length >= 4)
            return string.Concat(g.AsSpan(0, 2).ToString().ToUpperInvariant(), g.AsSpan(2, Math.Min(2, g.Length - 2)));
        return g.ToUpperInvariant();
    }

    [GeneratedRegex(@"^R?[+-]?\d{1,2}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReportPattern();
}
