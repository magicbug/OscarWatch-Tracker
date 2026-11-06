using System.Text.Json.Serialization;

namespace OscarWatch.Core.Models;

/// <summary>One radio endpoint (downlink or uplink) in dual-radio mode.</summary>
public sealed class RigEndpointSettings
{
    public const int SdrRigCtlDefaultPort = 4532;
    public const int SdrConnectRigCtlPort = 5454;
    public const string SdrRigCtlDefaultHost = "127.0.0.1";

    public RigType Type { get; set; } = RigType.None;

    public string Port { get; set; } = "";

    public int BaudRate { get; set; } = RigSettings.Ft817818DefaultBaudRate;

    public RigRegion Region { get; set; } = RigRegion.EU;

    public int CatDelayMs { get; set; } = 50;

    /// <summary>CI-V address as hex string (IC-705 default A4).</summary>
    public string CivAddress { get; set; } = "";

    /// <summary>
    /// COM or LAN for radios that support both (Kenwood TS-990S / TS-890S).
    /// Ignored for other types.
    /// </summary>
    public RigConnectionKind Connection { get; set; } = RigConnectionKind.Serial;

    /// <summary>
    /// TCP host for <see cref="RigType.SdrRigCtlTcp"/>, or the radio IP address for Kenwood LAN.
    /// </summary>
    public string NetworkHost { get; set; } = SdrRigCtlDefaultHost;

    /// <summary>TCP port for rigctl (default 4532) or Kenwood LAN (60000).</summary>
    public int NetworkPort { get; set; } = SdrRigCtlDefaultPort;

    /// <summary>Kenwood LAN administrator or user account name from the radio's LAN menu.</summary>
    public string NetworkUser { get; set; } = "";

    /// <summary>Kenwood LAN password from the radio's LAN menu.</summary>
    public string NetworkPassword { get; set; } = "";

    /// <summary>
    /// TS-890S LAN login account type: administrator ID when true, user ID when false.
    /// The TS-990S login has no account type.
    /// </summary>
    public bool NetworkUserIsAdministrator { get; set; } = true;

    /// <summary>
    /// When true, Kenwood HF COM CAT asserts hardware RTS. Turn off for cables without RTS/CTS.
    /// Ignored for other types.
    /// </summary>
    public bool KenwoodHardwareRtsEnabled { get; set; } = true;

    /// <summary>True when this endpoint talks to the radio over TCP rather than a COM port.</summary>
    [JsonIgnore]
    public bool UsesNetwork =>
        RigSettings.IsSdrDownlinkEndpoint(Type) || UsesKenwoodLan;

    /// <summary>The COM port this endpoint holds open, or empty for network and dummy endpoints.</summary>
    [JsonIgnore]
    public string SerialPortInUse =>
        UsesNetwork || Type == RigType.Dummy ? "" : Port?.Trim() ?? "";

    /// <summary>True for a TS-990S / TS-890S leg set to LAN.</summary>
    [JsonIgnore]
    public bool UsesKenwoodLan =>
        RigSettings.IsKenwoodLanCapable(Type) && Connection == RigConnectionKind.Lan;

    public bool IsConfigured
    {
        get
        {
            if (Type == RigType.Dummy)
                return true;

            if (RigSettings.IsSdrDownlinkEndpoint(Type))
                return HasNetworkHostAndPort;

            if (UsesKenwoodLan)
            {
                return HasNetworkHostAndPort
                    && !string.IsNullOrWhiteSpace(NetworkUser)
                    && !string.IsNullOrEmpty(NetworkPassword);
            }

            return RigSettings.IsDualCapableSerialEndpoint(Type) && !string.IsNullOrWhiteSpace(Port);
        }
    }

    private bool HasNetworkHostAndPort =>
        NetworkPort is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(NetworkHost);
}
