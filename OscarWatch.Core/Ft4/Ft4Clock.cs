namespace OscarWatch.Core.Ft4;

/// <summary>
/// UTC for FT4 slot timing: the PC clock, shifted by the measured GPS offset when the operator has a
/// GPS configured and the PC clock is far enough out to matter.
/// </summary>
public static class Ft4Clock
{
    /// <summary>PC error that switches FT4 onto GPS time.</summary>
    public static readonly TimeSpan ApplyThreshold = TimeSpan.FromMilliseconds(250);

    /// <summary>Once applied, keep GPS time until the PC is this close (stops slots jumping back and forth).</summary>
    public static readonly TimeSpan ReleaseThreshold = TimeSpan.FromMilliseconds(150);

    private static long _offsetTicks;
    private static long _measuredTicks;
    private static int _hasMeasurement;

    public static DateTime UtcNow => DateTime.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));

    /// <summary>Correction added to the PC clock (zero when FT4 is on the PC clock).</summary>
    public static TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public static bool UsingGps => Interlocked.Read(ref _offsetTicks) != 0;

    /// <summary>Latest GPS minus PC measurement, or null when no GPS time is available.</summary>
    public static TimeSpan? MeasuredOffset =>
        Volatile.Read(ref _hasMeasurement) == 1
            ? TimeSpan.FromTicks(Interlocked.Read(ref _measuredTicks))
            : null;

    /// <summary>Apply a GPS minus PC measurement (null when GPS is off, has no fix, or is still sampling).</summary>
    public static void Update(TimeSpan? measured)
    {
        if (measured is not { } m)
        {
            Volatile.Write(ref _hasMeasurement, 0);
            Interlocked.Exchange(ref _offsetTicks, 0);
            return;
        }

        Interlocked.Exchange(ref _measuredTicks, m.Ticks);
        Volatile.Write(ref _hasMeasurement, 1);

        var magnitude = m.Duration();
        var apply = magnitude >= ApplyThreshold || (UsingGps && magnitude >= ReleaseThreshold);
        Interlocked.Exchange(ref _offsetTicks, apply ? m.Ticks : 0);
    }
}
