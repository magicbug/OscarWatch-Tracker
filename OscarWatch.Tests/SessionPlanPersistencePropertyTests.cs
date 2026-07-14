using FsCheck;
using FsCheck.Xunit;
using OscarWatch.Core.Models;
using OscarWatch.Core.SessionPlanner;

namespace OscarWatch.Tests;

/// <summary>
/// Property-based tests for session plan serialisation round-trip.
/// Validates correctness property 15 from the session-planner design document.
/// </summary>
public sealed class SessionPlanPersistencePropertyTests
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
    /// Generates a set of pass IDs from a subset of available passes using a seed.
    /// </summary>
    private static HashSet<string> GenerateIdSubset(List<ScoredPass> passes, int seed, int maxCount)
    {
        var ids = new HashSet<string>();
        if (passes.Count == 0) return ids;

        var absSeed = Math.Abs(seed);
        var count = absSeed % (maxCount + 1); // 0..maxCount

        for (int i = 0; i < count && i < passes.Count; i++)
        {
            var idx = (absSeed / (i + 1)) % passes.Count;
            ids.Add(passes[idx].Id);
        }

        return ids;
    }

    // ─── Property 15: Serialisation Round-Trip ───────────────────────────────────
    // Feature: session-planner, Property 15: Serialisation Round-Trip
    // **Validates: Requirements 12.1, 12.3**

    /// <summary>
    /// For any valid SessionPlan (with scheduled passes, exclusions, and forced inclusions),
    /// serialising to JSON and then deserialising SHALL produce an equivalent plan with
    /// identical session bounds, pass data, scores, reasons, and adjustment sets.
    /// </summary>
    [Property]
    public bool SerialisationRoundTrip(int[] seeds, int excludeSeed, int forceSeed)
    {
        if (seeds == null || seeds.Length == 0)
            return true;

        // Limit to 1-10 passes
        var limited = seeds.Take(10).ToArray();
        var passes = BuildNonOverlappingPasses(limited);

        if (passes.Count == 0)
            return true;

        // Build scheduled passes with mixed reasons
        var scheduled = passes.Select((p, idx) => new ScheduledPass
        {
            Scored = p,
            Reason = idx % 3 == 0 ? PassSelectionReason.ForceIncluded : PassSelectionReason.AlgorithmSelected
        }).ToList();

        // Generate random exclusion and forced inclusion ID sets
        var excludedIds = GenerateIdSubset(passes, excludeSeed, 3);
        var forcedInclusionIds = GenerateIdSubset(passes, forceSeed, 3);

        var originalPlan = new SessionPlan
        {
            SessionStartUtc = SessionStart,
            SessionEndUtc = SessionEnd,
            ScheduledPasses = scheduled,
            AllCandidates = passes,
            ExcludedIds = excludedIds,
            ForcedInclusionIds = forcedInclusionIds
        };

        // Serialise → Deserialise
        var json = SessionPlanPersistence.Serialise(originalPlan);
        var restored = SessionPlanPersistence.Deserialise(json);

        // Assert result is not null
        if (restored is null)
            return false;

        // Assert session bounds match
        if (restored.SessionStartUtc != originalPlan.SessionStartUtc)
            return false;
        if (restored.SessionEndUtc != originalPlan.SessionEndUtc)
            return false;

        // Assert scheduled passes count matches
        if (restored.ScheduledPasses.Count != originalPlan.ScheduledPasses.Count)
            return false;

        // Assert each scheduled pass matches
        for (int i = 0; i < originalPlan.ScheduledPasses.Count; i++)
        {
            var orig = originalPlan.ScheduledPasses[i];
            var rest = restored.ScheduledPasses[i];

            if (rest.Scored.Pass.SatelliteName != orig.Scored.Pass.SatelliteName)
                return false;
            if (rest.Scored.Pass.NoradId != orig.Scored.Pass.NoradId)
                return false;
            if (rest.Scored.Pass.AosUtc != orig.Scored.Pass.AosUtc)
                return false;
            if (rest.Scored.Pass.LosUtc != orig.Scored.Pass.LosUtc)
                return false;
            if (Math.Abs(rest.Scored.Pass.MaxElevationDeg - orig.Scored.Pass.MaxElevationDeg) > 0.001)
                return false;
            if (Math.Abs(rest.Scored.QualityScore - orig.Scored.QualityScore) > 0.0001)
                return false;
            if (Math.Abs(rest.Scored.CompositeScore - orig.Scored.CompositeScore) > 0.0001)
                return false;
            if (rest.Scored.SatellitePriority != orig.Scored.SatellitePriority)
                return false;
            if (rest.Reason != orig.Reason)
                return false;
        }

        // Assert ExcludedIds match
        if (!restored.ExcludedIds.SetEquals(originalPlan.ExcludedIds))
            return false;

        // Assert ForcedInclusionIds match
        if (!restored.ForcedInclusionIds.SetEquals(originalPlan.ForcedInclusionIds))
            return false;

        return true;
    }
}
