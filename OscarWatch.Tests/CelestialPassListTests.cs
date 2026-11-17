using OscarWatch.Core.Display;
using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using OscarWatch.ViewModels;
using static OscarWatch.Localization.LocalizationCulture;
namespace OscarWatch.Tests;
public class CelestialPassListTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 6, 0, 0, DateTimeKind.Utc);
    private static AppSettings Settings(bool moon) => new()
    {
        Eme = new EmeSettings { ShowMoonInPasses = moon },
        GroundStation = new GroundStation { LatitudeDeg = 51.5, LongitudeDeg = -0.1, AltitudeMetersAsl = 50 }
    };
    private static PassInfo Pass(string name, string id, DateTime aos, int minutes, CelestialBodyKind? body = null) => new()
    {
        SatelliteName = name,
        NoradId = id,
        AosUtc = aos,
        LosUtc = aos.AddMinutes(minutes),
        MaxElevationDeg = 40,
        MaxElevationUtc = aos.AddMinutes(minutes / 2),
        Body = body
    };
    [Fact]
    public void The_moon_is_off_by_default()
    {
        var settings = new AppSettings();
        Assert.False(settings.Eme.ShowMoonInPasses);
        Assert.Empty(CelestialPassList.Build(settings, Now, 48));
    }

    [Fact]
    public void Switched_on_it_lists_the_moon_earliest_first_and_within_the_window()
    {
        var passes = CelestialPassList.Build(Settings(true), Now, 48);
        Assert.NotEmpty(passes);
        Assert.All(passes, p => Assert.Equal("MOON", p.NoradId));
        Assert.All(passes, p => Assert.True(p.IsCelestialBody));
        Assert.All(passes, p => Assert.Equal(CelestialBodyKind.Moon, p.Body));
        Assert.Equal(passes.OrderBy(p => p.AosUtc).Select(p => p.AosUtc), passes.Select(p => p.AosUtc));
        Assert.All(passes, p => Assert.True(p.AosUtc < Now.AddHours(48)));
    }

    [Fact]
    public void The_list_follows_the_active_station()
    {
        var london = CelestialPassList.Build(Settings(true), Now, 24);
        var settings = Settings(true);
        settings.GroundStation = new GroundStation { LatitudeDeg = -33.87, LongitudeDeg = 151.21, AltitudeMetersAsl = 50 };
        var sydney = CelestialPassList.Build(settings, Now, 24);
        Assert.NotEqual(london[0].AosUtc, sydney[0].AosUtc);
    }

    [Fact]
    public void The_moon_elevation_limit_moves_the_times_and_the_satellite_minimum_is_not_used()
    {
        var plain = CelestialPassList.Build(Settings(true), Now, 48);
        var raised = Settings(true);
        raised.Eme.ElevationLimitDeg = 10;
        var withLimit = CelestialPassList.Build(raised, Now, 48);
        Assert.True(withLimit[0].AosUtc > plain[0].AosUtc || withLimit[0].LosUtc < plain[0].LosUtc);
        // The satellite minimum elevation setting makes no difference to the Moon.
        var highMinimum = Settings(true);
        highMinimum.MinimumElevationDeg = 60;
        var same = CelestialPassList.Build(highMinimum, Now, 48);
        Assert.Equal(plain.Select(p => p.AosUtc), same.Select(p => p.AosUtc));
    }

    [Fact]
    public void The_horizon_mask_is_applied_only_when_asked()
    {
        var settings = Settings(true);
        settings.GroundStation.HorizonMask = new HorizonMask
        {
            Points = [new(0, 25), new(90, 25), new(180, 25), new(270, 25)]
        };
        var ignoring = CelestialPassList.Build(settings, Now, 48);
        settings.Eme.UseHorizonMask = true;
        var applying = CelestialPassList.Build(settings, Now, 48);
        Assert.NotEqual(ignoring[0].AosUtc, applying[0].AosUtc);
        // A mask at 25 degrees all round is the same as a limit of 25 degrees.
        var limit = Settings(true);
        limit.Eme.ElevationLimitDeg = 25;
        var byLimit = CelestialPassList.Build(limit, Now, 48);
        Assert.Equal(byLimit.Count, applying.Count);
        Assert.True(Math.Abs((byLimit[0].AosUtc - applying[0].AosUtc).TotalSeconds) <= 2);
    }

    [Fact]
    public void A_celestial_entry_sorts_in_among_the_satellite_passes()
    {
        var iss = Pass("ISS", "25544", Now.AddHours(1), 10);
        var so50 = Pass("SO-50", "27607", Now.AddHours(5), 10);
        var moon = Pass("Moon", "MOON", Now.AddHours(3), 500, CelestialBodyKind.Moon);
        var merged = PassSidebarMerge.WithCelestialBodies([iss, so50], [moon]);
        Assert.Equal(["ISS", "Moon", "SO-50"], merged.Select(p => p.SatelliteName).ToList());
    }
    [Fact]
    public void Without_celestial_entries_the_satellite_list_is_returned_as_it_was()
    {
        IReadOnlyList<PassInfo> satellites = [Pass("ISS", "25544", Now.AddHours(1), 10)];
        Assert.Same(satellites, PassSidebarMerge.WithCelestialBodies(satellites, []));
    }
    [Fact]
    public void The_upcoming_passes_minimum_duration_applies_to_moon_entries_like_any_other()
    {
        var short1 = Pass("Moon", "MOON", Now.AddHours(1), 5, CelestialBodyKind.Moon);
        var kept = PassSidebarMerge.WithinPredictionWindow([short1], Now, 48, 10);
        Assert.Empty(kept);
    }
    [Fact]
    public void Switching_it_on_survives_a_save_and_a_load()
    {
        Assert.True(SettingsService.TryParse("{ \"eme\": { \"showMoonInPasses\": true, \"elevationLimitDeg\": -2.5 } }", out var parsed, out _));
        Assert.True(parsed.Eme.ShowMoonInPasses);
        Assert.Equal(-2.5, parsed.Eme.ElevationLimitDeg);
        Assert.True(SettingsService.TryParse("{ \"minimumElevationDeg\": 5 }", out var without, out _));
        Assert.False(without.Eme.ShowMoonInPasses);
        Assert.Equal(0, without.Eme.ElevationLimitDeg);
        Assert.Equal(90, without.Eme.MaximumElevationDeg);
    }

    [Fact]
    public void A_moon_row_is_named_in_the_ui_language_and_has_no_schedule_button()
    {
        using var _ = TestUiCulture.Apply(DefaultLanguage);
        var moon = Pass("Moon", "MOON", Now.AddHours(1), 500, CelestialBodyKind.Moon);
        var row = PassRowViewModel.From(moon, ClockDisplayFormat.TwentyFourHour, useUtc: true);
        Assert.True(row.IsCelestialBody);
        Assert.Equal("Moon", row.SatelliteName);
        Assert.False(row.ShowScheduleButton);
    }
    [Fact]
    public void A_satellite_row_still_has_its_schedule_button()
    {
        using var _ = TestUiCulture.Apply(DefaultLanguage);
        var row = PassRowViewModel.From(Pass("ISS", "25544", Now.AddHours(1), 10), ClockDisplayFormat.TwentyFourHour, useUtc: true);
        Assert.False(row.IsCelestialBody);
        Assert.Equal("ISS", row.SatelliteName);
        Assert.True(row.ShowScheduleButton);
    }
    [Fact]
    public void The_moon_name_is_translated_in_every_language()
    {
        const string english = "Moon";
        foreach (var culture in new[] { "de", "es", "id", "it", "ja", "pt-BR", "ru", "th", "zh-CN" })
        {
            using var _ = TestUiCulture.Apply(culture);
            var moon = PassRowViewModel.From(Pass("Moon", "MOON", Now.AddHours(1), 500, CelestialBodyKind.Moon), ClockDisplayFormat.TwentyFourHour, useUtc: true).SatelliteName;
            Assert.NotEqual(english, moon);
        }
    }
}
