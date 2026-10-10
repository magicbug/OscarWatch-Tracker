using OscarWatch.Core.Models;

namespace OscarWatch.Rotator;

public interface IRotatorDriver : IDisposable
{
    void Open();
    void SetPosition(double azimuthDeg, double elevationDeg, RotatorSettings settings);
    void Stop();
    (int? Azimuth, int? Elevation) GetPosition();

    /// <summary>
    /// One read-only check that this open link answered as this rotator.
    /// False means close the port and do not send a goto.
    /// </summary>
    bool TryConfirmLink();
}
