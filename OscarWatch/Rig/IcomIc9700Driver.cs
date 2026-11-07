using OscarWatch.Core.Models;

namespace OscarWatch.Rig;

/// <summary>
/// IC-9700 CI-V satellite mode (0x16 0x5A). Shares freq/VFO/tone path with IC-910.
/// </summary>
public sealed class IcomIc9700Driver : IcomCivDriverBase
{
    public IcomIc9700Driver(string port, int baudRate, string civAddressHex, int catDelayMs = 50)
        : base(RigType.IcomIc9700, port, baudRate, civAddressHex, catDelayMs)
    {
    }

    internal IcomIc9700Driver(IIcomCivTransport transport)
        : base(RigType.IcomIc9700, transport)
    {
    }

    internal IcomIc9700Driver(
        Func<IIcomCivTransport> transportFactory, string endpointLabel, string civAddressHex, int catDelayMs = 50)
        : base(RigType.IcomIc9700, transportFactory, endpointLabel, civAddressHex, catDelayMs)
    {
    }

    public override bool SupportsTracking => true;

    public override void SetSatelliteMode(bool on) =>
        WriteWithRetry(on ? [0x16, 0x5A, 0x01] : [0x16, 0x5A, 0x00]);

    protected override bool SupportsDataMode => true;
}
