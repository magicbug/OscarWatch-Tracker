using FsCheck;
using FsCheck.Xunit;
using OscarWatch.Core.Export;
using OscarWatch.Core.Models;
using OscarWatch.Core.SessionPlanner;

namespace OscarWatch.Tests;

/// <summary>
/// Property-based tests for session plan export (CSV and ICS).
/// Validates correctness properties 16–17 from the session-planner design document.
/// </summary>
public sealed class SessionPlanExportPropertyTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static readonly DateTime SessionStart = new(2025, 7, 1, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SessionEnd = new(2025, 7, 1, 20, 0, 0, DateTimeKind.Utc);

    private static readonly GroundStation TestStation = new()
    {
        DisplayName = "TestStation",
        GridSquare = "IO91wm",
        LatitudeDeg = 51.5,
        LongitudeDeg = -0.1
    };

    /// <summary>
    /// Builds a list of non-overlapping passes within the session window from seed values.
    /// Each pass is placed sequentially to guarantee no overlap.
    /// </summary>
    private static List<ScoredPass> BuildNonOverlappingPasses(int[] seeds)
    {
        var passes = new List<ScoredPass>();
        int currentOffset = 0;
        var sessionMinutes = (int)(SessionEnd - SessionStart).TotalMinutes; // 360

        for (int i = 0; i < seeds.Length; i++)
        {
            var seed = Math.Abs(seeds[i]);
            var durationMinutes = 3 + (seed % 13); // 3..15
            var gap = 1 + ((seed / 13) % 20);      // 1..20 minute gap before this pass
            var elevation = 10 + ((seed / 260) % 81); // 10..90
            var priority = 1 + ((seed / 21060) % 10); // 1..10

            var startOffset = currentOffset + gap;
            if (startOffset + durationMinutes > sessionMinutes)
                break; // No more room

            var aos = SessionStart.AddMinutes(startOffset);
            var los = aos.AddMinutes(durationMinutes);
            var pass = new PassInfo
            {
                SatelliteName = $"SAT-{i}",
                NoradId = $"{25544 + i}",
                AosUtc = aos,
                LosUtc = los,
                MaxElevationDeg = elevation,
                MaxElevationUtc = aos.AddMinutes(durationMinutes / 2.0)
            };
            var quality = PassQualityScorer.ComputeScore(elevation, durationMinutes, TransponderCategory.Unknown);
            var composite = PassQualityScorer.ComputeCompositeScore(quality, priority);
            passes.Add(new ScoredPass
            {
                Pass = pass,
                QualityScore = quality,
                SatellitePriority = priority,
                CompositeScore = composite
            });
            currentOffset = startOffset + durationMinutes;
        }

        return passes;
    }

    /// <summary>
    /// Builds a SessionPlan with the given non-overlapping passes.
    /// </summary>
    private static SessionPlan BuildPlan(List<ScoredPass> passes)
    {
        var scheduled = passes.Select(p => new ScheduledPass
        {
            Scored = p,
            Reason = PassSelectionReason.AlgorithmSelected
        }).ToList();

        return new SessionPlan
        {
            SessionStartUtc = SessionStart,
            SessionEndUtc = SessionEnd,
            ScheduledPasses = scheduled,
            AllCandidates = passes,
            ExcludedIds = new HashSet<string>(),
            ForcedInclusionIds = new HashSet<string>()
        };
    }

    // ─── Property 16: ICS Export Structure ───────────────────────────────────────
    // Feature: session-planner, Property 16: ICS Export Structure
    // **Validates: Requirements 10.2, 10.3**

    /// <summary>
    /// For any session plan with N scheduled passes and pre-alert lead time L,
    /// SessionPlanExporter.BuildCalendar SHALL produce output containing exactly N
    /// VEVENT blocks, each with a VALARM whose TRIGGER is -PT{L}M, and DTSTART/DTEND
    /// matching the pass's AOS/LOS in UTC format (yyyyMMddTHHmmssZ).
    /// </summary>
    [Property]
    public bool IcsExportStructure(int[] seeds, PositiveInt preAlertMinutesRaw)
    {
        if (seeds == null || seeds.Length == 0)
            return true;

        var limited = seeds.Take(15).ToArray();
        var passes = BuildNonOverlappingPasses(limited);

        if (passes.Count == 0)
            return true;

        var plan = BuildPlan(passes);
        var preAlertMinutes = 1 + (preAlertMinutesRaw.Get % 15); // 1..15

        var ics = SessionPlanExporter.BuildCalendar(plan, TestStation, preAlertMinutes);

        // Count VEVENT blocks
        var veventCount = CountOccurrences(ics, "BEGIN:VEVENT");
        if (veventCount != passes.Count)
            return false;

        // Verify each pass has correct DTSTART, DTEND, and VALARM TRIGGER
        for (int i = 0; i < passes.Count; i++)
        {
            var pass = passes[i].Pass;
            var expectedDtStart = $"DTSTART:{pass.AosUtc.ToString("yyyyMMdd'T'HHmmss'Z'")}";
            var expectedDtEnd = $"DTEND:{pass.LosUtc.ToString("yyyyMMdd'T'HHmmss'Z'")}";
            var expectedTrigger = $"TRIGGER:-PT{preAlertMinutes}M";

            if (!ics.Contains(expectedDtStart))
                return false;
            if (!ics.Contains(expectedDtEnd))
                return false;
            if (CountOccurrences(ics, expectedTrigger) != passes.Count)
                return false;
        }

        // Verify each VEVENT has a VALARM block
        var valarmCount = CountOccurrences(ics, "BEGIN:VALARM");
        if (valarmCount != passes.Count)
            return false;

        return true;
    }

    // ─── Property 17: CSV Export Completeness ────────────────────────────────────
    // Feature: session-planner, Property 17: CSV Export Completeness
    // **Validates: Requirements 10.1**

    /// <summary>
    /// For any session plan with N scheduled passes, SessionPlanExporter.BuildCsv SHALL
    /// produce output with exactly N+1 lines (header + N data rows), and each row SHALL
    /// contain values for SatelliteName, NoradId, AOS_UTC, LOS_UTC, MaxElevationDeg,
    /// CompositeScore, and Status.
    /// </summary>
    [Property]
    public bool CsvExportCompleteness(int[] seeds)
    {
        if (seeds == null || seeds.Length == 0)
            return true;

        var limited = seeds.Take(15).ToArray();
        var passes = BuildNonOverlappingPasses(limited);

        if (passes.Count == 0)
            return true;

        var plan = BuildPlan(passes);
        var csv = SessionPlanExporter.BuildCsv(plan);

        // Split into lines, removing any trailing empty line from final newline
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                       .Select(l => l.TrimEnd('\r'))
                       .Where(l => !string.IsNullOrWhiteSpace(l))
                       .ToArray();

        // Exactly N+1 lines (header + N data rows)
        if (lines.Length != passes.Count + 1)
            return false;

        // Verify header contains all required columns
        var header = lines[0];
        var requiredColumns = new[] { "SatelliteName", "NoradId", "AOS_UTC", "LOS_UTC", "MaxElevationDeg", "CompositeScore", "Status" };
        foreach (var col in requiredColumns)
        {
            if (!header.Contains(col))
                return false;
        }

        // Verify each data row has 7 comma-separated values
        for (int i = 1; i < lines.Length; i++)
        {
            var fields = lines[i].Split(',');
            if (fields.Length != 7)
                return false;

            // Each field should be non-empty
            foreach (var field in fields)
            {
                if (string.IsNullOrWhiteSpace(field))
                    return false;
            }
        }

        return true;
    }

    // ─── Utility ─────────────────────────────────────────────────────────────────

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
