using System.Globalization;
using System.Text;

namespace OscarWatch.Core.Cw;

/// <summary>
/// Kenwood CW by CAT: <c>KY</c> sends up to 24 characters that the radio keys itself,
/// <c>KS</c> sets the keyer speed.
/// </summary>
public static class KenwoodCwCodec
{
    public const int MaxCharacters = 24;
    public const int MaxWpm = 60;

    /// <summary>Lowest speed the TS-590, TS-890 and TS-990 accept.</summary>
    public const int MinWpmHf = 4;

    /// <summary>Lowest speed the TS-2000 accepts.</summary>
    public const int MinWpmTs2000 = 10;

    /// <summary>One <c>KY</c> set command per piece of the message.</summary>
    public static IReadOnlyList<(string Command, CwMessage Piece)> BuildSendCommands(CwMessage message) =>
        CwTextChunker.Split(message, MaxCharacters, _ => 1)
            .Select(piece => (BuildSendCommand(piece), piece))
            .ToList();

    /// <summary><c>KY</c>, a space, the text padded with spaces to 24 characters, then <c>;</c>.</summary>
    public static string BuildSendCommand(CwMessage piece)
    {
        var text = new StringBuilder(MaxCharacters);
        foreach (var symbol in piece.Symbols)
            text.Append(MapSymbol(symbol));

        if (text.Length > MaxCharacters)
            text.Length = MaxCharacters;

        return "KY " + text.ToString().PadRight(MaxCharacters) + ";";
    }

    /// <summary>
    /// Stop sending. The TS-590, TS-890 and TS-990 take <c>KY0;</c>. The TS-2000 stops when every
    /// character of the text is a space.
    /// </summary>
    public static string BuildStopCommand(bool ts2000) =>
        ts2000 ? "KY " + new string(' ', MaxCharacters) + ";" : "KY0;";

    public static string BuildSpeedCommand(int wpm, bool ts2000)
    {
        var min = ts2000 ? MinWpmTs2000 : MinWpmHf;
        var clamped = Math.Clamp(wpm, min, MaxWpm);
        return "KS" + clamped.ToString("000", CultureInfo.InvariantCulture) + ";";
    }

    private static string MapSymbol(CwSymbol symbol)
    {
        if (!symbol.IsProsign)
            return symbol.Value;

        return symbol.Value switch
        {
            "SK" => ">",
            "KN" => "]",
            "AR" => "+",
            "BT" => "=",
            _ => symbol.Value
        };
    }
}
