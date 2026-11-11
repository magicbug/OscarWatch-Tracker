using System.Text;

namespace OscarWatch.Core.Cw;

/// <summary>One sendable unit: a character, a word gap (space), or a prosign such as <c>SK</c>.</summary>
public readonly record struct CwSymbol(string Value)
{
    public static CwSymbol WordGap { get; } = new(" ");

    public bool IsWordGap => Value == " ";

    /// <summary>Letters sent together with no gap between them, for example <c>SK</c>.</summary>
    public bool IsProsign => Value.Length > 1;

    public static CwSymbol Character(char c) => new(c.ToString());

    public static CwSymbol Prosign(string letters) => new(letters);
}

/// <summary>An expanded macro, ready for a keyer: no leading, trailing or doubled word gaps.</summary>
public sealed class CwMessage
{
    public static CwMessage Empty { get; } = new([]);

    public CwMessage(IReadOnlyList<CwSymbol> symbols)
    {
        Symbols = symbols;
    }

    public IReadOnlyList<CwSymbol> Symbols { get; }

    public bool IsEmpty => Symbols.Count == 0;

    /// <summary>Text for the status line and tooltips. Prosigns are shown as <c>&lt;SK&gt;</c>.</summary>
    public string DisplayText
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var symbol in Symbols)
            {
                if (symbol.IsProsign)
                    sb.Append('<').Append(symbol.Value).Append('>');
                else
                    sb.Append(symbol.Value);
            }

            return sb.ToString();
        }
    }

    /// <summary>Drop unsupported characters and tidy word gaps.</summary>
    public static CwMessage FromSymbols(IEnumerable<CwSymbol> symbols)
    {
        var list = new List<CwSymbol>();
        foreach (var symbol in symbols)
        {
            if (symbol.IsWordGap)
            {
                if (list.Count > 0 && !list[^1].IsWordGap)
                    list.Add(CwSymbol.WordGap);
                continue;
            }

            if (symbol.IsProsign || MorseCode.IsSupported(symbol.Value[0]))
                list.Add(symbol);
        }

        while (list.Count > 0 && list[^1].IsWordGap)
            list.RemoveAt(list.Count - 1);

        return new CwMessage(list);
    }

    /// <summary>Plain text, upper-cased, with every character sent as itself.</summary>
    public static CwMessage FromText(string? text)
    {
        var symbols = new List<CwSymbol>();
        foreach (var c in (text ?? "").ToUpperInvariant())
            symbols.Add(char.IsWhiteSpace(c) ? CwSymbol.WordGap : CwSymbol.Character(c));
        return FromSymbols(symbols);
    }
}
