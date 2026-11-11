using OscarWatch.Core.Models;
using OscarWatch.Core.Terminal;

namespace OscarWatch.Core.Hardware;

public static class SerialPortConflictHelper
{
    public static bool HasConflict(RotatorSettings rotator, RigSettings rig, GpsSettings? gps = null) =>
        TryDescribeConflict(rotator, rig, gps, out _);

    public static bool TryDescribeConflict(
        RotatorSettings rotator,
        RigSettings rig,
        out string message) =>
        TryDescribeConflict(rotator, rig, gps: null, out message);

    public static bool TryDescribeConflict(
        RotatorSettings rotator,
        RigSettings rig,
        GpsSettings? gps,
        out string message)
    {
        message = "";
        var gpsPort = gps is { Enabled: true, ConnectionKind: GpsConnectionKind.Serial }
            ? gps.Port?.Trim() ?? ""
            : "";

        var rotatorPorts = GetRotatorSerialPorts(rotator);

        if (rotator.Enabled
            && rotator.UsesDualSerialPorts
            && rotatorPorts.Count >= 2
            && string.Equals(rotatorPorts[0], rotatorPorts[1], StringComparison.OrdinalIgnoreCase))
        {
            message =
                $"Rotator azimuth and elevation both use {rotatorPorts[0]}. Use different COM ports for each axis.";
            return true;
        }

        if (rig.Enabled)
        {
            if (rig.DualRadioEnabled)
            {
                var downPort = rig.Downlink.SerialPortInUse;
                var upPort = rig.Uplink.SerialPortInUse;

                if (downPort.Length > 0 && upPort.Length > 0
                    && string.Equals(downPort, upPort, StringComparison.OrdinalIgnoreCase))
                {
                    message =
                        $"Downlink and uplink radios both use {downPort}. Use different COM ports for each radio.";
                    return true;
                }

                if (TryDescribeGpsConflict(gpsPort, downPort, "downlink radio", out message)
                    || TryDescribeGpsConflict(gpsPort, upPort, "uplink radio", out message))
                    return true;

                foreach (var rotatorPort in rotatorPorts)
                {
                    if (downPort.Length > 0
                        && string.Equals(rotatorPort, downPort, StringComparison.OrdinalIgnoreCase))
                    {
                        message =
                            $"Rotator and downlink radio both use {rotatorPort}. Use different COM ports or disable one device.";
                        return true;
                    }

                    if (upPort.Length > 0
                        && string.Equals(rotatorPort, upPort, StringComparison.OrdinalIgnoreCase))
                    {
                        message =
                            $"Rotator and uplink radio both use {rotatorPort}. Use different COM ports or disable one device.";
                        return true;
                    }
                }

                return false;
            }

            var rigPort = rig.SerialPortInUse;
            if (TryDescribeGpsConflict(gpsPort, rigPort, "radio", out message))
                return true;

            if (rotatorPorts.Count == 0)
                return false;

            if (rigPort.Length == 0)
                return false;

            foreach (var rotatorPort in rotatorPorts)
            {
                if (!string.Equals(rotatorPort, rigPort, StringComparison.OrdinalIgnoreCase))
                    continue;

                message =
                    $"Rotator and radio both use {rotatorPort}. Use different COM ports or disable one device.";
                return true;
            }

            return false;
        }

        if (gpsPort.Length == 0 || rotatorPorts.Count == 0)
            return false;

        foreach (var rotatorPort in rotatorPorts)
        {
            if (!string.Equals(gpsPort, rotatorPort, StringComparison.OrdinalIgnoreCase))
                continue;

            message =
                $"GPS and rotator both use {gpsPort}. Use different COM ports or disable one device.";
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when an FT4 separate-PTT COM port would steal the radio, rotator, or GPS serial port.
    /// </summary>
    public static bool TryDescribeFt4SeparatePttConflict(
        string? separatePttPort,
        RigSettings rig,
        RotatorSettings rotator,
        GpsSettings? gps,
        out string message) =>
        TryDescribeSeparatePttConflict("FT4", separatePttPort, rig, rotator, gps, out message);

    /// <summary>
    /// FT4 separate PTT port in <paramref name="settings"/> conflicts with the radio, rotator, or GPS.
    /// The same port may also be Terminal's separate PTT port: only one of those windows holds it open.
    /// </summary>
    public static bool TryDescribeFt4SeparatePttConflict(AppSettings settings, string? separatePttPort, out string message) =>
        TryDescribeSeparatePttConflict(
            "FT4", separatePttPort, settings.Rig, settings.Rotator, settings.Gps, out message);

    /// <summary>
    /// Terminal modem separate PTT port conflicts with the radio, rotator, or GPS.
    /// The same port may also be FT4's separate PTT port: only one of those windows holds it open.
    /// </summary>
    public static bool TryDescribeTerminalSeparatePttConflict(AppSettings settings, string? separatePttPort, out string message) =>
        TryDescribeSeparatePttConflict(
            "Terminal", separatePttPort, settings.Rig, settings.Rotator, settings.Gps, out message);

    /// <summary>
    /// True when the Terminal KISS serial TNC port is also the radio, rotator or GPS port, or the FT4 separate
    /// PTT port. The built-in modem's PTT port is not checked: only one Terminal transport is open at a time.
    /// </summary>
    public static bool TryDescribeKissSerialConflict(AppSettings settings, string? kissPort, out string message)
    {
        message = "";
        var kiss = kissPort?.Trim() ?? "";
        if (kiss.Length == 0)
            return false;

        foreach (var (port, label) in EnumerateOccupiedPorts(settings.Rig, settings.Rotator, settings.Gps))
        {
            if (!string.Equals(kiss, port, StringComparison.OrdinalIgnoreCase))
                continue;

            message = $"Terminal KISS TNC and {label} both use {kiss}. Use different COM ports or disable one device.";
            return true;
        }

        var ft4 = Ft4SeparatePttPort(settings)?.Trim() ?? "";
        if (string.Equals(kiss, ft4, StringComparison.OrdinalIgnoreCase))
        {
            message = $"Terminal KISS TNC and FT4 separate PTT both use {kiss}. Use different COM ports or disable one device.";
            return true;
        }

        return false;
    }

    /// <summary>The COM port FT4 keys on, or null when it does not use one.</summary>
    public static string? Ft4SeparatePttPort(AppSettings settings) =>
        settings.Ft4.PttMethod == PttMethod.SeparateComPort ? settings.Ft4.SeparatePttPort : null;

    /// <summary>
    /// True when <paramref name="owner"/>'s separate-PTT COM port would steal the radio, rotator or GPS port.
    /// FT4 and Terminal may name the same separate PTT port: each releases it when its window closes.
    /// </summary>
    public static bool TryDescribeSeparatePttConflict(
        string owner,
        string? separatePttPort,
        RigSettings rig,
        RotatorSettings rotator,
        GpsSettings? gps,
        out string message)
    {
        message = "";
        var ptt = separatePttPort?.Trim() ?? "";
        if (ptt.Length == 0)
            return false;

        foreach (var (port, label) in EnumerateOccupiedPorts(rig, rotator, gps))
        {
            if (!string.Equals(ptt, port, StringComparison.OrdinalIgnoreCase))
                continue;

            message = $"{owner} separate PTT and {label} both use {ptt}. Use different COM ports or disable one device.";
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when the CW keyer's own COM port is the radio, rotator or GPS port, or a separate PTT
    /// port that FT4 or Terminal key on.
    /// </summary>
    public static bool IsCwKeyPortOccupied(AppSettings settings, string? cwPort)
    {
        var port = cwPort?.Trim() ?? "";
        if (port.Length == 0)
            return false;

        if (EnumerateOccupiedPorts(settings.Rig, settings.Rotator, settings.Gps)
            .Any(p => string.Equals(p.Port, port, StringComparison.OrdinalIgnoreCase)))
            return true;

        var ft4 = Ft4SeparatePttPort(settings)?.Trim() ?? "";
        var terminal = settings.Terminal.BuiltIn.PttMethod == PttMethod.SeparateComPort
            ? settings.Terminal.BuiltIn.SeparatePttPort?.Trim() ?? ""
            : "";
        return string.Equals(port, ft4, StringComparison.OrdinalIgnoreCase)
            || string.Equals(port, terminal, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<(string Port, string Label)> EnumerateOccupiedPorts(
        RigSettings rig,
        RotatorSettings rotator,
        GpsSettings? gps)
    {
        if (rig.Enabled)
        {
            if (rig.DualRadioEnabled)
            {
                var down = rig.Downlink.SerialPortInUse;
                if (down.Length > 0)
                    yield return (down, "downlink radio");

                var up = rig.Uplink.SerialPortInUse;
                if (up.Length > 0)
                    yield return (up, "uplink radio");
            }
            else
            {
                var port = rig.SerialPortInUse;
                if (port.Length > 0)
                    yield return (port, "radio");
            }
        }

        foreach (var rotatorPort in GetRotatorSerialPorts(rotator))
            yield return (rotatorPort, "rotator");

        if (gps is { Enabled: true, ConnectionKind: GpsConnectionKind.Serial })
        {
            var gpsPort = gps.Port?.Trim() ?? "";
            if (gpsPort.Length > 0)
                yield return (gpsPort, "GPS");
        }
    }

    /// <summary>COM ports when the rotator is enabled and using local serial transport.</summary>
    private static IReadOnlyList<string> GetRotatorSerialPorts(RotatorSettings rotator)
    {
        if (!rotator.Enabled || !rotator.UsesSerialPort)
            return Array.Empty<string>();

        return rotator.GetConfiguredSerialPorts();
    }

    private static bool TryDescribeGpsConflict(
        string gpsPort,
        string devicePort,
        string deviceLabel,
        out string message)
    {
        message = "";
        if (gpsPort.Length == 0 || devicePort.Length == 0)
            return false;

        if (!string.Equals(gpsPort, devicePort, StringComparison.OrdinalIgnoreCase))
            return false;

        message =
            $"GPS and {deviceLabel} both use {gpsPort}. Use different COM ports or disable one device.";
        return true;
    }
}
