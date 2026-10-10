using OscarWatch.Core.Models;

namespace OscarWatch.Rotator;

/// <summary>
/// SAEBRTrack rotator driver for fire-and-forget Arduino / BASIC Stamp / PSR-100 style interfaces.
/// Sends compact whole-degree <c>AZnnnELnnn</c> gotos; does not query position
/// (bare <c>AZ</c>/<c>EL</c> would be misread as move-to-0/0 by these controllers).
/// </summary>
public sealed class SaebrtRotator : IRotatorDriver
{
    private const string LineEnding = "\n";

    private readonly IRotatorSerialTransport _port;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SaebrtRotator(string portName, int baudRate)
        : this(new SerialRotatorTransport(portName, baudRate, 200, 200, LineEnding, dtrEnable: false, rtsEnable: false))
    {
    }

    internal SaebrtRotator(IRotatorSerialTransport transport)
    {
        _port = transport;
    }

    public void Open() => _port.Open();

    /// <summary>
    /// SAEBRTrack treats a position query as a goto to 0,0. There is no safe read,
    /// so the link cannot be proved before the first move command.
    /// </summary>
    public bool TryConfirmLink() => true;

    public void SetPosition(double azimuthDeg, double elevationDeg, RotatorSettings settings)
    {
        var az = Math.Clamp(azimuthDeg, 0, settings.MaxAzimuthDeg);
        var el = Math.Clamp(elevationDeg, 0, settings.MaxElevationDeg);
        SendCommand(SaebrtCommandFormatter.FormatSetPosition(az, el));
    }

    public void Stop()
    {
        // SAEBRTrack has no standard stop command; sending SA/SE can confuse dumb controllers.
    }

    public (int? Azimuth, int? Elevation) GetPosition()
    {
        // Fire-and-forget firmware treats every serial read as a goto. Sending EasyComm-style
        // AZ/EL queries parses as az=0/el=0 and drives the rotor back to park.
        return (null, null);
    }

    public void Dispose()
    {
        try
        {
            if (_port.IsOpen)
            {
                // Avoid a long DTR/RTS toggle hang when releasing Arduino-style adapters.
                try
                {
                    _port.DtrEnable = false;
                    _port.RtsEnable = false;
                    _port.DiscardInBuffer();
                    _port.DiscardOutBuffer();
                }
                catch
                {
                    // ignore pre-close cleanup
                }
            }
        }
        catch
        {
            // ignore dispose errors
        }

        _port.Dispose();
        _gate.Dispose();
    }

    private void SendCommand(string command)
    {
        _gate.Wait();
        try
        {
            _port.DiscardInBuffer();
            _port.Write(command + LineEnding);
            // No reply expected; brief yield only so the adapter can accept the next write.
            Thread.Sleep(20);
        }
        finally
        {
            _gate.Release();
        }
    }
}
