namespace OscarWatch.Core.Models;

public sealed record HamsAtActivationHints(
    string? SuggestedMode,
    IReadOnlyList<string> AvailableModes,
    double? UplinkMhz,
    double? DownlinkMhz)
{
    public static HamsAtActivationHints Empty { get; } =
        new(null, HamsAtApiModes.All, null, null);
}
