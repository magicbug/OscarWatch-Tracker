using OscarWatch.Core.Models;

namespace OscarWatch.Core.Radio;

/// <summary>
/// Minimum Main-dial movement (Hz) before linear passband-trim capture treats a change as operator tuning.
/// Ignores small CAT/display jitter; FM modes use a wider window (passband trim is not used on FM).
/// </summary>
public static class KnobTuneCapturePolicy
{
    public const int LinearThresholdHz = 30;
    public const int FmThresholdHz = 250;

    /// <summary>
    /// Flex slice frequencies arrive as pushed SmartSDR status, so an external change can be accepted
    /// immediately. Polled CAT radios still require a stable sample history to reject stale reads.
    /// </summary>
    public static bool UsesImmediateStatusCapture(RigType rigType) =>
        rigType == RigType.FlexSmartSdr;

    public static int Resolve(string? downlinkMode)
    {
        if (string.IsNullOrWhiteSpace(downlinkMode))
            return LinearThresholdHz;

        var mode = TransponderCatModes.Normalize(downlinkMode);
        return mode switch
        {
            "FM" or "FMN" or "DATA-FM" or "DV" => FmThresholdHz,
            _ => LinearThresholdHz
        };
    }
}
