using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;
using OscarWatch.Localization;
using OscarWatch.ViewModels;

namespace OscarWatch.Tests;

/// <summary>
/// The Earth View's text is built from resource strings. These tests read the resource files directly, so they
/// need no running application and do not change the process culture: every string the builder asks for must
/// exist in every language, format with the arguments the builder gives it, and never fall back to a raw key.
/// </summary>
public sealed class EarthViewStringsTests
{
    private static readonly (string File, string Culture)[] Locales =
    [
        ("Strings.resx", "en-GB"),
        ("Strings.de.resx", "de"),
        ("Strings.es.resx", "es"),
        ("Strings.id.resx", "id"),
        ("Strings.it.resx", "it"),
        ("Strings.ja.resx", "ja"),
        ("Strings.pt-BR.resx", "pt-BR"),
        ("Strings.ru.resx", "ru"),
        ("Strings.th.resx", "th"),
        ("Strings.zh-CN.resx", "zh-CN")
    ];

    public static TheoryData<string, string> AllLocales
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (file, culture) in Locales)
                data.Add(file, culture);
            return data;
        }
    }

    /// <summary>How many arguments the text builder passes for each key, which is how many placeholders the string needs.</summary>
    private static readonly Dictionary<string, int> ArgumentCounts = new()
    {
        [EarthViewTextBuilder.KeyWindowTitle] = 0,
        [EarthViewTextBuilder.KeyEmptyNoSatellite] = 0,
        [EarthViewTextBuilder.KeyEmptyNoPosition] = 0,
        [EarthViewTextBuilder.KeySectionSatellite] = 0,
        [EarthViewTextBuilder.KeySectionStation] = 0,
        [EarthViewTextBuilder.KeySectionCoverage] = 0,
        [EarthViewTextBuilder.KeyLabelAltitude] = 0,
        [EarthViewTextBuilder.KeyLabelTime] = 0,
        [EarthViewTextBuilder.KeyLabelLighting] = 0,
        [EarthViewTextBuilder.KeyLabelAzimuth] = 0,
        [EarthViewTextBuilder.KeyLabelElevation] = 0,
        [EarthViewTextBuilder.KeyLabelRange] = 0,
        [EarthViewTextBuilder.KeyLabelFootprint] = 0,
        [EarthViewTextBuilder.KeyLabelMinElevation] = 0,
        [EarthViewTextBuilder.KeyValueKilometres] = 1,
        [EarthViewTextBuilder.KeyValueDegrees] = 1,
        [EarthViewTextBuilder.KeyStatusTime] = 1,
        [EarthViewTextBuilder.KeyStatusBelowHorizon] = 0,
        [EarthViewTextBuilder.KeyStatusAboveHorizon] = 0,
        [EarthViewTextBuilder.KeyStatusInsideMinElevation] = 1,
        [EarthViewTextBuilder.KeyStatusOutsideMinElevation] = 1,
        [EarthViewTextBuilder.KeyStatusSunlit] = 0,
        [EarthViewTextBuilder.KeyStatusEclipse] = 0,
        [EarthViewTextBuilder.KeyAccessibleName] = 1,
        [EarthViewTextBuilder.KeyAccessibleUsable] = 4,
        [EarthViewTextBuilder.KeyAccessibleAboveHorizon] = 4,
        [EarthViewTextBuilder.KeyAccessibleBelowHorizon] = 1,
        [EarthViewTextBuilder.KeyAccessibleNoAngles] = 1
    };

    private static readonly Regex Placeholder = new(@"\{(\d+)(?::[^}]*)?\}", RegexOptions.Compiled);

    private static Dictionary<string, string> Load(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", file);
        Assert.True(File.Exists(path), $"Missing {path}");
        return XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => d.Element("value")?.Value ?? "", StringComparer.Ordinal);
    }

    /// <summary>Looks strings up in one resource file and formats with that language's culture. A bad format throws.</summary>
    private sealed class ResxLocalizer(Dictionary<string, string> entries, CultureInfo culture) : ILocalizationService
    {
        public string Get(string key) => entries.TryGetValue(key, out var v) && v.Length > 0 ? v : key;

        public string Get(string key, params object[] args) => string.Format(culture, Get(key), args);
    }

    private static EarthViewFrame Frame(EarthViewSituation situation, LookAngles? look, bool sunlit) => new(
        NoradId: "25544",
        SatelliteName: "ISS",
        Camera: new GlobeCamera(10, 20, 419.6),
        DisplayUtc: new DateTime(2026, 6, 21, 12, 5, 9, DateTimeKind.Utc),
        GroundTrack: [],
        MinimumElevationDeg: 5,
        MinimumElevationRadiusDeg: 18,
        Stations: [],
        Subsolar: new GeoCoordinate(23, 0),
        IsSunlit: sunlit,
        LookFromHome: look,
        HomeSituation: situation);

    [Fact]
    public void Every_key_the_text_builder_uses_is_accounted_for_in_this_test()
    {
        var keys = typeof(EarthViewTextBuilder)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.StartsWith("Key", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

        // If a key is added to the builder, this fails until its argument count is listed above.
        Assert.Equal(keys.OrderBy(k => k), ArgumentCounts.Keys.OrderBy(k => k));
    }

    [Theory]
    [MemberData(nameof(AllLocales))]
    public void Every_key_exists_and_takes_the_arguments_the_builder_passes(string file, string culture)
    {
        var entries = Load(file);

        foreach (var (key, count) in ArgumentCounts)
        {
            Assert.True(entries.TryGetValue(key, out var value) && value.Length > 0, $"{file} has no text for {key}");
            Assert.True(
                Placeholder.Matches(value!).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet()
                    .SetEquals(Enumerable.Range(0, count)),
                $"{file} {key} should use placeholders {{0}} to {{{count - 1}}}: {value}");
        }
    }

    [Theory]
    [MemberData(nameof(AllLocales))]
    public void Other_languages_do_not_just_repeat_the_english_text(string file, string culture)
    {
        if (file == "Strings.resx")
            return;

        var english = Load("Strings.resx");
        var entries = Load(file);
        // Units, "{0} UTC" and a few words that really are the same in a language (for example "Footprint" in German).
        var sameInEveryLanguage = new HashSet<string>
        {
            EarthViewTextBuilder.KeyStatusTime,
            EarthViewTextBuilder.KeyValueDegrees,
            EarthViewTextBuilder.KeyValueKilometres
        };
        var sameInThisLanguage = new HashSet<(string File, string Key)>
        {
            ("Strings.de.resx", EarthViewTextBuilder.KeyLabelFootprint),
            ("Strings.de.resx", EarthViewTextBuilder.KeyLabelElevation),
            ("Strings.id.resx", EarthViewTextBuilder.KeyLabelFootprint),
            ("Strings.it.resx", EarthViewTextBuilder.KeySectionSatellite),
            ("Strings.pt-BR.resx", EarthViewTextBuilder.KeyLabelFootprint),
            ("Strings.pt-BR.resx", EarthViewTextBuilder.KeyLabelAltitude)
        };
        var untranslated = ArgumentCounts.Keys
            .Where(k => !sameInEveryLanguage.Contains(k))
            .Where(k => !sameInThisLanguage.Contains((file, k)))
            .Where(k => string.Equals(english[k], entries[k], StringComparison.Ordinal))
            .ToList();

        Assert.True(untranslated.Count == 0, $"{file} still has English text for: {string.Join(", ", untranslated)}");
    }

    [Theory]
    [MemberData(nameof(AllLocales))]
    public void All_text_renders_without_a_raw_key_or_a_leftover_brace(string file, string culture)
    {
        var l = new ResxLocalizer(Load(file), CultureInfo.GetCultureInfo(culture));
        var look = new LookAngles(100.4, 29.6, 912.3);
        var texts = new List<string>
        {
            l.Get(EarthViewTextBuilder.KeyEmptyNoSatellite),
            l.Get(EarthViewTextBuilder.KeyEmptyNoPosition),
            EarthViewTextBuilder.AccessibleName(l, null)
        };

        foreach (var situation in Enum.GetValues<EarthViewSituation>())
        {
            foreach (var lookAngles in new LookAngles?[] { look, null })
            {
                foreach (var sunlit in new[] { true, false })
                {
                    var frame = Frame(situation, lookAngles, sunlit);
                    texts.Add(EarthViewTextBuilder.Status(l, frame));
                    texts.Add(EarthViewTextBuilder.AccessibleSummary(l, frame));
                    texts.Add(EarthViewTextBuilder.AccessibleName(l, frame));
                }
            }
        }

        Assert.All(texts, text =>
        {
            Assert.False(string.IsNullOrWhiteSpace(text), $"{file}: empty text");
            Assert.DoesNotContain("EarthView.", text);
            Assert.DoesNotContain("Window.EarthView", text);
            Assert.DoesNotContain("{", text);
            Assert.DoesNotContain("}", text);
        });
    }

    [Theory]
    [MemberData(nameof(AllLocales))]
    public void Status_text_has_a_heading_and_one_line_per_row(string file, string culture)
    {
        var l = new ResxLocalizer(Load(file), CultureInfo.GetCultureInfo(culture));

        // Satellite (heading, name, altitude, time, lighting), your station (heading, three angles),
        // coverage (heading, footprint, minimum elevation).
        var usable = EarthViewTextBuilder.Sections(l, Frame(EarthViewSituation.Usable, new LookAngles(100, 30, 900), true));
        Assert.Equal([5, 4, 3], usable.Select(sec => 1 + sec.Rows.Count));
        Assert.Equal(12, EarthViewTextBuilder.Status(l, Frame(EarthViewSituation.Usable, new LookAngles(100, 30, 900), true)).Split('\n').Length);

        // Below the horizon there is no minimum elevation row.
        var below = EarthViewTextBuilder.Sections(l, Frame(EarthViewSituation.BelowHorizon, new LookAngles(100, -30, 4000), false));
        Assert.Equal([5, 4, 2], below.Select(sec => 1 + sec.Rows.Count));

        // With no look angles the station section is left out.
        var none = EarthViewTextBuilder.Sections(l, Frame(EarthViewSituation.BelowHorizon, null, false));
        Assert.Equal(2, none.Count);
    }

    [Fact]
    public void English_text_reads_correctly()
    {
        var l = new ResxLocalizer(Load("Strings.resx"), CultureInfo.GetCultureInfo("en-GB"));
        var usable = Frame(EarthViewSituation.Usable, new LookAngles(100.4, 29.6, 912.3), sunlit: true);

        Assert.Equal(
            string.Join("\n",
                "Satellite",
                "ISS",
                "Altitude: 420 km",
                "Time: 2026-06-21 12:05:09 UTC",
                "Lighting: In sunlight",
                "Your station",
                "Azimuth: 100°",
                "Elevation: 30°",
                "Range: 912 km",
                "Coverage",
                "Footprint: Inside: satellite above your horizon",
                "Minimum elevation: Satellite above 5°"),
            EarthViewTextBuilder.Status(l, usable));

        Assert.Equal(
            "ISS is 30 degrees above your horizon at azimuth 100 degrees, above your minimum elevation of 5 degrees.",
            EarthViewTextBuilder.AccessibleSummary(l, usable));
        Assert.Equal(
            "ISS is 30 degrees above your horizon at azimuth 100 degrees, below your minimum elevation of 5 degrees.",
            EarthViewTextBuilder.AccessibleSummary(l, usable with { HomeSituation = EarthViewSituation.AboveHorizon }));
        Assert.Equal(
            "ISS is below your horizon.",
            EarthViewTextBuilder.AccessibleSummary(l, usable with { HomeSituation = EarthViewSituation.BelowHorizon }));
        Assert.Equal("ISS is above your horizon.", EarthViewTextBuilder.AccessibleSummary(l, usable with { LookFromHome = null }));
        Assert.Equal("Earth View, ISS", EarthViewTextBuilder.AccessibleName(l, usable));
        Assert.Equal("Earth View", EarthViewTextBuilder.AccessibleName(l, null));
        Assert.Equal("In the Earth's shadow", l.Get(EarthViewTextBuilder.KeyStatusEclipse));
    }

    [Fact]
    public void New_text_follows_the_writing_style_rules()
    {
        // .cursor/rules/writing-style.mdc: no em dashes anywhere, and no ellipsis in titles.
        foreach (var (file, _) in Locales)
        {
            foreach (var (key, value) in Load(file).Where(p => p.Key.Contains("EarthView", StringComparison.Ordinal)))
            {
                Assert.DoesNotContain('\u2014', value);
                if (key.EndsWith(".Title", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain("...", value);
                    Assert.DoesNotContain('\u2026', value);
                }
            }
        }
    }
}
