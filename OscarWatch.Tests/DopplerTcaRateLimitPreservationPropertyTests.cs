// Feature: doppler-tca-rate-limit, Property 2: Preservation — Low-Acceleration Tracking Unchanged

using FsCheck.Xunit;
using OscarWatch.Core.Radio;

namespace OscarWatch.Tests;

/// <summary>
/// **Validates: Requirements 3.1, 3.2, 3.3, 3.4**
///
/// Preservation property tests: verify that for all inputs where the Doppler acceleration
/// (range-rate slope) is below <c>SlopeBlendStartKmPerSec2</c> (0.010 km/s²), the
/// <see cref="DopplerWritePacer"/> does not interfere with normal tracking behaviour.
///
/// These tests MUST PASS on unfixed (stub) code AND continue to pass after the full
/// implementation — the adaptive logic is gated on slope ≥ 0.010 km/s², so low-slope
/// inputs remain completely unaffected.
/// </summary>
public class DopplerTcaRateLimitPreservationPropertyTests
{
    /// <summary>
    /// The slope threshold below which all adaptive logic is dormant.
    /// Matches <see cref="DopplerCatLead.SlopeBlendStartKmPerSec2"/>.
    /// </summary>
    private const double SlopeBlendStartKmPerSec2 = 0.010;

    // ─── Property-Based Tests ────────────────────────────────────────────────────

    /// <summary>
    /// Property 2a: ShouldDeferWrite returns false for all slope values below 0.010 km/s²
    /// regardless of timing and direction state.
    ///
    /// For any (slope &lt; 0.010, catDelay, timeSinceLastWrite, dopplerReversed) tuple,
    /// the pacer never defers a write — preserving the existing 100 ms cadence.
    ///
    /// **Validates: Requirements 3.1, 3.4**
    /// </summary>
    [Property(MaxTest = 100)]
    public bool ShouldDeferWrite_returns_false_for_all_low_slope_inputs(
        int rawSlope, int rawCatDelay, int rawTimeSinceWrite, bool dopplerReversed)
    {
        // Constrain slope to [0, 0.010) km/s² — the low-acceleration domain
        var slopeKmPerSec2 = Math.Abs(rawSlope % 10000) * 0.000001; // 0.0 to 0.009999
        if (slopeKmPerSec2 >= SlopeBlendStartKmPerSec2)
            slopeKmPerSec2 = SlopeBlendStartKmPerSec2 - 0.000001;

        // catDelayMs: 0 to 200 ms (covers typical operator configurations)
        var catDelayMs = Math.Abs(rawCatDelay % 201);

        // timeSinceLastWriteMs: 0 to 1000 ms (covers all relevant timing windows)
        var timeSinceLastWriteMs = (double)Math.Abs(rawTimeSinceWrite % 1001);

        var result = DopplerWritePacer.ShouldDeferWrite(
            slopeKmPerSec2, catDelayMs, timeSinceLastWriteMs, dopplerReversed);

        // Low slope → never defer, regardless of timing or direction state
        return result == false;
    }

    /// <summary>
    /// Property 2b: AdaptiveThresholdHz returns the base threshold unchanged for all
    /// slope values below 0.010 km/s².
    ///
    /// For any (baseThreshold, slope &lt; 0.010) pair, the adaptive threshold equals
    /// the base threshold — no widening occurs on gentle passes.
    ///
    /// **Validates: Requirements 3.1, 3.4**
    /// </summary>
    [Property(MaxTest = 100)]
    public bool AdaptiveThresholdHz_returns_base_unchanged_for_all_low_slope_inputs(
        int rawBaseThreshold, int rawSlope)
    {
        // baseThresholdHz: 1 to 500 Hz (covers 50 Hz linear, 350 Hz FM, and beyond)
        var baseThresholdHz = Math.Abs(rawBaseThreshold % 500) + 1;

        // Constrain slope to [0, 0.010) km/s²
        var slopeKmPerSec2 = Math.Abs(rawSlope % 10000) * 0.000001; // 0.0 to 0.009999
        if (slopeKmPerSec2 >= SlopeBlendStartKmPerSec2)
            slopeKmPerSec2 = SlopeBlendStartKmPerSec2 - 0.000001;

        var result = DopplerWritePacer.AdaptiveThresholdHz(baseThresholdHz, slopeKmPerSec2);

        // Low slope → threshold unchanged
        return result == baseThresholdHz;
    }

