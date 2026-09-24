namespace OscarWatch.Core.Ft4;

/// <summary>High-resolution wait until a UTC instant (used to kick FT4 TX at the slot boundary).</summary>
public static class Ft4SlotWait
{
    /// <summary>
    /// Block until <paramref name="targetUtc"/> (or later). Sleeps coarsely, then spins for the last few ms
    /// so Windows timer coarseness does not hold the first tone.
    /// </summary>
    public static void UntilUtc(DateTime targetUtc, CancellationToken cancellationToken = default)
    {
        var target = targetUtc.Kind == DateTimeKind.Utc ? targetUtc : targetUtc.ToUniversalTime();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = target - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
                return;

            if (left > TimeSpan.FromMilliseconds(25))
            {
                var sleep = left - TimeSpan.FromMilliseconds(10);
                if (sleep > TimeSpan.Zero)
                    Thread.Sleep(sleep);
                continue;
            }

            while (DateTime.UtcNow < target)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.SpinWait(80);
            }

            return;
        }
    }
}
