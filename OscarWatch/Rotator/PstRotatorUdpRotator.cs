using OscarWatch.Core.Models;

namespace OscarWatch.Rotator;

/// <summary>
/// PstRotator (YO3DMU) as the rotator: OscarWatch sends az/el over UDP and PstRotator drives
/// whatever controller it is connected to (locally or as a PstRotator server/client pair).
/// Enable UDP control in PstRotator; replies arrive on UDP port + 1.
/// </summary>
public sealed class PstRotatorUdpRotator : IRotatorDriver
{
    private const int ReplyTimeoutMs = 600;

    private readonly IPstRotatorUdpChannel _channel;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PstRotatorUdpRotator(string host, int port)
        : this(new PstRotatorUdpChannel(host, port))
    {
    }

    internal PstRotatorUdpRotator(IPstRotatorUdpChannel channel)
    {
        _channel = channel;
    }

    public void Open() => _channel.Open();

    public void SetPosition(double azimuthDeg, double elevationDeg, RotatorSettings settings)
    {
        var az = Math.Clamp(azimuthDeg, 0, settings.MaxAzimuthDeg);
        var el = Math.Clamp(elevationDeg, 0, settings.MaxElevationDeg);

        _gate.Wait();
        try
        {
            _channel.Send(PstRotatorUdpCodec.BuildAzimuthCommand(az));
            _channel.Send(PstRotatorUdpCodec.BuildElevationCommand(el));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Stop()
    {
        _gate.Wait();
        try
        {
            _channel.Send(PstRotatorUdpCodec.StopCommand);
        }
        finally
        {
            _gate.Release();
        }
    }

    public (int? Azimuth, int? Elevation) GetPosition()
    {
        _gate.Wait();
        try
        {
            _channel.DiscardPending();
            _channel.Send(PstRotatorUdpCodec.AzimuthQuery);
            _channel.Send(PstRotatorUdpCodec.ElevationQuery);

            double? az = null;
            double? el = null;
            var deadline = Environment.TickCount64 + ReplyTimeoutMs;
            while (az is null || el is null)
            {
                var remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0)
                    break;

                var reply = _channel.Receive(remaining);
                if (reply is null)
                    break;

                if (PstRotatorUdpCodec.TryParseReply(reply, out var replyAz, out var replyEl))
                {
                    az ??= replyAz;
                    el ??= replyEl;
                }
            }

            return (Round(az), Round(el));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        try
        {
            _channel.Dispose();
        }
        catch
        {
            // ignore dispose errors
        }

        _gate.Dispose();
    }

    private static int? Round(double? value) =>
        value is { } v ? (int)Math.Round(v) : null;
}
