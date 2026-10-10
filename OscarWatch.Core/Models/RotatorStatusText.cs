namespace OscarWatch.Core.Models;

/// <summary>English rotator status text for logs and diagnostics.</summary>
public static class RotatorStatusText
{
    public static string ToEnglish(RotatorPositionStatus status)
    {
        switch (status.ConnectionKind)
        {
            case RotatorConnectionKind.Disabled:
                return "Rotator disabled";
            case RotatorConnectionKind.NoPortSelected:
                return "No COM port selected";
            case RotatorConnectionKind.Connected:
                return "Connected";
            case RotatorConnectionKind.IdentityMismatch:
                return string.IsNullOrWhiteSpace(status.ConnectionDetail)
                    ? "Opened the rotator port, but it did not answer as the selected rotator."
                    : $"Opened {status.ConnectionDetail}, but it did not answer as the selected rotator.";
            case RotatorConnectionKind.ConnectFailed:
                return string.IsNullOrWhiteSpace(status.ConnectionDetail)
                    ? "Rotator not connected"
                    : $"Rotator not connected: {status.ConnectionDetail}";
            case RotatorConnectionKind.Disconnected:
                return "Disconnected";
            default:
                return status.ConnectionDetail ?? status.ConnectionKind.ToString();
        }
    }
}
