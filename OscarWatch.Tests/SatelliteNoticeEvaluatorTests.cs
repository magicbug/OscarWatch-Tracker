using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

public class SatelliteNoticeEvaluatorTests
{
    [Fact]
    public void Wednesday_notice_is_active_only_on_wednesday_utc()
    {
        var entry = EntryWithNotice(
            "Wednesdays (UTC): FSK digital modes.",
            days: ["Wednesday"]);

        var wednesday = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc); // Wednesday
        var thursday = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc); // Thursday

        Assert.True(SatelliteNoticeEvaluator.HasActiveNotice(entry, wednesday));
        Assert.False(SatelliteNoticeEvaluator.HasActiveNotice(entry, thursday));
        Assert.Equal(
            "Wednesdays (UTC): FSK digital modes.",
            SatelliteNoticeEvaluator.FormatActiveNoticeText(entry, wednesday));
        Assert.Equal("", SatelliteNoticeEvaluator.FormatActiveNoticeText(entry, thursday));
    }

    [Fact]
    public void Notice_without_days_is_always_active()
    {
        var entry = EntryWithNotice("Always on notice.");

        var monday = new DateTime(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc);
        Assert.True(SatelliteNoticeEvaluator.HasActiveNotice(entry, monday));
        Assert.Equal("Always on notice.", SatelliteNoticeEvaluator.FormatActiveNoticeText(entry, monday));
    }

    [Fact]
    public void Mode_filter_hides_notice_for_other_modes()
    {
        var entry = EntryWithNotice(
            "FM schedule",
            days: null,
            modes: ["FM Repeater"]);

        var utc = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(SatelliteNoticeEvaluator.HasActiveNotice(entry, utc, modeType: null));
        Assert.True(SatelliteNoticeEvaluator.HasActiveNotice(entry, utc, modeType: "FM Repeater"));
        Assert.False(SatelliteNoticeEvaluator.HasActiveNotice(entry, utc, modeType: "CW"));
    }

    [Fact]
    public void CanonicalizeDayOfWeek_accepts_case_insensitive_names()
    {
        Assert.Equal("Wednesday", SatelliteNoticeEvaluator.CanonicalizeDayOfWeek("wednesday"));
        Assert.Equal("Sunday", SatelliteNoticeEvaluator.CanonicalizeDayOfWeek("SUNDAY"));
        Assert.Null(SatelliteNoticeEvaluator.CanonicalizeDayOfWeek("Wed"));
        Assert.Null(SatelliteNoticeEvaluator.CanonicalizeDayOfWeek(""));
    }

    private static SatelliteRadioEntry EntryWithNotice(
        string message,
        IEnumerable<string>? days = null,
        IEnumerable<string>? modes = null) =>
        new()
        {
            Name = "SO-131",
            Modes =
            [
                new SatelliteTransponderMode
                {
                    Type = "FM Repeater",
                    DownlinkKHz = 436_888,
                    UplinkKHz = 145_925,
                    DownlinkMode = "FM",
                    UplinkMode = "FM"
                }
            ],
            Notices =
            [
                new SatelliteOperatingNotice
                {
                    Message = message,
                    DaysOfWeekUtc = days?.ToList(),
                    ModeTypes = modes?.ToList()
                }
            ]
        };
}
