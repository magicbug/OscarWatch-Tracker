using OscarWatch.Core.CwDecode;
using Xunit;

namespace OscarWatch.Tests.CwDecode;

public sealed class CwPosteriorStitcherTests
{
    private static readonly CwModelMetadata Model = CwModelMetadata.DeepCw();

    /// <summary>Logits that are blank everywhere except the given characters, each held for three frames.</summary>
    private static float[] Logits(int frames, params (int Frame, char Ch)[] marks)
    {
        var classes = Model.NumClasses;
        var logits = new float[frames * classes];
        for (var t = 0; t < frames; t++)
            logits[t * classes + Model.BlankIndex] = 10f;

        foreach (var (frame, ch) in marks)
        {
            var c = Model.Chars.ToList().IndexOf(ch.ToString());
            for (var t = frame; t < Math.Min(frames, frame + 3); t++)
            {
                logits[t * classes + Model.BlankIndex] = 0f;
                logits[t * classes + c] = 10f;
            }
        }

        return logits;
    }

    [Fact]
    public void RecentLettersStayPendingUntilFinal()
    {
        var stitcher = new CwPosteriorStitcher(Model);
        var frames = 200;
        stitcher.AddWindow(0, Logits(frames, (20, 'C'), (40, 'Q'), (190, 'K')), frames, isFinal: false);

        var (committed, pending) = stitcher.Take();

        Assert.Equal("CQ", committed?.Text);
        Assert.Equal("K", pending.Text.Trim());

        var (rest, tail) = stitcher.Take(final: true);
        Assert.Equal("K", rest?.Text);
        Assert.Equal("", tail.Text);
    }

    [Fact]
    public void LongBlankRunBecomesWordGap()
    {
        var stitcher = new CwPosteriorStitcher(Model);
        var frames = 300;
        stitcher.AddWindow(0, Logits(frames, (10, 'D'), (20, 'E'), (120, 'K')), frames, isFinal: true);

        var (committed, _) = stitcher.Take(final: true);

        Assert.Equal("DE K", committed?.Text);
    }

    [Fact]
    public void OverlappingWindowsDoNotRepeatText()
    {
        var stitcher = new CwPosteriorStitcher(Model);
        var text = new List<string>();

        stitcher.AddWindow(0, Logits(150, (20, 'A'), (40, 'B'), (100, 'C')), 150, isFinal: false);
        if (stitcher.Take().Committed is { } first)
            text.Add(first.Text);

        var start = stitcher.ContextStartFrame;
        var marks = new[] { (20, 'A'), (40, 'B'), (100, 'C'), (200, 'D') }
            .Where(m => m.Item1 >= start)
            .Select(m => (m.Item1 - start, m.Item2))
            .ToArray();
        stitcher.AddWindow(start, Logits(250 - start, marks), 250 - start, isFinal: true);
        if (stitcher.Take(final: true).Committed is { } second)
            text.Add(second.Text);

        Assert.Equal("ABCD", string.Concat(text).Replace(" ", ""));
    }
}
