using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

public class ScheduledPassReminderTests
{
    private static PassInfo Pass(string noradId, string name, DateTime aosUtc, DateTime? losUtc = null) =>
        new()
        {
            NoradId = noradId,
            SatelliteName = name,
            AosUtc = aosUtc,
            LosUtc = losUtc ?? aosUtc.AddMinutes(10),
            MaxElevationDeg = 45,
            MaxElevationUtc = aosUtc.AddMinutes(5),
            AosAzimuthDeg = 90,
            LosAzimuthDeg = 270
        };

    private static ScheduledPassEntry Entry(string noradId, DateTime aosUtc) =>
        new() { NoradId = noradId, AosUtc = aosUtc };

    [Fact]
    public void Process_fires_once_when_entering_lead_window()
    {
        var aos = new DateTime(2026, 8, 24, 12, 10, 0, DateTimeKind.Utc);
        var pass = Pass("25544", "ISS", aos);
        var reminder = new ScheduledPassReminder();
        var scheduled = new[] { Entry("25544", aos) };

        var early = reminder.Process(
            aos.AddMinutes(-10),
            scheduled,
            [pass],
            leadMinutesBeforeAos: 5);
        Assert.Empty(early);

        var due = reminder.Process(
            aos.AddMinutes(-5),
            scheduled,
            [pass],
            leadMinutesBeforeAos: 5);
        Assert.Single(due);
        Assert.Equal("25544", due[0].NoradId);

        var again = reminder.Process(
            aos.AddMinutes(-4),
            scheduled,
            [pass],
            leadMinutesBeforeAos: 5);
        Assert.Empty(again);
    }

    [Fact]
    public void Process_does_not_fire_after_aos()
    {
        var aos = new DateTime(2026, 8, 24, 12, 10, 0, DateTimeKind.Utc);
        var pass = Pass("25544", "ISS", aos);
        var reminder = new ScheduledPassReminder();

        var due = reminder.Process(
            aos.AddMinutes(1),
            [Entry("25544", aos)],
            [pass],
            leadMinutesBeforeAos: 5);

        Assert.Empty(due);
    }

    [Fact]
    public void Process_skips_when_no_scheduled_entries()
    {
        var aos = new DateTime(2026, 8, 24, 12, 10, 0, DateTimeKind.Utc);
        var reminder = new ScheduledPassReminder();

        var due = reminder.Process(
            aos.AddMinutes(-1),
            [],
            [Pass("25544", "ISS", aos)],
            leadMinutesBeforeAos: 5);

        Assert.Empty(due);
    }

    [Fact]
    public void RematchAndPrune_updates_aos_within_tolerance()
    {
        var storedAos = new DateTime(2026, 8, 24, 12, 10, 0, DateTimeKind.Utc);
        var predictedAos = storedAos.AddSeconds(45);
        var pass = Pass("25544", "ISS", predictedAos);

        var rematched = ScheduledPassReminder.RematchAndPrune(
            [Entry("25544", storedAos)],
            [pass],
            utcNow: storedAos.AddMinutes(-30));

        Assert.Single(rematched);
        Assert.Equal(predictedAos, rematched[0].AosUtc);
    }

    [Fact]
    public void RematchAndPrune_drops_entries_past_los()
    {
        var aos = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
        var los = aos.AddMinutes(8);
        var pass = Pass("25544", "ISS", aos, los);

        var rematched = ScheduledPassReminder.RematchAndPrune(
            [Entry("25544", aos)],
            [pass],
            utcNow: los.AddSeconds(1));

        Assert.Empty(rematched);
    }

    [Fact]
    public void RematchAndPrune_drops_unmatched_entries()
    {
        var aos = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

        var rematched = ScheduledPassReminder.RematchAndPrune(
            [Entry("25544", aos)],
            [Pass("43017", "AO-91", aos)],
            utcNow: aos.AddMinutes(-30));

        Assert.Empty(rematched);
    }

    [Fact]
    public void FindMatchingPass_rejects_aos_outside_tolerance()
    {
        var aos = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
        var pass = Pass("25544", "ISS", aos.AddMinutes(3));

        Assert.Null(ScheduledPassReminder.FindMatchingPass([pass], "25544", aos));
    }

    [Fact]
    public void Toggle_adds_and_removes()
    {
        var aos = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

        var added = ScheduledPassReminder.Toggle([], "25544", aos);
        Assert.Single(added);
        Assert.True(ScheduledPassReminder.IsScheduled(added, "25544", aos));

        var removed = ScheduledPassReminder.Toggle(added, "25544", aos.AddSeconds(30));
        Assert.Empty(removed);
    }

    [Fact]
    public void ClampLeadMinutes_bounds_values()
    {
        Assert.Equal(1, PassScheduleSettings.ClampLeadMinutes(0));
        Assert.Equal(60, PassScheduleSettings.ClampLeadMinutes(99));
        Assert.Equal(5, PassScheduleSettings.ClampLeadMinutes(5));
    }
}
