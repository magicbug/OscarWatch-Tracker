using System.Globalization;
using System.Text;

namespace OscarWatch.Troubleshoot;

internal readonly record struct Gs232Position(int? Azimuth, int? Elevation)
{
    public bool HasAny => Azimuth is not null || Elevation is not null;
}

/// <summary>Command text for the probe, formatted the way the tracker's GS-232 driver sends it.</summary>
internal static class Gs232Commands
{
    public const int NudgeDeg = 5;

    /// <summary>Five degrees clockwise, or anticlockwise when that would pass the azimuth limit.</summary>
    public static int NudgeAzimuth(int current, int maxAzimuthDeg)
    {
        var target = current + NudgeDeg;
        if (target > maxAzimuthDeg)
            target = current - NudgeDeg;
        return Math.Clamp(target, 0, maxAzimuthDeg);
    }

    public static string Move(int azimuth, int elevation, bool azimuthOnly, int maxAzimuthDeg, int maxElevationDeg)
    {
        var az = Math.Clamp(azimuth, 0, maxAzimuthDeg);
        if (azimuthOnly)
            return string.Create(CultureInfo.InvariantCulture, $"M{az:000}");

        var el = Math.Clamp(elevation, 0, maxElevationDeg);
        return string.Create(CultureInfo.InvariantCulture, $"W{az:000} {el:000}");
    }
}

/// <summary>
/// English transcript of a probe session for support: every byte written and read, with timings and how
/// the tracker's parser read each reply.
/// </summary>
internal sealed class Gs232ProbeLog
{
    private readonly object _gate = new();
    private readonly List<string> _lines = [];
    private readonly Func<DateTime> _now;
    private readonly DateTime _started;

    public Gs232ProbeLog(Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.Now);
        _started = _now();
    }

    public event Action<string>? LineAdded;

    public string Text
    {
        get
        {
            lock (_gate)
                return string.Join(Environment.NewLine, _lines);
        }
    }

    public void Info(string text) => Add("INFO", text);

    public void Tx(byte[] bytes) => Add("TX", DescribeBytes(bytes));

    public void Rx(byte[] bytes) => Add("RX", DescribeBytes(bytes));

    /// <summary>Bytes already waiting before a write. The tracker discards these without reading them.</summary>
    public void Stale(byte[] bytes) => Add("STALE", DescribeBytes(bytes));

    public void Parsed(string text) => Add("PARSE", text);

    public void Error(string text) => Add("ERROR", text);

    internal static string FormatLine(DateTime at, TimeSpan sinceStart, string tag, string text) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{at:HH:mm:ss.fff}  +{sinceStart.TotalSeconds,8:0.000}s  {tag,-5}  {text}");

    /// <summary>
    /// Printable ASCII as text, CR and LF as <c>\r</c> and <c>\n</c>, anything else as a dot, with the hex
    /// bytes added when any byte had to be shown as a dot.
    /// </summary>
    internal static string DescribeBytes(byte[] bytes)
    {
        var text = new StringBuilder(bytes.Length + 8);
        var unprintable = false;
        foreach (var b in bytes)
        {
            switch (b)
            {
                case (byte)'\r':
                    text.Append("\\r");
                    break;
                case (byte)'\n':
                    text.Append("\\n");
                    break;
                case >= 0x20 and <= 0x7E:
                    text.Append((char)b);
                    break;
                default:
                    text.Append('.');
                    unprintable = true;
                    break;
            }
        }

        if (unprintable)
            text.Append("  [hex ")
                .AppendJoin(' ', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))
                .Append(']');

        return text.ToString();
    }

    private void Add(string tag, string text)
    {
        var now = _now();
        var line = FormatLine(now, now - _started, tag, text);
        lock (_gate)
            _lines.Add(line);
        LineAdded?.Invoke(line);
    }
}

/// <summary>
/// One open COM port. Queries and commands follow the tracker's GS-232 driver (CR terminator, 150 ms settle,
/// one second for a reply) but log the bytes the driver discards.
/// </summary>
internal sealed class Gs232Session(IGs232Port port, Gs232ProbeLog log, Action<int>? sleep = null)
{
    public const int SettleMs = 150;
    public const int ReplyTimeoutMs = 1000;
    private const int PollMs = 20;
    private const int TrailingBytesMs = 40;

    private readonly Action<int> _sleep = sleep ?? Thread.Sleep;

