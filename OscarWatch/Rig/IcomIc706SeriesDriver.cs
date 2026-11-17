using OscarWatch.Core.Models;

namespace OscarWatch.Rig;

/// <summary>
/// IC-706 / IC-706MKII / IC-706MKIIG CI-V driver for dual-radio endpoints (one VFO per physical radio).
/// No dedicated satellite mode — dual layout uses VFO A only.
/// </summary>
public sealed class IcomIc706SeriesDriver : IcomCivDriverBase
{
    public IcomIc706SeriesDriver(
        RigType rigType,
        string port,
        int baudRate,
        string civAddressHex,
        int catDelayMs = 50)
        : base(rigType, port, baudRate, civAddressHex, catDelayMs)
    {
    }

    internal IcomIc706SeriesDriver(RigType rigType, IIcomCivTransport transport)
        : base(rigType, transport)
    {
    }

    public override bool SupportsTracking => true;
    public override bool SupportsVfoExchange => false;

    public override void SetSatelliteMode(bool on)
    {
    }

    protected override RigVfo MapOperationalVfo(RigVfo vfo) =>
        vfo is RigVfo.Main or RigVfo.Sub ? RigVfo.VfoA : vfo;

    protected override bool IsFrequencyAllowedHz(long hz) => RigType switch
    {
        RigType.IcomIc706 or RigType.IcomIc706Mkii =>
            hz is >= 1_800_000 and <= 54_000_000
                or >= 144_000_000 and <= 148_000_000,
        RigType.IcomIc706MkiiG =>
            hz is >= 1_800_000 and <= 54_000_000
                or >= 144_000_000 and <= 148_000_000
                or >= 430_000_000 and <= 450_000_000,
        _ => base.IsFrequencyAllowedHz(hz)
    };
}
