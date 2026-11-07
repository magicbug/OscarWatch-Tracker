using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Diagnostics;

/// <summary>Builds a redacted English diagnostics bundle for support and debug.</summary>
public static class DiagnosticsBundleBuilder
{
    private const int LogTailLineCount = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Build(
        ISettingsService settings,
        IRigController? rig,
        IRotatorController? rotator)
    {
        var version = typeof(DiagnosticsBundleBuilder).Assembly.GetName().Version?.ToString(3) ?? "dev";
        var rigStatus = rig?.GetStatus();
        var rotatorStatus = rotator?.GetPositionStatus();

        var builder = new StringBuilder();
        builder.AppendLine("OscarWatch diagnostics");
        builder.AppendLine($"Generated (UTC): {DateTime.UtcNow:O}");
        builder.AppendLine($"Version: {version}");
        builder.AppendLine();

        builder.AppendLine("== Rig ==");
        if (rigStatus is null)
            builder.AppendLine("Rig controller unavailable");
        else
        {
            builder.AppendLine($"Connected: {rigStatus.IsConnected}");
            builder.AppendLine($"Tracking: {rigStatus.IsTracking}");
            builder.AppendLine($"Status kind: {rigStatus.StatusKind}");
            builder.AppendLine($"Status (English): {RigStatusText.ToEnglish(rigStatus)}");
            if (!string.IsNullOrWhiteSpace(rigStatus.StatusDetail))
                builder.AppendLine($"Detail: {rigStatus.StatusDetail}");
        }

        builder.AppendLine();
        builder.AppendLine("== Rotator ==");
        if (rotatorStatus is null)
            builder.AppendLine("Rotator controller unavailable");
        else
        {
            builder.AppendLine($"Connected: {rotatorStatus.IsConnected}");
            builder.AppendLine($"Connection kind: {rotatorStatus.ConnectionKind}");
            builder.AppendLine($"Status (English): {RotatorStatusText.ToEnglish(rotatorStatus)}");
            if (!string.IsNullOrWhiteSpace(rotatorStatus.ConnectionDetail))
                builder.AppendLine($"Detail: {rotatorStatus.ConnectionDetail}");
        }

        builder.AppendLine();
        builder.AppendLine("== Settings (redacted) ==");
        builder.AppendLine(RedactSettings(settings.Current));

        builder.AppendLine();
        builder.AppendLine("== Log tail ==");
        builder.AppendLine(ReadLogTail());

        return builder.ToString();
    }

    internal static string RedactSettings(AppSettings settings)
    {
        var clone = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings, JsonOptions),
            JsonOptions) ?? new AppSettings();

        RedactApiKey(clone.Cloudlog);
        RedactApiKey(clone.HamsAt);
        RedactApiKey(clone.SatelliteStatus);
        RedactQrz(clone.Qrz);
        RedactHamQth(clone.HamQth);
        RedactRigPasswords(clone.Rig);

        return JsonSerializer.Serialize(clone, JsonOptions);
    }

    private static void RedactRigPasswords(RigSettings rig)
    {
        if (!string.IsNullOrEmpty(rig.NetworkPassword))
            rig.NetworkPassword = "***";
        if (!string.IsNullOrEmpty(rig.Downlink.NetworkPassword))
            rig.Downlink.NetworkPassword = "***";
        if (!string.IsNullOrEmpty(rig.Uplink.NetworkPassword))
            rig.Uplink.NetworkPassword = "***";
    }

    private static void RedactApiKey(CloudlogSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            settings.ApiKey = "***";
    }

    private static void RedactApiKey(HamsAtSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            settings.ApiKey = "***";
    }

    private static void RedactApiKey(SatelliteStatusSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ApiToken))
            settings.ApiToken = "***";
    }

    private static void RedactQrz(QrzSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Password))
            settings.Password = "***";
    }

    private static void RedactHamQth(HamQthSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Password))
            settings.Password = "***";
    }

    private static string ReadLogTail()
    {
        try
        {
            var logDir = AppLogging.LogDirectory;
            if (!Directory.Exists(logDir))
                return "(no log directory)";

            var latest = Directory.EnumerateFiles(logDir, "oscarwatch-*.log")
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .FirstOrDefault();

            if (latest is null || !latest.Exists)
                return "(no log files)";

            return FormatLogTail(ReadSharedLogLines(latest.FullName));
        }
        catch (Exception ex)
        {
            return $"(could not read log tail: {ex.Message})";
        }
    }

    /// <summary>Read log lines while Serilog (or another writer) still has the file open.</summary>
    internal static IReadOnlyList<string> ReadSharedLogLines(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);

        return lines;
    }

    internal static string FormatLogTail(IReadOnlyList<string> lines, int maxLines = LogTailLineCount)
    {
        if (lines.Count == 0)
            return "(log file empty)";

        var start = Math.Max(0, lines.Count - maxLines);
        return string.Join(Environment.NewLine, lines.Skip(start));
    }
}
