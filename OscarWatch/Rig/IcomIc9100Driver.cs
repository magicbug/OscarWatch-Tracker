using OscarWatch.Core.Models;

namespace OscarWatch.Rig;

/// <summary>
/// IC-9100 CI-V satellite mode (0x16 0x5A). Same Main/Sub/VFO/tone path as IC-9700 (Hamlib ic9700_set_vfo).
/// Catalogue DATA-* modes select USB-D / LSB-D with the same 0x1A 0x06 sequence as the IC-9700.
/// </summary>
public sealed class IcomIc9100Driver : IcomCivDriverBase
{
    public IcomIc9100Driver(string port, int baudRate, string civAddressHex, int catDelayMs = 50)
        : base(RigType.IcomIc9100, port, baudRate, civAddressHex, catDelayMs)
    {
    }

    internal IcomIc9100Driver(IIcomCivTransport transport)
        : base(RigType.IcomIc9100, transport)
    {
    }

    public override bool SupportsTracking => true;

    protected override byte[]? SatelliteModeQueryCommand => [0x16, 0x5A];

    public override void SetSatelliteMode(bool on)
    {
        InvalidateSelectedVfo();
        WriteWithRetry(on ? [0x16, 0x5A, 0x01] : [0x16, 0x5A, 0x00]);
    }

    protected override bool SupportsDataMode => true;
}
