using OscarWatch.Core.Models;

namespace OscarWatch.Rig;

/// <summary>
/// IC-7300 CI-V driver for dual-radio endpoints (one VFO per physical radio).
/// No dedicated satellite mode — dual layout uses VFO A only.
/// </summary>
public sealed class IcomIc7300Driver : IcomCivDriverBase
{
    public IcomIc7300Driver(string port, int baudRate, string civAddressHex, int catDelayMs = 50)
        : base(RigType.IcomIc7300, port, baudRate, civAddressHex, catDelayMs)
    {
    }

    internal IcomIc7300Driver(IIcomCivTransport transport)
        : base(RigType.IcomIc7300, transport)
    {
    }

    internal IcomIc7300Driver(
        Func<IIcomCivTransport> transportFactory, string endpointLabel, string civAddressHex, int catDelayMs = 50)
        : base(RigType.IcomIc7300, transportFactory, endpointLabel, civAddressHex, catDelayMs)
    {
    }

    public override bool SupportsTracking => true;

    public override void SetSatelliteMode(bool on)
    {
    }

    protected override bool SupportsDataMode => true;

    protected override RigVfo MapOperationalVfo(RigVfo vfo) =>
        vfo is RigVfo.Main or RigVfo.Sub ? RigVfo.VfoA : vfo;

    protected override bool IsFrequencyAllowedHz(long hz) =>
        hz is >= 1_800_000 and <= 54_000_000;
}
