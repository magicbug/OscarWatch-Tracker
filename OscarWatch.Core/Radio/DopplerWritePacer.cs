namespace OscarWatch.Core.Radio;

/// <summary>
/// Adaptive rate-limiting for Doppler CAT writes near TCA.
/// Below SlopeBlendStartKmPerSec2, all methods are no-ops (preserving existing behaviour).
/// </summary>
public static class DopplerWritePacer
{
    public const int PostCatWriteDialSettleMs = 350;

    /// <summary>
    /// Determines whether a CAT write should be deferred based on Doppler acceleration,
    /// radio settle time, and direction-change state.
    /// </summary>
    /// <param name="slopeKmPerSec2">Current range-rate slope (km/s²).</param>
    /// <param name="catDelayMs">Operator-configured inter-command spacing (ms).</param>
    /// <param name="timeSinceLastWriteMs">Elapsed time since the previous CAT write (ms).</param>
    /// <param name="dopplerReversed">True if Doppler direction has reversed since last write.</param>
    /// <returns>True if the write should be deferred (suppressed this iteration).</returns>
    public static bool ShouldDeferWrite(double slopeKmPerSec2, int catDelayMs, double timeSinceLastWriteMs, bool dopplerReversed)
    {
        // Below the blend start threshold, never defer — preserves existing behaviour
        if (slopeKmPerSec2 < DopplerCatLead.SlopeBlendStartKmPerSec2)
            return false;

        double adaptiveMinInterval;

        if (dopplerReversed)
        {
            // On Doppler reversal, enforce the FULL settle window regardless of blend
            adaptiveMinInterval = catDelayMs + PostCatWriteDialSettleMs;
        }
        else
        {
            // Compute blend factor: ramps 0→1 over [SlopeBlendStartKmPerSec2, SteepRangeRateSlopeKmPerSec2]
            var blendFactor = ComputeBlendFactor(slopeKmPerSec2);
            adaptiveMinInterval = catDelayMs + PostCatWriteDialSettleMs * blendFactor;
        }

        return timeSinceLastWriteMs < adaptiveMinInterval;
    }

    /// <summary>
    /// Computes an adaptive frequency-change threshold that widens on steep Doppler legs,
    /// reducing the number of writes that pass the ShouldWrite gate.
    /// </summary>
    /// <param name="baseThresholdHz">The base threshold (e.g. 50 Hz linear, 350 Hz FM).</param>
    /// <param name="slopeKmPerSec2">Current range-rate slope (km/s²).</param>
    /// <returns>The adaptive threshold (Hz), ranging from 1× to 3× the base.</returns>
    public static int AdaptiveThresholdHz(int baseThresholdHz, double slopeKmPerSec2)
    {
        // Below the blend start threshold, return base unchanged — preserves existing behaviour
        if (slopeKmPerSec2 < DopplerCatLead.SlopeBlendStartKmPerSec2)
            return baseThresholdHz;

        // Compute blend factor: ramps 0→1 over [SlopeBlendStartKmPerSec2, SteepRangeRateSlopeKmPerSec2]
        var blendFactor = ComputeBlendFactor(slopeKmPerSec2);

        // Ramp from 1× to 3× base threshold (adding up to 2× base at full blend)
        return baseThresholdHz + (int)(baseThresholdHz * 2 * blendFactor);
    }

    /// <summary>
    /// Computes the blend factor (0→1) over the slope ramp range, clamped at 1.0.
    /// </summary>
    private static double ComputeBlendFactor(double slopeKmPerSec2)
    {
        if (slopeKmPerSec2 >= DopplerCatLead.SteepRangeRateSlopeKmPerSec2)
            return 1.0;

        return (slopeKmPerSec2 - DopplerCatLead.SlopeBlendStartKmPerSec2)
            / (DopplerCatLead.SteepRangeRateSlopeKmPerSec2 - DopplerCatLead.SlopeBlendStartKmPerSec2);
    }
}
