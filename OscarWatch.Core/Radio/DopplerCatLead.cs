using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;

namespace OscarWatch.Core.Radio;

public readonly record struct DopplerLeadRangeRates(
    double RxRangeRateKmPerSec,
    double TxRangeRateKmPerSec,
    double LeadBlend)
{
    public void Deconstruct(out double rxRangeRateKmPerSec, out double txRangeRateKmPerSec)
    {
        rxRangeRateKmPerSec = RxRangeRateKmPerSec;
        txRangeRateKmPerSec = TxRangeRateKmPerSec;
    }
}

public static class DopplerCatLead
{
    /// <summary>
    /// CAT delay spaces serial commands; it is often much larger than actual tune latency.
    /// Cap lead so high pacing values do not over-predict Doppler for the whole pass.
    /// </summary>
    public const double MaxLeadMs = 50;

    /// <summary>Sample interval for detecting a steep range-rate leg (TCA vicinity).</summary>
    public const double RangeRateSlopeSampleSec = 1.0;

    /// <summary>
    /// Range-rate slope (km/s²) where lead blend begins ramping from snapshot rate.
    /// Below this, CAT uses the instantaneous range rate only.
    /// </summary>
    public const double SlopeBlendStartKmPerSec2 = 0.010;

    /// <summary>
    /// Range-rate slope (km/s²) where lead reaches full strength.
    /// Between start and full, blend ramps linearly to avoid a step at the gate.
    /// </summary>
    public const double SteepRangeRateSlopeKmPerSec2 = 0.016;

    /// <summary>
    /// On residual pass legs (moderate elevation after TCA), range rate is still large
    /// while acceleration is modest. Assist lead when both slope and |range rate| exceed these.
    /// </summary>
    public const double ResidualAssistRangeRateKmPerSec = 0.45;

    public const double ResidualAssistSlopeStartKmPerSec2 = 0.010;

    /// <summary>
    /// After TCA the satellite recedes (positive range rate) while slope falls below
    /// <see cref="SlopeBlendStartKmPerSec2"/>. Assist lead on that leg without affecting
    /// the symmetric AOS approach (negative range rate at similar |range rate|).
    /// </summary>
    public const double RecedingAssistMaxBlend = 0.6;

    public static DopplerLeadRangeRates ResolveRangeRates(
        IOrbitPropagator? propagator,
        RigSettings settings,
        GroundStation site,
        SatelliteTrackState state,
        DateTime utc)
    {
        var fallback = state.LookAngles?.RangeRateKmPerSec ?? 0;
        if (!settings.DopplerCatLeadEnabled
            || propagator is null
            || state.LookAngles is null
            || settings.ReceiveCatDelayMs() <= 0 && settings.TransmitCatDelayMs() <= 0)
        {
            return new DopplerLeadRangeRates(fallback, fallback, 0);
        }

        try
        {
            var slope = ComputeRangeRateSlopeKmPerSec2(propagator, state.NoradId, site, utc, fallback);
            var blend = ComputeLeadBlend(slope, fallback);
            if (blend <= 0)
                return new DopplerLeadRangeRates(fallback, fallback, 0);

            var rxLeadMs = ResolveLeadMs(settings.ReceiveCatDelayMs());
            var txLeadMs = ResolveLeadMs(settings.TransmitCatDelayMs());

            // Short-circuit: when both leads are equal and non-zero, call propagator once
            if (rxLeadMs > 0 && NearlyEqual(rxLeadMs, txLeadMs))
            {
                var sharedRate = propagator.GetLookAngles(state.NoradId, site, utc.AddMilliseconds(rxLeadMs)).RangeRateKmPerSec;
                var rate = Lerp(fallback, sharedRate, blend);
                return new DopplerLeadRangeRates(rate, rate, blend);
            }

            // Different leads: existing two-call path
            var rxLead = rxLeadMs > 0
                ? propagator.GetLookAngles(state.NoradId, site, utc.AddMilliseconds(rxLeadMs)).RangeRateKmPerSec
                : fallback;
            var txLead = txLeadMs > 0
                ? propagator.GetLookAngles(state.NoradId, site, utc.AddMilliseconds(txLeadMs)).RangeRateKmPerSec
                : fallback;

            var rxRate = Lerp(fallback, rxLead, blend);
            var txRate = Lerp(fallback, txLead, blend);
            return new DopplerLeadRangeRates(rxRate, txRate, blend);
        }
        catch
        {
            return new DopplerLeadRangeRates(fallback, fallback, 0);
        }
    }

