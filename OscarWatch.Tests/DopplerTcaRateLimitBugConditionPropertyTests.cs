// Feature: doppler-tca-rate-limit, Property 1: Bug Condition — High-Acceleration Writes Outpace Radio Settle Time

using FsCheck.Xunit;
using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

/// <summary>
/// **Validates: Requirements 1.1, 1.2, 1.3**
///
/// Bug condition exploration test: demonstrates that during high Doppler acceleration
/// (near TCA), the system issues CAT writes faster than the radio can settle.
///
/// The current <c>ShouldWrite</c> logic returns true every 100ms loop iteration when
/// frequency delta exceeds threshold (50 Hz linear) — which happens every iteration on
/// high-slope passes. The radio needs <c>catDelayMs + PostCatWriteDialSettleMs</c> (≈400 ms)
/// to settle, yet writes fire every 100 ms.
///
/// This test is EXPECTED TO FAIL on unfixed code — failure confirms the bug exists.
/// After the fix (DopplerWritePacer), these tests will pass.
/// </summary>
public class DopplerTcaRateLimitBugConditionPropertyTests
{
    /// <summary>
    /// The radio's dial-settle window after a CAT write (from RigController.PostCatWriteDialSettleMs).
    /// </summary>
    private const int PostCatWriteDialSettleMs = 350;

    /// <summary>
    /// Bug Condition — High-Slope Continuous Writes
    ///
    /// With slope = 0.018 km/s² (well above SlopeBlendStartKmPerSec2 = 0.010) and
    /// CatDelayMs = 50 ms, the current code fires writes every time the frequency delta
    /// exceeds the 50 Hz threshold — which with a fast-changing Doppler happens much faster
    /// than the radio's settle time of catDelayMs + PostCatWriteDialSettleMs (≈400 ms).
    ///
    /// Expected behaviour: writes should be deferred until at least
    /// <c>catDelayMs + PostCatWriteDialSettleMs × blendFactor</c> (≈400 ms) has elapsed.
    ///
    /// Actual (unfixed): writes fire as soon as CatDelayMs (50ms) has elapsed and the
    /// frequency delta exceeds threshold — no awareness of the radio settle window.
    ///
    /// To reliably demonstrate the bug, we use a large range rate shift between iterations
    /// (simulating the led+slope scenario where the target frequency jumps significantly
    /// each loop) so the 50 Hz threshold is exceeded every iteration.
    /// </summary>
    [Fact]
    public void High_slope_writes_should_not_fire_faster_than_radio_settle_time()
    {
        // Arrange: Use a scenario where each iteration produces a large enough frequency
        // change to exceed the 50 Hz threshold. This simulates what happens near TCA:
        // the led frequency shifts rapidly because the propagator-predicted rate changes
        // significantly within the loop interval.
        //
        // At 435 MHz, 50 Hz threshold corresponds to ΔrangeRate ≈ 50 / (435850/299792) ≈ 0.034 km/s
        // So we need at least 0.035 km/s change per iteration to guarantee threshold crossing.
        // With slope = 0.018 km/s², lead enabled and high base rate, the target shifts ~150 Hz/iteration.
        const double initialRangeRate = -5.0; // km/s — high base rate for large absolute shifts
        const double slopeKmPerSec2 = 0.018;
        const int catDelayMs = 50;

        // Each iteration shifts range rate by 0.05 km/s (simulating 500ms of slope × iteration,
        // representing the cumulative shift the lead predictor sees). This produces ~73 Hz delta
        // per iteration on a 435 MHz downlink, exceeding the 50 Hz threshold every time.
        const double rangeRateShiftPerIteration = 0.05;

        var propagator = new SteepSlopePropagator(initialRangeRate, initialRangeRate + slopeKmPerSec2);
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig, propagator: propagator);

        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            DopplerCatLeadEnabled = true,
            CatDelayMs = catDelayMs
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_850.45,
            UplinkKHz = 145_952.65,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var state = new SatelliteTrackState
        {
            Name = "FO-29",
            NoradId = "99999",
            Subpoint = new GeoCoordinate(0, 0, 400),
            LookAngles = new LookAngles(180, 85, 400, initialRangeRate)
        };

