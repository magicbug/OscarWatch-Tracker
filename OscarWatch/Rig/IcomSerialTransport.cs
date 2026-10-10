using System.IO.Ports;
using OscarWatch.Core.Radio;
using Serilog;

namespace OscarWatch.Rig;

internal sealed class IcomSerialTransport : IIcomCivTransport
{
    private static readonly ILogger Log = Serilog.Log.ForContext<IcomSerialTransport>();
    private readonly SerialPort _port;
    private readonly int _civAddress;
    private readonly int _defaultPostDelayMs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _writeTimeoutStreak;

    public IcomSerialTransport(string portName, int baudRate, int civAddress, int catDelayMs = 50)
    {
        _civAddress = civAddress;
        _defaultPostDelayMs = Math.Max(catDelayMs, 15);
        _port = new SerialPort(portName, baudRate)
        {
            ReadTimeout = 200,
            WriteTimeout = 1000,
            DtrEnable = false,
            RtsEnable = false
        };
    }

    public bool IsOpen => _port.IsOpen;

    public void Open()
    {
        _writeTimeoutStreak = 0;
        if (!_port.IsOpen)
            _port.Open();
    }

    public byte[] WriteCommand(ReadOnlySpan<byte> body, int postDelayMs = 50)
    {
        if (!_port.IsOpen)
            return [];

        var delayMs = postDelayMs > 0 ? postDelayMs : _defaultPostDelayMs;
        var frame = IcomCivCodec.BuildCommandFrame(_civAddress, body);

        _gate.Wait();
        try
        {
            if (!_port.IsOpen)
                return [];

            _port.DiscardInBuffer();
            _port.Write(frame, 0, frame.Length);
            _writeTimeoutStreak = 0;
            Thread.Sleep(delayMs);
            return ReadResponse(delayMs);
        }
        catch (Exception ex)
        {
            if (ex is TimeoutException or IOException)
                NoteWriteFailure(ex);
            else
                Log.Warning(ex, "CI-V write failed on {PortName}", _port.PortName);
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    private void NoteWriteFailure(Exception ex)
    {
        _writeTimeoutStreak++;
        if (_writeTimeoutStreak < 3)
        {
            Log.Warning(ex, "CI-V write failed on {PortName} ({Streak}/3)", _port.PortName, _writeTimeoutStreak);
            return;
        }

        Log.Warning(
            ex,
            "CI-V write failed on {PortName}. Closing the port so OscarWatch can reconnect. Check the CI-V cable, baud, and that no other app uses this COM port.",
            _port.PortName);
        ClosePort();
    }

    private void ClosePort()
    {
        try
        {
            if (_port.IsOpen)
                _port.Close();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "CI-V port close failed on {PortName}", _port.PortName);
        }
    }

    private byte[] ReadResponse(int postDelayMs)
    {
        var buffer = new List<byte>();
        var deadline = Environment.TickCount64 + Math.Max(postDelayMs, 50) + 800;

        while (Environment.TickCount64 < deadline)
        {
            try
            {
                while (_port.BytesToRead > 0)
                {
                    var b = _port.ReadByte();
                    if (b >= 0)
                        buffer.Add((byte)b);
                }
            }
            catch (TimeoutException)
            {
                // keep polling until deadline
            }

            if (TryExtractLastFrame(buffer, out var frame))
                return frame;

            Thread.Sleep(15);
        }

        return TryExtractLastFrame(buffer, out var partial) ? partial : [];
    }

    internal static bool TryExtractLastFrame(IReadOnlyList<byte> buffer, out byte[] frame)
    {
        frame = [];
        if (buffer.Count == 0)
            return false;

        var lastFd = -1;
        for (var i = buffer.Count - 1; i >= 0; i--)
        {
            if (buffer[i] == 0xFD)
            {
                lastFd = i;
                break;
            }
        }

        if (lastFd < 0)
            return false;

        var start = 0;
        for (var i = lastFd - 1; i >= 1; i--)
        {
            if (buffer[i] == 0xFE && buffer[i - 1] == 0xFE)
            {
                start = i - 1;
                break;
            }
        }

        frame = new byte[lastFd - start + 1];
        for (var i = 0; i < frame.Length; i++)
            frame[i] = buffer[start + i];

        return frame.Length > 0;
    }

    public void SetHandshakeLine(bool useRts, bool assert)
    {
        if (!_port.IsOpen)
            return;

        if (useRts)
            _port.RtsEnable = assert;
        else
            _port.DtrEnable = assert;
    }

    public void Dispose()
    {
        try
        {
            if (_port.IsOpen)
                _port.Close();
        }
        catch
        {
            // ignore
        }

        _port.Dispose();
        _gate.Dispose();
    }
}
