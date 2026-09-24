using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Gps;

/// <summary>Routes GPS updates to serial NMEA or network gpsd backends.</summary>
public sealed class GpsService : IGpsService, IDisposable
{
    private readonly GpsController _serial = new();
    private readonly GpsdController _gpsd = new();
    private IGpsService? _active;

    public void Update(GpsSettings settings)
    {
        var next = settings.ConnectionKind == GpsConnectionKind.Gpsd
            ? (IGpsService)_gpsd
            : _serial;

        if (!ReferenceEquals(_active, next))
        {
            _serial.Disconnect();
            _gpsd.Disconnect();
            _active = next;
        }

        _active.Update(settings);
    }

    public void Disconnect()
    {
        _serial.Disconnect();
        _gpsd.Disconnect();
    }

    public void DisconnectAndWait()
    {
        _serial.DisconnectAndWait();
        _gpsd.DisconnectAndWait();
    }

    public GpsConnectionStatus GetStatus() =>
        (_active ?? _serial).GetStatus();

    public DateTime? GetTrackingUtc() =>
        (_active ?? _serial).GetTrackingUtc();

    public TimeSpan? GetFt4ClockOffset() =>
        (_active ?? _serial).GetFt4ClockOffset();

    public void Dispose()
    {
        _serial.Dispose();
        _gpsd.Dispose();
    }
}
