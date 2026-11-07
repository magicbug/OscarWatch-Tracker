using OscarWatch.Core.Display;
using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

/// <summary>
/// How far ahead reminder rematch must look so a shorter sidebar or planner window
/// cannot drop a future scheduled pass.
/// </summary>
public static class ScheduledPassHorizon
{
    public const int MaxHours = 168;

    public static int Hours(AppSettings settings, DateTime utcNow, int additionalHours = 0)
    {
        var hours = Math.Max(settings.PassPredictionHours, settings.PassPlannerPredictionHours);
        if (additionalHours > hours)
            hours = additionalHours;

        var now = PassUtc.Normalize(utcNow);
        foreach (var entry in settings.ScheduledPasses ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.NoradId))
                continue;

            var aos = PassUtc.Normalize(entry.AosUtc);
            if (aos <= now)
                continue;

            var needed = (int)Math.Ceiling((aos - now).TotalHours) + 1;
            if (needed > hours)
                hours = needed;
        }

        return Math.Clamp(hours, 1, MaxHours);
    }
}
