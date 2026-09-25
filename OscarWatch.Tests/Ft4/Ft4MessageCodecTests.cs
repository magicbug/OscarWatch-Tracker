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
        Assert.Equal("CQ MM9SQL/M IO85", Ft4MessageCodec.BuildCq("MM9SQL/M", "IO85"));
        Assert.True(Ft4MessageCodec.TryParse("CQ MM9SQL/M IO85", out var to, out var de, out var extra));
        Assert.Equal("CQ", to);
        Assert.Equal("MM9SQL/M", de);
        Assert.Equal("IO85", extra);
    }
}
