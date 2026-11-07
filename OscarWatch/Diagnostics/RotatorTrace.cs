using System.Globalization;
using System.Reflection;
using System.Text;

namespace OscarWatch.Diagnostics;

/// <summary>
/// Opt-in rotator wire trace for support. The troubleshooter writes a UTC end time to
/// <see cref="RequestFileName"/> in the OscarWatch data folder. Until then every rotator command, reply,
/// timeout and controller decision goes to <c>logs/rotator-trace-*.log</c>.
/// Keep the names in step with <c>OscarWatch.Troubleshoot.RotatorTraceRequest</c>.
/// </summary>
public static class RotatorTrace
{
    public const string RequestFileName = "rotator-trace-request.txt";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);
    private static readonly object Gate = new();

    private static readonly string DefaultRequestPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OscarWatch",
        RequestFileName);

    private static string _requestPath = DefaultRequestPath;
    private static string _logDirectory = AppLogging.LogDirectory;
    private static Func<DateTime> _utcNow = () => DateTime.UtcNow;
    private static DateTime _nextCheckUtc = DateTime.MinValue;
    private static DateTime _startedUtc;
    private static StreamWriter? _writer;

    /// <summary>Cheap enough to call before building trace text: the request file is read at most every two seconds.</summary>
    public static bool IsActive
    {
        get
        {
            lock (Gate)
            {
                RefreshLocked();
                return _writer is not null;
            }
        }
    }

    /// <summary>The file being written, or null when not recording.</summary>
    public static string? CurrentFile
    {
        get
        {
            lock (Gate)
                return (_writer?.BaseStream as FileStream)?.Name;
        }
    }

    public static void Event(string text) => Write("EVENT", text);

    public static void Write(string tag, string text)
    {
        lock (Gate)
        {
            RefreshLocked();
            if (_writer is null)
                return;

            try
            {
                var now = _utcNow();
                var since = (now - _startedUtc).TotalSeconds;
                _writer.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{now.ToLocalTime():HH:mm:ss.fff}  +{since,8:0.000}s  {tag,-7}  {text}"));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                CloseLocked(null);
            }
        }
    }

    /// <summary>Printable ASCII as is, CR and LF as \r and \n, anything else as a dot with the hex appended.</summary>
    public static string DescribeBytes(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder();
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
                case >= 0x20 and < 0x7F:
                    text.Append((char)b);
                    break;
                default:
                    text.Append('.');
                    unprintable = true;
                    break;
            }
        }

        if (unprintable)
            text.Append("  [hex ").AppendJoin(' ', bytes.ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture))).Append(']');
        return text.ToString();
    }

    public static string DescribeText(string text) => DescribeBytes(Encoding.Latin1.GetBytes(text));

    internal static void ConfigureForTests(string requestPath, string logDirectory, Func<DateTime> utcNow)
    {
        lock (Gate)
        {
            CloseLocked(null);
            _requestPath = requestPath;
            _logDirectory = logDirectory;
            _utcNow = utcNow;
            _nextCheckUtc = DateTime.MinValue;
        }
    }

    internal static void RestoreDefaults() =>
        ConfigureForTests(DefaultRequestPath, AppLogging.LogDirectory, () => DateTime.UtcNow);

    private static void RefreshLocked()
    {
        var now = _utcNow();
        if (now < _nextCheckUtc)
            return;
        _nextCheckUtc = now + CheckInterval;

        var until = ReadUntil();
        if (until is { } end && end > now)
        {
            if (_writer is null)
                OpenLocked(now, end);
        }
        else if (_writer is not null)
        {
            CloseLocked(until is null ? "Recording stopped from the troubleshooter." : "Recording time ended.");
        }
    }

    private static DateTime? ReadUntil()
    {
        try
        {
            if (!File.Exists(_requestPath))
                return null;

            var text = File.ReadAllText(_requestPath).Trim();
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var until)
                ? until
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void OpenLocked(DateTime now, DateTime until)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            var path = Path.Combine(
                _logDirectory,
                $"rotator-trace-{now.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log");
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            _startedUtc = now;

            var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            _writer.WriteLine($"OscarWatch rotator recording, version {version}");
            _writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Started {now:O} (UTC), until {until:O}"));
            _writer.WriteLine("TX is sent to the rotator, RX is received, STALE is unread bytes thrown away before the next command.");
            _writer.WriteLine();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _writer = null;
        }
    }

    private static void CloseLocked(string? reason)
    {
        if (_writer is null)
            return;

        try
        {
            if (reason is not null)
                _writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{_utcNow().ToLocalTime():HH:mm:ss.fff}  {reason}"));
            _writer.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // already gone
        }

        _writer = null;
    }
}
