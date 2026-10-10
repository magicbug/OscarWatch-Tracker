using OscarWatch.Core.Models;

namespace OscarWatch.Rig;

public interface IRigDriver : IDisposable
{
    bool IsConnected { get; }
    RigType RigType { get; }

    /// <summary>
    /// One read-only frequency check that this open link answered as a radio.
    /// False means close the port and do not send control commands.
    /// Drivers that already prove the link in <see cref="Open"/> keep the default.
    /// </summary>
    bool TryConfirmIdentity() => true;

    void Open();
    /// <summary>Read frequency for a specific VFO (selects that VFO first on Icom).</summary>
    long? ReadFrequencyHz(RigVfo vfo);
    bool SetFrequencyHz(long hz);
    void SelectVfo(RigVfo vfo, bool force = false);
    void SetMode(string mode);
    void SetSplitOn(bool on);
    void SetSatelliteMode(bool on);
    void ExchangeVfos();
    void SetToneOn(bool on);
    void SetToneSquelchOn(bool on);
    void SetToneHz(double hz, bool squelchTone);
    bool SupportsTracking { get; }
    /// <summary>
    /// True when rig-specific satellite mode is currently active.
    /// Drivers that do not expose/require explicit satellite-mode state return true.
    /// </summary>
    bool IsSatelliteModeActive => true;
    /// <summary>False when the radio cannot swap VFOs remotely (e.g. FT-847).</summary>
    bool SupportsVfoExchange => true;

    /// <summary>True when <see cref="SetPtt"/> sends a CAT transmit command.</summary>
    bool SupportsCatPtt => false;

    /// <summary>Key or unkey via CAT when <see cref="SupportsCatPtt"/> is true.</summary>
    void SetPtt(bool transmit)
    {
    }

    /// <summary>
    /// True when CAT can report set RF power via <see cref="TryReadRfPowerWatts"/> and/or
    /// <see cref="TryReadRfPowerLevel"/>.
    /// </summary>
    bool SupportsRfPowerRead => false;

    /// <summary>
    /// Read the set RF power in watts when the radio reports watts directly (e.g. Yaesu <c>PC;</c>).
    /// Returns false when unsupported or the radio did not answer.
    /// </summary>
    bool TryReadRfPowerWatts(out double watts)
    {
        watts = 0;
        return false;
    }

    /// <summary>
    /// Read the set RF power as a 0–255 relative level (ICOM CI-V 0x14 0x0A). Returns false when
    /// unsupported or the radio did not answer. Prefer <see cref="TryReadRfPowerWatts"/> when available.
    /// </summary>
    bool TryReadRfPowerLevel(out int level0To255)
    {
        level0To255 = 0;
        return false;
    }

    /// <summary>
    /// True when CAT can set RF power (ICOM CI-V 0x14 0x0A, or Yaesu/Kenwood <c>PCnnn;</c>).
    /// </summary>
    bool SupportsRfPowerWrite => false;

    /// <summary>
    /// Set RF power in watts when the radio accepts a watt value (Yaesu and Kenwood <c>PC</c>).
    /// Returns false when unsupported, out of range, or the radio did not accept the command.
    /// </summary>
    bool TrySetRfPowerWatts(double watts) => false;

    /// <summary>
    /// Set RF power as a 0–255 relative level (ICOM CI-V 0x14 0x0A). Returns false when
    /// unsupported or the radio did not acknowledge.
    /// </summary>
    bool TrySetRfPowerLevel(int level0To255) => false;

    /// <summary>
    /// Toggle RTS or DTR on the CAT serial port. Returns false when the driver cannot
    /// expose handshake lines (e.g. network Flex, or RTS already used for flow control).
    /// </summary>
    bool TrySetHandshakeLine(bool useRts, bool assert) => false;
}
