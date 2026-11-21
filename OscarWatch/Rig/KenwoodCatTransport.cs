using System.IO.Ports;
using System.Text;
using OscarWatch.Core.Radio;
using Serilog;

namespace OscarWatch.Rig;

/// <summary>
/// Kenwood ASCII CAT: semicolon-terminated commands over serial.
/// Default is 8N1 (TS-2000). Pass <see cref="StopBits.Two"/> for the TS-790 (4800 8N2).
/// Hardware RTS is enabled by default (required for replies on full CAT cables); can be disabled for simple cables.
/// </summary>
internal sealed class KenwoodCatTransport : IKenwoodCatTransport
{
    private static readonly ILogger Log = Serilog.Log.ForContext<KenwoodCatTransport>();

    private readonly SerialPort _port;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _rxBuffer = new();
    private string? _lastRejectedSetCommand;
    private int _rejectedSetRepeatCount;

    public KenwoodCatTransport(
        string portName,
        int baudRate,
        bool hardwareRtsEnabled = true,
        StopBits stopBits = StopBits.One)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, stopBits)
        {
            Handshake = hardwareRtsEnabled ? Handshake.RequestToSend : Handshake.None,
            ReadTimeout = 200,
            WriteTimeout = 1000,
            DtrEnable = false,
            RtsEnable = hardwareRtsEnabled,
            NewLine = ";"
        };
    }

    public bool IsOpen => _port.IsOpen;

    public void Open()
    {
        if (!_port.IsOpen)
            _port.Open();
    }

    public bool SendCommand(string command, int postDelayMs = 50) =>
        SendFireAndForget(command, postDelayMs);

    public bool SendFireAndForget(string command, int postDelayMs = 50)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var cmd = NormalizeCommand(command);
        _gate.Wait();
        try
        {
            if (!_port.IsOpen)
                return false;

            // TS-2000 usually echoes nothing on successful sets; ?; / E; means rejection.
            DrainInputBuffer();
            _port.Write(cmd);
            Thread.Sleep(Math.Max(postDelayMs, 0));

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
            Log.Warning(ex, "Kenwood CAT send failed on {Port} for {Cmd}", _port.PortName, command);
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
        var readTimeoutMs = KenwoodCatCodec.GetReplyTimeoutMs(cmd, postDelayMs);

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

                var reply = ReadUntilSemicolon(readTimeoutMs);
                if (reply is null)
                    return null;

                if (IsSyntaxError(reply))
                {
                    if (attempt == 0)
                    {
                        Log.Debug("Kenwood CAT {Cmd} rejected ({Reply}), retrying once", cmd, reply);
                        continue;
                    }

                    Log.Warning(
                        "Kenwood CAT command failed: {Cmd} → {Reply}. Close any radio menu and confirm SATL if tracking.",
                        cmd,
                        reply);
                    return null;
                }

                if (IsProcessingIncomplete(reply))
                {
                    Thread.Sleep(postDelayMs);
                    continue;
                }

                return reply;
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Kenwood CAT transaction failed on {Port} for {Cmd}", _port.PortName, command);
            return null;
        }
        finally
        {
            _gate.Release();
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
        reply.Contains("?;", StringComparison.Ordinal) || reply.Contains("E;", StringComparison.Ordinal);

    private static bool IsProcessingIncomplete(string reply) =>
        reply.Contains("O;", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Log the first rejection of a command, then every 100th repeat — SM can reject every Doppler tick.
    /// </summary>
    private void LogSetRejection(string cmd, string reply)
    {
        if (string.Equals(cmd, _lastRejectedSetCommand, StringComparison.Ordinal))
        {
            _rejectedSetRepeatCount++;
            if (_rejectedSetRepeatCount % 100 != 0)
                return;

            Log.Warning(
                "Kenwood CAT set rejected on {Port}: {Cmd} → {Reply} (repeated ×{Count})",
                _port.PortName,
                cmd,
                reply,
                _rejectedSetRepeatCount);
            return;
        }

        _lastRejectedSetCommand = cmd;
        _rejectedSetRepeatCount = 1;
        Log.Warning(
            "Kenwood CAT set rejected on {Port}: {Cmd} → {Reply}",
            _port.PortName,
            cmd,
            reply);
    }

    public void Dispose()
    {
        var portName = _port.PortName;
        try
        {
            if (_port.IsOpen)
            {
                // Drop RTS/DTR before Close so Windows releases the USB serial adapter for SatPC32/etc.
                try
                {
                    _port.Handshake = Handshake.None;
                    _port.RtsEnable = false;
                    _port.DtrEnable = false;
                    _port.DiscardInBuffer();
                    _port.DiscardOutBuffer();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Kenwood CAT pre-close cleanup failed on {Port}", portName);
                }

                _port.Close();
                Log.Information(
                    "Kenwood CAT closed {Port}; IsOpen={IsOpen}",
                    portName,
                    _port.IsOpen);
            }
            else
            {
                Log.Information("Kenwood CAT dispose on {Port}; port was already closed", portName);
            }
        }
        catch (Exception ex)
        {
            var stillOpen = true;
            try
            {
                stillOpen = _port.IsOpen;
            }
            catch
            {
                // Port may already be in a bad state; report as still open for diagnostics.
            }

            Log.Warning(ex, "Kenwood CAT port close failed on {Port}; IsOpen={IsOpen}", portName, stillOpen);
        }

        try
        {
            _port.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Kenwood CAT port dispose failed on {Port}", portName);
        }

        _gate.Dispose();
    }
}
