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
            // A directed CQ (CQ POTA, CQ DX, CQ 290) is still a CQ from the call after the word.
            var at = parts.Length >= 3 && IsCqModifier(parts[1]) ? 2 : 1;
            if (parts.Length > at)
                callDe = NormalizeCall(parts[at]);
            if (parts.Length > at + 1)
                extra = parts[at + 1].ToUpperInvariant();
            return callDe is not null;
        }

        if (parts.Length < 2)
            return false;

        // A compound call cannot share CQ with a grid, so the station sends
        // "SM/R1CR KP04": their call, then their grid, and no CQ. The grid is not
        // another station.
        if (parts.Length == 2 && IsCallAndGrid(parts[0], parts[1]))
        {
            callTo = "CQ";
            callDe = NormalizeCall(parts[0]);
            extra = parts[1].ToUpperInvariant();
            return callDe.Length > 0;
        }

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

    /// <summary>
    /// Two tokens, callsign then locator: the station is announcing where they are.
    /// RR73 matches the locator shape, and a locator is not a callsign.
    /// </summary>
    private static bool IsCallAndGrid(string call, string grid)
    {
        if (!IsGrid(grid) || IsClosing(grid))
            return false;

        var station = NormalizeCall(call);
        if (station.Length < 3 || station is "CQ" or "DE" or "QRZ")
            return false;
        if (IsGrid(station) && !IsClosing(station))
            return false;

        var hasLetter = false;
        var hasDigit = false;
        foreach (var ch in station)
        {
            if (char.IsAsciiLetter(ch))
                hasLetter = true;
            else if (char.IsAsciiDigit(ch))
                hasDigit = true;
            else if (ch != '/')
                return false;
        }

        return hasLetter && hasDigit;
    }

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

    // A call in <> goes out as a hash. A non-standard call written bare goes out in full
    // (type 4 message) where the message has room: CQ, a reply with no grid, RRR, RR73 and 73.
    // Grids and reports need the standard message, which only carries such a call as a hash.
    // That is the WSJT-X sequence, so a non-standard own call reaches the other station in full.

    /// <summary>
    /// CQ with no grid when <paramref name="myCall"/> is non-standard: the full call takes the room.
    /// A valid <paramref name="modifier"/> goes between CQ and the call (CQ POTA MM9SQL IO85).
    /// The full-call message has only a CQ flag, so a non-standard call sends no modifier either.
    /// </summary>
    public static string BuildCq(string myCall, string myGrid, string? modifier = null)
    {
        var my = NormalizeCall(myCall);
        if (IsHashedCall(my))
            return $"CQ {my}";

        var word = NormalizeCqModifier(modifier);
        var cq = word.Length > 0 && IsCqModifier(word) ? $"CQ {word}" : "CQ";
        return $"{cq} {my} {NormalizeGrid(myGrid)}";
    }

    /// <summary>
    /// True for a word a standard message can carry after CQ: 1 to 4 letters, or exactly 3 digits
    /// (the ft8_lib and WSJT-X rule).
    /// </summary>
    public static bool IsCqModifier(string? word)
    {
        if (string.IsNullOrEmpty(word))
            return false;
        if (word.Length == 3 && word.All(char.IsAsciiDigit))
            return true;
        return word.Length <= 4 && word.All(char.IsAsciiLetter);
    }

    /// <summary>Trim, upper case, and drop a leading CQ so a pasted "CQ POTA" reads as POTA.</summary>
    public static string NormalizeCqModifier(string? text)
    {
        var word = (text ?? "").Trim().ToUpperInvariant();
        if (word == "CQ")
            return "";
        if (word.StartsWith("CQ ", StringComparison.Ordinal))
            word = word[3..].Trim();
        return word;
    }

    public static string BuildGridReply(string theirCall, string myCall, string myGrid)
    {
        var my = NormalizeCall(myCall);
        return IsHashedCall(my)
            ? $"{NormalizeCall(theirCall)} {my}"
            : $"{Bracketed(theirCall)} {my} {NormalizeGrid(myGrid)}";
    }

    public static string BuildReport(string theirCall, string myCall, string report)
    {
        var my = NormalizeCall(myCall);
        var rpt = report.Trim().ToUpperInvariant();
        return IsHashedCall(my)
            ? $"{NormalizeCall(theirCall)} <{my}> {rpt}"
            : $"{Bracketed(theirCall)} {my} {rpt}";
    }

    public static string BuildRrr(string theirCall, string myCall) => BuildClosing(theirCall, myCall, "RRR");

    public static string BuildRr73(string theirCall, string myCall) => BuildClosing(theirCall, myCall, "RR73");

    public static string Build73(string theirCall, string myCall) => BuildClosing(theirCall, myCall, "73");

    private static string BuildClosing(string theirCall, string myCall, string closing) =>
        $"{Bracketed(theirCall)} {NormalizeCall(myCall)} {closing}";

    /// <summary>Their call, in &lt;&gt; when it is non-standard so it goes out as a hash.</summary>
    private static string Bracketed(string call)
    {
        var c = NormalizeCall(call);
        return IsHashedCall(c) ? $"<{c}>" : c;
    }

    /// <summary>
    /// True for a callsign a standard FT4 message can only carry as a hash: a prefix or
    /// suffix other than /P or /R, or a call that does not fit the 6-character standard form.
    /// </summary>
    public static bool IsHashedCall(string? call)
    {
        var c = NormalizeCall(call ?? "");
        if (c.Length < 3 || c == "CQ" || c == "DE" || c == "QRZ")
            return false;
        if (c.EndsWith("/P", StringComparison.Ordinal) || c.EndsWith("/R", StringComparison.Ordinal))
            c = c[..^2];

        // Same special cases as ft8_lib: 3DA0XYZ packs as 3D0XYZ, 3XA0XYZ as QA0XYZ.
        if (c.StartsWith("3DA0", StringComparison.Ordinal) && c.Length is > 4 and <= 7)
            c = "3D0" + c[4..];
        else if (c.StartsWith("3X", StringComparison.Ordinal) && c.Length is > 2 and <= 7 && char.IsAsciiLetter(c[2]))
            c = "Q" + c[2..];

        string six;
        if (c.Length is >= 3 and <= 6 && char.IsAsciiDigit(c[2]))
            six = c;
        else if (c.Length is >= 3 and <= 5 && char.IsAsciiDigit(c[1]))
            six = " " + c;
        else
            return true;
        six = six.PadRight(6);

        return !((six[0] == ' ' || char.IsAsciiLetterOrDigit(six[0]))
            && char.IsAsciiLetterOrDigit(six[1])
            && char.IsAsciiDigit(six[2])
            && IsLetterOrSpace(six[3])
            && IsLetterOrSpace(six[4])
            && IsLetterOrSpace(six[5]));

        static bool IsLetterOrSpace(char ch) => ch == ' ' || char.IsAsciiLetterUpper(ch);
    }

    public static string NormalizeCall(string call)
    {
        if (string.IsNullOrWhiteSpace(call))
            return "";

        // Operators sometimes paste a Unicode slash; ft8_lib only accepts ASCII '/'.
        // <CALL> is only how a hashed call is shown; it is sent (and matched) as the bare call,
        // and <...> is a hash nobody has resolved yet.
        var normalized = call.Trim()
            .Replace('\u2215', '/') // division slash
            .Replace('\u2044', '/') // fraction slash
            .Replace('\\', '/')
            .Trim('<', '>');
        return normalized == "..." ? "" : normalized.ToUpperInvariant();
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
