using OscarWatch.Core.Geo;
using OscarWatch.Core.Qrz;

namespace OscarWatch.Core.CwDecode;

public enum CwTokenKind
{
    Text,
    Callsign,
    Locator,
    /// <summary>The operator's own callsign (someone calling you). Not offered to the logbook.</summary>
    OwnCallsign,
}

/// <summary>One word of decoded copy. <see cref="Value"/> is what goes to the logbook.</summary>
public readonly record struct CwToken(string Text, CwTokenKind Kind, string Value);

/// <summary>Picks callsigns and Maidenhead locators out of decoded CW so they can be sent to the logbook.</summary>
public static class CwCopySpotter
{
    private static readonly char[] Trim = [',', '.', '?'];

    /// <summary>Four-character groups that read as locators but are exchange words.</summary>
    private static readonly HashSet<string> NotLocators = new(StringComparer.Ordinal) { "RR73" };

    public static IReadOnlyList<CwToken> Tokenize(string? text, string? ownCallsign = null, string? ownLocator = null)
    {
        var tokens = new List<CwToken>();
        if (string.IsNullOrWhiteSpace(text))
            return tokens;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var kind = Classify(word, ownCallsign, ownLocator, out var value);
            tokens.Add(new CwToken(word, kind, value));
        }

        return tokens;
    }

    public static CwTokenKind Classify(string word, string? ownCallsign, string? ownLocator, out string value)
    {
        value = word;
        var core = word.Trim(Trim).ToUpperInvariant();
        if (core.Length == 0)
            return CwTokenKind.Text;

        if (IsLocator(core) && !IsOwnLocator(core, ownLocator))
        {
            value = core;
            return CwTokenKind.Locator;
        }

        if (IsCallsign(core))
        {
            value = core;
            return IsOwnCallsign(core, ownCallsign) ? CwTokenKind.OwnCallsign : CwTokenKind.Callsign;
        }

        return CwTokenKind.Text;
    }

    public static bool IsLocator(string core) =>
        core.Length is 4 or 6
        && !NotLocators.Contains(core)
        && MaidenheadLocator.IsValidSegment(core);

    /// <summary>
    /// A plausible callsign that ends in a letter (so <c>599</c> and <c>IO85</c> are not offered).
    /// A prefix or suffix after <c>/</c> is allowed, for example <c>MM9SQL/P</c> or <c>EA8/MM9SQL</c>.
    /// </summary>
    public static bool IsCallsign(string core)
    {
        if (core.Length < 3 || core.StartsWith('/') || core.EndsWith('/'))
            return false;

        return BaseCall(core) is { } baseCall
            && char.IsAsciiLetter(baseCall[^1])
            && baseCall.Any(char.IsAsciiLetter)
            && baseCall.Any(char.IsAsciiDigit);
    }

    private static string? BaseCall(string core)
    {
        if (QrzCallsignHelper.IsPlausible(core) && !core.Contains('/'))
            return core;

        var lookup = QrzCallsignHelper.ToLookupCall(core);
        if (!lookup.Contains('/') && QrzCallsignHelper.IsPlausible(lookup))
            return lookup;

        return core.Split('/')
            .Where(QrzCallsignHelper.IsPlausible)
            .OrderByDescending(p => p.Length)
            .FirstOrDefault();
    }

    private static bool IsOwnCallsign(string core, string? ownCallsign)
    {
        if (string.IsNullOrWhiteSpace(ownCallsign))
            return false;

        var own = BaseCall(ownCallsign.Trim().ToUpperInvariant());
        return own is not null && string.Equals(BaseCall(core), own, StringComparison.Ordinal);
    }

    private static bool IsOwnLocator(string core, string? ownLocator)
    {
        if (string.IsNullOrWhiteSpace(ownLocator))
            return false;

        var own = ownLocator.Trim().ToUpperInvariant();
        return own.StartsWith(core, StringComparison.Ordinal) || core.StartsWith(own, StringComparison.Ordinal);
    }
}
