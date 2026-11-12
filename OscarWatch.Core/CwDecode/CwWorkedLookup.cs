using OscarWatch.Core.Geo;
using OscarWatch.Core.Qrz;

namespace OscarWatch.Core.CwDecode;

/// <summary>Whether a callsign or locator heard in the copy is already in the logbook.</summary>
public enum CwWorkedStatus
{
    /// <summary>The logbook has not been read yet.</summary>
    Unknown,
    New,
    Worked,
}

/// <summary>
/// Checks decoded callsigns and locators against the worked calls and 4-character grid fields
/// from every logbook (the same sets FT4 and Terminal use for new call and new grid).
/// </summary>
public static class CwWorkedLookup
{
    /// <summary>
    /// A call counts as worked when it, or its home call without a prefix or suffix
    /// (<c>MM9SQL</c> for <c>MM9SQL/P</c>), is in the logbook.
    /// </summary>
    public static CwWorkedStatus ForCall(string call, IReadOnlySet<string>? workedCalls)
    {
        if (workedCalls is null)
            return CwWorkedStatus.Unknown;

        var normal = MaidenheadLocator.NormalizeCallsign(call);
        if (normal.Length == 0)
            return CwWorkedStatus.Unknown;
        if (workedCalls.Contains(normal))
            return CwWorkedStatus.Worked;

        var home = MaidenheadLocator.NormalizeCallsign(QrzCallsignHelper.ToLookupCall(normal));
        return home.Length > 0 && workedCalls.Contains(home) ? CwWorkedStatus.Worked : CwWorkedStatus.New;
    }

    /// <summary>A locator counts as worked when its 4-character field is in the logbook.</summary>
    public static CwWorkedStatus ForLocator(string locator, IReadOnlySet<string>? workedFields)
    {
        if (workedFields is null)
            return CwWorkedStatus.Unknown;

        var trimmed = locator.Trim().ToUpperInvariant();
        if (trimmed.Length < 4)
            return CwWorkedStatus.Unknown;
        return workedFields.Contains(trimmed[..4]) ? CwWorkedStatus.Worked : CwWorkedStatus.New;
    }
}
