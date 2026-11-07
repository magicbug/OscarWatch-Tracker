using OscarWatch.Core.Models;

namespace OscarWatch.Rig;

/// <summary>
/// IC-905 CI-V driver for dual-radio endpoints (one VFO per physical radio).
/// No dedicated satellite mode — dual layout uses VFO A only.
/// </summary>
public sealed class IcomIc905Driver : IcomCivDriverBase
{
    public IcomIc905Driver(string port, int baudRate, string civAddressHex, int catDelayMs = 50)
        : base(RigType.IcomIc905, port, baudRate, civAddressHex, catDelayMs)
    {
    }

    internal IcomIc905Driver(IIcomCivTransport transport)
        : base(RigType.IcomIc905, transport)
    {
    }

    internal IcomIc905Driver(
        Func<IIcomCivTransport> transportFactory, string endpointLabel, string civAddressHex, int catDelayMs = 50)
        : base(RigType.IcomIc905, transportFactory, endpointLabel, civAddressHex, catDelayMs)
    {
    }

    public override bool SupportsTracking => true;

    public override void SetSatelliteMode(bool on)
    {
    }

    protected override bool SupportsDataMode => true;

    protected override RigVfo MapOperationalVfo(RigVfo vfo) =>
        vfo is RigVfo.Main or RigVfo.Sub ? RigVfo.VfoA : vfo;
}
