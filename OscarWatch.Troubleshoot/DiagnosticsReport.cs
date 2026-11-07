using System.Runtime.InteropServices;
using System.Text;

namespace OscarWatch.Troubleshoot;

/// <summary>English support bundle: environment, device switches, redacted settings, and the log tail.</summary>
internal static class DiagnosticsReport
{
    private const int LogTailLineCount = 200;
    private const int RotatorTraceTailLineCount = 400;

    public static string Build()
    {
        var builder = new StringBuilder();
        builder.AppendLine("OscarWatch troubleshooter diagnostics");
        builder.AppendLine($"Generated (UTC): {DateTime.UtcNow:O}");
        builder.AppendLine($"Troubleshooter version: {typeof(DiagnosticsReport).Assembly.GetName().Version?.ToString(3) ?? "dev"}");
        builder.AppendLine($"OS: {OsName.Describe()}");
        builder.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine();

        builder.AppendLine("== OscarWatch ==");
        builder.AppendLine(TrackerProcess.ExecutableExists
            ? $"Application: {TrackerProcess.ExecutablePath} (version {TrackerProcess.ReadVersion() ?? "unknown"})"
            : $"Application: not found at {TrackerProcess.ExecutablePath}");
        var running = TrackerProcess.FindRunning();
        builder.AppendLine(running.Count == 0
            ? "Process: not running"
            : "Process: running, " + string.Join(", ", running.Select(p => $"PID {p.Id} started {p.StartedLocal:O}")));
        builder.AppendLine($"Data folder: {TroubleshootPaths.DataFolder}");
        builder.AppendLine();

        var settings = SettingsFile.Read(TroubleshootPaths.SettingsPath);
        builder.AppendLine("== Settings file ==");
        builder.AppendLine($"State: {settings.State}");
        if (settings.LastWriteLocal is { } written)
            builder.AppendLine($"Written: {written:O}");
        if (!string.IsNullOrEmpty(settings.Error))
            builder.AppendLine($"Error: {settings.Error}");
        builder.AppendLine($"Radio: {Describe(settings.Radio)}");
        builder.AppendLine($"Rotator: {Describe(settings.Rotator)}");
        builder.AppendLine($"GPS: {Describe(settings.Gps)}");
        builder.AppendLine($"FT4 echo calibrations: {settings.EchoCalibrationCount}");
        builder.AppendLine();

        builder.AppendLine("== Settings (redacted) ==");
        builder.AppendLine(SettingsFile.Redacted(TroubleshootPaths.SettingsPath));
        builder.AppendLine();

        var latest = LogFiles.FindLatest(TroubleshootPaths.LogFolder);
        IReadOnlyList<string> lines = [];
        builder.AppendLine("== Latest log ==");
        if (latest is null)
        {
            builder.AppendLine("(no log files)");
        }
        else
        {
            builder.AppendLine($"{latest.FullName} (written {latest.LastWriteTime:O})");
            try
            {
                lines = LogFiles.ReadLines(latest.FullName);
            }
            catch (Exception ex)
            {
                builder.AppendLine($"(could not read log: {ex.Message})");
            }
        }

        builder.AppendLine();
        builder.AppendLine("== Recent errors ==");
        var problems = LogFiles.RecentProblems(lines);
        builder.AppendLine(problems.Count == 0 ? "(none)" : string.Join(Environment.NewLine + Environment.NewLine, problems));
        builder.AppendLine();

        builder.AppendLine("== Log tail ==");
        builder.AppendLine(lines.Count == 0 ? "(empty)" : string.Join(Environment.NewLine, LogFiles.Tail(lines, LogTailLineCount)));

        AppendRotatorRecording(builder);
        return builder.ToString();
    }

    private static void AppendRotatorRecording(StringBuilder builder)
    {
        var trace = RotatorTraceRequest.FindLatest(TroubleshootPaths.LogFolder);
        var until = RotatorTraceRequest.ReadUntilUtc(TroubleshootPaths.DataFolder, DateTime.UtcNow);
        if (trace is null && until is null)
            return;

        builder.AppendLine();
        builder.AppendLine("== Latest rotator recording ==");
        builder.AppendLine(until is { } end ? $"Recording until {end:O} (UTC)" : "Not recording now");
        if (trace is null)
        {
            builder.AppendLine("(no recording file yet)");
            return;
        }

        builder.AppendLine($"{trace.FullName} (written {trace.LastWriteTime:O})");
        try
        {
            builder.AppendLine(string.Join(Environment.NewLine, LogFiles.Tail(LogFiles.ReadLines(trace.FullName), RotatorTraceTailLineCount)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            builder.AppendLine($"(could not read recording: {ex.Message})");
        }
    }

    private static string Describe(DeviceState device)
    {
        var state = device.Enabled switch
        {
            true => "enabled",
            false => "disabled",
            null => "not set"
        };
        return device.Target.Length == 0 ? state : $"{state} ({device.Target})";
    }
}
