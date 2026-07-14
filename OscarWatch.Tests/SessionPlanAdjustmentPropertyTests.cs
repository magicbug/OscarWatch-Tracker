using FsCheck;
using FsCheck.Xunit;
using OscarWatch.Core.Models;
using OscarWatch.Core.SessionPlanner;

namespace OscarWatch.Tests;

/// <summary>
/// Property-based tests for session plan adjustments.
/// Validates correctness properties 8–10 from the session-planner design document.
/// </summary>
public sealed class SessionPlanAdjustmentPropertyTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static readonly DateTime SessionStart = new(2025, 7, 1, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SessionEnd = new(2025, 7, 1, 20, 0, 0, DateTimeKind.Utc); // 6 hour window

    /// <summary>
    /// Creates a ScoredPass at a specific offset within the session window.
    /// </summary>
    private static ScoredPass CreatePass(int startOffsetMinutes, int durationMinutes, int elevation, int priority, int index)
    {
        var aos = SessionStart.AddMinutes(startOffsetMinutes);
        var los = aos.AddMinutes(durationMinutes);
        var pass = new PassInfo
        {
            SatelliteName = $"SAT-{index}",
            NoradId = $"{25544 + index}",
            AosUtc = aos,
            LosUtc = los,
            MaxElevationDeg = elevation,
            MaxElevationUtc = aos.AddMinutes(durationMinutes / 2.0)
        };
        var quality = PassQualityScorer.ComputeScore(elevation, durationMinutes, TransponderCategory.Unknown);
        var composite = PassQualityScorer.ComputeCompositeScore(quality, priority);
        return new ScoredPass
        {
            Pass = pass,
            QualityScore = quality,
            SatellitePriority = priority,
            CompositeScore = composite
        };
    }

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

            passes.Add(CreatePass(startOffset, durationMinutes, elevation, priority, i));
            currentOffset = startOffset + durationMinutes;
        }

        return passes;
    }

    /// <summary>
    /// Builds a SessionPlan with the given non-overlapping passes as both scheduled and candidates.
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

    // ─── Property 8: Session Time Accounting ─────────────────────────────────────
    // Feature: session-planner, Property 8: Session Time Accounting
    // **Validates: Requirements 5.4**

    /// <summary>
    /// For any session plan, the sum of all scheduled pass durations (total operating time)
    /// plus the sum of all gap durations (total gap time) SHALL equal the total session duration
    /// (SessionEndUtc − SessionStartUtc).
    /// </summary>
    [Property]
    public bool SessionTimeAccounting(int[] seeds)
    {
        if (seeds == null || seeds.Length == 0)
            return true;

        var limited = seeds.Take(15).ToArray();
        var passes = BuildNonOverlappingPasses(limited);
        var plan = BuildPlan(passes);

        var sessionDuration = plan.SessionEndUtc - plan.SessionStartUtc;
        var totalOperating = plan.TotalOperatingTime;
        var totalGap = plan.TotalGapTime;

        // TotalOperatingTime + TotalGapTime == SessionDuration
        var sum = totalOperating + totalGap;
        return Math.Abs((sum - sessionDuration).TotalSeconds) < 0.001;
    }

    // ─── Property 9: Exclusion Respected ─────────────────────────────────────────
    // Feature: session-planner, Property 9: Exclusion Respected
    // **Validates: Requirements 6.1, 6.2**

    /// <summary>
    /// For any session plan and any set of excluded pass IDs, after calling
    /// WeightedIntervalScheduler.Solve with those exclusions removed from candidates,
    /// (a) no excluded pass ID SHALL appear in the result, and
    /// (b) the non-overlap invariant SHALL still hold.
    /// </summary>
    [Property]
    public bool ExclusionRespected(int[] seeds, int exclusionSeed)
    {
        if (seeds == null || seeds.Length < 2)
            return true;

        var limited = seeds.Take(12).ToArray();
        var passes = BuildNonOverlappingPasses(limited);

        if (passes.Count < 2)
            return true;

        // Select passes to exclude using the seed
        var absSeed = Math.Abs(exclusionSeed);
        var excludeCount = 1 + (absSeed % Math.Min(3, passes.Count));
        var excludedIds = new HashSet<string>();
        for (int i = 0; i < excludeCount && i < passes.Count; i++)
        {
            var idx = (absSeed / (i + 1)) % passes.Count;
            excludedIds.Add(passes[idx].Id);
        }

        // Filter out excluded passes and re-solve (mirrors AdjustPlan logic)
        var filteredCandidates = passes.Where(c => !excludedIds.Contains(c.Id)).ToList();
        var result = WeightedIntervalScheduler.Solve(filteredCandidates);

        // (a) No excluded pass ID in result
        foreach (var sp in result)
        {
            if (excludedIds.Contains(sp.Scored.Id))
                return false;
        }

        // (b) Non-overlap invariant holds
        for (int i = 0; i < result.Count - 1; i++)
        {
            if (result[i].Scored.Pass.LosUtc > result[i + 1].Scored.Pass.AosUtc)
                return false;
        }

        return true;
    }

    // ─── Property 10: Forced Inclusion Respected ─────────────────────────────────
    // Feature: session-planner, Property 10: Forced Inclusion Respected
    // **Validates: Requirements 6.3, 6.4**

    /// <summary>
    /// For any session plan and any set of force-included pass IDs (which themselves
    /// do not mutually overlap), after calling WeightedIntervalScheduler.Solve with
    /// those forced inclusions, (a) every forced pass SHALL appear in the result,
    /// and (b) the non-overlap invariant SHALL still hold.
    /// </summary>
    [Property]
    public bool ForcedInclusionRespected(int[] seeds, int forceSeed)
    {
        if (seeds == null || seeds.Length < 2)
            return true;

        var limited = seeds.Take(12).ToArray();
        var passes = BuildNonOverlappingPasses(limited);

        if (passes.Count < 2)
            return true;

        // Select passes to force-include using the seed.
        // Since our passes are already non-overlapping, any subset is valid for forced inclusion.
        var absSeed = Math.Abs(forceSeed);
        var forceCount = 1 + (absSeed % Math.Min(3, passes.Count));
        var forcedIds = new HashSet<string>();
        var forcedIndices = new HashSet<int>();
        for (int i = 0; i < forceCount; i++)
        {
            var idx = (absSeed / (i + 1)) % passes.Count;
            if (!forcedIndices.Contains(idx))
            {
                forcedIndices.Add(idx);
                forcedIds.Add(passes[idx].Id);
            }
        }

        if (forcedIds.Count == 0)
            return true;

        var result = WeightedIntervalScheduler.Solve(passes, forcedInclusionIds: forcedIds);

        // (a) Every forced pass appears in result
        var resultIds = result.Select(sp => sp.Scored.Id).ToHashSet();
        foreach (var fid in forcedIds)
        {
            if (!resultIds.Contains(fid))
                return false;
        }

        // (b) Non-overlap invariant holds
        for (int i = 0; i < result.Count - 1; i++)
        {
            if (result[i].Scored.Pass.LosUtc > result[i + 1].Scored.Pass.AosUtc)
                return false;
        }

        return true;
    }
}
