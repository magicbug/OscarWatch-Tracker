using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4DecodeHighlightTests
{
    private static Ft4DecodedMessage Line(
        string text,
        string? callTo,
        string? callDe,
        bool echo = false,
        bool transmitted = false) =>
        new(DateTime.UtcNow, text, 1500, 0.2f, -8, callTo, callDe, null, echo, transmitted);

    [Fact]
    public void Directed_decode_is_calling_me_until_they_are_the_qso_partner()
    {
        var msg = Line("MM9SQL G4ABC IO91", "MM9SQL", "G4ABC");
        Assert.Equal(Ft4DecodeHighlightKind.CallingMe, Ft4DecodeHighlight.Classify(msg, "MM9SQL", null));
        Assert.Equal(Ft4DecodeHighlightKind.Replying, Ft4DecodeHighlight.Classify(msg, "mm9sql", "G4ABC"));
    }

    [Fact]
    public void Other_callers_stay_calling_me_during_a_qso()
    {
        var msg = Line("MM9SQL M0ABC +05", "MM9SQL", "M0ABC");
        Assert.Equal(Ft4DecodeHighlightKind.CallingMe, Ft4DecodeHighlight.Classify(msg, "MM9SQL", "G4ABC"));
    }

    [Fact]
    public void Cq_tx_and_echo_are_not_highlighted()
    {
        Assert.Equal(
            Ft4DecodeHighlightKind.None,
            Ft4DecodeHighlight.Classify(Line("CQ G4ABC JO01", "CQ", "G4ABC"), "MM9SQL", null));
        Assert.Equal(
            Ft4DecodeHighlightKind.None,
            Ft4DecodeHighlight.Classify(Line("CQ MM9SQL IO87", "CQ", "MM9SQL", transmitted: true), "MM9SQL", null));
        Assert.Equal(
            Ft4DecodeHighlightKind.None,
            Ft4DecodeHighlight.Classify(Line("G4ABC MM9SQL +10", "G4ABC", "MM9SQL", echo: true), "MM9SQL", "G4ABC"));
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
}