    /// <summary>
    /// Property 2c: Low-slope scenarios produce normal write cadence — the pacer
    /// doesn't interfere regardless of how rapidly writes are attempted.
    ///
    /// Simulates a sequence of write-decision checks at various timings (including
    /// very rapid ones within the settle window) and confirms the pacer never blocks
    /// any of them when slope is below the threshold.
    ///
    /// **Validates: Requirements 3.1, 3.2, 3.3, 3.4**
    /// </summary>
    [Property(MaxTest = 100)]
    public bool Low_slope_write_cadence_is_never_throttled(
        int rawSlope, int rawCatDelay, int rawIterations)
    {
        // Low slope: [0, 0.010)
        var slopeKmPerSec2 = Math.Abs(rawSlope % 10000) * 0.000001;
        if (slopeKmPerSec2 >= SlopeBlendStartKmPerSec2)
            slopeKmPerSec2 = SlopeBlendStartKmPerSec2 - 0.000001;

        var catDelayMs = Math.Abs(rawCatDelay % 201);

        // Simulate 1 to 20 rapid write-decision checks at various timings
        var iterations = Math.Abs(rawIterations % 20) + 1;

        for (var i = 0; i < iterations; i++)
        {
            // Vary timing: some within settle window, some outside
            var timeSinceLastWriteMs = (double)(i * 50); // 0ms, 50ms, 100ms, ...
            var dopplerReversed = i % 3 == 0; // alternate reversal state

            var shouldDefer = DopplerWritePacer.ShouldDeferWrite(
                slopeKmPerSec2, catDelayMs, timeSinceLastWriteMs, dopplerReversed);

            if (shouldDefer)
                return false; // Pacer interfered with low-slope write — violation
        }

        return true; // All checks passed — pacer never interfered
    }

    // ─── Concrete Observation Tests (Fact) ───────────────────────────────────────

    /// <summary>
    /// Observation: with slope = 0.003 km/s² (typical low-elevation pass), the pacer
    /// never defers writes even at very short intervals with reversal active.
    ///
    /// **Validates: Requirements 3.1, 3.4**
    /// </summary>
    [Fact]
    public void Low_slope_0_003_never_defers_regardless_of_timing_or_reversal()
    {
        const double slope = 0.003; // well below 0.010 threshold

        // Test a range of catDelayMs, timings, and reversal states
        int[] catDelays = [0, 25, 50, 100, 200];
        double[] timings = [0, 10, 50, 100, 200, 350, 400, 1000];
        bool[] reversals = [false, true];

        foreach (var catDelay in catDelays)
        foreach (var timing in timings)
        foreach (var reversed in reversals)
        {
            var result = DopplerWritePacer.ShouldDeferWrite(slope, catDelay, timing, reversed);
            Assert.False(result,
                $"ShouldDeferWrite returned true for low slope={slope}, " +
                $"catDelay={catDelay}, time={timing}, reversed={reversed}");
        }
    }

    /// <summary>
    /// Observation: with slope = 0.003 km/s², AdaptiveThresholdHz returns the exact
    /// base threshold for all standard operating thresholds (50 Hz linear, 350 Hz FM).
    ///
    /// **Validates: Requirements 3.1, 3.4**
    /// </summary>
    [Fact]
    public void Low_slope_0_003_threshold_unchanged_for_standard_values()
    {
        const double slope = 0.003;

        Assert.Equal(50, DopplerWritePacer.AdaptiveThresholdHz(50, slope));
        Assert.Equal(350, DopplerWritePacer.AdaptiveThresholdHz(350, slope));
        Assert.Equal(1, DopplerWritePacer.AdaptiveThresholdHz(1, slope));
        Assert.Equal(500, DopplerWritePacer.AdaptiveThresholdHz(500, slope));
    }

    /// <summary>
    /// Observation: at the boundary (slope = 0.009999 km/s² — just below threshold),
    /// both pacer methods still behave as pass-through.
    ///
    /// **Validates: Requirements 3.1, 3.4**
    /// </summary>
    [Fact]
    public void Boundary_slope_just_below_threshold_preserves_passthrough()
    {
        const double slope = 0.009999; // just below 0.010

        // ShouldDeferWrite: never defers
        Assert.False(DopplerWritePacer.ShouldDeferWrite(slope, 50, 0, false));
        Assert.False(DopplerWritePacer.ShouldDeferWrite(slope, 50, 0, true));
        Assert.False(DopplerWritePacer.ShouldDeferWrite(slope, 100, 10, true));
        Assert.False(DopplerWritePacer.ShouldDeferWrite(slope, 0, 350, false));

        // AdaptiveThresholdHz: returns base unchanged
        Assert.Equal(50, DopplerWritePacer.AdaptiveThresholdHz(50, slope));
        Assert.Equal(350, DopplerWritePacer.AdaptiveThresholdHz(350, slope));
    }

    /// <summary>
    /// Observation: at zero slope (stationary or nearly so), both methods are no-ops.
    ///
    /// **Validates: Requirements 3.1, 3.4**
    /// </summary>
    [Fact]
    public void Zero_slope_is_complete_passthrough()
    {
        const double slope = 0.0;

        Assert.False(DopplerWritePacer.ShouldDeferWrite(slope, 50, 0, false));
        Assert.False(DopplerWritePacer.ShouldDeferWrite(slope, 50, 0, true));
        Assert.False(DopplerWritePacer.ShouldDeferWrite(slope, 200, 500, true));

        Assert.Equal(50, DopplerWritePacer.AdaptiveThresholdHz(50, slope));
        Assert.Equal(350, DopplerWritePacer.AdaptiveThresholdHz(350, slope));
    }
}
