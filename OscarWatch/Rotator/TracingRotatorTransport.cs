using OscarWatch.Diagnostics;

namespace OscarWatch.Rotator;

/// <summary>
/// Passes every call through, and while <see cref="RotatorTrace"/> is recording logs what was sent, received,
/// timed out or thrown away. When not recording it only adds a cheap check.
/// </summary>
internal sealed class TracingRotatorTransport(IRotatorSerialTransport inner, string label) : IRotatorSerialTransport
{
    internal IRotatorSerialTransport Inner => inner;

    public bool IsOpen => inner.IsOpen;

    public bool DtrEnable
    {
        get => inner.DtrEnable;
        set
        {
            inner.DtrEnable = value;
            Trace("LINE", $"DTR {(value ? "on" : "off")}");
        }
    }

    public bool RtsEnable
    {
        get => inner.RtsEnable;
        set
        {
            inner.RtsEnable = value;
            Trace("LINE", $"RTS {(value ? "on" : "off")}");
        }
    }

    public void Open()
    {
        try
        {
            inner.Open();
            Trace("OPEN", "Opened");
        }
        catch (Exception ex)
        {
            Trace("ERROR", $"Open failed: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public void Write(string text)
    {
        try
        {
            inner.Write(text);
        }
        catch (Exception ex)
        {
            Trace("ERROR", $"Write {RotatorTrace.DescribeText(text)} failed: {ex.Message}");
            throw;
        }

        if (RotatorTrace.IsActive)
            Trace("TX", RotatorTrace.DescribeText(text));
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        try
        {
            inner.Write(buffer, offset, count);
        }
        catch (Exception ex)
        {
            Trace("ERROR", $"Write of {count} bytes failed: {ex.Message}");
            throw;
        }

        if (RotatorTrace.IsActive)
            Trace("TX", RotatorTrace.DescribeBytes(buffer.AsSpan(offset, count)));
    }

    /// <summary>While recording, reads what is waiting first so replies the driver ignores still show up.</summary>
    public void DiscardInBuffer()
    {
        if (RotatorTrace.IsActive)
        {
            try
            {
                var stale = inner.ReadExisting();
                if (stale.Length > 0)
                    Trace("STALE", RotatorTrace.DescribeText(stale));
            }
            catch (Exception)
            {
                // the discard below still runs
            }
        }

        inner.DiscardInBuffer();
    }

    public void DiscardOutBuffer() => inner.DiscardOutBuffer();

    public string ReadLine()
    {
        try
        {
            var line = inner.ReadLine();
            if (RotatorTrace.IsActive)
                Trace("RX", RotatorTrace.DescribeText(line) + " (line)");
            return line;
        }
        catch (TimeoutException)
        {
            Trace("TIMEOUT", "No complete reply line before the read timeout");
            throw;
        }
    }

    public string ReadExisting()
    {
        var text = inner.ReadExisting();
        if (text.Length > 0 && RotatorTrace.IsActive)
            Trace("RX", RotatorTrace.DescribeText(text));
        return text;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0 && RotatorTrace.IsActive)
                Trace("RX", RotatorTrace.DescribeBytes(buffer.AsSpan(offset, read)));
            return read;
        }
        catch (TimeoutException)
        {
            Trace("TIMEOUT", $"No bytes before the read timeout (wanted up to {count})");
            throw;
        }
    }

    public void Dispose()
    {
        inner.Dispose();
        Trace("CLOSE", "Closed");
    }

    private void Trace(string tag, string text) => RotatorTrace.Write(tag, $"[{label}] {text}");
}
