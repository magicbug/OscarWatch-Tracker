using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4MessageCodecTests
{
    [Theory]
    [InlineData("CQ MM9SQL IO85", "CQ", "MM9SQL", "IO85")]
    [InlineData("G4ABC MM9SQL IO85", "G4ABC", "MM9SQL", "IO85")]
    [InlineData("G4ABC MM9SQL +05", "G4ABC", "MM9SQL", "+05")]
    [InlineData("G4ABC MM9SQL RR73", "G4ABC", "MM9SQL", "RR73")]
    [InlineData("G4ABC MM9SQL 73", "G4ABC", "MM9SQL", "73")]
    public void TryParse_standard_messages(string text, string to, string de, string extra)
    {
        Assert.True(Ft4MessageCodec.TryParse(text, out var callTo, out var callDe, out var parsedExtra));
        Assert.Equal(to, callTo);
        Assert.Equal(de, callDe);
        Assert.Equal(extra, parsedExtra);
    }

    [Fact]
    public void Report_and_closing_helpers()
    {
        Assert.True(Ft4MessageCodec.IsReport("+05"));
        Assert.True(Ft4MessageCodec.IsReport("-12"));
        Assert.True(Ft4MessageCodec.IsReport("R+05"));
        Assert.True(Ft4MessageCodec.IsRogerReport("R+05"));
        Assert.False(Ft4MessageCodec.IsRogerReport("+05"));
        Assert.Equal("R+05", Ft4MessageCodec.FormatRogerReport(5.2f));
        Assert.True(Ft4MessageCodec.IsGrid("IO85"));
        Assert.True(Ft4MessageCodec.IsRr73("RR73"));
        Assert.True(Ft4MessageCodec.IsRrr("RRR"));
        Assert.True(Ft4MessageCodec.Is73("73"));
        Assert.False(Ft4MessageCodec.IsReport("IO85"));
    }

    [Fact]
    public void FormatSnrReport_clamps_and_signs()
    {
        Assert.Equal("+05", Ft4MessageCodec.FormatSnrReport(5.2f));
        Assert.Equal("-12", Ft4MessageCodec.FormatSnrReport(-12.4f));
        Assert.Equal("+00", Ft4MessageCodec.FormatSnrReport(0.2f));
    }

    [Fact]
    public void NormalizeSnrReport_strips_roger_prefix_for_the_log()
    {
        Assert.Equal("+14", Ft4MessageCodec.NormalizeSnrReport("R+14"));
        Assert.Equal("-08", Ft4MessageCodec.NormalizeSnrReport("R-08"));
        Assert.Equal("+05", Ft4MessageCodec.NormalizeSnrReport("+05"));
        Assert.Equal("-12", Ft4MessageCodec.NormalizeSnrReport("-12"));
        Assert.Equal("", Ft4MessageCodec.NormalizeSnrReport(null));
    }

    [Fact]
    public void Builders_use_upper_case()
    {
        Assert.Equal("CQ MM9SQL IO85", Ft4MessageCodec.BuildCq("mm9sql", "io85"));
        Assert.Equal("G4ABC MM9SQL IO85", Ft4MessageCodec.BuildGridReply("g4abc", "mm9sql", "io85"));
        Assert.Equal("G4ABC MM9SQL +03", Ft4MessageCodec.BuildReport("g4abc", "mm9sql", "+03"));
        Assert.Equal("G4ABC MM9SQL RR73", Ft4MessageCodec.BuildRr73("g4abc", "mm9sql"));
        Assert.Equal("G4ABC MM9SQL RRR", Ft4MessageCodec.BuildRrr("g4abc", "mm9sql"));
        Assert.Equal("G4ABC MM9SQL 73", Ft4MessageCodec.Build73("g4abc", "mm9sql"));
    }

    [Fact]
    public void Hashed_display_form_is_read_as_the_bare_call()
    {
        Assert.Equal("R0CM/4", Ft4MessageCodec.NormalizeCall("<R0CM/4>"));
        Assert.Equal("", Ft4MessageCodec.NormalizeCall("<...>"));
        Assert.True(Ft4MessageCodec.TryParse("MM9SQL <R0CM/4> -14", out var to, out var de, out var extra));
        Assert.Equal("MM9SQL", to);
        Assert.Equal("R0CM/4", de);
        Assert.Equal("-14", extra);
    }

    [Fact]
    public void Portable_callsign_MM9SQL_M_is_preserved()
    {
        Assert.Equal("MM9SQL/M", Ft4MessageCodec.NormalizeCall("mm9sql/m"));
        Assert.Equal("MM9SQL/M", Ft4MessageCodec.NormalizeCall("mm9sql\u2215m"));
        Assert.True(Ft4MessageCodec.TryParse("CQ MM9SQL/M IO85", out var to, out var de, out var extra));
        Assert.Equal("CQ", to);
        Assert.Equal("MM9SQL/M", de);
        Assert.Equal("IO85", extra);
    }

    [Theory]
    [InlineData("MM9SQL", false)]
    [InlineData("G4ABC", false)]
    [InlineData("K1A", false)]
    [InlineData("G4ABC/P", false)]
    [InlineData("G4ABC/R", false)]
    [InlineData("3DA0ABC", false)]
    [InlineData("MM9SQL/M", true)]
    [InlineData("PA/DL1ABC", true)]
    [InlineData("PA/DL1ABC/P", true)]
    [InlineData("3DA0ABCD", true)]
    [InlineData("<R0CM/4>", true)]
    [InlineData("CQ", false)]
    public void Hashed_call_matches_the_ft8_lib_standard_form(string call, bool hashed) =>
        Assert.Equal(hashed, Ft4MessageCodec.IsHashedCall(call));

    [Fact]
    public void Own_compound_call_uses_the_wsjtx_message_forms()
    {
        Assert.Equal("CQ MM9SQL/M", Ft4MessageCodec.BuildCq("MM9SQL/M", "IO85"));
        Assert.Equal("G4ABC MM9SQL/M", Ft4MessageCodec.BuildGridReply("G4ABC", "MM9SQL/M", "IO85"));
        Assert.Equal("G4ABC <MM9SQL/M> R-05", Ft4MessageCodec.BuildReport("G4ABC", "MM9SQL/M", "R-05"));
        Assert.Equal("G4ABC MM9SQL/M RR73", Ft4MessageCodec.BuildRr73("G4ABC", "MM9SQL/M"));
    }

    [Theory]
    [InlineData("SM/R1CR KP04", "CQ", "SM/R1CR", "KP04")]
    [InlineData("sm/r1cr kp04", "CQ", "SM/R1CR", "KP04")]
    [InlineData("<SM/R1CR> KP04", "CQ", "SM/R1CR", "KP04")]
    [InlineData("G4ABC JO01", "CQ", "G4ABC", "JO01")]
    [InlineData("G4ABC/P IO91", "CQ", "G4ABC/P", "IO91")]
    public void TryParse_call_and_grid_without_cq_is_that_station(string text, string to, string de, string extra)
    {
        Assert.True(Ft4MessageCodec.TryParse(text, out var callTo, out var callDe, out var parsedExtra));
        Assert.Equal(to, callTo);
        Assert.Equal(de, callDe);
        Assert.Equal(extra, parsedExtra);
    }

    [Theory]
    [InlineData("G4ABC MM9SQL", "G4ABC", "MM9SQL")]
    [InlineData("G4ABC RR73", "G4ABC", "RR73")]
    [InlineData("KP04 IO87", "KP04", "IO87")]
    [InlineData("HELLO KP04", "HELLO", "KP04")]
    public void TryParse_two_tokens_that_are_not_a_grid_announcement(string text, string to, string de)
    {
        Assert.True(Ft4MessageCodec.TryParse(text, out var callTo, out var callDe, out var extra));
        Assert.Equal(to, callTo);
        Assert.Equal(de, callDe);
        Assert.Null(extra);
    }

    [Theory]
    [InlineData(null, "CQ MM9SQL IO85")]
    [InlineData("", "CQ MM9SQL IO85")]
    [InlineData("POTA", "CQ POTA MM9SQL IO85")]
    [InlineData("dx", "CQ DX MM9SQL IO85")]
    [InlineData("CQ sota", "CQ SOTA MM9SQL IO85")]
    [InlineData("290", "CQ 290 MM9SQL IO85")]
    [InlineData("POTA1", "CQ MM9SQL IO85")]
    [InlineData("12", "CQ MM9SQL IO85")]
    public void BuildCq_puts_a_valid_word_between_cq_and_the_call(string? modifier, string expected) =>
        Assert.Equal(expected, Ft4MessageCodec.BuildCq("mm9sql", "io85", modifier));

    [Fact]
    public void BuildCq_with_a_compound_call_has_no_room_for_the_word()
    {
        Assert.Equal("CQ MM9SQL/M", Ft4MessageCodec.BuildCq("MM9SQL/M", "IO85", "POTA"));
    }

    [Theory]
    [InlineData("POTA", true)]
    [InlineData("DX", true)]
    [InlineData("A", true)]
    [InlineData("290", true)]
    [InlineData("", false)]
    [InlineData("WWFFX", false)]
    [InlineData("POTA1", false)]
    [InlineData("DX/", false)]
    [InlineData("12", false)]
    [InlineData("1234", false)]
    public void IsCqModifier_follows_the_77_bit_rule(string word, bool valid) =>
        Assert.Equal(valid, Ft4MessageCodec.IsCqModifier(word));

    [Theory]
    [InlineData("CQ POTA MM9SQL IO85", "MM9SQL", "IO85")]
    [InlineData("CQ DX W1AW FN20", "W1AW", "FN20")]
    [InlineData("CQ 290 K1ABC FN42", "K1ABC", "FN42")]
    [InlineData("CQ POTA W1AW", "W1AW", null)]
    [InlineData("CQ MM9SQL IO85", "MM9SQL", "IO85")]
    [InlineData("CQ W1AW", "W1AW", null)]
    public void TryParse_directed_cq_is_a_cq_from_the_call_after_the_word(string text, string de, string? grid)
    {
        Assert.True(Ft4MessageCodec.TryParse(text, out var callTo, out var callDe, out var extra));
        Assert.Equal("CQ", callTo);
        Assert.Equal(de, callDe);
        Assert.Equal(grid, extra);
    }

    [Fact]
    public void Their_compound_call_is_bracketed_when_ours_is_standard()
    {
        Assert.Equal("<PA/DL1ABC> MM9SQL IO85", Ft4MessageCodec.BuildGridReply("PA/DL1ABC", "MM9SQL", "IO85"));
        Assert.Equal("<PA/DL1ABC> MM9SQL -05", Ft4MessageCodec.BuildReport("PA/DL1ABC", "MM9SQL", "-05"));
        Assert.Equal("<PA/DL1ABC> MM9SQL 73", Ft4MessageCodec.Build73("PA/DL1ABC", "MM9SQL"));
    }
}
