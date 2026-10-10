namespace OscarWatch.Core.Models;

public sealed class RigSettings
{
    /// <summary>Factory / Hamlib-style default CAT rate for FT-817 and FT-818 (menu #14).</summary>
    public const int Ft817818DefaultBaudRate = 4800;

    /// <summary>Default CAT rate for FT-857 / FT-857D (menu 020 CAT RATE; 9600 and 38400 also supported).</summary>
    public const int Ft857DefaultBaudRate = 4800;

    /// <summary>Factory CI-V USB default baud for IC-705 (must match radio menu).</summary>
    public const int Ic705DefaultBaudRate = 115200;

    /// <summary>Factory CI-V USB default baud for IC-7300 (must match radio menu).</summary>
    public const int Ic7300DefaultBaudRate = 115200;

    /// <summary>Factory CI-V USB default baud for IC-905 (must match radio Set mode).</summary>
    public const int Ic905DefaultBaudRate = 115200;

    /// <summary>Typical CI-V baud for IC-7100 (must match radio menu; USB max 19200).</summary>
    public const int Ic7100DefaultBaudRate = 19200;

    /// <summary>Typical menu 031 CAT RATE for FT-991 / FT-991A (4800–38400 supported).</summary>
    public const int Ft991DefaultBaudRate = 38400;

    /// <summary>Typical menu CAT-1 RATE for FTX-1 series (4800–115200 supported).</summary>
    public const int Ftx1DefaultBaudRate = 38400;
    /// <summary>Kenwood TH-D74/TH-D75 PC command port default (8N1, CR-terminated ASCII).</summary>
    public const int KenwoodThD7xDefaultBaudRate = 9600;

    public static bool IsKenwoodThD7xEndpoint(RigType type) =>
        type is RigType.KenwoodThD74 or RigType.KenwoodThD75;

    public static bool IsYaesuNewCatDualEndpoint(RigType type) =>
        type is RigType.YaesuFt991 or RigType.YaesuFt991a or RigType.YaesuFtx1;

    /// <summary>Typical CI-V baud for IC-706 series (must match radio menu).</summary>
    public const int Ic706SeriesDefaultBaudRate = 19200;

    /// <summary>Default lead time (ms) when lead Doppler is enabled (0 = automatic half CAT delay).</summary>
    public const int DefaultDopplerCatLeadMs = 40;

    /// <summary>Default lead strength (%); many operators use 70–85 on fast birds.</summary>
    public const int DefaultDopplerCatLeadGainPercent = 70;

    public static bool IsIc706SeriesEndpoint(RigType type) =>
        type is RigType.IcomIc706 or RigType.IcomIc706Mkii or RigType.IcomIc706MkiiG;

    public bool Enabled { get; set; }

    /// <summary>When true, downlink and uplink use separate radios (<see cref="Downlink"/> / <see cref="Uplink"/>).</summary>
    public bool DualRadioEnabled { get; set; }

    public RigEndpointSettings Downlink { get; set; } = new();

    public RigEndpointSettings Uplink { get; set; } = new();

    public const int FlexSmartSdrDefaultPort = 4992;

    public RigType Type { get; set; } = RigType.None;

    /// <summary>
    /// Power class when <see cref="Type"/> is <see cref="RigType.IcomIc910"/>.
    /// Absent settings stay on <see cref="Ic910PowerClass.H"/>.
    /// </summary>
    public Ic910PowerClass Ic910PowerClass { get; set; } = Ic910PowerClass.H;

    /// <summary>
    /// Power class when <see cref="Type"/> is <see cref="RigType.IcomIc9700"/>.
    /// Absent settings stay on <see cref="Ic9700PowerClass.Export"/>.
    /// </summary>
    public Ic9700PowerClass Ic9700PowerClass { get; set; } = Ic9700PowerClass.Export;

    public string Port { get; set; } = "";

    public int BaudRate { get; set; } = 19200;

