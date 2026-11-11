namespace OscarWatch.Core.Cw;

/// <summary>International Morse code and PARIS timing (one dot is 1200 / WPM milliseconds).</summary>
public static class MorseCode
{
    public const int DotUnits = 1;
    public const int DashUnits = 3;
    public const int ElementGapUnits = 1;
    public const int CharacterGapUnits = 3;
    public const int WordGapUnits = 7;

    private static readonly Dictionary<char, string> Patterns = new()
    {
        ['A'] = ".-", ['B'] = "-...", ['C'] = "-.-.", ['D'] = "-..", ['E'] = ".",
        ['F'] = "..-.", ['G'] = "--.", ['H'] = "....", ['I'] = "..", ['J'] = ".---",
        ['K'] = "-.-", ['L'] = ".-..", ['M'] = "--", ['N'] = "-.", ['O'] = "---",
        ['P'] = ".--.", ['Q'] = "--.-", ['R'] = ".-.", ['S'] = "...", ['T'] = "-",
        ['U'] = "..-", ['V'] = "...-", ['W'] = ".--", ['X'] = "-..-", ['Y'] = "-.--",
        ['Z'] = "--..",
        ['0'] = "-----", ['1'] = ".----", ['2'] = "..---", ['3'] = "...--", ['4'] = "....-",
        ['5'] = ".....", ['6'] = "-....", ['7'] = "--...", ['8'] = "---..", ['9'] = "----.",
        ['.'] = ".-.-.-", [','] = "--..--", ['?'] = "..--..", ['/'] = "-..-.",
        ['='] = "-...-", ['+'] = ".-.-.", ['-'] = "-....-", [':'] = "---...",
        ['\''] = ".----.", ['('] = "-.--.", [')'] = "-.--.-", ['"'] = ".-..-.",
        ['@'] = ".--.-."
    };

    public static bool IsSupported(char c) => Patterns.ContainsKey(char.ToUpperInvariant(c));

    public static string? PatternFor(char c) =>
        Patterns.TryGetValue(char.ToUpperInvariant(c), out var pattern) ? pattern : null;

    /// <summary>Length of one dot in milliseconds at <paramref name="wpm"/>.</summary>
    public static double DotMs(int wpm) => 1200.0 / Math.Max(1, wpm);

    /// <summary>
    /// Key-down and key-up periods, in dot units, for a whole message. The list alternates and
    /// always starts with a key-down. Gaps after the last element are not included.
    /// </summary>
    public static IReadOnlyList<CwKeyEvent> BuildKeyEvents(CwMessage message)
    {
        var events = new List<CwKeyEvent>();
        var pendingGap = 0;

        foreach (var symbol in message.Symbols)
        {
            if (symbol.IsWordGap)
            {
                if (events.Count > 0)
                    pendingGap = WordGapUnits;
                continue;
            }

            var pattern = SymbolPattern(symbol);
            if (pattern.Length == 0)
                continue;

            if (events.Count > 0)
            {
                events.Add(new CwKeyEvent(false, Math.Max(pendingGap, CharacterGapUnits)));
            }

            for (var i = 0; i < pattern.Length; i++)
            {
                if (i > 0)
                    events.Add(new CwKeyEvent(false, ElementGapUnits));
                events.Add(new CwKeyEvent(true, pattern[i] == '-' ? DashUnits : DotUnits));
            }

            pendingGap = 0;
        }

        return events;
    }

    /// <summary>Total dot units a message takes, including the gaps between characters and words.</summary>
    public static int CountUnits(CwMessage message) =>
        BuildKeyEvents(message).Sum(e => e.Units);

    /// <summary>
    /// Time a radio needs to send <paramref name="message"/>, plus one word gap so the next piece
    /// does not run into this one.
    /// </summary>
    public static TimeSpan EstimateDuration(CwMessage message, int wpm)
    {
        if (message.IsEmpty)
            return TimeSpan.Zero;

        var units = CountUnits(message) + WordGapUnits;
        return TimeSpan.FromMilliseconds(units * DotMs(wpm));
    }

    private static string SymbolPattern(CwSymbol symbol)
    {
        if (!symbol.IsProsign)
            return PatternFor(symbol.Value[0]) ?? "";

        // A prosign is its letters run together, with only element gaps between them.
        return string.Concat(symbol.Value.Select(c => PatternFor(c) ?? ""));
    }
}

/// <summary>Key down (<see cref="KeyDown"/> true) or key up for <see cref="Units"/> dot lengths.</summary>
public readonly record struct CwKeyEvent(bool KeyDown, int Units);
