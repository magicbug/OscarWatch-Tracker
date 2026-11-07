using System.Text.RegularExpressions;

namespace OscarWatch.Troubleshoot;

/// <summary>Reads the tracker's Serilog files (<c>logs/oscarwatch-*.log</c>).</summary>
internal static partial class LogFiles
{
    private const int MaxLinesPerEntry = 25;

    public static FileInfo? FindLatest(string logFolder)
    {
        try
        {
            if (!Directory.Exists(logFolder))
                return null;

            return Directory.EnumerateFiles(logFolder, "oscarwatch-*.log")
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Opens with shared access so a running or hung OscarWatch still holding the file does not block the read.</summary>
    public static IReadOnlyList<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines;
    }

    public static IReadOnlyList<string> Tail(IReadOnlyList<string> lines, int count) =>
        lines.Skip(Math.Max(0, lines.Count - count)).ToList();

    public static int CountProblems(IReadOnlyList<string> lines) => lines.Count(line =>
        EntryStart().IsMatch(line)
        && (line.Contains(" [ERR] ", StringComparison.Ordinal) || line.Contains(" [FTL] ", StringComparison.Ordinal)));

    /// <summary>The last <paramref name="maxEntries"/> ERR and FTL entries, each with its exception lines.</summary>
    public static IReadOnlyList<string> RecentProblems(IReadOnlyList<string> lines, int maxEntries = 5)
    {
        var entries = new List<string>();
        List<string>? current = null;

        foreach (var line in lines)
        {
            if (EntryStart().IsMatch(line))
            {
                Flush();
                if (line.Contains(" [ERR] ", StringComparison.Ordinal) || line.Contains(" [FTL] ", StringComparison.Ordinal))
                    current = [line];
                continue;
            }

            if (current is not null && current.Count < MaxLinesPerEntry)
                current.Add(line);
        }

        Flush();
        return entries.Skip(Math.Max(0, entries.Count - maxEntries)).ToList();

        void Flush()
        {
            if (current is null)
                return;
            entries.Add(string.Join(Environment.NewLine, current).TrimEnd());
            current = null;
        }
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}")]
    private static partial Regex EntryStart();
}
