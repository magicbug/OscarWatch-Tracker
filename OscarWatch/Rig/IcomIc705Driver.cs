using OscarWatch.Core.Models;

namespace OscarWatch.Rig;

/// <summary>
/// IC-705 CI-V driver for dual-radio endpoints (one VFO per physical radio).
/// No dedicated satellite mode — dual layout uses VFO A only.
/// </summary>
public sealed class IcomIc705Driver : IcomCivDriverBase
{
    public IcomIc705Driver(string port, int baudRate, string civAddressHex, int catDelayMs = 50)
        : base(RigType.IcomIc705, port, baudRate, civAddressHex, catDelayMs)
    {
    }

    internal IcomIc705Driver(IIcomCivTransport transport)
        : base(RigType.IcomIc705, transport)
    {
    }

    internal IcomIc705Driver(
        Func<IIcomCivTransport> transportFactory, string endpointLabel, string civAddressHex, int catDelayMs = 50)
        : base(RigType.IcomIc705, transportFactory, endpointLabel, civAddressHex, catDelayMs)
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
