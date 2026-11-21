using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;

namespace OscarWatch.Core.CwDecode;

/// <summary>Outcome of evaluating a measured CW echo against transponder math and safety limits.</summary>
public enum CwUplinkAlignDecision
{
    Apply,
    AlreadyAligned,
    ExceedsSafetyCap,
    OutOfPassbandBounds,
    InvalidMode
}

/// <summary>Deterministic result of computing a CW uplink TX-offset correction.</summary>
public sealed record CwUplinkAlignEvaluation(
    CwUplinkAlignDecision Decision,
    double EchoAudioHz,
    double TargetPitchHz,
    double DownlinkErrorHz,
    double TxDeltaHz,
    double NewTransmitOffsetKHz);

/// <summary>
/// Pure RF sign math, linear-transponder eligibility, and safety-cap checks for automatic
/// CW uplink alignment on linear transponders.
/// </summary>
public static class CwUplinkAlignCalculator
{
    /// <summary>Corrections smaller than this are treated as already aligned.</summary>
    public const double DeadbandHz = 5.0;

    /// <summary>
    /// True only for two-way linear transponders (USB/LSB/CW on both legs). Excludes beacons,
    /// FM voice/APRS repeaters, FT4/FT8 DATA modes, and unknown modes.
    /// </summary>
    public static bool IsEligibleTransponder(SatelliteTransponderMode? mode) =>
        mode is not null
        && !mode.IsBeaconOnly
        && !mode.IsFmMode
        && !string.IsNullOrWhiteSpace(mode.UplinkMode)
        && !string.IsNullOrWhiteSpace(mode.DownlinkMode)
        && SetupVfosPolicy.IsLinearMode(mode.UplinkMode)
        && SetupVfosPolicy.IsLinearMode(mode.DownlinkMode);

    /// <summary>True when the active operating mode is CW or CW-R.</summary>
    public static bool IsCwMode(string? mode)
    {
        var m = mode?.Trim();
        return string.Equals(m, "CW", StringComparison.OrdinalIgnoreCase)
            || string.Equals(m, "CW-R", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sign relating audio pitch change to downlink RF frequency change:
    /// +1 for CW/USB (higher audio pitch = higher RF), -1 for CW-R/LSB (higher audio pitch = lower RF),
    /// 0 for unsupported or non-linear receive modes.
    /// </summary>
    public static int ResolveReceiveSidebandSign(string? receiveMode)
    {
        var m = receiveMode?.Trim().ToUpperInvariant() ?? "";
        if (m is "CW-R" or "CWR" or "CW-L" or "CW-LSB" or "LSB")
            return -1;
        if (m is "USB" or "CW" or "CW-U" or "CW-USB")
            return 1;
        return 0;
    }

    /// <summary>
    /// Compute the signed TX offset change (Hz) needed to move the heard echo from
    /// <paramref name="echoAudioHz"/> onto <paramref name="targetPitchHz"/>.
    /// </summary>
    public static double ComputeTxDeltaHz(
        double echoAudioHz,
        double targetPitchHz,
        DopplerCorrection dopplerSense,
        string? receiveMode)
    {
        var rxSign = ResolveReceiveSidebandSign(receiveMode);
        if (rxSign == 0)
            return double.NaN;

        // Downlink RF error: positive means echo RF is higher than intended.
        // CW / USB: higher audio tone = higher RF (+1). CW-R / LSB: higher audio tone = lower RF (-1).
        var downlinkErrorHz = rxSign * (echoAudioHz - targetPitchHz);

        // NOR: high echo means uplink was high -> decrease TX offset (-downlinkErrorHz).
        // REV: high echo means uplink was low  -> increase TX offset (+downlinkErrorHz).
        return dopplerSense == DopplerCorrection.Reverse ? downlinkErrorHz : -downlinkErrorHz;
    }

    public static CwUplinkAlignEvaluation Evaluate(
        SatelliteTransponderMode? mode,
        string? receiveMode,
        double echoAudioHz,
        double targetPitchHz,
        double currentTransmitOffsetKHz,
        double maxCorrectionHz,
        double offsetMinKHz = -20.0,
        double offsetMaxKHz = 20.0)
    {
        var rxSign = ResolveReceiveSidebandSign(receiveMode);
        if (!IsEligibleTransponder(mode)
            || rxSign == 0
            || !double.IsFinite(echoAudioHz)
            || !double.IsFinite(targetPitchHz)
            || !double.IsFinite(currentTransmitOffsetKHz)
            || !double.IsFinite(maxCorrectionHz)
            || maxCorrectionHz <= 0)
        {
            return new CwUplinkAlignEvaluation(
                CwUplinkAlignDecision.InvalidMode,
                echoAudioHz,
                targetPitchHz,
                0,
                0,
                currentTransmitOffsetKHz);
        }

        var downlinkErrorHz = rxSign * (echoAudioHz - targetPitchHz);
        var txDeltaHz = mode!.DopplerCorrection == DopplerCorrection.Reverse
            ? downlinkErrorHz
            : -downlinkErrorHz;

        if (Math.Abs(txDeltaHz) < DeadbandHz)
        {
            return new CwUplinkAlignEvaluation(
                CwUplinkAlignDecision.AlreadyAligned,
                echoAudioHz,
                targetPitchHz,
                downlinkErrorHz,
                txDeltaHz,
                currentTransmitOffsetKHz);
        }

        var newTransmitOffsetKHz = currentTransmitOffsetKHz + txDeltaHz / 1000.0;

        if (Math.Abs(txDeltaHz) > maxCorrectionHz)
        {
            return new CwUplinkAlignEvaluation(
                CwUplinkAlignDecision.ExceedsSafetyCap,
                echoAudioHz,
                targetPitchHz,
                downlinkErrorHz,
                txDeltaHz,
                newTransmitOffsetKHz);
        }

        if (newTransmitOffsetKHz < offsetMinKHz
            || newTransmitOffsetKHz > offsetMaxKHz
            || mode.UplinkKHz + newTransmitOffsetKHz <= 0
            || mode.DownlinkKHz <= 0)
        {
            return new CwUplinkAlignEvaluation(
                CwUplinkAlignDecision.OutOfPassbandBounds,
                echoAudioHz,
                targetPitchHz,
                downlinkErrorHz,
                txDeltaHz,
                newTransmitOffsetKHz);
        }

        return new CwUplinkAlignEvaluation(
            CwUplinkAlignDecision.Apply,
            echoAudioHz,
            targetPitchHz,
            downlinkErrorHz,
            txDeltaHz,
            newTransmitOffsetKHz);
    }
}
