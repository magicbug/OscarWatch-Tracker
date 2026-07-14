namespace OscarWatch.Core.SessionPlanner;

/// <summary>
/// Selects the maximum-weight non-overlapping subset of candidate passes
/// using dynamic-programming weighted interval scheduling.
/// O(n log n) time, O(n) space.
/// </summary>
public static class WeightedIntervalScheduler
{
    /// <summary>
    /// Selects the optimal non-overlapping subset of candidate passes,
    /// respecting forced inclusions and minimum elevation filtering.
    /// </summary>
    /// <param name="candidates">Scored candidate passes (any order).</param>
    /// <param name="forcedInclusionIds">Pass IDs that must appear in the solution (may be null).</param>
    /// <param name="minimumElevationDeg">Passes below this max elevation are excluded.</param>
    /// <returns>Optimal non-overlapping selection ordered by AOS time.</returns>
    public static IReadOnlyList<ScheduledPass> Solve(
        IReadOnlyList<ScoredPass> candidates,
        IReadOnlySet<string>? forcedInclusionIds = null,
        double minimumElevationDeg = 0)
    {
        if (candidates.Count == 0)
            return [];

        // Step 1: Filter by minimum elevation threshold.
        var filtered = new List<ScoredPass>();
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Pass.MaxElevationDeg >= minimumElevationDeg)
                filtered.Add(candidates[i]);
        }

        if (filtered.Count == 0)
            return [];

        // Separate forced passes from algorithm candidates.
        var forcedIds = forcedInclusionIds ?? new HashSet<string>();
        var forcedPasses = new List<ScoredPass>();
        var remainingCandidates = new List<ScoredPass>();

        for (int i = 0; i < filtered.Count; i++)
        {
            if (forcedIds.Contains(filtered[i].Id))
                forcedPasses.Add(filtered[i]);
            else
                remainingCandidates.Add(filtered[i]);
        }

        // If no forced passes, solve the entire set directly.
        if (forcedPasses.Count == 0)
        {
            var selected = SolveSubProblem(remainingCandidates);
            return SortByAos(selected, PassSelectionReason.AlgorithmSelected);
        }

        // Step 6: Handle forced inclusions.
        // Sort forced passes by AOS to establish gap intervals.
        forcedPasses.Sort((a, b) => a.Pass.AosUtc.CompareTo(b.Pass.AosUtc));

        // Remove candidates that overlap with any forced pass.
        var nonOverlapping = new List<ScoredPass>();
        for (int i = 0; i < remainingCandidates.Count; i++)
        {
            if (!OverlapsAnyForced(remainingCandidates[i], forcedPasses))
                nonOverlapping.Add(remainingCandidates[i]);
        }

        // Solve sub-problems in gaps between forced intervals.
        var result = new List<ScheduledPass>();

        // Add forced passes to result.
        for (int i = 0; i < forcedPasses.Count; i++)
        {
            result.Add(new ScheduledPass
            {
                Scored = forcedPasses[i],
                Reason = PassSelectionReason.ForceIncluded
            });
        }

        // Solve the remaining candidates that fit in gaps.
        if (nonOverlapping.Count > 0)
        {
            var gapSelected = SolveSubProblem(nonOverlapping);
            for (int i = 0; i < gapSelected.Count; i++)
            {
                result.Add(new ScheduledPass
                {
                    Scored = gapSelected[i],
                    Reason = PassSelectionReason.AlgorithmSelected
                });
            }
        }

        // Sort final output by AOS time.
        result.Sort((a, b) => a.Scored.Pass.AosUtc.CompareTo(b.Scored.Pass.AosUtc));
        return result;
    }

    /// <summary>
    /// Solves the weighted interval scheduling problem for a set of candidates
    /// using dynamic programming with backtracking.
    /// </summary>
    private static List<ScoredPass> SolveSubProblem(List<ScoredPass> candidates)
    {
        if (candidates.Count == 0)
            return [];

        // Sort by LOS time (end time).
        candidates.Sort((a, b) => a.Pass.LosUtc.CompareTo(b.Pass.LosUtc));

        int n = candidates.Count;

        // Compute effective weights with tie-breaking epsilon.
        var weights = new double[n];
        for (int i = 0; i < n; i++)
        {
            weights[i] = candidates[i].CompositeScore
                         + candidates[i].Pass.MaxElevationDeg * 1e-10;
        }

        // Compute p[i]: the largest index j < i such that candidates[j] does not overlap candidates[i].
        // candidates[j].LosUtc <= candidates[i].AosUtc (no overlap means j ends at or before i starts).
        var p = new int[n];
        for (int i = 0; i < n; i++)
        {
            p[i] = FindLatestNonOverlapping(candidates, i);
        }

        // DP recurrence: dp[i] = max total weight using candidates[0..i].
        // dp[i] = max(dp[i-1], dp[p[i]] + weights[i])
        // We use 1-indexed dp where dp[0] = 0 (no candidates selected).
        var dp = new double[n + 1];
        dp[0] = 0;

        for (int i = 1; i <= n; i++)
        {
            double includeI = weights[i - 1] + dp[p[i - 1] + 1]; // p is 0-indexed returning -1..n-1, shift to dp index
            double excludeI = dp[i - 1];
            dp[i] = Math.Max(includeI, excludeI);
        }

        // Backtrack to recover the selected set.
        var selected = new List<ScoredPass>();
        int j = n;
        while (j > 0)
        {
            double includeJ = weights[j - 1] + dp[p[j - 1] + 1];
            if (includeJ >= dp[j - 1])
            {
                selected.Add(candidates[j - 1]);
                j = p[j - 1] + 1; // jump to the dp index of p[j-1]
            }
            else
            {
                j--;
            }
        }

        return selected;
    }

    /// <summary>
    /// Binary search for the largest index j &lt; i such that candidates[j].LosUtc &lt;= candidates[i].AosUtc.
    /// Returns -1 if no such index exists. Candidates must be sorted by LOS.
    /// </summary>
    private static int FindLatestNonOverlapping(List<ScoredPass> candidates, int i)
    {
        DateTime target = candidates[i].Pass.AosUtc;
        int lo = 0;
        int hi = i - 1;
        int result = -1;

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (candidates[mid].Pass.LosUtc <= target)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }

    /// <summary>
    /// Checks whether a candidate pass overlaps any of the forced passes.
    /// </summary>
    private static bool OverlapsAnyForced(ScoredPass candidate, List<ScoredPass> forcedPasses)
    {
        for (int i = 0; i < forcedPasses.Count; i++)
        {
            if (Overlaps(candidate, forcedPasses[i]))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Two passes overlap if their [AOS, LOS] intervals intersect
    /// (i.e. one starts before the other ends and vice versa).
    /// Touching boundaries (LOS == AOS) is not considered an overlap.
    /// </summary>
    private static bool Overlaps(ScoredPass a, ScoredPass b)
    {
        return a.Pass.AosUtc < b.Pass.LosUtc && b.Pass.AosUtc < a.Pass.LosUtc;
    }

    /// <summary>
    /// Wraps selected passes as ScheduledPass with the given reason and sorts by AOS.
    /// </summary>
    private static IReadOnlyList<ScheduledPass> SortByAos(
        List<ScoredPass> selected,
        PassSelectionReason reason)
    {
        var result = new List<ScheduledPass>(selected.Count);
        for (int i = 0; i < selected.Count; i++)
        {
            result.Add(new ScheduledPass
            {
                Scored = selected[i],
                Reason = reason
            });
        }
        result.Sort((a, b) => a.Scored.Pass.AosUtc.CompareTo(b.Scored.Pass.AosUtc));
        return result;
    }
}
