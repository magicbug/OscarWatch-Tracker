using OscarWatch.Core.Models;
using OscarWatch.Rotator;

namespace OscarWatch.Tests;

internal sealed class RecordingRotatorDriver : IRotatorDriver
{
    public List<double> AzimuthHistory { get; } = [];

    public double? LastAzimuthDeg { get; private set; }
    public double? LastElevationDeg { get; private set; }
    public int SetPositionCallCount { get; private set; }
    public int GetPositionCallCount { get; private set; }
    public int StopCallCount { get; private set; }
    public int OpenCallCount { get; private set; }
    public int DisposeCallCount { get; private set; }

    /// <summary>
    /// When true, <see cref="GetPosition"/> returns <see cref="ForcedAzimuth"/> / <see cref="ForcedElevation"/>
    /// instead of the last commanded position (for arrival-retry tests).
    /// </summary>
    public bool ForceReportedPosition { get; set; }

    public int? ForcedAzimuth { get; set; }
    public int? ForcedElevation { get; set; }

    public void Open() => OpenCallCount++;

    public void SetPosition(double azimuthDeg, double elevationDeg, RotatorSettings settings)
    {
        SetPositionCallCount++;
        LastAzimuthDeg = azimuthDeg;
        LastElevationDeg = elevationDeg;
        AzimuthHistory.Add(azimuthDeg);
    }

    public void Stop() => StopCallCount++;

    public (int? Azimuth, int? Elevation) GetPosition()
    {
        GetPositionCallCount++;
        if (ForceReportedPosition)
            return (ForcedAzimuth, ForcedElevation);

        return LastAzimuthDeg is { } az && LastElevationDeg is { } el
            ? ((int?)Math.Round(az), (int?)Math.Round(el))
            : (null, null);
    }

    public void Dispose() => DisposeCallCount++;
}
