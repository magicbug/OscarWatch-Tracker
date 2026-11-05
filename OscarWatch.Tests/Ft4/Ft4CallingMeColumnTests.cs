using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4CallingMeColumnTests
{
    private static Ft4DecodedMessage Line(
        string text,
        string? callTo,
        string? callDe,
        bool echo = false,
        bool transmitted = false,
        bool rejected = false) =>
        new(DateTime.UtcNow, text, 1500, 0.2f, -8, callTo, callDe, null, echo, transmitted, rejected, rejected);

    [Fact]
    public void Calls_to_me_are_included()
    {
        Assert.True(Ft4CallingMeColumn.Includes(Line("MM9SQL G4ABC IO91", "MM9SQL", "G4ABC"), "mm9sql"));
        Assert.True(Ft4CallingMeColumn.Includes(Line("MM9SQL G4ABC RR73", "MM9SQL", "G4ABC"), "MM9SQL"));
    }

    [Fact]
    public void Own_transmissions_are_included_even_without_a_callsign()
    {
        var tx = Line("CQ MM9SQL IO87", "CQ", "MM9SQL", transmitted: true);
        Assert.True(Ft4CallingMeColumn.Includes(tx, "MM9SQL"));
        Assert.True(Ft4CallingMeColumn.Includes(tx, null));
    }

    [Fact]
    public void Own_echoes_are_included()
    {
        var echo = Line("CQ MM9SQL IO87", "CQ", "MM9SQL", echo: true);
        Assert.True(Ft4CallingMeColumn.Includes(echo, "MM9SQL"));
        Assert.True(Ft4CallingMeColumn.Includes(echo, null));
    }

    [Fact]
    public void Other_traffic_and_rejected_hints_are_left_out()
    {
        Assert.False(Ft4CallingMeColumn.Includes(Line("CQ PD1B JO32", "CQ", "PD1B"), "MM9SQL"));
        Assert.False(Ft4CallingMeColumn.Includes(Line("G4ABC PD1B -10", "G4ABC", "PD1B"), "MM9SQL"));
        Assert.False(Ft4CallingMeColumn.Includes(Line("MM9SQL G4ABC -12", "MM9SQL", "G4ABC", rejected: true), "MM9SQL"));
    }

    [Fact]
    public void No_callsign_shows_no_received_lines()
    {
        Assert.False(Ft4CallingMeColumn.Includes(Line("MM9SQL G4ABC IO91", "MM9SQL", "G4ABC"), ""));
    }
}
