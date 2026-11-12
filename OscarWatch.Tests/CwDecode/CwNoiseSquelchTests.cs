using OscarWatch.Core.CwDecode;
using Xunit;

namespace OscarWatch.Tests.CwDecode;

public sealed class CwNoiseSquelchTests
{
    private static float[] Scores(string text, float letters) =>
        text.Select(c => c == ' ' ? 1f : letters).ToArray();

    [Fact]
    public void ConfidentCopyPassesStraightThrough()
    {
        var squelch = new CwNoiseSquelch();

        Assert.Equal("CQ CQ DE", squelch.Process("CQ CQ DE", Scores("CQ CQ DE", 0.98f)));
        Assert.Equal(" MM9SQL K", squelch.Process(" MM9SQL K", Scores(" MM9SQL K", 0.95f)));
    }

    [Fact]
    public void LoneUnsureLetterBetweenWordsIsDropped()
    {
        var squelch = new CwNoiseSquelch();

        Assert.Equal("CQ DE", squelch.Process("CQ S DE", [1, 1, 1, 0.3f, 1, 1, 1]));
    }

    [Fact]
    public void UnsureLetterOnItsOwnIsHeldThenDropped()
    {
        var squelch = new CwNoiseSquelch();

        Assert.Equal("", squelch.Process("S", [0.6f]));
        Assert.Equal(" CQ", squelch.Process(" CQ", Scores(" CQ", 1f)));
    }

    [Fact]
    public void HeldLettersThatCarryOnIntoAWordAreKept()
    {
        var squelch = new CwNoiseSquelch();

        Assert.Equal("DE", squelch.Process("DE M", [1, 1, 1, 0.5f]).TrimEnd());
        Assert.Equal(" M9SQL", squelch.Process("9SQL", Scores("9SQL", 0.9f)));
    }

    [Fact]
    public void ConfidentShortWordsStay()
    {
        var squelch = new CwNoiseSquelch();

        Assert.Equal(" K", squelch.Process(" K", [1, 0.9f]));
    }

    [Fact]
    public void TheEndOfAWordAlreadyShownIsNeverDropped()
    {
        var squelch = new CwNoiseSquelch();

        Assert.Equal("MM9SQ", squelch.Process("MM9SQ", Scores("MM9SQ", 1f)));
        Assert.Equal("L", squelch.Process("L", [0.4f]));
    }

    [Fact]
    public void FlushDropsAHeldWord()
    {
        var squelch = new CwNoiseSquelch();

        Assert.Equal("", squelch.Process("E", [0.2f]));
        squelch.Flush();
        Assert.Equal("TEST", squelch.Process("TEST", Scores("TEST", 1f)));
    }

    [Fact]
    public void PendingNoiseIsRecognised()
    {
        Assert.True(CwNoiseSquelch.IsLikelyNoise(new CwLaneText("S", true, [0.4f])));
        Assert.False(CwNoiseSquelch.IsLikelyNoise(new CwLaneText("S", true, [0.9f])));
        Assert.False(CwNoiseSquelch.IsLikelyNoise(new CwLaneText("CQ DE", false, [0.3f, 0.3f, 1, 0.3f, 0.3f])));
    }
}
