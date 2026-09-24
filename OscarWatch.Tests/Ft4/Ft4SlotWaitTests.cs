using System.Diagnostics;
using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

[Collection(Ft4ClockCollection.Name)]
public sealed class Ft4SlotWaitTests
{
    [Fact]
    public void UntilUtc_wakes_within_25ms_of_a_short_target()
    {
        var samples = new List<double>(5);
        for (var i = 0; i < 5; i++)
        {
            var target = DateTime.UtcNow.AddMilliseconds(120);
            var sw = Stopwatch.StartNew();
            Ft4SlotWait.UntilUtc(target);
            sw.Stop();
            var lateMs = (DateTime.UtcNow - target).TotalMilliseconds;
            Assert.True(lateMs >= -1, $"woke early by {-lateMs:0.0} ms");
            Assert.True(lateMs <= 25, $"woke {lateMs:0.0} ms late (budget 25 ms)");
            samples.Add(lateMs);
        }

        Assert.True(
            samples.Average() <= 15,
            $"mean wake lateness {samples.Average():0.0} ms over 5 × 120 ms waits");
    }

    [Fact]
    public void UntilUtc_hits_the_next_ft4_slot_boundary_within_25ms()
    {
        var now = DateTime.UtcNow;
        var current = Ft4SlotClock.SlotStartUtc(now, Ft4SlotClock.Ft4SlotSeconds);
        var next = current.AddSeconds(Ft4SlotClock.Ft4SlotSeconds);
        if ((next - now).TotalMilliseconds < 80)
            next = next.AddSeconds(Ft4SlotClock.Ft4SlotSeconds);

        var waitMs = (next - DateTime.UtcNow).TotalMilliseconds;
        Assert.True(waitMs > 50, "need a real wait ahead of the slot");
        Assert.True(waitMs < 8000, $"unexpected wait {waitMs:0} ms");

        Ft4SlotWait.UntilUtc(next);
        var lateMs = (DateTime.UtcNow - next).TotalMilliseconds;

        Assert.True(lateMs >= -1, $"woke early by {-lateMs:0.0} ms before slot {next:O}");
        Assert.True(
            lateMs <= 25,
            $"FT4 slot-boundary wait woke {lateMs:0.0} ms late (budget 25 ms; waited {waitMs:0} ms)");
    }
}
