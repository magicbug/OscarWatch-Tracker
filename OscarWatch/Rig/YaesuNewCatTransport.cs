using System.IO.Ports;
using System.Text;
using Serilog;

namespace OscarWatch.Rig;

/// <summary>FT-991 / FT-991A ASCII CAT: 8N2, hardware RTS, semicolon-terminated commands.</summary>
internal sealed class YaesuNewCatTransport : IYaesuNewCatTransport
{
    private static readonly ILogger Log = Serilog.Log.ForContext<YaesuNewCatTransport>();
    private readonly SerialPort _port;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _rxBuffer = new();
    private string? _lastRejectedSetCommand;
    private int _rejectedSetRepeatCount;

    public YaesuNewCatTransport(string portName, int baudRate)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.Two)
        {
            Handshake = Handshake.RequestToSend,
            ReadTimeout = 200,
            WriteTimeout = 1000,
            DtrEnable = false,
            RtsEnable = true,
            NewLine = ";"
        };
    }

    public bool IsOpen => _port.IsOpen;

    public void Open()
    {
        if (!_port.IsOpen)
            _port.Open();
    }

    /// <summary>
    /// Fire-and-forget set: Yaesu newcat sets normally return no reply.
    /// Waiting for <c>;</c> (as in <see cref="Transact"/>) saturates the rig worker.
    /// </summary>
    public bool SendCommand(string command, int postDelayMs = 50)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var cmd = NormalizeCommand(command);
        _gate.Wait();
        try
        {
            if (!_port.IsOpen)
                return false;

            DrainInputBuffer();
            _port.Write(cmd);
            Thread.Sleep(Math.Max(postDelayMs, 0));

            // Optional rejection peek: successful sets usually leave the buffer empty.
            if (_port.BytesToRead > 0)
            {
                _rxBuffer.Clear();
                var peek = ReadUntilSemicolon(Math.Min(120, Math.Max(postDelayMs, 40)));
                if (peek is not null && IsSyntaxError(peek))
                {
                    LogSetRejection(cmd, peek);
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Yaesu newcat send failed on {Port} for {Cmd}", _port.PortName, command);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? Transact(string command, int postDelayMs = 50)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var cmd = NormalizeCommand(command);

        _gate.Wait();
        try
        {
            if (!_port.IsOpen)
                return null;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                DrainInputBuffer();
                _rxBuffer.Clear();
                _port.Write(cmd);

                var reply = ReadUntilSemicolon(Math.Max(postDelayMs, 50) + 800);
                if (reply is null)
                    return null;

                if (IsSyntaxError(reply))
                {
                    if (attempt == 0)
                    {
                        Log.Debug("Yaesu newcat {Cmd} rejected ({Reply}), retrying once", cmd, reply);
                        continue;
                    }

                    Log.Warning("Yaesu newcat command failed: {Cmd} → {Reply}", cmd, reply);
                    return null;
                }

                return reply;
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Yaesu newcat transaction failed on {Port} for {Cmd}", _port.PortName, command);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Toggles DTR without the command gate so a CW element is not held up by a CAT read.
    /// </summary>
    public bool TrySetDtr(bool assert)
    {
        try
        {
            if (!_port.IsOpen)
                return false;
            _port.DtrEnable = assert;
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Yaesu newcat DTR change failed on {Port}", _port.PortName);
            return false;
        }
    }

    private void DrainInputBuffer()
    {
        try
        {
            _port.DiscardInBuffer();
            _rxBuffer.Clear();
        }
        catch
        {
            // ignore
        }
    }

    private string? ReadUntilSemicolon(int readTimeoutMs)
    {
        var deadline = Environment.TickCount64 + readTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                while (_port.BytesToRead > 0)
                {
                    var chunk = _port.ReadExisting();
                    if (string.IsNullOrEmpty(chunk))
                        break;
                    _rxBuffer.Append(chunk);
                }
            }
            catch (TimeoutException)
            {
                // keep polling until deadline
            }

            var text = _rxBuffer.ToString();
            var semi = text.IndexOf(';');
            if (semi >= 0)
                return text[..(semi + 1)].Trim();

            Thread.Sleep(15);
        }

        var partial = _rxBuffer.ToString().Trim();
        return partial.Length > 0 ? partial : null;
    }

    private static string NormalizeCommand(string command)
    {
        var cmd = command.TrimEnd();
        return cmd.EndsWith(';') ? cmd : cmd + ";";
    }

    private static bool IsSyntaxError(string reply) =>
        reply.Contains("?;", StringComparison.Ordinal);

    private void LogSetRejection(string cmd, string reply)
    {
        if (string.Equals(cmd, _lastRejectedSetCommand, StringComparison.Ordinal))
        {
            _rejectedSetRepeatCount++;
            if (_rejectedSetRepeatCount % 100 != 0)
                return;

            Log.Warning(
                "Yaesu newcat set rejected on {Port}: {Cmd} → {Reply} (repeated ×{Count})",
                _port.PortName,
                cmd,
                reply,
                _rejectedSetRepeatCount);
            return;
        }

        _lastRejectedSetCommand = cmd;
        _rejectedSetRepeatCount = 1;
        Log.Warning(
            "Yaesu newcat set rejected on {Port}: {Cmd} → {Reply}",
            _port.PortName,
            cmd,
            reply);
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
