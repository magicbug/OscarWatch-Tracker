using System.Text;

namespace OscarWatch.Core.CwDecode;

/// <summary>
/// Drops the stray letters the model reads out of noise: short words where no letter is
/// confident. A short weak word at the end of a step is held until the next step shows
/// whether it carries on into a longer word.
/// </summary>
public sealed class CwNoiseSquelch
{
    public const int NormalMaxNoiseWordLength = 2;
    public const float NormalMinWordConfidence = 0.7f;
    public const int StrictMaxNoiseWordLength = 2;
    public const float StrictMinWordConfidence = 0.85f;

    /// <summary>Kept for older tests and callers that still name the Normal thresholds.</summary>
    public const int MaxNoiseWordLength = NormalMaxNoiseWordLength;
    public const float MinWordConfidence = NormalMinWordConfidence;

    private string _heldText = "";
    private float[] _heldConfidence = [];
    /// <summary>The last text let through ended inside a word that more letters may join.</summary>
    private bool _insideWord;

    public CwNoiseSquelchMode Mode { get; set; } = CwNoiseSquelchMode.Normal;

    public static int MaxWordLength(CwNoiseSquelchMode mode) => mode switch
    {
        CwNoiseSquelchMode.Strict => StrictMaxNoiseWordLength,
        _ => NormalMaxNoiseWordLength,
    };

    public static float MinConfidence(CwNoiseSquelchMode mode) => mode switch
    {
        CwNoiseSquelchMode.Strict => StrictMinWordConfidence,
        _ => NormalMinWordConfidence,
    };

    /// <summary>
    /// Settled text from one decode step, with a confidence per character (1 for spaces).
    /// Returns the text to show, which may be empty.
    /// </summary>
    public string Process(string text, ReadOnlySpan<float> confidence)
    {
        if (Mode == CwNoiseSquelchMode.Off)
            return text;

        var maxLength = MaxWordLength(Mode);
        var minConfidence = MinConfidence(Mode);
        var combined = _heldText + text;
        var scores = new float[combined.Length];
        _heldConfidence.CopyTo(scores, 0);
        for (var i = 0; i < text.Length; i++)
            scores[_heldConfidence.Length + i] = i < confidence.Length ? confidence[i] : 1f;

        var startsInsideWord = _insideWord && _heldText.Length == 0;
        _heldText = "";
        _heldConfidence = [];

        var output = new StringBuilder();
        var i0 = 0;
        while (i0 < combined.Length)
        {
            var wordStart = i0;
            while (wordStart < combined.Length && combined[wordStart] == ' ')
                wordStart++;
            if (wordStart == combined.Length)
            {
                output.Append(combined, i0, wordStart - i0);
                _insideWord = false;
                break;
            }

            var wordEnd = wordStart;
            var best = 0f;
            while (wordEnd < combined.Length && combined[wordEnd] != ' ')
                best = Math.Max(best, scores[wordEnd++]);

            var continues = wordStart == 0 && startsInsideWord;
            var isNoise = !continues
                && wordEnd - wordStart <= maxLength
                && best < minConfidence;
            var atEnd = wordEnd == combined.Length;

            if (isNoise && atEnd)
            {
                _heldText = combined[i0..];
                _heldConfidence = scores[i0..];
                _insideWord = false;
                break;
            }

            if (!isNoise)
            {
                output.Append(combined, i0, wordEnd - i0);
                _insideWord = atEnd;
            }

            i0 = wordEnd;
        }

        return output.ToString();
    }

    /// <summary>The stream has ended or paused: a held word was a whole word, so drop it.</summary>
    public void Flush()
    {
        _heldText = "";
        _heldConfidence = [];
        _insideWord = false;
    }

    /// <summary>True when <paramref name="pending"/> is only a short word the model is unsure of.</summary>
    public static bool IsLikelyNoise(CwLaneText pending, CwNoiseSquelchMode mode = CwNoiseSquelchMode.Normal)
    {
        if (mode == CwNoiseSquelchMode.Off)
            return false;

        var text = pending.Text.Trim();
        if (text.Length is 0 || text.Length > MaxWordLength(mode) || text.Contains(' '))
            return false;
        return pending.Confidence is { Length: > 0 } c && c.Max() < MinConfidence(mode);
    }
}
