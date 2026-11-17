using OscarWatch.Core.Models;

namespace OscarWatch.Rig;

/// <summary>
/// IC-7100 CI-V driver for dual-radio endpoints (one VFO per physical radio).
/// No dedicated satellite mode — dual layout uses VFO A only.
/// </summary>
public sealed class IcomIc7100Driver : IcomCivDriverBase
{
    public IcomIc7100Driver(string port, int baudRate, string civAddressHex, int catDelayMs = 50)
        : base(RigType.IcomIc7100, port, baudRate, civAddressHex, catDelayMs)
    {
    }

    internal IcomIc7100Driver(IIcomCivTransport transport)
        : base(RigType.IcomIc7100, transport)
    {
    }

    public override bool SupportsTracking => true;
    public override bool SupportsVfoExchange => false;

    public override void SetSatelliteMode(bool on)
    {
    }

    protected override bool SupportsDataMode => true;

    protected override RigVfo MapOperationalVfo(RigVfo vfo) =>
        vfo is RigVfo.Main or RigVfo.Sub ? RigVfo.VfoA : vfo;

    protected override bool IsFrequencyAllowedHz(long hz) =>
        hz is >= 1_800_000 and <= 54_000_000
            or >= 144_000_000 and <= 148_000_000
            or >= 430_000_000 and <= 450_000_000;
}
