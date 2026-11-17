using OscarWatch.Core.Eme;
using OscarWatch.Core.Models;

namespace OscarWatch.Core.Orbit;

/// <summary>The Moon entries for the upcoming pass list, following the settings and the active station.</summary>
public static class CelestialPassList
{
    /// <summary>
    /// Moon rise and set entries covering <paramref name="hours"/> hours from <paramref name="utcNow"/>.
    /// Empty when the Moon is switched off.
    /// </summary>
    public static IReadOnlyList<PassInfo> Build(AppSettings settings, DateTime utcNow, int hours)
    {
        var eme = settings.Eme;
        if (eme is null || !eme.ShowMoonInPasses)
            return [];
        var site = settings.GroundStation;
        var mask = eme.UseHorizonMask ? site.HorizonMask : null;
        var rule = new MoonVisibilityRule(eme.ElevationLimitDeg, eme.MaximumElevationDeg, mask);
        var station = new StationPosition(site.LatitudeDeg, site.LongitudeDeg, site.AltitudeMetersAsl);
        return MoonWindows.Find(rule, station, utcNow, utcNow.AddHours(Math.Max(1, hours)));
    }
}
