using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4DecodeHighlightTests
{
    private static Ft4DecodedMessage Line(
        string text,
        string? callTo,
        string? callDe,
        string? extra = null,
        bool echo = false,
        bool transmitted = false) =>
        new(DateTime.UtcNow, text, 1500, 0.2f, -8, callTo, callDe, extra, echo, transmitted);

    private static HashSet<string> Calls(params string[] calls) =>
        new(calls, StringComparer.Ordinal);

    private static HashSet<string> Grids(params string[] grids) =>
        new(grids, StringComparer.Ordinal);

    [Fact]
    public void Directed_decode_is_calling_me_until_they_are_the_qso_partner()
    {
        var msg = Line("MM9SQL G4ABC IO91", "MM9SQL", "G4ABC", "IO91");
        Assert.Equal(Ft4DecodeHighlightKind.CallingMe, Ft4DecodeHighlight.Classify(msg, "MM9SQL", null));
        Assert.Equal(Ft4DecodeHighlightKind.Replying, Ft4DecodeHighlight.Classify(msg, "mm9sql", "G4ABC"));
    }

    [Fact]
    public void Other_callers_stay_calling_me_during_a_qso()
    {
        var msg = Line("MM9SQL M0ABC +05", "MM9SQL", "M0ABC", "+05");
        Assert.Equal(Ft4DecodeHighlightKind.CallingMe, Ft4DecodeHighlight.Classify(msg, "MM9SQL", "G4ABC"));
    }

    [Fact]
    public void Tx_and_echo_are_not_highlighted()
    {
        Assert.Equal(
            Ft4DecodeHighlightKind.None,
            Ft4DecodeHighlight.Classify(
                Line("CQ MM9SQL IO87", "CQ", "MM9SQL", "IO87", transmitted: true),
                "MM9SQL",
                null));
        Assert.Equal(
            Ft4DecodeHighlightKind.None,
            Ft4DecodeHighlight.Classify(
                Line("G4ABC MM9SQL +10", "G4ABC", "MM9SQL", "+10", echo: true),
                "MM9SQL",
                "G4ABC"));
    }

    [Fact]
    public void Unworked_callsign_is_new_call()
    {
        var msg = Line("CQ G4ABC JO01", "CQ", "G4ABC", "JO01");
        Assert.Equal(
            Ft4DecodeHighlightKind.NewCall,
            Ft4DecodeHighlight.Classify(msg, "MM9SQL", null, Calls(), Grids()));
        Assert.Equal(
            Ft4DecodeHighlightKind.None,
            Ft4DecodeHighlight.Classify(msg, "MM9SQL", null, Calls("G4ABC"), Grids("JO01")));
    }

    [Fact]
    public void Worked_call_with_new_grid_is_new_grid()
    {
        var msg = Line("CQ G4ABC JO01", "CQ", "G4ABC", "JO01");
        Assert.Equal(
            Ft4DecodeHighlightKind.NewGrid,
            Ft4DecodeHighlight.Classify(msg, "MM9SQL", null, Calls("G4ABC"), Grids("IO91")));
    }

    [Fact]
    public void Calling_me_outranks_new_call()
    {
        var msg = Line("MM9SQL G4ABC IO91", "MM9SQL", "G4ABC", "IO91");
        Assert.Equal(
            Ft4DecodeHighlightKind.CallingMe,
            Ft4DecodeHighlight.Classify(msg, "MM9SQL", null, Calls(), Grids()));
    }

    [Fact]
    public void Colour_text_normalises_to_eight_digits()
    {
        Assert.Equal("#FFE6B15A", Ft4DecodeHighlight.NormalizeColour("#e6b15a"));
        Assert.Equal("#66E6B15A", Ft4DecodeHighlight.NormalizeColour("66e6b15a"));
        Assert.Equal("#FFFF8800", Ft4DecodeHighlight.NormalizeColour("#f80"));
        Assert.Null(Ft4DecodeHighlight.NormalizeColour("orange"));
        Assert.Null(Ft4DecodeHighlight.NormalizeColour("#12"));
    }

    [Fact]
    public void Grid_field_uses_four_characters()
    {
        Assert.Equal("JO01", Ft4DecodeHighlight.GridField("JO01"));
        Assert.Equal("IO91", Ft4DecodeHighlight.GridField("IO91WM"));
        Assert.Null(Ft4DecodeHighlight.GridField("+10"));
        Assert.Null(Ft4DecodeHighlight.GridField("RR73"));
    }
}
