namespace OscarWatch.Core.Models;

public sealed class GpsSettings
{
    public const int DefaultBaudRate = 4800;
    public const int DefaultGpsdPort = 2947;
    public const string DefaultGpsdHost = "127.0.0.1";

    public bool Enabled { get; set; }

    public GpsConnectionKind ConnectionKind { get; set; } = GpsConnectionKind.Serial;

    public string Port { get; set; } = "";

    public int BaudRate { get; set; } = DefaultBaudRate;

    public string GpsdHost { get; set; } = DefaultGpsdHost;

    public int GpsdPort { get; set; } = DefaultGpsdPort;

    /// <summary>Continuously update ground station lat/lon (and optionally altitude) from GPS fix.</summary>
    public bool AutoUpdateStation { get; set; }

    /// <summary>When auto-updating station, apply GGA altitude (meters ASL).</summary>
    public bool UseGpsAltitude { get; set; } = true;

    /// <summary>Use GPS UTC for satellite tracking instead of the system clock.</summary>
    public bool UseGpsTimeForTracking { get; set; }

    /// <summary>Correct FT4 slot timing from GPS when the PC clock is out (never changes the system clock).</summary>
    public bool UseGpsTimeForFt4 { get; set; } = true;

    /// <summary>Minimum satellites in use before accepting a fix (GGA).</summary>
    public int MinSatellites { get; set; } = 3;

    public bool UsesSerialPort => Enabled && ConnectionKind == GpsConnectionKind.Serial;

    public bool IsConfigured => Enabled && ConnectionKind switch
    {
        GpsConnectionKind.Gpsd => !string.IsNullOrWhiteSpace(GpsdHost) && GpsdPort is > 0 and <= 65535,
        _ => !string.IsNullOrWhiteSpace(Port)
    };
}