    /// <summary>
    /// When true, Kenwood TS-2000 CAT asserts hardware RTS (required for replies on full cables).
    /// Ignored for non-Kenwood rig types. Turn off for CAT cables that do not pass RTS/CTS.
    /// </summary>
    public bool KenwoodHardwareRtsEnabled { get; set; } = true;

    /// <summary>
    /// When true, SATL SA commands enable TRACE/TRACE REV on the radio.
    /// Ignored for non-Kenwood rig types. Turn off to leave Doppler entirely to OscarWatch.
    /// </summary>
    public bool KenwoodTraceEnabled { get; set; } = true;

    /// <summary>TCP host when <see cref="Type"/> is <see cref="RigType.FlexSmartSdr"/>.</summary>
    public string NetworkHost { get; set; } = "";

    /// <summary>TCP port when <see cref="Type"/> is <see cref="RigType.FlexSmartSdr"/> (default 4992).</summary>
    public int NetworkPort { get; set; } = FlexSmartSdrDefaultPort;

    /// <summary>Optional Flex radio serial from discovery, used to re-select the same radio.</summary>
    public string FlexRadioSerial { get; set; } = "";

    /// <summary>SmartSDR RX antenna for VHF downlink/uplink (ANT1, ANT2, RX_A, RX_B, XVTR; empty = leave unchanged).</summary>
    public string FlexVhfRxAnt { get; set; } = "";

    /// <summary>SmartSDR RX antenna for UHF and above (empty = leave unchanged).</summary>
    public string FlexUhfRxAnt { get; set; } = "";

    /// <summary>SmartSDR TX antenna for VHF uplink (empty = leave unchanged).</summary>
    public string FlexVhfTxAnt { get; set; } = "";

    /// <summary>SmartSDR TX antenna for UHF and above (empty = leave unchanged).</summary>
    public string FlexUhfTxAnt { get; set; } = "";

    /// <summary>CI-V address as hex string (factory default for most ICOM rigs is 60).</summary>
    public string CivAddress { get; set; } = "60";

    public RigRegion Region { get; set; } = RigRegion.EU;

    public int DopplerThresholdFmHz { get; set; } = 350;

    public int DopplerThresholdLinearHz { get; set; } = 50;

    /// <summary>
    /// On linear USB/LSB/CW, milliseconds the receive dial must stay still before Doppler CAT resumes.
    /// 0 means use the default 800 ms.
    /// </summary>
    public int InteractiveDialSettleMs { get; set; }

    /// <summary>
    /// On single-radio Main/Sub rigs, milliseconds after an operator dial move before uplink (Sub) CAT.
    /// 0 means use the default 2500 ms. Dual radio ignores this.
    /// </summary>
    public int InteractiveUplinkResumeMs { get; set; }

    public int CatDelayMs { get; set; } = 50;

    /// <summary>When true, CAT Doppler uses range rate at utc + half Receive/Transmit CatDelayMs on steep legs only.</summary>
    public bool DopplerCatLeadEnabled { get; set; } = true;

    /// <summary>
    /// When true, linear Doppler threshold is lowered while downlink slew is fast (TCA vicinity).
    /// Base threshold from <see cref="DopplerThresholdLinearHz"/> is never exceeded.
    /// </summary>
    public bool DopplerAdaptiveThresholdEnabled { get; set; } = true;

    /// <summary>When true, write a CSV pass log for Doppler tuning (CAT writes, lead, adaptive threshold).</summary>
    public bool DopplerPassLogEnabled { get; set; }

    /// <summary>
    /// Lead time in ms (0 = automatic: half CAT delay, capped internally).
    /// Does not change CAT pacing delay.
    /// </summary>
    public int DopplerCatLeadMs { get; set; } = DefaultDopplerCatLeadMs;

    /// <summary>Scales computed lead strength (0–100). 100 = full blend; 0 = snapshot rate only.</summary>
    public int DopplerCatLeadGainPercent { get; set; } = DefaultDopplerCatLeadGainPercent;

