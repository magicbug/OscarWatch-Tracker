using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.Diagnostics.NETCore.Client;

namespace OscarWatch.Troubleshoot;

internal sealed record FreezeReportResult(int DumpsWritten, IReadOnlyList<string> Problems, long ZipBytes);

/// <summary>
/// A zip for a stuck OscarWatch: a process dump (thread stacks), process figures, the diagnostics report
/// and the latest logs. OscarWatch keeps running; the dump is taken first so it shows the stuck state.
/// </summary>
internal static class FreezeReport
{
    public const int LogFileCount = 2;

    public static string SuggestedFileName(DateTime now) =>
        $"oscarwatch-freeze-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.zip";

    public static FreezeReportResult Create(
        string zipPath,
        IReadOnlyList<int> processIds,
        string logFolder,
        Func<string> buildDiagnostics,
        Action<int, string>? writeDump = null,
        TimeSpan? cpuSample = null)
    {
        writeDump ??= WriteDump;
        var problems = new List<string>();
        var dumps = 0;
        var work = Directory.CreateTempSubdirectory("oscarwatch-freeze-");

        try
        {
            foreach (var id in processIds)
            {
                var dumpPath = Path.Combine(work.FullName, $"oscarwatch-{id}.dmp");
                try
                {
                    writeDump(id, dumpPath);
                    dumps++;
                }
                catch (Exception ex)
                {
                    problems.Add($"Dump of process {id} failed: {ex.Message}");
                }
            }

            File.WriteAllText(
                Path.Combine(work.FullName, "process.txt"),
                DescribeProcesses(processIds, cpuSample ?? TimeSpan.FromSeconds(2), problems));

            foreach (var log in LatestLogs(logFolder))
            {
                try
                {
                    CopyShared(log.FullName, Path.Combine(work.FullName, log.Name));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"Could not copy {log.Name}: {ex.Message}");
                }
            }

            File.WriteAllText(Path.Combine(work.FullName, "diagnostics.txt"), buildDiagnostics());

            if (problems.Count > 0)
                File.WriteAllLines(Path.Combine(work.FullName, "problems.txt"), problems);

            if (File.Exists(zipPath))
                File.Delete(zipPath);
            ZipFile.CreateFromDirectory(work.FullName, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return new FreezeReportResult(dumps, problems, new FileInfo(zipPath).Length);
        }
        finally
        {
            try
            {
                work.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // left in the temp folder
            }
        }
    }

    /// <summary>Normal is a minidump with every thread's stack: enough to see where OscarWatch is stuck.</summary>
    private static void WriteDump(int processId, string path) =>
        new DiagnosticsClient(processId).WriteDump(DumpType.Normal, path, logDumpGeneration: false);

    /// <summary>The latest OscarWatch logs and the latest rotator recording.</summary>
    private static IEnumerable<FileInfo> LatestLogs(string logFolder)
    {
        if (!Directory.Exists(logFolder))
            return [];

        var folder = new DirectoryInfo(logFolder);
        return folder.EnumerateFiles("oscarwatch-*.log")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(LogFileCount)
            .Concat(folder.EnumerateFiles(RotatorTraceRequest.TraceFilePattern)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(1))
            .ToList();
    }

    private static void CopyShared(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = File.Create(destination);
        input.CopyTo(output);
    }

    /// <summary>CPU over a short sample tells a busy loop (high) from a deadlock or wait (near zero).</summary>
    private static string DescribeProcesses(IReadOnlyList<int> processIds, TimeSpan sample, List<string> problems)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Captured (UTC): {DateTime.UtcNow:O}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Logical processors: {Environment.ProcessorCount}");
        if (processIds.Count == 0)
            text.AppendLine("No OscarWatch process was running.");

        foreach (var id in processIds)
        {
            text.AppendLine();
            try
            {
                using var process = Process.GetProcessById(id);
                var cpuBefore = process.TotalProcessorTime;
                var clock = Stopwatch.StartNew();
                Thread.Sleep(sample);
                process.Refresh();
                var cpuPercent = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds
                                 / clock.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;

                text.AppendLine(CultureInfo.InvariantCulture, $"Process {id}");
                text.AppendLine(CultureInfo.InvariantCulture, $"  Started: {TryGet(() => process.StartTime.ToString("O", CultureInfo.InvariantCulture))}");
                text.AppendLine(CultureInfo.InvariantCulture, $"  Responding: {TryGet(() => OperatingSystem.IsWindows() ? process.Responding.ToString() : "not known on this platform")}");
                text.AppendLine(CultureInfo.InvariantCulture, $"  Threads: {process.Threads.Count}");
                text.AppendLine(CultureInfo.InvariantCulture, $"  Working set: {process.WorkingSet64 / (1024 * 1024)} MB");
                text.AppendLine(CultureInfo.InvariantCulture, $"  CPU over {sample.TotalSeconds:0.#} s: {cpuPercent:0.0}% of the whole machine");
                text.AppendLine(CultureInfo.InvariantCulture, $"  Total CPU time: {process.TotalProcessorTime}");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                problems.Add($"Process {id} could not be read: {ex.Message}");
                text.AppendLine(CultureInfo.InvariantCulture, $"Process {id}: could not be read ({ex.Message})");
            }
        }

        return text.ToString();
    }

    private static string TryGet(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return $"unknown ({ex.Message})";
        }
    }
}
