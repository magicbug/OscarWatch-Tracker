using OscarWatch.Core.CwDecode;
using OscarWatch.Cw;
using Xunit;

namespace OscarWatch.Tests.CwDecode;

public sealed class CwCopyLogTests
{
    private static readonly DateTime T0 = new(2026, 11, 12, 14, 0, 0, DateTimeKind.Utc);

    private static CwCopyLog NewLog() => new((text, token) => token.Kind is CwTokenKind.Callsign or CwTokenKind.Locator
        ? new CwCopyLink(text, token.Kind, token.Value, token.Value)
        : new CwCopyWord(text))
    {
        OwnCallsign = "MM9SQL",
        OwnLocator = "IO85MW",
    };

    [Fact]
    public void WordSettledInTwoPiecesIsJoinedAndLinked()
    {
        var log = NewLog();

        log.Append("CQ DE G4", T0);
        log.Append("ABC", T0.AddSeconds(1));
        log.Append(" IO91", T0.AddSeconds(2));

        var line = Assert.Single(log.Lines);
        Assert.Equal("CQ DE G4ABC IO91", line.Text);
        var call = Assert.IsType<CwCopyLink>(line.Words[2]);
        Assert.Equal("G4ABC", call.Value);
        Assert.True(call.IsCallsign);
        var grid = Assert.IsType<CwCopyLink>(line.Words[3]);
        Assert.Equal(CwTokenKind.Locator, grid.Kind);
    }

    [Fact]
    public void OwnCallAndReportStayPlainText()
    {
        var log = NewLog();

        log.Append("G4ABC DE MM9SQL 599 IO85 K", T0);

        var words = log.Lines[0].Words;
        Assert.IsType<CwCopyLink>(words[0]);
        Assert.IsNotType<CwCopyLink>(words[2]);
        Assert.IsNotType<CwCopyLink>(words[3]);
        Assert.IsNotType<CwCopyLink>(words[4]);
    }

    [Fact]
    public void PauseStartsNewStampedLine()
    {
        var log = NewLog();

        log.Append("CQ CQ", T0);
        log.Append("DE G4ABC", T0.AddSeconds(30));

        Assert.Equal(2, log.Lines.Count);
        Assert.Equal("14:00:30", log.Lines[1].Stamp);
        Assert.Equal("DE G4ABC", log.Lines[1].Text);
    }

    [Fact]
    public void LongOverWrapsAtAWord()
    {
        var log = NewLog();

        log.Append(string.Join(' ', Enumerable.Repeat("CQ DE G4ABC", 12)), T0);

        Assert.True(log.Lines.Count > 1);
        Assert.All(log.Lines, l => Assert.True(l.Length <= CwCopyLog.MaxLineLength));
        Assert.Equal(36, log.Lines.Sum(l => l.Words.Count));
    }

    [Fact]
    public void ToTextWritesStampedLines()
    {
        var log = NewLog();
        log.Append("CQ DE G4ABC", T0);

        Assert.Equal("14:00:00  CQ DE G4ABC" + Environment.NewLine, log.ToText());

        log.Clear();
        Assert.True(log.IsEmpty);
        Assert.Equal("", log.ToText());
    }

    [Theory]
    [InlineData("CW")]
    [InlineData("cw")]
    [InlineData("USB")]
    public void FeedForwardTreatsCwAsUpperSideband(string mode)
    {
        Assert.Equal(150, CwDopplerFeedForward.AudioOffsetHz(mode, 145_800.150, 145_800_000), 3);
    }

    [Theory]
    [InlineData("CW-R")]
    [InlineData("LSB")]
    public void FeedForwardTreatsCwReverseAsLowerSideband(string mode)
    {
        Assert.Equal(-150, CwDopplerFeedForward.AudioOffsetHz(mode, 145_800.150, 145_800_000), 3);
    }

    [Fact]
    public void FeedForwardIgnoresFm()
    {
        Assert.Equal(0, CwDopplerFeedForward.AudioOffsetHz("FM", 145_800.150, 145_800_000));
    }
}
