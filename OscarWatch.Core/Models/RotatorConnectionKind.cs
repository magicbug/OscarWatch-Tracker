namespace OscarWatch.Core.Models;

/// <summary>Machine-readable rotator connection state for diagnostics.</summary>
public enum RotatorConnectionKind
{
    Unknown,
    Disabled,
    NoPortSelected,
    Disconnected,
    Connected,
    ConnectFailed,
    /// <summary>The port opened, but a position read did not answer as this rotator.</summary>
    IdentityMismatch,
}
