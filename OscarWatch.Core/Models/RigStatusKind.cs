namespace OscarWatch.Core.Models;

/// <summary>Machine-readable rig connection state for UI localization.</summary>
public enum RigStatusKind
{
    None,
    Disconnected,
    Connected,
    CatPaused,
    Tracking,
    NoComPort,
    SelectDualComPorts,
    NotConnected,
    DualNotConnected,
    SerialPortNotFound,
    SerialPortBusy,
    DualRadioSamePort,
    /// <summary>FlexRadio connected, but full-duplex satellite setup failed.</summary>
    FlexControlFailed,
    /// <summary>TS-2000 cross-band tracking on FA/FB because SA; did not confirm SATL.</summary>
    Ts2000SatlUnconfirmed,
    /// <summary>The COM port opened, but the radio did not answer as the selected model.</summary>
    IdentityMismatch,
}
