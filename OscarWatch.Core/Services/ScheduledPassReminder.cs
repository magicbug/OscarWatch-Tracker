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

        // Build dictionary for O(1) pass lookup by NoradId
        var passesByNoradId = new Dictionary<string, List<PassInfo>>(StringComparer.Ordinal);
        foreach (var pass in upcomingPasses)
        {
            if (string.IsNullOrWhiteSpace(pass.NoradId))
                continue;
                
            if (!passesByNoradId.TryGetValue(pass.NoradId, out var passes))
            {
                passes = new List<PassInfo>(2); // Most satellites have 1-2 upcoming passes
                passesByNoradId[pass.NoradId] = passes;
            }
            passes.Add(pass);
        }

        var due = new List<PassInfo>();

        foreach (var entry in scheduled)
        {
            if (string.IsNullOrWhiteSpace(entry.NoradId))
                continue;

            if (!passesByNoradId.TryGetValue(entry.NoradId, out var candidatePasses))
                continue;

            var pass = FindBestMatchFromCandidates(candidatePasses, entry.AosUtc);
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

    /// <summary>
    /// Optimized pass matching from a small candidate set (same NoradId).
    /// </summary>
    private static PassInfo? FindBestMatchFromCandidates(List<PassInfo> candidates, DateTime targetAosUtc)
    {
        var target = PassUtc.Normalize(targetAosUtc);
        PassInfo? best = null;
        var bestDelta = TimeSpan.MaxValue;

        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var delta = Abs(PassUtc.Normalize(candidate.AosUtc) - target);
            if (delta > AosMatchTolerance)
                continue;

            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = candidate;
            }
        }

        return best;
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

    public static List<ScheduledPassEntry> RematchAndPrune(
        IReadOnlyList<ScheduledPassEntry> scheduled,
        IReadOnlyList<PassInfo> upcomingPasses,
        DateTime utcNow)
    {
        var now = PassUtc.Normalize(utcNow);
        var result = new List<ScheduledPassEntry>(scheduled.Count);
        var seen = new HashSet<string>(scheduled.Count, StringComparer.Ordinal);

        // Build pass lookup dictionary for efficiency
        var passesByNoradId = new Dictionary<string, List<PassInfo>>(StringComparer.Ordinal);
        foreach (var pass in upcomingPasses)
        {
            if (string.IsNullOrWhiteSpace(pass.NoradId))
                continue;
                
            if (!passesByNoradId.TryGetValue(pass.NoradId, out var passes))
            {
                passes = new List<PassInfo>(2);
                passesByNoradId[pass.NoradId] = passes;
            }
            passes.Add(pass);
        }

        foreach (var entry in scheduled)
        {
            if (string.IsNullOrWhiteSpace(entry.NoradId))
                continue;

            if (!passesByNoradId.TryGetValue(entry.NoradId, out var candidatePasses))
                continue;

            var pass = FindBestMatchFromCandidates(candidatePasses, entry.AosUtc);
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

        for (int i = 0; i < scheduled.Count; i++)
        {
            var entry = scheduled[i];
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
        var list = new List<ScheduledPassEntry>(scheduled.Count + 1);
        var existing = FindMatchingEntry(scheduled, noradId, aosUtc);
        var foundMatch = false;
        
        foreach (var entry in scheduled)
        {
            if (!foundMatch && ReferenceEquals(entry, existing))
            {
                foundMatch = true;
                continue; // Skip the matching entry (remove it)
            }
            list.Add(entry);
        }

        if (!foundMatch)
        {
            // No existing entry found, add new one
            list.Add(new ScheduledPassEntry
            {
                NoradId = noradId,
                AosUtc = PassUtc.Normalize(aosUtc)
            });
        }

        return list;
    }

    public static List<ScheduledPassEntry> EnsureScheduled(
        IReadOnlyList<ScheduledPassEntry> scheduled,
        string noradId,
        DateTime aosUtc)
    {
        if (IsScheduled(scheduled, noradId, aosUtc))
            return scheduled.Count == 0 ? new List<ScheduledPassEntry>() : new List<ScheduledPassEntry>(scheduled);

        var list = new List<ScheduledPassEntry>(scheduled.Count + 1);
        foreach (var entry in scheduled)
            list.Add(entry);
            
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
