using System.Globalization;
using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

/// <summary>
/// Resolves which <see cref="SatelliteOperatingNotice"/> entries are active for a UTC instant
/// and optional transponder mode.
/// </summary>
public static class SatelliteNoticeEvaluator
{
    private static readonly string[] CanonicalDayNames =
    [
        nameof(DayOfWeek.Sunday),
        nameof(DayOfWeek.Monday),
        nameof(DayOfWeek.Tuesday),
        nameof(DayOfWeek.Wednesday),
        nameof(DayOfWeek.Thursday),
        nameof(DayOfWeek.Friday),
        nameof(DayOfWeek.Saturday)
    ];

    /// <summary>Returns notices that apply at <paramref name="utc"/> for the optional mode.</summary>
    public static IReadOnlyList<SatelliteOperatingNotice> GetActiveNotices(
        SatelliteRadioEntry? entry,
        DateTime utc,
        string? modeType = null)
    {
        if (entry?.Notices is not { Count: > 0 })
            return [];

        var day = utc.Kind == DateTimeKind.Utc
            ? utc.DayOfWeek
            : DateTime.SpecifyKind(utc, DateTimeKind.Utc).DayOfWeek;

        var active = new List<SatelliteOperatingNotice>();
        foreach (var notice in entry.Notices)
        {
            if (string.IsNullOrWhiteSpace(notice.Message))
                continue;

            if (!MatchesDay(notice, day))
                continue;

            if (!MatchesMode(notice, modeType))
                continue;

            active.Add(notice);
        }

        return active;
    }

    /// <summary>True when at least one notice is active.</summary>
    public static bool HasActiveNotice(
        SatelliteRadioEntry? entry,
        DateTime utc,
        string? modeType = null) =>
        GetActiveNotices(entry, utc, modeType).Count > 0;

    /// <summary>Joined banner / tooltip text for active notices, or empty when none apply.</summary>
    public static string FormatActiveNoticeText(
        SatelliteRadioEntry? entry,
        DateTime utc,
        string? modeType = null)
    {
        var active = GetActiveNotices(entry, utc, modeType);
        if (active.Count == 0)
            return "";

        return string.Join(
            Environment.NewLine,
            active.Select(n => n.Message.Trim()));
    }

    /// <summary>
    /// Parses a weekday name case-insensitively to <see cref="DayOfWeek"/>.
    /// Accepts full English names only (Sunday…Saturday).
    /// </summary>
    public static bool TryParseDayOfWeek(string? raw, out DayOfWeek day)
    {
        day = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return Enum.TryParse(raw.Trim(), ignoreCase: true, out day)
            && Enum.IsDefined(day);
    }

    /// <summary>Canonical English weekday name, or null when unrecognized.</summary>
    public static string? CanonicalizeDayOfWeek(string? raw) =>
        TryParseDayOfWeek(raw, out var day) ? CanonicalDayNames[(int)day] : null;

    public static bool NoticesEqual(
        IReadOnlyList<SatelliteOperatingNotice>? left,
        IReadOnlyList<SatelliteOperatingNotice>? right)
    {
        var leftList = left ?? [];
        var rightList = right ?? [];
        if (leftList.Count != rightList.Count)
            return false;

        for (var i = 0; i < leftList.Count; i++)
        {
            if (!NoticeEqual(leftList[i], rightList[i]))
                return false;
        }

        return true;
    }

    public static bool NoticeEqual(SatelliteOperatingNotice left, SatelliteOperatingNotice right) =>
        string.Equals(left.Message.Trim(), right.Message.Trim(), StringComparison.Ordinal)
        && StringListsEqualIgnoreCase(left.DaysOfWeekUtc, right.DaysOfWeekUtc)
        && StringListsEqualIgnoreCase(left.ModeTypes, right.ModeTypes);

    public static string DescribeNotice(SatelliteOperatingNotice notice)
    {
        var message = notice.Message.Trim();
        var days = FormatList(notice.DaysOfWeekUtc);
        var modes = FormatList(notice.ModeTypes);

        if (days is null && modes is null)
            return message;

        if (days is not null && modes is not null)
            return $"{message} ({days}; modes: {modes})";

        if (days is not null)
            return $"{message} ({days} UTC)";

        return $"{message} (modes: {modes})";
    }

    public static string Fingerprint(IReadOnlyList<SatelliteOperatingNotice>? notices)
    {
        if (notices is null || notices.Count == 0)
            return "";

        return string.Join(
            "||",
            notices.Select(n => string.Create(
                CultureInfo.InvariantCulture,
                $"{n.Message.Trim()}|{FingerprintList(n.DaysOfWeekUtc)}|{FingerprintList(n.ModeTypes)}")));
    }

    private static bool MatchesDay(SatelliteOperatingNotice notice, DayOfWeek day)
    {
        if (notice.DaysOfWeekUtc is not { Count: > 0 })
            return true;

        foreach (var raw in notice.DaysOfWeekUtc)
        {
            if (TryParseDayOfWeek(raw, out var parsed) && parsed == day)
                return true;
        }

        return false;
    }

    private static bool MatchesMode(SatelliteOperatingNotice notice, string? modeType)
    {
        if (notice.ModeTypes is not { Count: > 0 })
            return true;

        // Satellite-level consumers (sidebar / pass rows) pass null modeType and still see
        // mode-scoped notices, so the operator knows a schedule applies.
        if (string.IsNullOrWhiteSpace(modeType))
            return true;

        foreach (var type in notice.ModeTypes)
        {
            if (string.Equals(type.Trim(), modeType.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool StringListsEqualIgnoreCase(
        IReadOnlyList<string>? left,
        IReadOnlyList<string>? right)
    {
        var leftList = left ?? [];
        var rightList = right ?? [];
        if (leftList.Count != rightList.Count)
            return false;

        for (var i = 0; i < leftList.Count; i++)
        {
            if (!string.Equals(leftList[i].Trim(), rightList[i].Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static string? FormatList(IReadOnlyList<string>? values)
    {
        if (values is not { Count: > 0 })
            return null;

        var parts = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string FingerprintList(IReadOnlyList<string>? values)
    {
        if (values is not { Count: > 0 })
            return "";

        return string.Join(",", values.Select(v => v.Trim().ToUpperInvariant()));
    }
}
