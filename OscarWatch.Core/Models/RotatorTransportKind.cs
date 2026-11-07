namespace OscarWatch.Core.Models;

/// <summary>How serial-protocol rotators (GS-232, EasyComm, SPID, SAEBRTrack) reach the controller.</summary>
public enum RotatorTransportKind
{
    /// <summary>Local COM / serial device.</summary>
    Serial = 0,

    /// <summary>
    /// Raw TCP serial tunnel (e.g. ser2net).
    /// Ignored for <see cref="RotatorType.UrcTcp"/>, <see cref="RotatorType.SpidMd01"/>
    /// and <see cref="RotatorType.PstRotatorUdp"/>.
    /// </summary>
    Tcp = 1
}
