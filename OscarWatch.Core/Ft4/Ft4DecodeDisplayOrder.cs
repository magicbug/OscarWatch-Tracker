namespace OscarWatch.Core.Ft4;

/// <summary>
/// Maps the modem's newest-first decode list to the order shown in Band activity.
/// </summary>
public static class Ft4DecodeDisplayOrder
{
    /// <summary>
    /// Modem storage is newest-first. When <paramref name="newestAtBottom"/> is false,
    /// the display matches storage. When true, the display is oldest-first so new lines
    /// appear at the bottom.
    /// </summary>
    public static IReadOnlyList<T> ForDisplay<T>(IReadOnlyList<T> newestFirst, bool newestAtBottom)
    {
        if (!newestAtBottom || newestFirst.Count <= 1)
            return newestFirst;

        var reversed = new T[newestFirst.Count];
        for (var i = 0; i < newestFirst.Count; i++)
            reversed[i] = newestFirst[newestFirst.Count - 1 - i];
        return reversed;
    }
}
