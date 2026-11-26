namespace OscarWatch.Core.Radio;

public sealed record SetupVfosResult(int ThresholdHz, bool Interactive);

public static class SetupVfosPolicy
{
    public static SetupVfosResult Evaluate(
        string downlinkMode,
        int dopplerThresholdFmHz,
        int dopplerThresholdLinearHz)
    {
        var mode = TransponderCatModes.Normalize(downlinkMode);
        return mode switch
        {
            "FM" or "FMN" or "DATA-FM" or "DV" => new SetupVfosResult(dopplerThresholdFmHz, Interactive: false),
            "LSB" or "USB" or "CW" => new SetupVfosResult(dopplerThresholdLinearHz, Interactive: true),
            "DATA-LSB" or "DATA-USB" => new SetupVfosResult(0, Interactive: false),
            _ => new SetupVfosResult(dopplerThresholdLinearHz, Interactive: true)
        };
    }

    public static bool IsLinearMode(string downlinkMode)
    {
        var mode = downlinkMode.Trim().ToUpperInvariant();
        return mode is "LSB" or "USB" or "CW";
    }
}