    /// <summary>When true, automatic CAT frequency updates are suspended (SatPC32-style).</summary>
    public bool CatUpdatesPaused { get; set; }

    /// <summary>
    /// When the frequency panel CW style is active: keep receive in USB/LSB from the database
    /// instead of setting downlink to CW.
    /// </summary>
    public bool CwKeepSidebandDownlink { get; set; }

    public bool IsDualRadio => DualRadioEnabled;

    public bool IsDualRadioConfigured =>
        DualRadioEnabled && Downlink.IsConfigured && Uplink.IsConfigured;

    public static bool IsDualCapableEndpoint(RigType type) =>
        IsDualCapableSerialEndpoint(type) || IsSdrDownlinkEndpoint(type);

    public static bool IsDualCapableSerialEndpoint(RigType type) =>
        type is RigType.YaesuFt817 or RigType.YaesuFt818 or RigType.YaesuFt857 or RigType.YaesuFtx1
            or RigType.YaesuFt991 or RigType.YaesuFt991a
            or RigType.IcomIc705 or RigType.IcomIc7300 or RigType.IcomIc905
            or RigType.IcomIc7100
            or RigType.IcomIc706 or RigType.IcomIc706Mkii or RigType.IcomIc706MkiiG
            or RigType.KenwoodThD74 or RigType.KenwoodThD75;

    public static bool IsSdrDownlinkEndpoint(RigType type) => type == RigType.SdrRigCtlTcp;

    public static bool IsFlexNetworkRadio(RigType type) => type == RigType.FlexSmartSdr;

    public static bool IsDummyUplinkEndpoint(RigType type) => type == RigType.Dummy;

    public bool IsFlexNetworkConfigured =>
        IsFlexNetworkRadio(Type)
        && NetworkPort is > 0 and <= 65535
        && !string.IsNullOrWhiteSpace(NetworkHost);

    /// <summary>FT-817/818 are dual-radio only; move legacy single-radio config to the downlink endpoint.</summary>
    public void MigrateFt817818ToDualOnly()
    {
        if (DualRadioEnabled || Type is not (RigType.YaesuFt817 or RigType.YaesuFt818))
            return;

        DualRadioEnabled = true;
        Downlink.Type = Type;
        Downlink.Port = Port;
        Downlink.BaudRate = BaudRate > 0 ? BaudRate : Ft817818DefaultBaudRate;
        Downlink.Region = Region;
        Downlink.CatDelayMs = CatDelayMs;
        Type = RigType.None;
        Port = "";
    }

    /// <summary>Region for RX pass-init tone clear (dual downlink) or single-radio.</summary>
    public RigRegion ReceiveRegion() =>
        DualRadioEnabled ? Downlink.Region : Region;

    /// <summary>Region for uplink CTCSS (dual uplink) or single-radio.</summary>
    public RigRegion TransmitRegion() =>
        DualRadioEnabled ? Uplink.Region : Region;

    /// <summary>CAT delay for downlink / RX writes.</summary>
    public int ReceiveCatDelayMs() =>
        DualRadioEnabled ? Downlink.CatDelayMs : CatDelayMs;

    /// <summary>CAT delay for uplink / TX writes.</summary>
    public int TransmitCatDelayMs() =>
        DualRadioEnabled ? Uplink.CatDelayMs : CatDelayMs;

    /// <summary>Factory CI-V address defaults (9700=A2, 9100/910=7C). User may still use 60.</summary>
    public static string DefaultCivAddressFor(RigType type) => type switch
    {
        RigType.IcomIc9700 => "A2",
        RigType.IcomIc9100 => "7C",
        RigType.IcomIc910 => "7C",
        RigType.IcomIc821h => "4C",
        RigType.IcomIc705 => "A4",
        RigType.IcomIc7300 => "94",
        RigType.IcomIc905 => "AC",
        RigType.IcomIc7100 => "88",
        RigType.IcomIc706 => "48",
        RigType.IcomIc706Mkii => "4C",
        RigType.IcomIc706MkiiG => "58",
        _ => "60"
    };
}
