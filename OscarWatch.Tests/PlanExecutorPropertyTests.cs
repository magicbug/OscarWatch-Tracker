using FsCheck;
using FsCheck.Xunit;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Core.SessionPlanner;

namespace OscarWatch.Tests;

/// <summary>
/// Property-based tests for PlanExecutor.
/// Validates correctness properties 11–12 from the session-planner design document.
/// </summary>
public sealed class PlanExecutorPropertyTests
{
    // ─── Stub ILiveTrackingService ───────────────────────────────────────────────

    private sealed class StubLiveTrackingService : ILiveTrackingService
    {
        public string? FocusedNoradId { get; set; }
        public DateTime SnapshotUtc => DateTime.MinValue;
        public TimeSpan MapTimeOffset { get; set; }
        public DateTime LiveNowSnapshotUtc => DateTime.MinValue;
        public IReadOnlyList<SatelliteTrackState> GetSnapshot() => [];
        public IReadOnlyList<SatelliteTrackState> GetLiveNowSnapshot() => [];
        public void Start() { }
        public void RequestReload() { }
        public void Dispose() { }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static readonly DateTime SessionStart = new(2025, 7, 1, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SessionEnd = new(2025, 7, 1, 20, 0, 0, DateTimeKind.Utc);

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
    /// Guarantees gaps between passes for pre-alert testing.
    /// </summary>
    private static List<ScoredPass> BuildNonOverlappingPasses(int[] seeds, int minGap = 1)
    {
        var passes = new List<ScoredPass>();
        int currentOffset = 0;
        var sessionMinutes = (int)(SessionEnd - SessionStart).TotalMinutes;

        for (int i = 0; i < seeds.Length; i++)
        {
            var seed = Math.Abs(seeds[i]);
            var durationMinutes = 3 + (seed % 13);       // 3..15 minutes
            var gap = minGap + ((seed / 13) % 20);       // minGap..minGap+19 minute gap
            var elevation = 10 + ((seed / 260) % 81);    // 10..90
            var priority = 1 + ((seed / 21060) % 10);    // 1..10

            var startOffset = currentOffset + gap;
            if (startOffset + durationMinutes > sessionMinutes)
                break;

            passes.Add(CreatePass(startOffset, durationMinutes, elevation, priority, i));
            currentOffset = startOffset + durationMinutes;
        }

        return passes;
    }

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

    // ─── Property 11: Plan Executor Focus State ──────────────────────────────────
    // Feature: session-planner, Property 11: Plan Executor Focus State
    // **Validates: Requirements 7.1, 7.5**

    /// <summary>
    /// For any session plan and any UTC time within the session window,
    /// PlanExecutor.Tick(utcNow) SHALL determine the correct focused NORAD ID:
    /// the NoradId of the pass whose [AOS, LOS] contains utcNow, or during a gap,
    /// the NoradId of the most recent completed pass.
    /// </summary>
    [Property]
    public bool PlanExecutorFocusState(int[] seeds, byte tickOffsetFraction)
    {
        if (seeds == null || seeds.Length < 2)
            return true;

        var limited = seeds.Take(8).ToArray();
        var passes = BuildNonOverlappingPasses(limited);

        if (passes.Count < 2)
            return true;

        var plan = BuildPlan(passes);
        var stub = new StubLiveTrackingService();
        using var executor = new PlanExecutor(stub);
        executor.Start(plan, preAlertMinutes: 2);

        // Pick a pass index to test — we'll test the time at AOS of that pass
        var passIndex = tickOffsetFraction % passes.Count;
        var targetPass = passes[passIndex];
        var utcNow = targetPass.Pass.AosUtc;

        // Tick sequentially from the first pass's AOS to just before our target,
        // so that the executor builds up its internal state correctly.
        for (int i = 0; i <= passIndex; i++)
        {
            var p = passes[i];
            // Tick at AOS of each pass
            executor.Tick(p.Pass.AosUtc);
            // Tick at LOS of each pass (to mark it completed)
            if (i < passIndex)
                executor.Tick(p.Pass.LosUtc.AddSeconds(1));
        }

        // Verify: at the target pass's AOS time, the focus should be on the target pass's NoradId
        var expectedNoradId = targetPass.Pass.NoradId;
        if (stub.FocusedNoradId != expectedNoradId)
            return false;

        // Now test gap behaviour: tick in the gap after this pass (if not last)
        if (passIndex < passes.Count - 1)
        {
            var gapTime = targetPass.Pass.LosUtc.AddSeconds(5);
            var nextPassAos = passes[passIndex + 1].Pass.AosUtc;

            // Only test gap if gapTime is actually in the gap
            if (gapTime < nextPassAos)
            {
                executor.Tick(gapTime);
                // During gap, FocusedNoradId should still be the last completed pass's NoradId
                if (stub.FocusedNoradId != expectedNoradId)
                    return false;
            }
        }

        return true;
    }

    // ─── Property 12: Pre-Alert Timing ───────────────────────────────────────────
    // Feature: session-planner, Property 12: Pre-Alert Timing
    // **Validates: Requirements 8.1, 8.4**

    /// <summary>
    /// For any session plan with pre-alert lead time L minutes, for each scheduled pass,
    /// the pre-alert SHALL fire at max(previousLOS, AOS − L minutes).
    /// If the gap before the pass is ≥ L, alert at AOS − L; if the gap &lt; L, alert at
    /// the previous pass's LOS time.
    /// </summary>
    [Property]
    public bool PreAlertTiming(int[] seeds, byte preAlertSeed)
    {
        if (seeds == null || seeds.Length < 2)
            return true;

        var limited = seeds.Take(8).ToArray();
        var passes = BuildNonOverlappingPasses(limited);

        if (passes.Count < 2)
            return true;

        // Use a pre-alert lead time between 1 and 15
        var preAlertMinutes = 1 + (preAlertSeed % 15);

        var plan = BuildPlan(passes);
        var stub = new StubLiveTrackingService();
        using var executor = new PlanExecutor(stub);
        executor.Start(plan, preAlertMinutes: preAlertMinutes);

        // Test pre-alert for the second pass (index 1) — it has a previous pass for gap calculation
        var targetPassIndex = 1;
        var targetPass = passes[targetPassIndex];
        var previousPass = passes[targetPassIndex - 1];

        var aos = targetPass.Pass.AosUtc;
        var previousLos = previousPass.Pass.LosUtc;
        var alertLeadTime = TimeSpan.FromMinutes(preAlertMinutes);

        // Expected alert time: max(previousLOS, AOS - L)
        var aosMinusL = aos - alertLeadTime;
        var expectedAlertTime = previousLos > aosMinusL ? previousLos : aosMinusL;

        // First, tick at AOS of the first pass so it's tracked
        executor.Tick(previousPass.Pass.AosUtc);

        // Tick just before the expected alert time — should NOT get a pre-alert for target pass
        var justBefore = expectedAlertTime.AddSeconds(-1);
        if (justBefore > previousPass.Pass.AosUtc)
        {
            var actionBefore = executor.Tick(justBefore);
            // If we get a pre-alert for the target pass before the expected time, that's a failure
            if (actionBefore is PlanExecutorAction.RaisePreAlert alertBefore
                && alertBefore.SatelliteName == targetPass.Pass.SatelliteName)
                return false;
        }

        // Tick at the expected alert time — should get a pre-alert for the target pass
        var actionAt = executor.Tick(expectedAlertTime);
        if (actionAt is PlanExecutorAction.RaisePreAlert alertAt)
        {
            // The alert should be for our target pass
            return alertAt.SatelliteName == targetPass.Pass.SatelliteName;
        }

        // If the alert didn't fire at the expected time, it might be because we already
        // triggered it (or the first pass's AOS triggered a switch instead).
        // Try ticking one more second to account for timing
        var actionAfter = executor.Tick(expectedAlertTime.AddSeconds(1));
        if (actionAfter is PlanExecutorAction.RaisePreAlert alertAfter)
        {
            return alertAfter.SatelliteName == targetPass.Pass.SatelliteName;
        }

        // The pre-alert should have fired at or very near the expected time
        return false;
    }
}
