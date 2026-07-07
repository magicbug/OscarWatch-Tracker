using FsCheck.Xunit;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

/// <summary>
/// **Validates: Requirements 1.1, 1.2, 1.3, 1.4**
///
/// Property-based tests verifying the sweep-line conflict detector:
/// overlap duration correctness, same-satellite exclusion, threshold
/// filtering, symmetry, and no false positives.
/// </summary>
public class PassConflictDetectorPropertyTests
{
    /// <summary>
    /// Property 1: Overlap duration correctness.
    ///
    /// **Validates: Requirements 1.1, 1.3**
    ///
    /// For any two passes from different satellites where intervals overlap,
    /// OverlapDuration SHALL equal min(LOS_A, LOS_B) - max(AOS_A, AOS_B).
    /// </summary>
    [Property(MaxTest = 200)]
    public bool Overlap_duration_equals_min_los_minus_max_aos(
        int aosOffsetSeconds, int durationA, int durationB)
    {
        if (!IsFinite(aosOffsetSeconds) || !IsFinite(durationA) || !IsFinite(durationB))
            return true;

        var baseTime = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var durA = TimeSpan.FromSeconds(Math.Abs(durationA % 600) + 60);
        var durB = TimeSpan.FromSeconds(Math.Abs(durationB % 600) + 60);
        var offset = TimeSpan.FromSeconds(aosOffsetSeconds % 300);

        var passA = CreatePass("11111", "SAT-A", baseTime, durA);
        var passB = CreatePass("22222", "SAT-B", baseTime + offset, durB);

        // Check if they actually overlap
        var overlapStart = passA.AosUtc > passB.AosUtc ? passA.AosUtc : passB.AosUtc;
        var overlapEnd = passA.LosUtc < passB.LosUtc ? passA.LosUtc : passB.LosUtc;
        var expectedOverlap = overlapEnd - overlapStart;

        if (expectedOverlap <= TimeSpan.Zero)
            return true; // No overlap, not relevant to this property

        var result = PassConflictDetector.Detect([passA, passB], TimeSpan.Zero);

        if (result.Conflicts.Count != 1)
            return false;

        return result.Conflicts[0].OverlapDuration == expectedOverlap;
    }

    /// <summary>
    /// Property 2: Same-satellite exclusion.
    ///
    /// **Validates: Requirements 1.2**
    ///
    /// For any passes from the same satellite (same NoradId), the detector
    /// SHALL NOT report a conflict between them regardless of temporal overlap.
    /// </summary>
    [Property(MaxTest = 200)]
    public bool Same_satellite_passes_never_produce_conflicts(
        int aosOffsetSeconds, int durationA, int durationB)
    {
        if (!IsFinite(aosOffsetSeconds) || !IsFinite(durationA) || !IsFinite(durationB))
            return true;

        var baseTime = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var durA = TimeSpan.FromSeconds(Math.Abs(durationA % 600) + 60);
        var durB = TimeSpan.FromSeconds(Math.Abs(durationB % 600) + 60);
        var offset = TimeSpan.FromSeconds(Math.Abs(aosOffsetSeconds % 120));

        // Same NoradId — heavily overlapping passes
        var passA = CreatePass("25544", "ISS", baseTime, durA);
        var passB = CreatePass("25544", "ISS", baseTime + offset, durB);

        var result = PassConflictDetector.Detect([passA, passB], TimeSpan.Zero);

        return result.Conflicts.Count == 0;
    }

    /// <summary>
    /// Property 3: Threshold filtering.
    ///
    /// **Validates: Requirements 1.4**
    ///
    /// For any pair of overlapping passes where OverlapDuration is less than
    /// the minimumOverlap threshold, the detector SHALL NOT include them.
    /// </summary>
    [Property(MaxTest = 200)]
    public bool Overlaps_below_threshold_are_excluded(
        int overlapSeconds, int thresholdSeconds)
    {
        if (!IsFinite(overlapSeconds) || !IsFinite(thresholdSeconds))
            return true;

        // Ensure threshold > overlap so the conflict should be filtered
        var overlap = TimeSpan.FromSeconds(Math.Abs(overlapSeconds % 60) + 1); // 1-60s overlap
        var threshold = overlap + TimeSpan.FromSeconds(Math.Abs(thresholdSeconds % 60) + 1); // threshold always > overlap

        var baseTime = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var passA = CreatePass("11111", "SAT-A", baseTime, TimeSpan.FromMinutes(10));
        // Place passB so overlap is exactly 'overlap' duration
        var passB = CreatePass("22222", "SAT-B", passA.LosUtc - overlap, TimeSpan.FromMinutes(10));

        var result = PassConflictDetector.Detect([passA, passB], threshold);

        return result.Conflicts.Count == 0;
    }

