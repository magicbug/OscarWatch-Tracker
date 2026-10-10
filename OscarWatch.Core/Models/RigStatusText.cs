namespace OscarWatch.Core.Models;

/// <summary>English rig status text for logs, diagnostics, and debug output.</summary>
public static class RigStatusText
{
    public static string ToEnglish(RigConnectionStatus status)
    {
        switch (status.StatusKind)
        {
            case RigStatusKind.None:
                return status.IsConnected ? "Connected" : "Disconnected";
            case RigStatusKind.Disconnected:
                return "Disconnected";
            case RigStatusKind.Connected:
                return "Connected";
            case RigStatusKind.CatPaused:
                return "CAT paused (manual tuning)";
            case RigStatusKind.Tracking:
                return "Tracking";
            case RigStatusKind.Ts2000SatlUnconfirmed:
                return "Tracking (TS-2000 SATL not confirmed; using FA/FB)";
            case RigStatusKind.NoComPort:
                return "No COM port selected";
            case RigStatusKind.SelectDualComPorts:
                return "Select COM ports for downlink and uplink radios";
            case RigStatusKind.DualNotConnected:
                return FormatConnectionFailure("Dual radio not connected", status.StatusPort, status.StatusDetail);
            case RigStatusKind.SerialPortNotFound:
                return FormatEndpointSerialPortFailure(
                    "Serial port not found",
                    status.StatusPort,
                    status.StatusDetail,
                    "Check the USB cable and refresh the port list.");
            case RigStatusKind.SerialPortBusy:
                return FormatEndpointSerialPortFailure(
                    "Serial port in use",
                    status.StatusPort,
                    status.StatusDetail,
                    "Close other CAT programs or choose a different port.");
            case RigStatusKind.DualRadioSamePort:
                return string.IsNullOrWhiteSpace(status.StatusPort)
                    ? "Downlink and uplink radios use the same serial port. Use different ports for each radio."
                    : $"Downlink and uplink radios both use {status.StatusPort}. Use different COM ports for each radio.";
            case RigStatusKind.FlexControlFailed:
                return string.IsNullOrWhiteSpace(status.StatusDetail)
                    ? "FlexRadio satellite setup failed"
                    : $"FlexRadio satellite setup failed: {status.StatusDetail}";
            case RigStatusKind.IdentityMismatch:
                return string.IsNullOrWhiteSpace(status.StatusPort)
                    ? $"Opened the radio port, but it did not answer as {status.StatusDetail}."
                    : $"Opened {status.StatusPort}, but it did not answer as {status.StatusDetail}.";
            case RigStatusKind.NotConnected:
                var baseMessage = string.IsNullOrWhiteSpace(status.StatusPort)
                    ? "Rig not connected"
                    : $"Rig not connected ({status.StatusPort})";
                return string.IsNullOrWhiteSpace(status.StatusDetail)
                    ? baseMessage
                    : $"{baseMessage}: {status.StatusDetail}";
            default:
                return status.StatusDetail ?? status.StatusKind.ToString();
        }
    }

    private static string FormatConnectionFailure(string baseMessage, string? port, string? detail)
    {
        if (!string.IsNullOrWhiteSpace(port) && string.IsNullOrWhiteSpace(detail))
            return $"{baseMessage} ({port})";

        return string.IsNullOrWhiteSpace(detail)
            ? baseMessage
            : $"{baseMessage}: {detail}";
    }

    private static string FormatEndpointSerialPortFailure(
        string baseMessage,
        string? port,
        string? endpointLabel,
        string guidance)
    {
        if (!string.IsNullOrWhiteSpace(endpointLabel) && !string.IsNullOrWhiteSpace(port))
            return $"{endpointLabel} {baseMessage.ToLowerInvariant()} ({port}). {guidance}";

        if (!string.IsNullOrWhiteSpace(port))
            return $"{baseMessage} ({port}). {guidance}";

        return $"{baseMessage}. {guidance}";
    }
}
