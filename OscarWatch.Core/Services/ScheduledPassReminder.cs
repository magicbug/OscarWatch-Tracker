using OscarWatch.Core.Display;
using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

/// <summary>
/// Fires once per scheduled pass when wall-clock UTC enters the lead window before AOS.
/// </summary>
public sealed class ScheduledPassReminder
{
    public static readonly TimeSpan AosMatchTolerance = TimeSpan.FromMinutes(2);

    private readonly HashSet<string> _alertedKeys = new(StringComparer.Ordinal);

    public IReadOnlyList<PassInfo> Process(
        DateTime utcNow,
        IReadOnlyList<ScheduledPassEntry> scheduled,
        IReadOnlyList<PassInfo> upcomingPasses,
        int leadMinutesBeforeAos)
    {
        if (scheduled.Count == 0 || upcomingPasses.Count == 0)
            return [];

        var now = PassUtc.Normalize(utcNow);
        var lead = TimeSpan.FromMinutes(PassScheduleSettings.ClampLeadMinutes(leadMinutesBeforeAos));
        var due = new List<PassInfo>();

        foreach (var entry in scheduled)
        {
            if (string.IsNullOrWhiteSpace(entry.NoradId))
                continue;

            var pass = FindMatchingPass(upcomingPasses, entry.NoradId, entry.AosUtc);
            if (pass is null)
                continue;

            var aos = PassUtc.Normalize(pass.AosUtc);
            var los = PassUtc.Normalize(pass.LosUtc);
            if (now > los)
                continue;

            // Only alert before/at AOS entering the lead window (not mid-pass after AOS).
            if (now > aos)
                continue;

            if (aos - now > lead)
                continue;

            var key = AlertKey(pass.NoradId, aos);
            if (!_alertedKeys.Add(key))
                continue;

            due.Add(pass);
        }

        return due;
    }

    public static string AlertKey(string noradId, DateTime aosUtc)
    {
        var aos = PassUtc.Normalize(aosUtc);
        var roundedTicks = RoundToMinute(aos).Ticks;
        return $"{noradId}|{roundedTicks}";
    }

    public static PassInfo? FindMatchingPass(
        IReadOnlyList<PassInfo> passes,
        string noradId,
        DateTime aosUtc)
    {
        var target = PassUtc.Normalize(aosUtc);
        PassInfo? best = null;
        var bestDelta = TimeSpan.MaxValue;

        foreach (var pass in passes)
        {
            if (!string.Equals(pass.NoradId, noradId, StringComparison.Ordinal))
                continue;

            var delta = Abs(PassUtc.Normalize(pass.AosUtc) - target);
            if (delta > AosMatchTolerance)
                continue;

            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = pass;
            }
        }

        return best;
    }

    /// <summary>
    /// Rematch scheduled entries to current predictions (AOS may drift after TLE refresh),
    /// update stored AOS, and drop entries past LOS or with no match.
    /// </summary>
    public static List<ScheduledPassEntry> RematchAndPrune(
        IReadOnlyList<ScheduledPassEntry> scheduled,
        IReadOnlyList<PassInfo> upcomingPasses,
        DateTime utcNow)
    {
        var now = PassUtc.Normalize(utcNow);
        var result = new List<ScheduledPassEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in scheduled)
        {
            if (string.IsNullOrWhiteSpace(entry.NoradId))
                continue;

            var pass = FindMatchingPass(upcomingPasses, entry.NoradId, entry.AosUtc);
            if (pass is null)
                continue;

            var los = PassUtc.Normalize(pass.LosUtc);
            if (now > los)
                continue;

            var aos = PassUtc.Normalize(pass.AosUtc);
            var key = AlertKey(pass.NoradId, aos);
            if (!seen.Add(key))
                continue;

            result.Add(new ScheduledPassEntry
            {
                NoradId = pass.NoradId,
                AosUtc = aos
            });
        }

        return result;
    }

    public static bool IsScheduled(
        IReadOnlyList<ScheduledPassEntry> scheduled,
        string noradId,
        DateTime aosUtc) =>
        FindMatchingEntry(scheduled, noradId, aosUtc) is not null;

    public static ScheduledPassEntry? FindMatchingEntry(
        IReadOnlyList<ScheduledPassEntry> scheduled,
        string noradId,
        DateTime aosUtc)
    {
        var target = PassUtc.Normalize(aosUtc);
        ScheduledPassEntry? best = null;
        var bestDelta = TimeSpan.MaxValue;

        foreach (var entry in scheduled)
        {
            if (!string.Equals(entry.NoradId, noradId, StringComparison.Ordinal))
                continue;

            var delta = Abs(PassUtc.Normalize(entry.AosUtc) - target);
            if (delta > AosMatchTolerance)
                continue;

            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = entry;
            }
        }

        return best;
    }

    public static List<ScheduledPassEntry> Toggle(
        IReadOnlyList<ScheduledPassEntry> scheduled,
        string noradId,
        DateTime aosUtc)
    {
        var list = scheduled.ToList();
        var existing = FindMatchingEntry(list, noradId, aosUtc);
        if (existing is not null)
        {
            list.Remove(existing);
            return list;
        }

        list.Add(new ScheduledPassEntry
        {
            NoradId = noradId,
            AosUtc = PassUtc.Normalize(aosUtc)
        });
        return list;
    }

    private static DateTime RoundToMinute(DateTime utc) =>
        new(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);

    private static TimeSpan Abs(TimeSpan value) => value < TimeSpan.Zero ? -value : value;
}
