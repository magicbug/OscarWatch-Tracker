using OscarWatch.Core.Models;

namespace OscarWatch.Rig;

/// <summary>
/// IC-821H CI-V. Satellite mode uses Main=RX / Sub=TX, but band-access bytes D0/D1 are
/// inverted vs IC-910/9700 while SAT is on. No split CAT; uplink CTCSS is front-panel only.
/// </summary>
public sealed class IcomIc821hDriver : IcomCivDriverBase
{
    private bool _satelliteModeActive;

    public IcomIc821hDriver(string port, int baudRate, string civAddressHex, int catDelayMs = 50)
        : base(RigType.IcomIc821h, port, baudRate, civAddressHex, catDelayMs)
    {
    }

    internal IcomIc821hDriver(IIcomCivTransport transport)
        : base(RigType.IcomIc821h, transport)
    {
    }

    public override bool SupportsTracking => true;

    public override void SetSatelliteMode(bool on)
    {
        // The IC-821H CI-V command table ends at 0x0F (no 1A 07 satellite-mode toggle; hardware NAKs it).
        // Track the expected layout so 07 D0 and 07 D1 match satellite vs normal Main/Sub operation.
        if (_satelliteModeActive != on)
            InvalidateSelectedVfo();
        _satelliteModeActive = on;
    }

    public override void SetSplitOn(bool on)
    {
        // Manual: sub band cannot perform split/duplex/offset in satellite layout.
    }

    public override void SetToneOn(bool on)
    {
    }

    public override void SetToneSquelchOn(bool on)
    {
    }

    public override void SetToneHz(double hz, bool squelchTone)
    {
    }

    /// <summary>
    /// After SAT on, select Main (RX/downlink) so software VFO state matches the radio.
    /// </summary>
    public void EstablishSatelliteVfoState()
    {
        SelectVfo(RigVfo.Main, force: true);
    }

    /// <summary>
    /// In satellite mode, CI-V Main band access (D0) selects TX/uplink; Sub band access (D1) selects RX/downlink.
    /// </summary>
    protected override RigVfo MapOperationalVfo(RigVfo vfo) =>
        !_satelliteModeActive
            ? vfo
            : vfo switch
            {
                RigVfo.Main => RigVfo.Sub,
                RigVfo.Sub => RigVfo.Main,
                _ => vfo
            };

    protected override bool IsFrequencyAllowedHz(long hz) =>
        hz is >= 144_000_000 and <= 148_000_000
            or >= 430_000_000 and <= 450_000_000;
}
