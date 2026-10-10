using OscarWatch.Core.Models;
using Serilog;

namespace OscarWatch.Rotator;

/// <summary>
/// OZ9AAR Ultimate Rotator Controller over TCP/JSON (POLL / GOTO).
/// URC must be in remote (REM) mode for GOTO to move the antennas.
/// </summary>
public sealed class UrcTcpRotator : IRotatorDriver
{
    private static readonly ILogger Log = Serilog.Log.ForContext<UrcTcpRotator>();

    private readonly UrcTcpClient _client;
    private double? _lastAzimuthDeg;
    private double? _lastElevationDeg;

    public UrcTcpRotator(string host, int port)
    {
        _client = new UrcTcpClient(host, port);
    }

    public void Open() => _client.Open();

    public bool TryConfirmLink()
    {
        var (az, el) = GetPosition();
        return az is not null || el is not null;
    }

    public void SetPosition(double azimuthDeg, double elevationDeg, RotatorSettings settings)
    {
        var az = Math.Clamp(azimuthDeg, 0, settings.MaxAzimuthDeg);
        var el = Math.Clamp(elevationDeg, 0, settings.MaxElevationDeg);
        var reply = _client.Transact(UrcJsonCodec.BuildGotoRequest(az, el));
        CachePositionFromReply(reply);
        _lastAzimuthDeg = az;
        _lastElevationDeg = el;
    }

    public void Stop()
    {
        // Protocol has no STOP; GOTO current position cancels an in-progress slew.
        double az;
        double el;
        if (_lastAzimuthDeg is { } cachedAz && _lastElevationDeg is { } cachedEl)
        {
            az = cachedAz;
            el = cachedEl;
        }
        else
        {
            var poll = _client.Transact(UrcJsonCodec.PollRequest);
            if (!UrcJsonCodec.TryParsePosition(poll, out az, out el))
            {
                Log.Warning("URC Stop: could not read current position from POLL");
                return;
            }
        }

        var reply = _client.Transact(UrcJsonCodec.BuildGotoRequest(az, el));
        CachePositionFromReply(reply);
    }

    public (int? Azimuth, int? Elevation) GetPosition()
    {
        var reply = _client.Transact(UrcJsonCodec.PollRequest);
        if (!UrcJsonCodec.TryParsePosition(reply, out var az, out var el))
            return (null, null);

        _lastAzimuthDeg = az;
        _lastElevationDeg = el;
        return ((int)Math.Round(az), (int)Math.Round(el));
    }

    public void Dispose()
    {
        try
        {
            _client.Dispose();
        }
        catch
        {
            // ignore dispose errors
        }
    }

    private void CachePositionFromReply(string reply)
    {
        if (UrcJsonCodec.TryParsePosition(reply, out var az, out var el))
        {
            _lastAzimuthDeg = az;
            _lastElevationDeg = el;
        }
    }
}
