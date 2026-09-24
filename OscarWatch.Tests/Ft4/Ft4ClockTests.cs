using OscarWatch.Core.Ft4;
using OscarWatch.Core.Geo;

namespace OscarWatch.Tests.Ft4;

/// <summary>Tests that read or change the shared FT4 clock must not run alongside each other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Ft4ClockCollection
{
    public const string Name = "Ft4Clock";
}

[Collection(Ft4ClockCollection.Name)]
public sealed class Ft4ClockTests : IDisposable
{
    public void Dispose() => Ft4Clock.Update(null);

    [Fact]
    public void No_gps_measurement_leaves_ft4_on_the_pc_clock()
    {
        Ft4Clock.Update(null);

        Assert.False(Ft4Clock.UsingGps);
        Assert.Null(Ft4Clock.MeasuredOffset);
        Assert.Equal(TimeSpan.Zero, Ft4Clock.Offset);
    }

    [Fact]
    public void Small_pc_error_is_measured_but_not_applied()
    {
        Ft4Clock.Update(TimeSpan.FromMilliseconds(-120));

        Assert.False(Ft4Clock.UsingGps);
        Assert.Equal(TimeSpan.FromMilliseconds(-120), Ft4Clock.MeasuredOffset);
        Assert.Equal(TimeSpan.Zero, Ft4Clock.Offset);
    }

    [Fact]
    public void Large_pc_error_switches_ft4_to_gps_time()
    {
        Ft4Clock.Update(TimeSpan.FromSeconds(1.8));

        Assert.True(Ft4Clock.UsingGps);
        Assert.Equal(TimeSpan.FromSeconds(1.8), Ft4Clock.Offset);
        var shifted = (Ft4Clock.UtcNow - DateTime.UtcNow).TotalSeconds;
        Assert.InRange(shifted, 1.75, 1.85);
    }

    [Fact]
    public void Gps_time_is_kept_until_the_pc_is_well_inside_the_threshold()
    {
        Ft4Clock.Update(TimeSpan.FromMilliseconds(400));
        Assert.True(Ft4Clock.UsingGps);

        Ft4Clock.Update(TimeSpan.FromMilliseconds(200));
        Assert.True(Ft4Clock.UsingGps);

        Ft4Clock.Update(TimeSpan.FromMilliseconds(100));
        Assert.False(Ft4Clock.UsingGps);
    }

    [Fact]
    public void Losing_gps_drops_back_to_the_pc_clock()
    {
        Ft4Clock.Update(TimeSpan.FromSeconds(-2));
        Ft4Clock.Update(null);

        Assert.False(Ft4Clock.UsingGps);
        Assert.Equal(TimeSpan.Zero, Ft4Clock.Offset);
    }

    [Fact]
    public void Slot_wait_follows_the_gps_corrected_clock()
    {
        Ft4Clock.Update(TimeSpan.FromSeconds(3));
        var target = Ft4Clock.UtcNow.AddMilliseconds(120);

        var started = DateTime.UtcNow;
        Ft4SlotWait.UntilUtc(target);
        var waitedMs = (DateTime.UtcNow - started).TotalMilliseconds;

        Assert.InRange(waitedMs, 100, 200);
    }
}

public sealed class GpsClockOffsetEstimatorTests
{
    private static readonly DateTime Start = new(2026, 9, 24, 21, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Needs_enough_seconds_before_giving_an_offset()
    {
        var estimator = new GpsClockOffsetEstimator();
        for (var i = 0; i < GpsClockOffsetEstimator.MinSamples - 1; i++)
            estimator.AddSample(Start.AddSeconds(i), Start.AddSeconds(i).AddMilliseconds(100));

        Assert.Null(estimator.CurrentOffset);
    }

    [Fact]
    public void Pc_clock_behind_gps_gives_a_positive_offset_less_the_quickest_arrival()
    {
        var estimator = new GpsClockOffsetEstimator();
        var pcBehind = TimeSpan.FromSeconds(1.5);
        var rng = new Random(7);
        for (var i = 0; i < 40; i++)
        {
            var gps = Start.AddSeconds(i);
            var latency = TimeSpan.FromMilliseconds(80 + rng.Next(0, 200));
            estimator.AddSample(gps, gps - pcBehind + latency);
        }

        var offset = estimator.CurrentOffset;
        Assert.NotNull(offset);
        Assert.InRange(offset.Value.TotalSeconds, 1.5 - 0.15, 1.5 - 0.075);
    }

    [Fact]
    public void Later_sentences_in_the_same_second_do_not_drag_the_estimate()
    {
        var estimator = new GpsClockOffsetEstimator();
        for (var i = 0; i < 20; i++)
        {
            var gps = Start.AddSeconds(i);
            estimator.AddSample(gps, gps.AddMilliseconds(100));
            estimator.AddSample(gps, gps.AddMilliseconds(400));
            estimator.AddSample(gps, gps.AddMilliseconds(700));
        }

        Assert.Equal(-100, estimator.CurrentOffset!.Value.TotalMilliseconds, 3);
    }

    [Fact]
    public void Date_less_gga_times_are_matched_to_the_pc_day_across_midnight()
    {
        var estimator = new GpsClockOffsetEstimator();
        var midnight = new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
        for (var i = -10; i < 10; i++)
        {
            var trueUtc = midnight.AddSeconds(i);
            var ggaOnly = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) + trueUtc.TimeOfDay;
            estimator.AddSample(ggaOnly, trueUtc.AddSeconds(-2).AddMilliseconds(100));
        }

        Assert.Equal(1.9, estimator.CurrentOffset!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void Reset_clears_the_window()
    {
        var estimator = new GpsClockOffsetEstimator();
        for (var i = 0; i < 20; i++)
            estimator.AddSample(Start.AddSeconds(i), Start.AddSeconds(i));

        estimator.Reset();

        Assert.Null(estimator.CurrentOffset);
    }
}
