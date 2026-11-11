namespace OscarWatch.Core.Cw;

/// <summary>Values the logbook supplies when a macro message is sent.</summary>
public sealed record CwMacroContext(
    string MyCall = "",
    string Call = "",
    string RstSent = "",
    string RstRcvd = "",
    string Grid = "",
    string MyGrid = "",
    string Name = "",
    string Satellite = "");

/// <summary>An expanded message and any <c>{TOKEN}</c> this build does not know.</summary>
public sealed record CwExpansion(CwMessage Message, IReadOnlyList<string> UnknownTokens);

/// <summary>
/// Expands N1MM-style macros: <c>{MYCALL}</c> and <c>*</c>, <c>{CALL}</c> and <c>!</c>,
/// <c>{SENTRST}</c>, <c>{SENTRSTCUT}</c>, <c>{RCVDRST}</c>, <c>{GRID}</c>, <c>{MYGRID}</c>,
/// <c>{NAME}</c>, <c>{SAT}</c>, and the prosigns <c>{AR}</c> <c>{SK}</c> <c>{BT}</c> <c>{KN}</c>.
/// </summary>
public static class CwMacroExpander
{
    public static IReadOnlyList<string> KnownTokens { get; } =
    [
        "MYCALL", "CALL", "SENTRST", "SENTRSTCUT", "RCVDRST", "GRID", "MYGRID", "NAME", "SAT",
        "AR", "SK", "BT", "KN"
    ];

    public static CwExpansion Expand(string? template, CwMacroContext context)
    {
        var symbols = new List<CwSymbol>();
        var unknown = new List<string>();
        var text = template ?? "";

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '*':
                    AppendText(symbols, context.MyCall);
                    continue;
                case '!':
                    AppendText(symbols, context.Call);
                    continue;
                case '{':
                {
                    var close = text.IndexOf('}', i + 1);
                    if (close < 0)
                        break;

                    var token = text[(i + 1)..close].Trim().ToUpperInvariant();
                    i = close;
                    if (!TryAppendToken(symbols, token, context) && token.Length > 0
                        && !unknown.Contains(token, StringComparer.Ordinal))
                    {
                        unknown.Add(token);
                    }

                    continue;
                }
            }

            symbols.Add(char.IsWhiteSpace(c) ? CwSymbol.WordGap : CwSymbol.Character(char.ToUpperInvariant(c)));
        }

        return new CwExpansion(CwMessage.FromSymbols(symbols), unknown);
    }

    /// <summary>RST with every 9 sent as N, so 599 becomes 5NN.</summary>
    public static string CutNumbers(string? report) =>
        (report ?? "").Trim().Replace('9', 'N');

    private static bool TryAppendToken(List<CwSymbol> symbols, string token, CwMacroContext context)
    {
        switch (token)
        {
            case "MYCALL":
                AppendText(symbols, context.MyCall);
                return true;
            case "CALL":
                AppendText(symbols, context.Call);
                return true;
            case "SENTRST":
                AppendText(symbols, context.RstSent);
                return true;
            case "SENTRSTCUT":
                AppendText(symbols, CutNumbers(context.RstSent));
                return true;
            case "RCVDRST":
                AppendText(symbols, context.RstRcvd);
                return true;
            case "GRID":
                AppendText(symbols, context.Grid);
                return true;
            case "MYGRID":
                AppendText(symbols, context.MyGrid);
                return true;
            case "NAME":
                AppendText(symbols, context.Name);
                return true;
            case "SAT":
                AppendText(symbols, context.Satellite);
                return true;
            case "AR":
                symbols.Add(CwSymbol.Character('+'));
                return true;
            case "BT":
                symbols.Add(CwSymbol.Character('='));
                return true;
            case "SK":
            case "KN":
                symbols.Add(CwSymbol.Prosign(token));
                return true;
            default:
                return false;
        }
    }

    private static void AppendText(List<CwSymbol> symbols, string? value)
    {
        foreach (var c in (value ?? "").Trim().ToUpperInvariant())
            symbols.Add(char.IsWhiteSpace(c) ? CwSymbol.WordGap : CwSymbol.Character(c));
    }
}
