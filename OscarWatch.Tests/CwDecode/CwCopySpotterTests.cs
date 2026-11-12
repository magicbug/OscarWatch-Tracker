using OscarWatch.Core.CwDecode;
using Xunit;

namespace OscarWatch.Tests.CwDecode;

public sealed class CwCopySpotterTests
{
    [Theory]
    [InlineData("MM9SQL", "MM9SQL")]
    [InlineData("G4ABC", "G4ABC")]
    [InlineData("MM9SQL/P", "MM9SQL/P")]
    [InlineData("EA8/G4ABC", "EA8/G4ABC")]
    [InlineData("G4ABC,", "G4ABC")]
    public void CallsignsAreSpotted(string word, string expected)
    {
        var kind = CwCopySpotter.Classify(word, null, null, out var value);

        Assert.Equal(CwTokenKind.Callsign, kind);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("599")]
    [InlineData("5NN")]
    [InlineData("CQ")]
    [InlineData("DE")]
    [InlineData("TU")]
    [InlineData("73")]
    [InlineData("RR73")]
    [InlineData("K")]
    [InlineData("")]
    public void ExchangeWordsAreText(string word)
    {
        Assert.Equal(CwTokenKind.Text, CwCopySpotter.Classify(word, null, null, out _));
    }

    [Theory]
    [InlineData("IO85", "IO85")]
    [InlineData("IO85MW", "IO85MW")]
    [InlineData("io85.", "IO85")]
    [InlineData("JO62", "JO62")]
    public void LocatorsAreSpotted(string word, string expected)
    {
        var kind = CwCopySpotter.Classify(word, null, null, out var value);

        Assert.Equal(CwTokenKind.Locator, kind);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void OwnCallAndLocatorAreNotLinked()
    {
        Assert.Equal(CwTokenKind.OwnCallsign, CwCopySpotter.Classify("MM9SQL/P", "MM9SQL", "IO85MW", out _));
        Assert.Equal(CwTokenKind.Text, CwCopySpotter.Classify("IO85", "MM9SQL", "IO85MW", out _));
        Assert.Equal(CwTokenKind.Callsign, CwCopySpotter.Classify("G4ABC", "MM9SQL", "IO85MW", out _));
    }

    [Fact]
    public void TokenizeKeepsEveryWordInOrder()
    {
        var tokens = CwCopySpotter.Tokenize("CQ CQ DE G4ABC IO91 K", "MM9SQL", "IO85");

        Assert.Equal(["CQ", "CQ", "DE", "G4ABC", "IO91", "K"], tokens.Select(t => t.Text));
        Assert.Equal(CwTokenKind.Callsign, tokens[3].Kind);
        Assert.Equal(CwTokenKind.Locator, tokens[4].Kind);
    }
}
