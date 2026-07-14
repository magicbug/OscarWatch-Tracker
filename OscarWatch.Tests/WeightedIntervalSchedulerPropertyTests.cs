using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using OscarWatch.Core.Models;
using OscarWatch.Core.SessionPlanner;

namespace OscarWatch.Tests;

/// <summary>
/// Property-based tests for <see cref="WeightedIntervalScheduler"/>.
/// Validates correctness properties 3–7 from the session-planner design document.
/// </summary>
public sealed class WeightedIntervalSchedulerPropertyTests
{
    // ─── Custom Arbitrary Provider ───────────────────────────────────────────────

    /// <summary>
    /// Creates a ScoredPass from integer seeds for property-based testing.
    /// </summary>
    private static ScoredPass CreateScoredPass(
        int startMinutes, int durationMinutes, int elevation, int priority, string noradId, string name)
    {
        var baseTime = new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var aos = baseTime.AddMinutes(startMinutes);
        var los = aos.AddMinutes(durationMinutes);
        var pass = new PassInfo
        {
            SatelliteName = name,
            NoradId = noradId,
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
    /// Builds a list of ScoredPass from arrays of seed values.
    /// Each pass gets unique start time offsets to create variety.
    /// </summary>
    private static List<ScoredPass> BuildCandidates(int[] seeds)
    {
        var candidates = new List<ScoredPass>();
        for (int i = 0; i < seeds.Length; i++)
        {
            var seed = Math.Abs(seeds[i]);
            var startMinutes = (seed % 1440);
            var durationMinutes = 2 + (seed / 1440 % 14); // 2..15
            var elevation = 5 + (seed / 20160 % 86);       // 5..90
            var priority = 1 + (seed / 1733760 % 10);      // 1..10
            var noradId = $"{25544 + (i % 1000)}";
            var name = $"SAT-{i}";
            candidates.Add(CreateScoredPass(startMinutes, durationMinutes, elevation, priority, noradId, name));
        }
        return candidates;
    }

    // ─── Property 3: Non-Overlap Invariant ───────────────────────────────────────
    // Feature: session-planner, Property 3: Non-Overlap Invariant
    // **Validates: Requirements 4.1, 4.6**

    /// <summary>
    /// For any set of candidate ScoredPass values, the output of
    /// WeightedIntervalScheduler.Solve SHALL contain no two passes whose
    /// [AOS, LOS] intervals overlap — i.e., for every pair of selected passes
    /// A and B where A.AosUtc &lt; B.AosUtc, it must hold that A.LosUtc &lt;= B.AosUtc.
    /// </summary>
    [Property]
    public bool NonOverlapInvariant(int[] seeds)
    {
        if (seeds == null || seeds.Length == 0)
            return true;

        // Limit to reasonable size
        var limited = seeds.Take(20).ToArray();
        var candidates = BuildCandidates(limited);

        var result = WeightedIntervalScheduler.Solve(candidates);

        for (int i = 0; i < result.Count - 1; i++)
        {
            var a = result[i].Scored.Pass;
            var b = result[i + 1].Scored.Pass;

            if (a.LosUtc > b.AosUtc)
                return false;
        }

        return true;
    }

    // ─── Property 4: Optimality (Small Inputs) ──────────────────────────────────
    // Feature: session-planner, Property 4: Optimality (Small Inputs)
    // **Validates: Requirements 4.2**

    /// <summary>
    /// For any set of up to 8 candidate passes, the total composite score of the
    /// solution returned by Solve SHALL be >= the total composite score of every
    /// other valid (non-overlapping) subset of those candidates.
    /// Brute-force all 2^n subsets to verify.
    /// </summary>
    [Property]
    public bool OptimalitySmallInputs(int[] seeds)
    {
        if (seeds == null || seeds.Length == 0)
            return true;

        // Limit to 8 for brute-force feasibility
        var limited = seeds.Take(8).ToArray();
        var candidates = BuildCandidates(limited);

        if (candidates.Count == 0)
            return true;

        var result = WeightedIntervalScheduler.Solve(candidates);
        var solverScore = result.Sum(sp => sp.Scored.CompositeScore);

        // Brute-force: enumerate all 2^n subsets
        int n = candidates.Count;
        double bestValidScore = 0.0;

        for (long mask = 0; mask < (1L << n); mask++)
        {
            var subset = new List<ScoredPass>();
            for (int i = 0; i < n; i++)
            {
                if ((mask & (1L << i)) != 0)
                    subset.Add(candidates[i]);
            }

            // Check validity: non-overlapping
            if (!IsNonOverlapping(subset))
                continue;

            var subsetScore = subset.Sum(sp => sp.CompositeScore);
            if (subsetScore > bestValidScore)
                bestValidScore = subsetScore;
        }

        // Solver score should be >= best valid score (accounting for floating-point)
        return solverScore >= bestValidScore - 1e-9;
    }

    /// <summary>
    /// Checks whether a set of passes are all pairwise non-overlapping.
    /// </summary>
    private static bool IsNonOverlapping(List<ScoredPass> passes)
    {
        if (passes.Count <= 1)
            return true;

        var sorted = passes.OrderBy(p => p.Pass.AosUtc).ToList();
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            if (sorted[i].Pass.LosUtc > sorted[i + 1].Pass.AosUtc)
                return false;
        }

        return true;
    }

    // ─── Property 5: Elevation Threshold Filtering ───────────────────────────────
    // Feature: session-planner, Property 5: Elevation Threshold Filtering
    // **Validates: Requirements 4.3**

    /// <summary>
    /// For any set of candidate passes and minimum elevation threshold,
    /// no pass in the scheduler output SHALL have MaxElevationDeg below the threshold.
    /// </summary>
    [Property]
    public bool ElevationThresholdFiltering(int[] seeds, int rawThreshold)
    {
        if (seeds == null || seeds.Length == 0)
            return true;

        var limited = seeds.Take(15).ToArray();
        var candidates = BuildCandidates(limited);

        // Constrain threshold to [0, 85]
        var minElevation = ((rawThreshold % 86) + 86) % 86;

        var result = WeightedIntervalScheduler.Solve(
            candidates,
            minimumElevationDeg: minElevation);

        return result.All(sp => sp.Scored.Pass.MaxElevationDeg >= minElevation);
    }

    // ─── Property 6: Tie-Breaking by Elevation ───────────────────────────────────
    // Feature: session-planner, Property 6: Tie-Breaking by Elevation
    // **Validates: Requirements 4.5**

    /// <summary>
    /// For any pair of overlapping candidate passes with identical composite scores,
    /// if one has a strictly higher MaxElevationDeg, then the scheduler output SHALL
    /// contain the higher-elevation pass (not the lower).
    /// </summary>
    [Property]
    public bool TieBreakingByElevation(int rawStart, int rawDuration, int rawOffset, int rawElevLow, int rawElevHigh, int rawPriority)
    {
        // Constrain parameters
        var startMinutes = ((rawStart % 1400) + 1400) % 1400;
        var durationMinutes = 3 + (((rawDuration % 10) + 10) % 10); // 3..12
        var overlapOffset = 1 + (((rawOffset % (durationMinutes - 1)) + (durationMinutes - 1)) % (durationMinutes - 1)); // 1..durationMinutes-1
        var elevLow = 10 + (((rawElevLow % 35) + 35) % 35);   // 10..44
        var elevHigh = 45 + (((rawElevHigh % 45) + 45) % 45); // 45..89
        var priority = 1 + (((rawPriority % 10) + 10) % 10);  // 1..10

        var baseTime = new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var aos1 = baseTime.AddMinutes(startMinutes);
        var los1 = aos1.AddMinutes(durationMinutes);
        var aos2 = aos1.AddMinutes(overlapOffset); // overlaps with pass 1
        var los2 = aos2.AddMinutes(durationMinutes);

        // Use identical composite scores for both passes
        const double compositeScore = 5.0;

        var passLow = new ScoredPass
        {
            Pass = new PassInfo
            {
                SatelliteName = "SAT-LOW",
                NoradId = "25544",
                AosUtc = aos1,
                LosUtc = los1,
                MaxElevationDeg = elevLow,
                MaxElevationUtc = aos1.AddMinutes(durationMinutes / 2.0)
            },
            QualityScore = 0.5,
            SatellitePriority = priority,
            CompositeScore = compositeScore
        };

        var passHigh = new ScoredPass
        {
            Pass = new PassInfo
            {
                SatelliteName = "SAT-HIGH",
                NoradId = "25545",
                AosUtc = aos2,
                LosUtc = los2,
                MaxElevationDeg = elevHigh,
                MaxElevationUtc = aos2.AddMinutes(durationMinutes / 2.0)
            },
            QualityScore = 0.5,
            SatellitePriority = priority,
            CompositeScore = compositeScore
        };

        var candidates = new List<ScoredPass> { passLow, passHigh };
        var result = WeightedIntervalScheduler.Solve(candidates);

        // The solver should pick the higher-elevation pass due to tie-breaking
        if (result.Count != 1)
            return false;

        return result[0].Scored.Pass.MaxElevationDeg == elevHigh;
    }

    // ─── Property 7: Output Sorted by AOS ────────────────────────────────────────
    // Feature: session-planner, Property 7: Output Sorted by AOS
    // **Validates: Requirements 5.1**

    /// <summary>
    /// For any valid scheduler output, the list of selected passes SHALL be in
    /// strictly ascending order of AosUtc.
    /// </summary>
    [Property]
    public bool OutputSortedByAos(int[] seeds)
    {
        if (seeds == null || seeds.Length == 0)
            return true;

        var limited = seeds.Take(20).ToArray();
        var candidates = BuildCandidates(limited);

        var result = WeightedIntervalScheduler.Solve(candidates);

        for (int i = 0; i < result.Count - 1; i++)
        {
            if (result[i].Scored.Pass.AosUtc >= result[i + 1].Scored.Pass.AosUtc)
                return false;
        }

        return true;
    }
}
