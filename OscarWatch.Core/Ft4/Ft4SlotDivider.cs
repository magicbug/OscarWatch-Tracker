namespace OscarWatch.Core.Ft4;

/// <summary>
/// Marks the first displayed row of each FT4 slot so Band activity and Calling me
/// can draw a compact Even/Odd header without inserting selectable list items.
/// </summary>
public static class Ft4SlotDivider
{
    /// <summary>
    /// True when this row is the first in its list, or its <paramref name="slotUtc"/>
    /// differs from the previous row in that same list.
    /// </summary>
    public static bool StartsGroup(DateTime slotUtc, DateTime? previousSlotUtc) =>
        previousSlotUtc is null || previousSlotUtc.Value != slotUtc;
}