    /// <summary>
    /// Property 4: Symmetry.
    ///
    /// **Validates: Requirements 3.3**
    ///
    /// For any conflict between passes A and B, querying conflicts for A
    /// SHALL include B and querying conflicts for B SHALL include A.
    /// </summary>
    [Property(MaxTest = 200)]
    public bool Conflicts_are_symmetric(
        int aosOffsetSeconds, int durationA, int durationB)
    {
        if (!IsFinite(aosOffsetSeconds) || !IsFinite(durationA) || !IsFinite(durationB))
            return true;

        var baseTime = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var durA = TimeSpan.FromSeconds(Math.Abs(durationA % 600) + 120);
        var durB = TimeSpan.FromSeconds(Math.Abs(durationB % 600) + 120);
        var offset = TimeSpan.FromSeconds(Math.Abs(aosOffsetSeconds % 60));

        var passA = CreatePass("11111", "SAT-A", baseTime, durA);
        var passB = CreatePass("22222", "SAT-B", baseTime + offset, durB);

        var result = PassConflictDetector.Detect([passA, passB], TimeSpan.Zero);

        if (result.Conflicts.Count == 0)
            return true; // No conflict to check symmetry on

        var conflictsForA = result.GetConflictsFor(passA.NoradId, passA.AosUtc);
        var conflictsForB = result.GetConflictsFor(passB.NoradId, passB.AosUtc);

        return conflictsForA.Count > 0 && conflictsForB.Count > 0;
    }

    /// <summary>
    /// Property 5: No false positives.
    ///
    /// **Validates: Requirements 1.1**
    ///
    /// For any two passes where LOS_A ≤ AOS_B (no temporal overlap),
    /// the detector SHALL NOT report a conflict.
    /// </summary>
    [Property(MaxTest = 200)]
    public bool Non_overlapping_passes_produce_zero_conflicts(
        int gapSeconds, int durationA, int durationB)
    {
        if (!IsFinite(gapSeconds) || !IsFinite(durationA) || !IsFinite(durationB))
            return true;

        var baseTime = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var durA = TimeSpan.FromSeconds(Math.Abs(durationA % 600) + 60);
        var durB = TimeSpan.FromSeconds(Math.Abs(durationB % 600) + 60);
        var gap = TimeSpan.FromSeconds(Math.Abs(gapSeconds % 3600) + 1); // at least 1s gap

        var passA = CreatePass("11111", "SAT-A", baseTime, durA);
        var passB = CreatePass("22222", "SAT-B", passA.LosUtc + gap, durB);

        var result = PassConflictDetector.Detect([passA, passB], TimeSpan.Zero);

        return result.Conflicts.Count == 0;
    }

    // --- Unit Tests ---

    [Fact]
    public void Empty_list_returns_empty_result()
    {
        var result = PassConflictDetector.Detect([], TimeSpan.FromSeconds(30));

        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Boundary_touch_produces_no_conflict()
    {
        // LOS_A == AOS_B — touching but not overlapping
        var baseTime = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var passA = CreatePass("11111", "SAT-A", baseTime, TimeSpan.FromMinutes(10));
        var passB = CreatePass("22222", "SAT-B", passA.LosUtc, TimeSpan.FromMinutes(10));

        var result = PassConflictDetector.Detect([passA, passB], TimeSpan.Zero);

        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Three_way_conflict_detects_all_pairs()
    {
        var baseTime = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var passA = CreatePass("11111", "SAT-A", baseTime, TimeSpan.FromMinutes(10));
        var passB = CreatePass("22222", "SAT-B", baseTime.AddMinutes(2), TimeSpan.FromMinutes(10));
        var passC = CreatePass("33333", "SAT-C", baseTime.AddMinutes(4), TimeSpan.FromMinutes(10));

        var result = PassConflictDetector.Detect([passA, passB, passC], TimeSpan.Zero);

        // A↔B, A↔C, B↔C = 3 conflict pairs
        Assert.Equal(3, result.Conflicts.Count);
        Assert.True(result.HasConflicts("11111", passA.AosUtc));
        Assert.True(result.HasConflicts("22222", passB.AosUtc));
        Assert.True(result.HasConflicts("33333", passC.AosUtc));
    }

    // --- Helpers ---

    private static PassInfo CreatePass(string noradId, string name, DateTime aos, TimeSpan duration)
    {
        return new PassInfo
        {
            SatelliteName = name,
            NoradId = noradId,
            AosUtc = aos,
            LosUtc = aos + duration,
            MaxElevationDeg = 45.0,
            MaxElevationUtc = aos + duration / 2,
            AosAzimuthDeg = 180.0,
            LosAzimuthDeg = 0.0
        };
    }

    private static bool IsFinite(int value) => value != int.MinValue;
}
