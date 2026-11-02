using Avalonia.Media;
using Avalonia.Styling;
using OscarWatch.Controls;

namespace OscarWatch.Tests;

/// <summary>
/// Contrast checks for the Earth View colours (WCAG 2.1 non-text contrast, 3:1), following
/// .cursor/rules/accessibility.mdc. The world map texture is the same in both themes and runs from
/// dark ocean to bright ice, so each line is drawn as a bright core over a dark outline.
/// </summary>
public sealed class EarthViewPaletteTests
{
    private const double RequiredContrast = 3.0;

    public static TheoryData<string> Themes => new() { "Light", "Dark" };

    private static UiPalette PaletteFor(string theme) =>
        UiPaletteResolver.For(theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(double luminanceA, double luminanceB)
    {
        var hi = Math.Max(luminanceA, luminanceB);
        var lo = Math.Min(luminanceA, luminanceB);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>The line is readable over a surface when its core or its outline reaches 3:1.</summary>
    private static bool Readable(Color core, Color outline, double surfaceLuminance) =>
        Math.Max(Contrast(Luminance(core), surfaceLuminance), Contrast(Luminance(outline), surfaceLuminance))
            >= RequiredContrast;

    [Theory]
    [MemberData(nameof(Themes))]
    public void Limb_is_readable_over_any_surface_luminance(string theme)
    {
        var p = PaletteFor(theme);

        // Every luminance from black to white, so this does not depend on how the map was sampled.
        for (var l = 0.0; l <= 1.0; l += 0.005)
            Assert.True(Readable(p.GlobeLimbStroke, p.MapGroundTrackOutline, l), $"{theme} limb unreadable at luminance {l:F3}");
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Inner_ring_is_readable_over_any_surface_luminance(string theme)
    {
        var p = PaletteFor(theme);

        for (var l = 0.0; l <= 1.0; l += 0.005)
            Assert.True(Readable(p.GlobeRingStroke, p.MapGroundTrackOutline, l), $"{theme} ring unreadable at luminance {l:F3}");
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Ground_track_is_readable_over_any_surface_luminance(string theme)
    {
        var p = PaletteFor(theme);

        for (var l = 0.0; l <= 1.0; l += 0.005)
            Assert.True(Readable(p.MapGroundTrackStroke, p.MapGroundTrackOutline, l), $"{theme} track unreadable at luminance {l:F3}");
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Lines_are_readable_against_the_background_behind_the_globe(string theme)
    {
        var p = PaletteFor(theme);
        var background = Luminance(p.GlobeBackground);

        Assert.True(Readable(p.GlobeLimbStroke, p.MapGroundTrackOutline, background), $"{theme} limb vs background");
        Assert.True(Readable(p.GlobeRingStroke, p.MapGroundTrackOutline, background), $"{theme} ring vs background");
        Assert.True(Readable(p.MapGroundTrackStroke, p.MapGroundTrackOutline, background), $"{theme} track vs background");
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Label_text_is_readable_on_the_label_background(string theme)
    {
        // Station and satellite labels reuse the map label colours. Text needs 4.5:1.
        var p = PaletteFor(theme);

        Assert.True(
            Contrast(Luminance(p.MapLabelForeground), Luminance(p.MapLabelBackground)) >= 4.5,
            $"{theme} label text contrast");
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Globe_colours_are_set_and_opaque(string theme)
    {
        var p = PaletteFor(theme);

        foreach (var colour in new[] { p.GlobeBackground, p.GlobeLimbStroke, p.GlobeRingStroke, p.GlobeSeaFallback })
            Assert.Equal(255, colour.A);

        Assert.NotEqual(default, p.GlobeBackground);
    }

    [Fact]
    public void Background_follows_the_theme()
    {
        var light = PaletteFor("Light");
        var dark = PaletteFor("Dark");

        Assert.True(Luminance(light.GlobeBackground) > Luminance(dark.GlobeBackground));
    }

    [Fact]
    public void Limb_and_ring_differ_in_colour_as_well_as_style()
    {
        // Style (solid or dashed, width) is the real distinction. A different colour helps sighted users.
        var p = PaletteFor("Light");

        Assert.NotEqual(p.GlobeLimbStroke, p.GlobeRingStroke);
    }

    [Fact]
    public void Unknown_theme_uses_the_light_palette()
    {
        Assert.Equal(PaletteFor("Light"), UiPaletteResolver.For(null));
        Assert.Equal(PaletteFor("Light"), UiPaletteResolver.For(ThemeVariant.Default));
    }

    [Fact]
    public void Sea_fallback_converts_to_opaque_bgra()
    {
        var palette = GlobePalette.From(PaletteFor("Light"));
        var c = PaletteFor("Light").GlobeSeaFallback;

        var expected = 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        Assert.Equal(expected, palette.SeaBgra);
    }

    [Fact]
    public void Colour_to_bgra_puts_blue_in_the_lowest_byte()
    {
        Assert.Equal(0x80112233u, GlobePalette.ToBgra(Color.FromArgb(0x80, 0x11, 0x22, 0x33)));
    }
}