        var ctx = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, initialRangeRate, 0),
            TransmitOffsetKHz = 0,
            ReceiveOffsetKHz = 0
        };

        // Act: initialise — first Update triggers a write
        controller.Update(settings, ctx);
        controller.DrainCommandQueueForTests();

        var writesAfterInit = rig.SetFrequencyCallCount;

        // Simulate rapid loop iterations with changing range rates.
        var currentRangeRate = initialRangeRate;
        const int iterations = 10; // ~600ms total (10 × 60ms sleeps)

        for (var i = 0; i < iterations; i++)
        {
            // Advance the range rate to simulate rapid Doppler change near TCA
            currentRangeRate += rangeRateShiftPerIteration;

            // Update look angles and propagator with the new rate
            propagator.SnapshotRate = currentRangeRate;
            propagator.FutureRate = currentRangeRate + slopeKmPerSec2;

            state = new SatelliteTrackState
            {
                Name = "FO-29",
                NoradId = "99999",
                Subpoint = new GeoCoordinate(0, 0, 400),
                LookAngles = new LookAngles(180, 85, 400, currentRangeRate)
            };

            ctx = new RigTrackingContext
            {
                TrackState = state,
                Mode = mode,
                Corrected = DopplerFrequencyCalculator.Compute(mode, currentRangeRate, 0),
                TransmitOffsetKHz = 0,
                ReceiveOffsetKHz = 0
            };

            // Wait just enough to clear the CatDelayMs gate (50ms)
            // but NOT enough for the full settle time (400ms)
            Thread.Sleep(catDelayMs + 10); // 60ms — past CatDelayMs but well within settle time

            controller.Update(settings, ctx);
            controller.DrainCommandQueueForTests();
        }

        // Total elapsed ≈ 10 × 60ms = 600ms
        // Assert: The EXPECTED behaviour is that writes should NOT fire faster than
        // catDelayMs + PostCatWriteDialSettleMs × blendFactor.
        // With slope 0.018 (above SteepRangeRateSlopeKmPerSec2), blendFactor = 1.0,
        // so minimum interval = catDelayMs + PostCatWriteDialSettleMs = 50 + 350 = 400 ms.
        //
        // Over ~600ms with 400ms minimum interval, we expect at most 1 additional write.
        // Bug: unfixed code writes on nearly every iteration because CatDelayMs (50ms) has
        // elapsed and the frequency delta exceeds 50 Hz.
        var totalPhysicalWrites = rig.SetFrequencyCallCount - writesAfterInit;
        // Each logical Doppler write cycle writes BOTH RX and TX (full-duplex transponder),
        // so SetFrequencyCallCount increments by 2 per cycle.
        var totalDopplerCycles = totalPhysicalWrites / 2;
        const int expectedMaxCycles = 1; // 600ms / 400ms minimum interval = 1.5, so max 1 cycle

        Assert.True(totalDopplerCycles <= expectedMaxCycles,
            $"Bug confirmed: {totalDopplerCycles} Doppler write cycle(s) ({totalPhysicalWrites} physical writes) in ~600 ms with slope=0.018 km/s². " +
            $"Expected at most {expectedMaxCycles} cycle(s) (one per {catDelayMs + PostCatWriteDialSettleMs} ms settle window). " +
            $"The radio cannot keep up — commands are queuing.");
    }

    /// <summary>
    /// Bug Condition — Doppler Reversal Overshoot
    ///
    /// When range-rate crosses zero (sign change) with high slope, the current code fires
    /// a direction-reversed write immediately (within 100ms of the prior write), without
    /// waiting for the radio to settle.
    ///
    /// Expected behaviour: on reversal, a full settle window
    /// (<c>catDelayMs + PostCatWriteDialSettleMs</c>) should be enforced before the next write.
    ///
    /// Actual (unfixed): the reversed write fires as soon as CatDelayMs (50ms) has elapsed.
    /// </summary>
    [Fact]
    public void Doppler_reversal_should_enforce_full_settle_before_next_write()
    {
        const double slopeKmPerSec2 = 0.015;
        const int catDelayMs = 50;
        const double preReversalRate = -0.05; // approaching — just before zero crossing
        const double postReversalRate = 0.05;  // receding — just after zero crossing

        var propagator = new SteepSlopePropagator(preReversalRate, preReversalRate + slopeKmPerSec2);
        var rig = new RecordingRigDriver();
        var controller = new RigController(_ => rig, propagator: propagator);

        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.Dummy,
            DopplerThresholdLinearHz = 50,
            DopplerCatLeadEnabled = true,
            CatDelayMs = catDelayMs
        };

        var mode = new SatelliteTransponderMode
        {
            DownlinkKHz = 435_850.45,
            UplinkKHz = 145_952.65,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        // Start with a negative range rate (approaching)
        var state = new SatelliteTrackState
        {
            Name = "FO-29",
            NoradId = "99999",
            Subpoint = new GeoCoordinate(0, 0, 400),
            LookAngles = new LookAngles(180, 88, 400, preReversalRate)
        };

        var ctx = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, preReversalRate, 0),
            TransmitOffsetKHz = 0,
            ReceiveOffsetKHz = 0
        };

        // Initialise: first write at pre-reversal frequency
        controller.Update(settings, ctx);
        controller.DrainCommandQueueForTests();

        var writesBeforeReversal = rig.SetFrequencyCallCount;

        // Wait just enough to clear CatDelayMs but NOT the full settle time
        Thread.Sleep(catDelayMs + 10); // 60ms — within the 400ms settle window

        // Now reverse direction: range rate crosses zero to positive (receding)
        propagator.SnapshotRate = postReversalRate;
        propagator.FutureRate = postReversalRate + slopeKmPerSec2;

        state = new SatelliteTrackState
        {
            Name = "FO-29",
            NoradId = "99999",
            Subpoint = new GeoCoordinate(0, 0, 400),
            LookAngles = new LookAngles(180, 88, 400, postReversalRate)
        };

        ctx = new RigTrackingContext
        {
            TrackState = state,
            Mode = mode,
            Corrected = DopplerFrequencyCalculator.Compute(mode, postReversalRate, 0),
            TransmitOffsetKHz = 0,
            ReceiveOffsetKHz = 0
        };

        controller.Update(settings, ctx);
        controller.DrainCommandQueueForTests();

        var writesAfterReversal = rig.SetFrequencyCallCount - writesBeforeReversal;

        // Assert: On reversal with high slope, the EXPECTED behaviour is that the system
        // waits a full settle window (catDelayMs + PostCatWriteDialSettleMs = 400 ms)
        // before issuing the reversed-direction write.
        //
        // Since only 60ms has elapsed since the last write, no write should occur yet.
        // Bug: unfixed code writes immediately after CatDelayMs (50ms), ignoring settle needs.
        Assert.Equal(0, writesAfterReversal);
    }

    /// <summary>
    /// Property 1: Bug Condition (Property-Based) — High Slope Causes Write Deferral
    ///
    /// For any slope ≥ SlopeBlendStartKmPerSec2 (0.010 km/s²) with CatDelayMs between
    /// 20–100 ms, DopplerWritePacer.ShouldDeferWrite returns true when
    /// timeSinceLastWrite &lt; adaptiveMinInterval (catDelayMs + PostCatWriteDialSettleMs × blendFactor).
    ///
    /// This property tests the pure pacer logic directly, avoiding Thread.Sleep timing sensitivity.
    /// The companion Fact tests above exercise the full integration path.
    /// </summary>
    [Property(MaxTest = 100)]
    public bool High_slope_write_interval_respects_radio_settle_time(int rawSlope, int rawCatDelay, int rawTimeFraction)
    {
        // Constrain inputs to the bug condition domain
        // slope ≥ 0.010 km/s² (SlopeBlendStartKmPerSec2)
        var slopeKmPerSec2 = 0.010 + Math.Abs(rawSlope % 10) * 0.001; // 0.010 to 0.019
        var catDelayMs = 20 + Math.Abs(rawCatDelay % 81); // 20 to 100

        // Compute blend factor for this slope (same formula as the pacer)
        var blendFactor = slopeKmPerSec2 >= DopplerCatLead.SteepRangeRateSlopeKmPerSec2
            ? 1.0
            : (slopeKmPerSec2 - DopplerCatLead.SlopeBlendStartKmPerSec2)
              / (DopplerCatLead.SteepRangeRateSlopeKmPerSec2 - DopplerCatLead.SlopeBlendStartKmPerSec2);

        // Expected minimum interval between writes
        var expectedMinIntervalMs = catDelayMs + (int)(PostCatWriteDialSettleMs * blendFactor);

        // Generate a time since last write that is WITHIN the settle window
        // (0 to expectedMinIntervalMs - 1). This simulates the bug condition:
        // the radio hasn't settled yet, but a write would be attempted.
        var timeSinceLastWriteMs = (double)(Math.Abs(rawTimeFraction) % Math.Max(1, expectedMinIntervalMs));

        // Property: ShouldDeferWrite MUST return true (defer) when the elapsed time
        // is less than the adaptive minimum interval. This prevents radio queuing.
        var shouldDefer = DopplerWritePacer.ShouldDeferWrite(
            slopeKmPerSec2, catDelayMs, timeSinceLastWriteMs, dopplerReversed: false);

        return shouldDefer;
    }

    /// <summary>
    /// Propagator stub that produces a configurable steep slope.
    /// Returns <see cref="FutureRate"/> for any future time lookup (simulating slope),
    /// and <see cref="SnapshotRate"/> as the baseline.
    /// </summary>
    private sealed class SteepSlopePropagator : IOrbitPropagator
    {
        public double SnapshotRate { get; set; }
        public double FutureRate { get; set; }

        public SteepSlopePropagator(double snapshotRate, double futureRate)
        {
            SnapshotRate = snapshotRate;
            FutureRate = futureRate;
        }

        public void Clear() { }
        public void LoadSatellite(SatelliteCatalogEntry entry) { }
        public void RemoveSatellite(string noradId) { }
        public GeoCoordinate GetSubpoint(string noradId, DateTime utc) => new(0, 0, 400);
        public EciPosition GetEciPosition(string noradId, DateTime utc) => new(0, 0, 0);
        public bool HasSatellite(string noradId) => true;
        public IReadOnlyCollection<string> LoadedNoradIds => ["99999"];

        public LookAngles GetLookAngles(string noradId, GroundStation site, DateTime utc)
        {
            // DopplerCatLead.ComputeRangeRateSlopeKmPerSec2 calls GetLookAngles with
            // utc + RangeRateSlopeSampleSec (1.0s ahead). Return FutureRate for that.
            // DopplerCatLead.ResolveRangeRates also calls for lead time (half CatDelayMs ahead).
            // Both cases use FutureRate to produce a steep slope.
            return new LookAngles(180, 85, 400, FutureRate);
        }
    }
}
