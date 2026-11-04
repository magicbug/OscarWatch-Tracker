using OscarWatch.Core.Models;

namespace OscarWatch.Rotator;

public sealed class Gs232Rotator : IRotatorDriver
{
    private readonly IRotatorSerialTransport _port;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _azimuthOnly;

    /// <summary>
    /// Null until the first position read. False when the controller never answered, so later
    /// reads skip the serial queries (write-only interfaces such as the Foxdelta ST3). Once true,
    /// every read queries.
    /// </summary>
    private bool? _positionFeedback;

    public Gs232Rotator(string portName, int baudRate)
        : this(new SerialRotatorTransport(portName, baudRate, 1000, 1000, "\r"))
    {
    }

    /// <param name="azimuthOnly">
    /// Controller with no elevation axis: moves with <c>Maaa</c> and reads with <c>C</c> only.
    /// </param>
    internal Gs232Rotator(IRotatorSerialTransport transport, bool azimuthOnly = false)
    {
        _port = transport;
        _azimuthOnly = azimuthOnly;
    }

    public void Open() => _port.Open();

    public void SetPosition(double azimuthDeg, double elevationDeg, RotatorSettings settings)
    {
        var az = (int)Math.Clamp(Math.Round(azimuthDeg), 0, (int)settings.MaxAzimuthDeg);
        if (_azimuthOnly)
        {
            SendCommand($"M{az:000}");
            return;
        }

        var el = (int)Math.Clamp(Math.Round(elevationDeg), 0, (int)settings.MaxElevationDeg);
        SendCommand($"W{az:000} {el:000}");
    }

    public void Stop() => SendCommand("S");

    public (int? Azimuth, int? Elevation) GetPosition()
    {
        _gate.Wait();
        try
        {
            if (_positionFeedback == false)
                return (null, null);

            if (_azimuthOnly)
            {
                int? azimuth = Gs232PositionParser.TryParseAzimuthLine(Query("C"), out var azOnly)
                    ? azOnly
                    : null;
                if (azimuth is not null)
                    _positionFeedback = true;
                else
                    _positionFeedback ??= false;
                return (azimuth, null);
            }

            // C2 then C/B for missing axis. GS-232B: AZ=/EL=; GS-232A: +0aaa+0eee / +0nnn.
            Gs232PositionParser.TryParseParts(Query("C2"), out var az, out var el);

            if (az is null && Gs232PositionParser.TryParseAzimuthLine(Query("C"), out var azValue))
                az = azValue;

            if (el is null && Gs232PositionParser.TryParseElevationLine(Query("B"), out var elValue))
                el = elValue;

            if (az is not null || el is not null)
                _positionFeedback = true;
            else
                _positionFeedback ??= false;
            return (az, el);
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
            if (_port.IsOpen)
            {
                try { Stop(); } catch { /* ignore */ }
            }
        }
        catch
        {
            // ignore dispose errors
        }

        _port.Dispose();
        _gate.Dispose();
    }

    private string? Query(string command)
    {
        _port.DiscardInBuffer();
        _port.Write(command + "\r");
        return ReadLineResponse();
    }

    private void SendCommand(string command)
    {
        _gate.Wait();
        try
        {
            _port.DiscardInBuffer();
            _port.Write(command + "\r");
            Thread.Sleep(150);
            _port.DiscardInBuffer();
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? ReadLineResponse()
    {
        try
        {
            Thread.Sleep(150);
            var line = _port.ReadLine().Trim();
            return string.IsNullOrWhiteSpace(line) ? null : line;
        }
        catch (TimeoutException)
        {
            return ReadExistingLine();
        }
        catch
        {
            return null;
        }
    }

    private string? ReadExistingLine()
    {
        try
        {
            var text = _port.ReadExisting().Trim('\r', '\n', ' ');
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }
}
