using System.Globalization;

namespace OscarWatch.Rotator;

/// <summary>GRBL machine state and machine position (millimetres) from a <c>?</c> status report.</summary>
internal readonly record struct GrblStatus(string State, double MachineXMm, double MachineYMm)
{
    public bool IsMoving => State is "Jog" or "Run";
    public bool IsAlarm => State == "Alarm";
    public bool IsIdle => State == "Idle";
}

/// <summary>
/// Parses GRBL status reports and line replies. Accepts GRBL 1.1 (<c>&lt;Idle|MPos:x,y,z|...&gt;</c>)
/// and GRBL 0.9 (<c>&lt;Idle,MPos:x,y,z,WPos:...&gt;</c>). Only <c>MPos</c> is used.
/// </summary>
internal static class GrblStatusParser
{
    public static GrblStatus? TryParseStatus(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var text = line.Trim();
        var open = text.IndexOf('<');
        var close = text.IndexOf('>', open + 1);
        if (open < 0 || close < 0)
            return null;

        var body = text[(open + 1)..close];
        var stateEnd = body.IndexOfAny(['|', ',']);
        if (stateEnd <= 0)
            return null;

        var state = body[..stateEnd];
        var subState = state.IndexOf(':');
        if (subState > 0)
            state = state[..subState];

        var mpos = body.IndexOf("MPos:", StringComparison.Ordinal);
        if (mpos < 0)
            return null;

        var values = body[(mpos + "MPos:".Length)..];
        var fieldEnd = values.IndexOf('|');
        if (fieldEnd >= 0)
            values = values[..fieldEnd];

        var parts = values.Split(',');
        if (parts.Length < 2
            || !TryParseNumber(parts[0], out var x)
            || !TryParseNumber(parts[1], out var y))
            return null;

        return new GrblStatus(state, x, y);
    }

    public static bool IsOk(string? line) =>
        string.Equals(line?.Trim(), "ok", StringComparison.OrdinalIgnoreCase);

    public static bool IsError(string? line) =>
        line is not null && line.TrimStart().StartsWith("error", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a <c>$$</c> settings line such as <c>$100=1776.000</c>.</summary>
    public static bool TryParseSetting(string? line, out int id, out double value)
    {
        id = 0;
        value = 0;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var text = line.Trim();
        var eq = text.IndexOf('=');
        if (!text.StartsWith('$') || eq < 2)
            return false;

        var valueText = text[(eq + 1)..];
        var comment = valueText.IndexOf(' ');
        if (comment >= 0)
            valueText = valueText[..comment];

        return int.TryParse(text[1..eq], NumberStyles.Integer, CultureInfo.InvariantCulture, out id)
            && TryParseNumber(valueText, out value);
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
