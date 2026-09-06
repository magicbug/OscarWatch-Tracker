namespace OscarWatch.Core.Models;

public enum RotatorAzimuthRange
{
    Deg360 = 360,
    Deg450 = 450
}

public enum RotatorElevationRange
{
    Deg90 = 90,
    Deg180 = 180
}

public sealed class RotatorSettings
{
    public const string DefaultNetworkHost = "127.0.0.1";
    public const int DefaultNetworkPort = 1111;

    public bool Enabled { get; set; }
    public RotatorType Type { get; set; } = RotatorType.YaesuGs232;

    /// <summary>
    /// Serial vs raw TCP for GS-232 / EasyComm / SPID / SAEBRTrack.
    /// Ignored when <see cref="Type"/> is <see cref="RotatorType.UrcTcp"/> (always TCP/JSON)
    /// or <see cref="RotatorType.GreenHeronRt21"/> (always dual local serial).
    /// </summary>
    public RotatorTransportKind TransportKind { get; set; } = RotatorTransportKind.Serial;

    public string Port { get; set; } = "";
    public int BaudRate { get; set; } = 4800;

    /// <summary>
    /// Elevation COM port for <see cref="RotatorType.GreenHeronRt21"/> (azimuth uses <see cref="Port"/>).
    /// Ignored for other rotator types.
    /// </summary>
    public string ElevationPort { get; set; } = "";

    /// <summary>TCP host for URC or TCP serial (e.g. ser2net). Ignored when using a local serial port.</summary>
    public string NetworkHost { get; set; } = DefaultNetworkHost;

    /// <summary>TCP port for URC or TCP serial. URC default is 1111.</summary>
    public int NetworkPort { get; set; } = DefaultNetworkPort;

    public RotatorAzimuthRange AzimuthRange { get; set; } = RotatorAzimuthRange.Deg450;
    public RotatorElevationRange ElevationRange { get; set; } = RotatorElevationRange.Deg180;
    /// <summary>Start slewing when satellite elevation reaches this value while approaching.</summary>
    public double TrackStartElevationDeg { get; set; } = -3;
    public double ParkAzimuthDeg { get; set; }
    public double ParkElevationDeg { get; set; }

    /// <summary>Move to the park position when the tracked satellite drops below <see cref="TrackStartElevationDeg"/>.</summary>
    public bool ParkAfterPass { get; set; } = true;

    /// <summary>Added to commanded azimuth for tracking, park, and manual moves.</summary>
    public double AzimuthOffsetDeg { get; set; }

    /// <summary>Added to commanded elevation for tracking, park, and manual moves.</summary>
    public double ElevationOffsetDeg { get; set; }

    /// <summary>Use 361–450° commands for shortest path when <see cref="AzimuthRange"/> is 450°.</summary>
    public bool SmartAzimuth450 { get; set; } = true;

    /// <summary>When true, the keyhole avoidance system analyses high-elevation passes and may pre-position the rotator in a flipped orientation.</summary>
    public bool KeyholeAvoidanceEnabled { get; set; } = false;

    /// <summary>Maximum rotator slew rate in degrees per second, used for keyhole signal-loss computation.</summary>
    private double _slewRateDegPerSec = 3.0;
    public double SlewRateDegPerSec
    {
        get => _slewRateDegPerSec;
        set
        {
            if (value <= 0) return;
            _slewRateDegPerSec = value;
        }
    }

    /// <summary>Minimum max-elevation (degrees) for a pass to be classified as entering the keyhole zone. Valid range: [60, 89].</summary>
    private double _keyholeThresholdDeg = 80.0;
    public double KeyholeThresholdDeg
    {
        get => _keyholeThresholdDeg;
        set
        {
            if (value < 60 || value > 89) return;
            _keyholeThresholdDeg = value;
        }
    }

    /// <summary>
    /// Minimum angular change (degrees) required before a new position command is sent.
    /// Also the arrival window: if polled az/el is still outside this threshold, the last command is sent again.
    /// Valid range: [0.1, 10.0]. Default: 1.0°.
    /// </summary>
    private double _movementThresholdDeg = 1.0;
    public double MovementThresholdDeg
    {
        get => _movementThresholdDeg;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.1 || value > 10.0) return;
            _movementThresholdDeg = value;
        }
    }

    public double MaxAzimuthDeg => (double)AzimuthRange;
    public double MaxElevationDeg => (double)ElevationRange;

    /// <summary>True when this type always uses two local serial ports (azimuth + elevation).</summary>
    public bool UsesDualSerialPorts => Type == RotatorType.GreenHeronRt21;

    /// <summary>True when this configuration uses TCP host/port instead of a serial COM port.</summary>
    public bool UsesNetworkEndpoint =>
        Type == RotatorType.UrcTcp
        || (Type != RotatorType.GreenHeronRt21 && TransportKind == RotatorTransportKind.Tcp);

    /// <summary>
    /// True when a local serial COM port is the active endpoint (enabled device may still be checked separately).
    /// </summary>
    public bool UsesSerialPort =>
        Type == RotatorType.GreenHeronRt21
        || (Type != RotatorType.UrcTcp && TransportKind == RotatorTransportKind.Serial);

    /// <summary>True when the configured connection endpoint is present (serial port or host+port).</summary>
    public bool HasConfiguredEndpoint =>
        UsesDualSerialPorts
            ? !string.IsNullOrWhiteSpace(Port) && !string.IsNullOrWhiteSpace(ElevationPort)
            : UsesNetworkEndpoint
                ? !string.IsNullOrWhiteSpace(NetworkHost) && NetworkPort is > 0 and <= 65535
                : !string.IsNullOrWhiteSpace(Port);

    /// <summary>Local serial COM ports used by this configuration (empty when not using serial).</summary>
    public IReadOnlyList<string> GetConfiguredSerialPorts()
    {
        if (!UsesSerialPort)
            return Array.Empty<string>();

        var ports = new List<string>(UsesDualSerialPorts ? 2 : 1);
        var az = Port?.Trim() ?? "";
        if (az.Length > 0)
            ports.Add(az);

        if (UsesDualSerialPorts)
        {
            var el = ElevationPort?.Trim() ?? "";
            if (el.Length > 0)
                ports.Add(el);
        }

        return ports;
    }
}
