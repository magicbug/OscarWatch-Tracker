using System.Text;
using OscarWatch.Core.Models;
using OscarWatch.Core.SessionPlanner;

namespace OscarWatch.Core.Export;

/// <summary>
/// Exports a session plan as CSV or ICS calendar format.
/// </summary>
public static class SessionPlanExporter
{
    /// <summary>
    /// Exports the session plan as a CSV string with header and one row per scheduled pass.
    /// </summary>
    public static string BuildCsv(SessionPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SatelliteName,NoradId,AOS_UTC,LOS_UTC,MaxElevationDeg,CompositeScore,Status");

        foreach (var scheduled in plan.ScheduledPasses)
        {
            var pass = scheduled.Scored.Pass;
            sb.AppendLine(
                $"{pass.SatelliteName},{pass.NoradId}," +
                $"{FormatUtcCsv(pass.AosUtc)},{FormatUtcCsv(pass.LosUtc)}," +
                $"{pass.MaxElevationDeg:F1},{scheduled.Scored.CompositeScore:F1}," +
                $"{scheduled.Reason}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Exports the session plan as an ICS calendar string with VALARM pre-alerts.
    /// </summary>
    public static string BuildCalendar(
        SessionPlan plan,
        GroundStation station,
        int preAlertMinutes = 3)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BEGIN:VCALENDAR");
        sb.AppendLine("VERSION:2.0");
        sb.AppendLine("PRODID:-//OscarWatch//Session Planner//EN");
        sb.AppendLine("CALSCALE:GREGORIAN");
        sb.AppendLine($"X-WR-CALNAME:{Escape($"OscarWatch Session Plan")}");

        foreach (var scheduled in plan.ScheduledPasses)
        {
            var pass = scheduled.Scored.Pass;

            sb.AppendLine("BEGIN:VEVENT");
            sb.AppendLine($"UID:{pass.NoradId}-{pass.AosUtc.Ticks}@oscarwatch.org");
            sb.AppendLine($"DTSTAMP:{FormatUtc(DateTime.UtcNow)}");
            sb.AppendLine($"DTSTART:{FormatUtc(pass.AosUtc)}");
            sb.AppendLine($"DTEND:{FormatUtc(pass.LosUtc)}");
            sb.AppendLine($"SUMMARY:{Escape($"{pass.SatelliteName} pass (max {pass.MaxElevationDeg:F1}°)")}");
            sb.AppendLine(
                "DESCRIPTION:" + Escape(
                    $"Max elevation {pass.MaxElevationDeg:F1}°\n" +
                    $"Composite score {scheduled.Scored.CompositeScore:F1}\n" +
                    $"Duration {pass.Duration:mm\\:ss}\n" +
                    $"Status: {scheduled.Reason}"));
            sb.AppendLine($"LOCATION:{Escape($"{station.DisplayName} ({station.GridSquare})")}");

            sb.AppendLine("BEGIN:VALARM");
            sb.AppendLine($"TRIGGER:-PT{preAlertMinutes}M");
            sb.AppendLine("ACTION:DISPLAY");
            sb.AppendLine($"DESCRIPTION:{Escape($"{pass.SatelliteName} AOS in {preAlertMinutes} minutes")}");
            sb.AppendLine("END:VALARM");

            sb.AppendLine("END:VEVENT");
        }

        sb.AppendLine("END:VCALENDAR");
        return sb.ToString();
    }

    private static string FormatUtc(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");

    private static string FormatUtcCsv(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
