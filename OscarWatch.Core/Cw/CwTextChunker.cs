namespace OscarWatch.Core.Cw;

/// <summary>
/// Splits a message into pieces a radio's text keyer accepts in one command, breaking between words
/// where it can. A word gap that falls on a break starts the next piece, because some radios drop
/// trailing spaces.
/// </summary>
public static class CwTextChunker
{
    public static IReadOnlyList<CwMessage> Split(CwMessage message, int maxLength, Func<CwSymbol, int> width)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);
        var chunks = new List<CwMessage>();
        var current = new List<CwSymbol>();
        var currentLength = 0;

        foreach (var word in SplitWords(message.Symbols))
        {
            var wordLength = word.Sum(width);
            var gapLength = current.Count > 0 ? width(CwSymbol.WordGap) : 0;

            if (current.Count > 0 && currentLength + gapLength + wordLength > maxLength)
            {
                chunks.Add(new CwMessage(current));
                current = [CwSymbol.WordGap];
                currentLength = width(CwSymbol.WordGap);
            }
            else if (current.Count > 0)
            {
                current.Add(CwSymbol.WordGap);
                currentLength += gapLength;
            }

            foreach (var symbol in word)
            {
                var symbolLength = width(symbol);
                if (currentLength + symbolLength > maxLength && current.Count > 0)
                {
                    chunks.Add(new CwMessage(current));
                    current = [];
                    currentLength = 0;
                }

                current.Add(symbol);
                currentLength += symbolLength;
            }
        }

        if (current.Count > 0)
            chunks.Add(new CwMessage(current));

        return chunks;
    }

    private static IEnumerable<List<CwSymbol>> SplitWords(IReadOnlyList<CwSymbol> symbols)
    {
        var word = new List<CwSymbol>();
        foreach (var symbol in symbols)
        {
            if (symbol.IsWordGap)
            {
                if (word.Count > 0)
                    yield return word;
                word = [];
                continue;
            }

            word.Add(symbol);
        }

        if (word.Count > 0)
            yield return word;
    }
}
