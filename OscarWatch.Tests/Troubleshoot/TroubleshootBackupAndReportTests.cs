using System.IO.Compression;
using OscarWatch.Troubleshoot;

namespace OscarWatch.Tests.Troubleshoot;

public sealed class TroubleshootBackupAndReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"oscarwatch-backup-{Guid.NewGuid():N}");

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public TroubleshootBackupAndReportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // temp folder
        }
    }

    [Fact]
    public void ListBackups_finds_every_kind_newest_first_and_skips_unreadable_files()
    {
        Write(SettingsPath, "CURRENT", new DateTime(2026, 11, 7, 12, 0, 0));
        Write(SettingsPath + ".bak", "MM9SQL", new DateTime(2026, 11, 6, 12, 0, 0));
        Write(SettingsPath + ".bak-20261101-090000", "", new DateTime(2026, 11, 1, 9, 0, 0));
        Write(Path.Combine(_dir, "settings-aside-20261105-080000", "settings.json"), "ASIDE", new DateTime(2026, 11, 5, 8, 0, 0));
        File.WriteAllText(SettingsPath + ".bak-20261102-090000", "{ not json");

        var backups = SettingsFile.ListBackups(SettingsPath);

        Assert.Equal(
            new[] { "settings.json.bak", Path.Combine("settings-aside-20261105-080000", "settings.json"), "settings.json.bak-20261101-090000" },
            backups.Select(b => b.Name));
        Assert.Equal(new[] { "MM9SQL", "ASIDE", "" }, backups.Select(b => b.Callsign));
    }

    [Fact]
    public void RestoreBackup_backs_up_the_current_file_then_copies_the_backup()
    {
        Write(SettingsPath, "CURRENT", DateTime.Now);
        var backup = SettingsPath + ".bak";
        Write(backup, "MM9SQL", DateTime.Now);

        Assert.Equal(RestoreBackupResult.Restored, SettingsFile.RestoreBackup(SettingsPath, backup));

        Assert.Equal(File.ReadAllText(backup), File.ReadAllText(SettingsPath));
        var kept = Assert.Single(Directory.GetFiles(_dir, "settings.json.bak-*"));
        Assert.Contains("CURRENT", File.ReadAllText(kept), StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreBackup_refuses_an_unreadable_backup()
    {
        Write(SettingsPath, "CURRENT", DateTime.Now);
        var before = File.ReadAllText(SettingsPath);
        var backup = SettingsPath + ".bak";
        File.WriteAllText(backup, "{ not json");

        Assert.Equal(RestoreBackupResult.Unreadable, SettingsFile.RestoreBackup(SettingsPath, backup));
        Assert.Equal(before, File.ReadAllText(SettingsPath));
        Assert.Equal(RestoreBackupResult.Missing, SettingsFile.RestoreBackup(SettingsPath, backup + "-gone"));
    }

    [Fact]
    public void FreezeReport_zips_dumps_process_info_logs_and_diagnostics()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_dir, "logs")).FullName;
        for (var day = 1; day <= 3; day++)
        {
            var log = Path.Combine(logs, $"oscarwatch-2026110{day}.log");
            File.WriteAllText(log, $"day {day}");
            File.SetLastWriteTime(log, new DateTime(2026, 11, day));
        }

        File.WriteAllText(Path.Combine(logs, "rotator-trace-20261103-101500.log"), "TX W120 045");
        var zip = Path.Combine(_dir, "freeze.zip");
        var pid = Environment.ProcessId;

        var result = FreezeReport.Create(
            zip,
            [pid],
            logs,
            () => "DIAGNOSTICS",
            (id, path) => File.WriteAllText(path, $"dump {id}"),
            TimeSpan.FromMilliseconds(50));

        Assert.Equal(1, result.DumpsWritten);
        Assert.Empty(result.Problems);
        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(
            new[] { "diagnostics.txt", $"oscarwatch-{pid}.dmp", "oscarwatch-20261102.log", "oscarwatch-20261103.log", "process.txt", "rotator-trace-20261103-101500.log" }
                .Order(StringComparer.Ordinal),
            archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        using var reader = new StreamReader(archive.GetEntry("process.txt")!.Open());
        Assert.Contains($"Process {pid}", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void FreezeReport_still_saves_when_a_dump_fails()
    {
        var zip = Path.Combine(_dir, "freeze.zip");

        var result = FreezeReport.Create(
            zip,
            [Environment.ProcessId],
            Path.Combine(_dir, "no-logs"),
            () => "DIAGNOSTICS",
            (_, _) => throw new InvalidOperationException("denied"),
            TimeSpan.FromMilliseconds(10));

        Assert.Equal(0, result.DumpsWritten);
        Assert.Contains(result.Problems, p => p.Contains("denied", StringComparison.Ordinal));
        using var archive = ZipFile.OpenRead(zip);
        Assert.NotNull(archive.GetEntry("problems.txt"));
    }

    [Fact]
    public void TraceRequest_round_trips_and_expires()
    {
        var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

        Assert.Null(RotatorTraceRequest.ReadUntilUtc(_dir, now));
        RotatorTraceRequest.Start(_dir, now.AddMinutes(30));
        Assert.Equal(now.AddMinutes(30), RotatorTraceRequest.ReadUntilUtc(_dir, now));
        Assert.Null(RotatorTraceRequest.ReadUntilUtc(_dir, now.AddMinutes(31)));
        RotatorTraceRequest.Stop(_dir);
        Assert.False(File.Exists(RotatorTraceRequest.RequestPath(_dir)));
    }

    [Fact]
    public void Trace_names_match_the_tracker()
    {
        Assert.Equal(OscarWatch.Diagnostics.RotatorTrace.RequestFileName, RotatorTraceRequest.RequestFileName);
    }

    private static void Write(string path, string callsign, DateTime written)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""{ "groundStation": { "callsign": "{{callsign}}" } }""");
        File.SetLastWriteTime(path, written);
    }
}
