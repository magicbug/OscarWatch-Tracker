namespace OscarWatch.Core.Geo;

/// <summary>
/// Estimates how far the PC clock is from GPS UTC by comparing each GPS time stamp with the
/// PC time the sentence arrived. Sentences always arrive after the second they describe, so the
/// estimate uses the least-delayed samples and stays a little behind true UTC (receiver latency).
/// </summary>
public sealed class GpsClockOffsetEstimator
{
    public const int WindowSeconds = 60;
    public const int MinSamples = 10;
    private const double Percentile = 0.9;

    private readonly object _gate = new();
    private readonly Queue<(long Second, long DiffTicks)> _samples = new();
    private long _lastSecond = long.MinValue;
    private long _lastDiffTicks;

    /// <summary>Add a GPS time stamp and the PC UTC it was received. Date-less (GGA) times are fine.</summary>
    public void AddSample(DateTime gpsUtc, DateTime pcUtc)
    {
        var diff = pcUtc.Date + gpsUtc.TimeOfDay - pcUtc;
        if (diff > TimeSpan.FromHours(12))
            diff -= TimeSpan.FromDays(1);
        else if (diff < TimeSpan.FromHours(-12))
            diff += TimeSpan.FromDays(1);

        var second = (pcUtc + diff).Ticks / TimeSpan.TicksPerSecond;
        lock (_gate)
        {
            // Several sentences share one GPS second; the first to arrive is the least delayed.
            if (second == _lastSecond)
            {
                if (diff.Ticks > _lastDiffTicks)
                {
                    _lastDiffTicks = diff.Ticks;
                    ReplaceLast(second, diff.Ticks);
                }

                return;
            }

            _lastSecond = second;
            _lastDiffTicks = diff.Ticks;
            _samples.Enqueue((second, diff.Ticks));
            while (_samples.Count > WindowSeconds)
                _samples.Dequeue();
        }
    }

    /// <summary>GPS UTC minus PC UTC, or null until enough seconds have been sampled.</summary>
    public TimeSpan? CurrentOffset
    {
        get
        {
            lock (_gate)
            {
                if (_samples.Count < MinSamples)
                    return null;

                var sorted = _samples.Select(s => s.DiffTicks).Order().ToArray();
                var index = (int)Math.Floor(Percentile * (sorted.Length - 1));
                return TimeSpan.FromTicks(sorted[index]);
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _samples.Clear();
            _lastSecond = long.MinValue;
            _lastDiffTicks = 0;
        }
    }

    private void ReplaceLast(long second, long diffTicks)
    {
        var items = _samples.ToArray();
        items[^1] = (second, diffTicks);
        _samples.Clear();
        foreach (var item in items)
            _samples.Enqueue(item);
    }
}