    internal static bool IsSteepRangeRateLeg(
        IOrbitPropagator propagator,
        string noradId,
        GroundStation site,
        DateTime utc,
        double rangeRateKmPerSec) =>
        ComputeLeadBlend(ComputeRangeRateSlopeKmPerSec2(propagator, noradId, site, utc, rangeRateKmPerSec), rangeRateKmPerSec) >= 1.0;

    internal static double ComputeLeadBlend(double slopeKmPerSec2, double rangeRateKmPerSec = 0)
    {
        double SlopeBlend()
        {
            if (slopeKmPerSec2 <= SlopeBlendStartKmPerSec2)
                return 0;

            if (slopeKmPerSec2 >= SteepRangeRateSlopeKmPerSec2)
                return 1;

            return (slopeKmPerSec2 - SlopeBlendStartKmPerSec2)
                / (SteepRangeRateSlopeKmPerSec2 - SlopeBlendStartKmPerSec2);
        }

        var slopeBlend = SlopeBlend();
        var residualBlend = ComputeResidualAssistBlend(slopeKmPerSec2, rangeRateKmPerSec);
        var recedingBlend = ComputeRecedingAssistBlend(slopeKmPerSec2, rangeRateKmPerSec);

        return Math.Clamp(Math.Max(Math.Max(slopeBlend, residualBlend), recedingBlend), 0, 1);
    }

    internal static double ComputeResidualAssistBlend(double slopeKmPerSec2, double rangeRateKmPerSec)
    {
        if (Math.Abs(rangeRateKmPerSec) < ResidualAssistRangeRateKmPerSec
            || slopeKmPerSec2 <= ResidualAssistSlopeStartKmPerSec2)
        {
            return 0;
        }

        var cap = rangeRateKmPerSec > 0 ? 0.75 : 0.5;
        return Math.Min(cap,
            (slopeKmPerSec2 - ResidualAssistSlopeStartKmPerSec2)
            / (SteepRangeRateSlopeKmPerSec2 - ResidualAssistSlopeStartKmPerSec2) * cap);
    }

    /// <summary>
    /// Post-TCA receding leg: |range rate| stays high while slope drops below the blend start.
    /// AOS at the same elevation has negative range rate, so this path stays off on approach.
    /// </summary>
    internal static double ComputeRecedingAssistBlend(double slopeKmPerSec2, double rangeRateKmPerSec)
    {
        if (rangeRateKmPerSec <= 0
            || slopeKmPerSec2 >= SlopeBlendStartKmPerSec2
            || Math.Abs(rangeRateKmPerSec) < ResidualAssistRangeRateKmPerSec)
        {
            return 0;
        }

        var rateFactor = Math.Clamp(
            (Math.Abs(rangeRateKmPerSec) - ResidualAssistRangeRateKmPerSec) / 2.0,
            0,
            1);

        // Keep some assist while Doppler is still slewing; taper as slope nears zero.
        var slopeFactor = Math.Clamp(slopeKmPerSec2 / SlopeBlendStartKmPerSec2, 0.2, 1);

        return RecedingAssistMaxBlend * rateFactor * slopeFactor;
    }

    public static double ComputeRangeRateSlopeKmPerSec2(
        IOrbitPropagator propagator,
        string noradId,
        GroundStation site,
        DateTime utc,
        double rangeRateKmPerSec)
    {
        var ahead = propagator.GetLookAngles(
            noradId,
            site,
            utc.AddSeconds(RangeRateSlopeSampleSec));
        return Math.Abs(ahead.RangeRateKmPerSec - rangeRateKmPerSec) / RangeRateSlopeSampleSec;
    }

    internal static double ResolveLeadMs(int catDelayMs) =>
        catDelayMs <= 0 ? 0 : Math.Min(catDelayMs / 2.0, MaxLeadMs);

    private static double Lerp(double from, double to, double blend) =>
        from + (to - from) * blend;

    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 0.001;
}
