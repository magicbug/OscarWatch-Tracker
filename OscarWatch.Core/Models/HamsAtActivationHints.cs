namespace OscarWatch.Core.Models;

public sealed record HamsAtActivationHints(
    string? UplinkMode,
    string? DownlinkMode,
    double? UplinkMhz,
    double? DownlinkMhz,
    string? UplinkMhzDirection,
    string? DownlinkMhzDirection)
{
    public static HamsAtActivationHints Empty { get; } = new(null, null, null, null, null, null);
}
