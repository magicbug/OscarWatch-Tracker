using System.Globalization;

namespace OscarWatch.Troubleshoot;

/// <summary>
/// Asks a running OscarWatch (1.1.7 or later) to record rotator traffic. OscarWatch records while the request
/// file holds a UTC time in the future, and writes <c>logs/rotator-trace-*.log</c>.
/// Keep the names in step with <c>OscarWatch.Diagnostics.RotatorTrace</c>.
/// </summary>
internal static class RotatorTraceRequest
{
    public const string RequestFileName = "rotator-trace-request.txt";
    public const string TraceFilePattern = "rotator-trace-*.log";

    public static readonly TimeSpan RecordingLength = TimeSpan.FromMinutes(30);

    public static string RequestPath(string dataFolder) => Path.Combine(dataFolder, RequestFileName);

    /// <summary>The end of the current recording, or null when none is asked for or it has ended.</summary>
    public static DateTime? ReadUntilUtc(string dataFolder, DateTime nowUtc)
    {
        try
        {
            var path = RequestPath(dataFolder);
            if (!File.Exists(path))
                return null;

            var text = File.ReadAllText(path).Trim();
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var until))
                return null;
            return until > nowUtc ? until : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Start(string dataFolder, DateTime untilUtc)
    {
        Directory.CreateDirectory(dataFolder);
        File.WriteAllText(RequestPath(dataFolder), untilUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    public static void Stop(string dataFolder)
    {
        var path = RequestPath(dataFolder);
        if (File.Exists(path))
            File.Delete(path);
    }

    public static FileInfo? FindLatest(string logFolder)
    {
        try
        {
            if (!Directory.Exists(logFolder))
                return null;

            return new DirectoryInfo(logFolder).EnumerateFiles(TraceFilePattern)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
