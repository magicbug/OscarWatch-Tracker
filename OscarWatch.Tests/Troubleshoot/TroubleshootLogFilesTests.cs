using OscarWatch.Troubleshoot;

namespace OscarWatch.Tests.Troubleshoot;

public sealed class TroubleshootLogFilesTests
{
    [Fact]
    public void RecentProblems_keeps_error_and_fatal_entries_with_exception_lines()
    {
        string[] lines =
        [
            "2026-11-05 22:00:00.000 +00:00 [INF] OscarWatch.App Starting",
            "2026-11-05 22:00:01.000 +00:00 [ERR] OscarWatch.Rig CAT failed",
            "System.IO.IOException: Port busy",
            "   at Foo.Bar()",
            "2026-11-05 22:00:02.000 +00:00 [WRN] OscarWatch.Gps No fix",
            "2026-11-05 22:00:03.000 +00:00 [FTL]  Application terminated unexpectedly"
        ];

        var problems = LogFiles.RecentProblems(lines);

        Assert.Equal(2, problems.Count);
        Assert.Contains("CAT failed", problems[0]);
        Assert.Contains("   at Foo.Bar()", problems[0]);
        Assert.DoesNotContain("No fix", problems[0]);
        Assert.Contains("[FTL]", problems[1]);
    }

    [Fact]
    public void RecentProblems_returns_only_the_latest_entries()
    {
        var lines = Enumerable.Range(0, 10)
            .Select(i => $"2026-11-05 22:00:{i:00}.000 +00:00 [ERR] Error {i}")
            .ToList();

        var problems = LogFiles.RecentProblems(lines, maxEntries: 3);

        Assert.Equal(["Error 7", "Error 8", "Error 9"], problems.Select(p => p[^7..]).ToArray());
    }

    [Fact]
    public void ReadLines_reads_a_file_another_writer_still_has_open()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oscarwatch-troubleshoot-log-{Guid.NewGuid():N}.log");
        try
        {
            using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            using (var text = new StreamWriter(writer, leaveOpen: true))
                text.Write("line one\nline two\n");

            Assert.Equal(["line one", "line two"], LogFiles.ReadLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
