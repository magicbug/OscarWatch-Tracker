namespace OscarWatch.Core.Models;

public enum RotatorType
{
    YaesuGs232,
    EasyComm,
    Spid,
    Saebrt,
    /// <summary>OZ9AAR Ultimate Rotator Controller over TCP/JSON (POLL / GOTO).</summary>
    UrcTcp,
    /// <summary>
    /// SPID MD-01 / MD-02 over Ethernet (Rot2Prog binary on TCP, default port 23).
    /// Unlike serial Rot2Prog, set-position returns a 12-byte status frame.
    /// </summary>
    SpidMd01,
    /// <summary>
    /// Green Heron RT-21 Az-El: two independent DCU-1 serial links (azimuth and elevation COM ports).
    /// </summary>
    GreenHeronRt21,
    /// <summary>
    /// MuseLab AntRunner: GRBL 1.1 on ESP32 over serial (115200). X azimuth, Y elevation, 360° = 40 mm.
    /// </summary>
    AntRunner
}
