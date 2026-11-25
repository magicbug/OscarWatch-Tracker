using OscarWatch.Core.Models;
using OscarWatch.Diagnostics;

namespace OscarWatch.Rotator;

public sealed class Gs232Rotator : IRotatorDriver
{
    private readonly IRotatorSerialTransport _port;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _azimuthOnly;
    private readonly Func<long> _nowMs;
    private readonly Action<int> _sleep;

    /// <summary>
    /// Null until the first position read. False when the controller never answered, so later
    /// reads skip the serial queries (write-only interfaces such as the Foxdelta ST3). Once true,
    /// every read queries.
    /// </summary>
    private bool? _positionFeedback;

    /// <summary>
    /// Null until the first elevation <c>B</c> query. False when that query timed out, so later
    /// reads skip <c>B</c> (C2 already returns elevation on most interfaces). Once true, every
    /// read that still needs elevation asks again.
    /// </summary>
    private bool? _elevationBFeedback;

    /// <summary>UTC milliseconds of the last completed exchange, or null before the first write.</summary>
    private long? _lastExchangeUtcMs;

    private int _commandGapMs = 200;

    public Gs232Rotator(string portName, int baudRate)
        : this(new SerialRotatorTransport(portName, baudRate, 1000, 1000, "\r"))
    {
    }

    /// <param name="azimuthOnly">
    /// Controller with no elevation axis: moves with <c>Maaa</c> and reads with <c>C</c> only.
    /// </param>
    internal Gs232Rotator(
        IRotatorSerialTransport transport,
        bool azimuthOnly = false,
        int commandGapMs = 200,
        Func<long>? nowMs = null,
        Action<int>? sleep = null)
    {
        _port = transport;
        _azimuthOnly = azimuthOnly;
        CommandGapMs = commandGapMs;
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _sleep = sleep ?? Thread.Sleep;
    }

    /// <summary>
    /// Quiet time after a reply before the next write. Updated from settings each tracking tick
    /// so a change applies without reopening the port.
    /// </summary>
    public int CommandGapMs
    {
        get => _commandGapMs;
        set => _commandGapMs = Math.Clamp(value, 0, 1000);
    }

    public void Open() => _port.Open();

    public void SetPosition(double azimuthDeg, double elevationDeg, RotatorSettings settings)
    {
        CommandGapMs = settings.CommandGapMs;
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

            if (el is null && _elevationBFeedback != false)
            {
                var bReply = Query("B");
                if (Gs232PositionParser.TryParseElevationLine(bReply, out var elValue))
                {
                    el = elValue;
                    _elevationBFeedback = true;
                }
                else
                    _elevationBFeedback ??= false;
            }

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
        WaitForCommandGap(command);
        _port.DiscardInBuffer();
        _port.Write(command + "\r");
        var reply = ReadLineResponse();
        MarkExchangeComplete();
        return reply;
    }

    private void SendCommand(string command)
    {
        _gate.Wait();
        try
        {
            WaitForCommandGap(command);
            _port.DiscardInBuffer();
            _port.Write(command + "\r");
            _sleep(150);
            _port.DiscardInBuffer();
            MarkExchangeComplete();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void WaitForCommandGap(string command)
    {
        if (_commandGapMs <= 0 || _lastExchangeUtcMs is null)
            return;

        var elapsed = _nowMs() - _lastExchangeUtcMs.Value;
        var remaining = (int)(_commandGapMs - elapsed);
        if (remaining <= 0)
            return;

        if (RotatorTrace.IsActive)
            RotatorTrace.Event($"Waiting {remaining} ms before {command}");
        _sleep(remaining);
    }

    private void MarkExchangeComplete() => _lastExchangeUtcMs = _nowMs();

    private string? ReadLineResponse()
    {
        try
        {
            _sleep(150);
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
