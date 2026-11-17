namespace OscarWatch.Core.Eme;

/// <summary>The next time the Moon rises or sets under a <see cref="MoonVisibilityRule"/>.</summary>
/// <param name="IsSet">True when the Moon is up now and the event is its set; false when it is down and the event is its rise.</param>
/// <param name="TimeUtc">When it happens.</param>
public sealed record MoonNextEvent(bool IsSet, DateTime TimeUtc)
{
    /// <summary>The next rise or set after <paramref name="nowUtc"/>, looking up to three days ahead; null if there is none.</summary>
    public static MoonNextEvent? Find(MoonVisibilityRule rule, StationPosition station, DateTime nowUtc)
    {
        var windows = MoonWindows.Find(rule, station, nowUtc, nowUtc.AddDays(3));
        foreach (var w in windows)
        {
            if (w.AosUtc <= nowUtc && w.LosUtc > nowUtc)
                return new MoonNextEvent(true, w.LosUtc);
            if (w.AosUtc > nowUtc)
                return new MoonNextEvent(false, w.AosUtc);
        }
        return null;
    }
}
