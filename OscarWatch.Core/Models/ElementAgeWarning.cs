namespace OscarWatch.Core.Models;

/// <summary>
/// When a satellite's orbital element set is old enough to warn about. The age is counted from the element
/// epoch, not from when the catalogue was last downloaded: a catalogue fetched a minute ago can still hold an
/// element set that is weeks old, and predictions from it drift, most of all for low orbits.
/// </summary>
public static class ElementAgeWarning
{
    public const int DefaultDays = 14;
    public const int MinDays = 1;
    public const int MaxDays = 365;

    public static int ClampDays(int days) => Math.Clamp(days, MinDays, MaxDays);

    /// <summary>
    /// Whole days from the element epoch to <paramref name="utcNow"/>, or null when the epoch is not known.
    /// An epoch in the future counts as zero days.
    /// </summary>
    public static int? WholeDaysOld(DateTime? epochUtc, DateTime utcNow)
    {
        if (epochUtc is not { } epoch)
            return null;
        var age = utcNow - epoch;
        return age < TimeSpan.Zero ? 0 : (int)Math.Floor(age.TotalDays);
    }

    /// <summary>
    /// True when the element set is at least <paramref name="warningDays"/> whole days old. An unknown epoch
    /// never warns, because there is nothing to measure.
    /// </summary>
    public static bool IsOldEnough(DateTime? epochUtc, DateTime utcNow, int warningDays) =>
        WholeDaysOld(epochUtc, utcNow) is { } days && days >= ClampDays(warningDays);
}