    /// <summary>
    /// Opens the port, runs <paramref name="action"/>, and closes the port again. Port failures (in use, access
    /// denied, missing) are logged and returned as the error instead of thrown.
    /// </summary>
    public static (T? Value, string? Error) Run<T>(
        Func<IGs232Port> createPort,
        Gs232Target target,
        Gs232ProbeLog log,
        Func<Gs232Session, T> action,
        Action<int>? sleep = null)
    {
        log.Info(string.Create(
            CultureInfo.InvariantCulture,
            $"Opening {target.Port} at {target.BaudRate} baud, {Gs232SerialPort.LineSettings}"));
        try
        {
            using var port = createPort();
            port.Open();
            var value = action(new Gs232Session(port, log, sleep));
            log.Info($"Closed {target.Port}");
            return (value, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or ArgumentException or TimeoutException)
        {
            log.Error($"{ex.GetType().Name}: {ex.Message}");
            return (default, ex.Message);
        }
    }

    /// <summary>
    /// Azimuth only: <c>C</c>. Otherwise <c>C2</c>, then <c>C</c> and <c>B</c> for an axis it did not report.
    /// Every query is sent, even after silence, so write-only interfaces show up as a run of timeouts.
    /// </summary>
    public Gs232Position ReadPosition(bool azimuthOnly)
    {
        if (azimuthOnly)
        {
            var azOnly = Gs232PositionParser.TryParseAzimuthLine(Query("C"), out var azValue) ? azValue : (int?)null;
            log.Parsed(azOnly is null ? "C: no azimuth in the reply" : $"C: azimuth {azOnly}");
            return new Gs232Position(azOnly, null);
        }

        Gs232PositionParser.TryParseParts(Query("C2"), out var az, out var el);
        log.Parsed($"C2: azimuth {Show(az)}, elevation {Show(el)}");

        if (az is null)
        {
            az = Gs232PositionParser.TryParseAzimuthLine(Query("C"), out var cValue) ? cValue : null;
            log.Parsed($"C: azimuth {Show(az)}");
        }

        if (el is null)
        {
            el = Gs232PositionParser.TryParseElevationLine(Query("B"), out var bValue) ? bValue : null;
            log.Parsed($"B: elevation {Show(el)}");
        }

        return new Gs232Position(az, el);
    }

    /// <summary>A command with no expected reply (<c>S</c>, <c>W</c>, <c>M</c>). Anything that comes back is logged.</summary>
    public void Send(string command)
    {
        var reply = Exchange(command);
        if (reply.Length == 0)
            log.Info($"No reply to {command} within {ReplyTimeoutMs} ms. Most controllers send nothing back to a command.");
    }

    public void Wait(int milliseconds) => _sleep(milliseconds);

    private string? Query(string command)
    {
        var reply = Exchange(command);
        if (reply.Length == 0)
        {
            log.Info($"No reply to {command} within {ReplyTimeoutMs} ms");
            return null;
        }

        return Encoding.ASCII.GetString(reply);
    }

    private byte[] Exchange(string command)
    {
        var stale = ReadAvailable();
        if (stale.Length > 0)
            log.Stale(stale);

        var bytes = Encoding.ASCII.GetBytes(command + "\r");
        port.Write(bytes);
        log.Tx(bytes);

        _sleep(SettleMs);
        var reply = ReadReply();
        if (reply.Length > 0)
            log.Rx(reply);
        return reply;
    }

    /// <summary>Reads until a CR or LF arrives or the timeout passes, then picks up a trailing LF.</summary>
    private byte[] ReadReply()
    {
        var reply = new List<byte>();
        for (var waited = 0; waited <= ReplyTimeoutMs; waited += PollMs)
        {
            reply.AddRange(ReadAvailable());
            if (reply.Contains((byte)'\r') || reply.Contains((byte)'\n'))
            {
                _sleep(TrailingBytesMs);
                reply.AddRange(ReadAvailable());
                break;
            }

            _sleep(PollMs);
        }

        return reply.ToArray();
    }

    private byte[] ReadAvailable()
    {
        var count = port.BytesToRead;
        if (count <= 0)
            return [];

        var buffer = new byte[count];
        var read = port.Read(buffer, 0, count);
        return read == count ? buffer : buffer[..read];
    }

    private static string Show(int? degrees) => degrees?.ToString(CultureInfo.InvariantCulture) ?? "none";
}
