using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OscarWatch.Core.Eme;
using OscarWatch.Core.Models;
using OscarWatch.Localization;
using OscarWatch.ViewModels;

namespace OscarWatch.Tests;

/// <summary>
/// The Moon window's text is built from resource strings. These tests read the resource files directly, so they
/// need no running application: every string the builder or the window asks for must exist in every language,
/// take the same arguments as the English text, and never fall back to a raw key.
/// </summary>
public sealed class MoonStringsTests
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

    /// <summary>Keys used only in the window's markup and the menu, so the builder does not name them.</summary>
    private static readonly string[] MarkupKeys =
    [
        "Menu.Window.Moon", "Moon.Band", "Moon.Frequency", "Moon.Beamwidth", "Moon.Circular", "Moon.LargeDigits", "Moon.Copy"
    ];

    /// <summary>Strings that really are the same in every language: units, symbols and "{0} UTC".</summary>
    private static readonly HashSet<string> SameInEveryLanguage =
    [
        EmeTextBuilder.KeyValueDegrees, EmeTextBuilder.KeyValuePosition, EmeTextBuilder.KeyValueHertz,
        EmeTextBuilder.KeyValueDecibels, EmeTextBuilder.KeyValueExtraLoss, EmeTextBuilder.KeyValueDiameter,
        EmeTextBuilder.KeyValueApsis, EmeTextBuilder.KeyValueSeparation, EmeTextBuilder.KeyValueSeparationFlag,
        EmeTextBuilder.KeyValueTimeUtc
    ];

    /// <summary>Units written the same way in a language (km and s in Latin script languages).</summary>
    private static readonly HashSet<string> UnitKeys =
    [
        EmeTextBuilder.KeyValueKilometres, EmeTextBuilder.KeyValueRangeRate, EmeTextBuilder.KeyValueSeconds
    ];

    /// <summary>Words that are spelled the same in English and in that language.</summary>
    private static readonly HashSet<(string File, string Key)> SameInThisLanguage =
    [
        ("Strings.de.resx", EmeTextBuilder.KeySectionPosition),
        ("Strings.de.resx", EmeTextBuilder.KeyLabelElevation),
        ("Strings.de.resx", EmeTextBuilder.KeyLabelPhase),
        ("Strings.de.resx", EmeTextBuilder.KeyLabelStatus),
        ("Strings.de.resx", "Moon.Band"),
        ("Strings.id.resx", EmeTextBuilder.KeyLabelDiameter),
        ("Strings.id.resx", EmeTextBuilder.KeyLabelStatus),
        ("Strings.id.resx", EmeTextBuilder.KeyApsisPerigee),
        ("Strings.id.resx", EmeTextBuilder.KeyApsisApogee)
    ];

    private static readonly Regex Placeholder = new(@"\{(\d+)(?::[^}]*)?\}", RegexOptions.Compiled);

    private static Dictionary<string, string> Load(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", file);
        Assert.True(File.Exists(path), $"Missing {path}");
        return XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => d.Element("value")?.Value ?? "", StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> BuilderKeys() => typeof(EmeTextBuilder)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.Name.StartsWith("Key", StringComparison.Ordinal))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToList();

    private static IEnumerable<string> AllKeys() => BuilderKeys().Concat(MarkupKeys);

    private sealed class ResxLocalizer(Dictionary<string, string> entries, CultureInfo culture) : ILocalizationService
    {
        public string Get(string key) => entries.TryGetValue(key, out var v) ? v : key;

        public string Get(string key, params object[] args) =>
            string.Format(culture, entries.TryGetValue(key, out var v) ? v : key, args);
    }

    [Fact]
    public void There_are_enough_keys_to_be_worth_testing()
    {
        Assert.True(BuilderKeys().Count >= 70);
        Assert.Equal(BuilderKeys().Count, BuilderKeys().Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(AllLocales))]
    public void Every_key_exists_and_takes_the_same_arguments_as_the_english_text(string file, string culture)
    {
        _ = culture;
        var english = Load("Strings.resx");
        var entries = Load(file);
        foreach (var key in AllKeys())
        {
            Assert.True(entries.TryGetValue(key, out var value) && value.Length > 0, $"{file} has no text for {key}");
            var expected = Placeholder.Matches(english[key]).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
            var actual = Placeholder.Matches(value!).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
            Assert.True(expected.SetEquals(actual), $"{file} {key} should use placeholders {string.Join(",", expected)}: {value}");
        }
    }

    [Theory]
    [MemberData(nameof(AllLocales))]
    public void Other_languages_do_not_just_repeat_the_english_text(string file, string culture)
    {
        _ = culture;
        if (file == "Strings.resx")
            return;
        var english = Load("Strings.resx");
        var entries = Load(file);
        var untranslated = AllKeys()
            .Where(k => !SameInEveryLanguage.Contains(k) && !UnitKeys.Contains(k))
            .Where(k => !SameInThisLanguage.Contains((file, k)))
            .Where(k => string.Equals(english[k], entries[k], StringComparison.Ordinal))
            .ToList();
        Assert.True(untranslated.Count == 0, $"{file} still has English text for: {string.Join(", ", untranslated)}");
    }

    private static EmeFrame Frame(DateTime utc, EmeSettings? eme = null, bool usableFrequency = true, MoonNextEvent? next = null) =>
        EmeFrameBuilder.Build(utc, new StationPosition(51.5074, -0.1278, 11), eme ?? new EmeSettings(), null, next, usableFrequency)!;

    [Theory]
    [MemberData(nameof(AllLocales))]
    public void All_text_renders_without_a_raw_key_or_a_leftover_brace(string file, string culture)
    {
        var l = new ResxLocalizer(Load(file), CultureInfo.GetCultureInfo(culture));
        string Time(DateTime t) => l.Get(EmeTextBuilder.KeyValueTimeUtc, t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        var texts = new List<string>();
        var times = new[]
        {
            new DateTime(2026, 3, 20, 12, 30, 0, DateTimeKind.Utc),    // up
            new DateTime(2026, 11, 16, 13, 0, 0, DateTimeKind.Utc),    // just below the horizon
            new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc)      // near new Moon
        };
        foreach (var t in times)
        {
            foreach (var band in new[] { "144", "1296" })
            {
                foreach (var usable in new[] { true, false })
                {
                    var frame = Frame(t, new EmeSettings { SelectedBandId = band }, usable,
                        next: new MoonNextEvent(true, t.AddHours(4)));
                    foreach (var sec in EmeTextBuilder.Sections(l, frame, Time))
                    {
                        texts.Add(sec.Heading);
                        foreach (var row in sec.Rows)
                        {
                            texts.Add(row.Value);
                            if (row.Label is not null)
                                texts.Add(row.Label);
                        }
                    }
                    texts.Add(EmeTextBuilder.StatusText(l, frame));
                    texts.Add(EmeTextBuilder.NextText(l, frame, Time));
                    texts.Add(EmeTextBuilder.NextText(l, frame with { Next = new MoonNextEvent(false, t.AddHours(2)) }, Time));
                    texts.Add(EmeTextBuilder.NextText(l, frame with { Next = null }, Time));
                    texts.Add(EmeTextBuilder.AccessibleSummary(l, frame));
                    texts.Add(EmeTextBuilder.PlainText(l, frame, Time));
                }
            }
        }
        texts.Add(EmeTextBuilder.AccessibleName(l));
        texts.Add(l.Get(EmeTextBuilder.KeyEmptyNoPosition));
        texts.Add(l.Get(EmeTextBuilder.KeyFrequencyInvalid));
        foreach (var phase in Enum.GetValues<MoonPhaseName>())
            texts.Add(EmeTextBuilder.PhaseWord(l, phase));
        foreach (var c in Enum.GetValues<PathLossClass>())
            texts.Add(EmeTextBuilder.ClassWord(l, c));
        foreach (var key in MarkupKeys)
            texts.Add(l.Get(key));

        Assert.All(texts, text =>
        {
            Assert.False(string.IsNullOrWhiteSpace(text), $"{file}: empty text");
            Assert.DoesNotContain("Moon.", text);
            Assert.DoesNotContain("Window.Moon", text);
            Assert.DoesNotContain("Menu.Window.Moon", text);
            Assert.DoesNotContain("{", text);
            Assert.DoesNotContain("}", text);
        });
    }

    [Fact]
    public void The_path_loss_class_is_always_given_in_words_so_colour_is_never_the_only_cue()
    {
        var l = new ResxLocalizer(Load("Strings.resx"), CultureInfo.GetCultureInfo("en-GB"));
        var frame = Frame(new DateTime(2026, 3, 20, 12, 30, 0, DateTimeKind.Utc));
        Assert.Equal(PathLossClass.Good, frame.LossClass);
        Assert.Contains("good", EmeTextBuilder.AccessibleSummary(l, frame));
        var extraLossRow = EmeTextBuilder.Sections(l, frame, t => t.ToString("O", CultureInfo.InvariantCulture))
            .SelectMany(sec => sec.Rows).Single(r => r.Label == "Extra path loss");
        Assert.EndsWith("good", extraLossRow.Value);
        Assert.Equal(["good", "fair", "poor", "bad"],
            Enum.GetValues<PathLossClass>().Select(c => EmeTextBuilder.ClassWord(l, c)).ToList());
    }

    [Fact]
    public void The_new_moon_note_and_the_sun_flag_appear_when_they_apply_and_not_otherwise()
    {
        var l = new ResxLocalizer(Load("Strings.resx"), CultureInfo.GetCultureInfo("en-GB"));
        string Time(DateTime t) => t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var rows = (EmeFrame f) => EmeTextBuilder.Sections(l, f, Time).SelectMany(s => s.Rows).Select(r => r.Value).ToList();

        // Search for a day when the Moon is within 10 degrees of the Sun, and one when it is far from it.
        var days = Enumerable.Range(0, 30).Select(d => new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(d)).ToList();
        var close = days.First(d => Frame(d).Sky!.SunTooClose);
        var far = days.First(d => !Frame(d).Sky!.SunTooClose);
        Assert.Contains(rows(Frame(close)), v => v.Contains("Too close to the Sun"));
        Assert.DoesNotContain(rows(Frame(far)), v => v.Contains("Too close to the Sun"));
        // The new Moon note is shown for a new Moon and for no other phase.
        Assert.All(days, d =>
        {
            var f = Frame(d);
            Assert.Equal(f.Sky!.Phase == MoonPhaseName.New, rows(f).Any(v => v.StartsWith("New Moon:", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public void The_free_space_note_is_always_shown_with_the_echo_figures()
    {
        var l = new ResxLocalizer(Load("Strings.resx"), CultureInfo.GetCultureInfo("en-GB"));
        var f = Frame(new DateTime(2026, 3, 20, 12, 30, 0, DateTimeKind.Utc));
        var echo = EmeTextBuilder.Sections(l, f, t => t.ToString("O", CultureInfo.InvariantCulture))[1];
        Assert.Contains(echo.Rows, r => r.Value.Contains("not a link budget"));
    }
}
