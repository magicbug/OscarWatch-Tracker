using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

public sealed class PassSidebarMergeTests
{
    private static readonly DateTime Base = new(2026, 6, 15, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MergeInProgressPasses_retains_row_missing_from_new_prediction()
    {
        var current = Pass("RS-44", "999", Base.AddMinutes(-5), Base.AddMinutes(15));
        var next = Pass("RS-44", "999", Base.AddHours(1), Base.AddHours(1).AddMinutes(20));

        var merged = PassSidebarMerge.MergeInProgressPasses(
            [next],
            [current],
            Base);

        Assert.Equal(2, merged.Count);
        Assert.Equal(current.AosUtc, merged[0].AosUtc);
        Assert.Equal(next.AosUtc, merged[1].AosUtc);
    }

    [Fact]
    public void FindPassForRecording_does_not_attach_to_future_pass_when_recording_already_started()
    {
        var current = Pass("RS-44", "999", Base.AddMinutes(-5), Base.AddMinutes(15));
        var next = Pass("RS-44", "999", Base.AddHours(1), Base.AddHours(1).AddMinutes(20));

        var match = PassSidebarMerge.FindPassForRecording(
            [next],
            "999",
            Base,
            recordingStartedUtc: Base.AddMinutes(-2));

        Assert.Null(match);
    }

    [Fact]
    public void FindPassForRecording_prefers_in_progress_pass()
    {
        var current = Pass("RS-44", "999", Base.AddMinutes(-5), Base.AddMinutes(15));
        var next = Pass("RS-44", "999", Base.AddHours(1), Base.AddHours(1).AddMinutes(20));

        var match = PassSidebarMerge.FindPassForRecording(
            [current, next],
            "999",
            Base,
            recordingStartedUtc: Base.AddMinutes(-2));

        Assert.Equal(current.AosUtc, match!.AosUtc);
    }

    [Fact]
    public void FindPassForRecording_matches_pass_active_at_recording_start()
    {
        var current = Pass("RS-44", "999", Base.AddMinutes(-10), Base.AddMinutes(10));

        var match = PassSidebarMerge.FindPassForRecording(
            [current],
            "999",
            Base.AddMinutes(8),
            recordingStartedUtc: Base);

        Assert.Equal(current.AosUtc, match!.AosUtc);
    }

    [Fact]
    public void FindPassForRecording_binds_refreshed_pass_when_aos_shifts()
    {
        var refreshed = Pass("RS-44", "999", Base.AddMinutes(-4.8), Base.AddMinutes(10.2));

        var match = PassSidebarMerge.FindPassForRecording(
            [refreshed],
            "999",
            Base,
            recordingStartedUtc: Base.AddMinutes(-2));

        Assert.Equal(refreshed.AosUtc, match!.AosUtc);
    }

    [Fact]
    public void IsPassRecordingTarget_shows_rec_after_refresh_when_aos_drifts()
    {
        var beforeRefresh = Pass("RS-44", "999", Base.AddMinutes(-5), Base.AddMinutes(10));
        var afterRefresh = Pass("RS-44", "999", Base.AddMinutes(-4.8), Base.AddMinutes(10.2));
        var started = Base.AddMinutes(-2);

        Assert.True(PassSidebarMerge.IsPassRecordingTarget(
            afterRefresh,
            "999",
            afterRefresh.AosUtc,
            started,
            Base,
            isRecording: true));

        Assert.True(PassSidebarMerge.IsPassRecordingTarget(
            afterRefresh,
            "999",
            beforeRefresh.AosUtc,
            started,
            Base,
            isRecording: true));
    }

    [Fact]
    public void IsPassRecordingTarget_does_not_attach_to_later_pass_same_satellite()
    {
        var current = Pass("RS-44", "999", Base.AddMinutes(-5), Base.AddMinutes(10));
        var later = Pass("RS-44", "999", Base.AddHours(1), Base.AddHours(1).AddMinutes(15));
        var started = Base.AddMinutes(-2);

        Assert.False(PassSidebarMerge.IsPassRecordingTarget(
            later,
            "999",
            current.AosUtc,
            started,
            Base,
            isRecording: true));
    }

    [Fact]
    public void FindPassForRecording_binds_when_recording_started_just_before_aos()
    {
        var upcoming = Pass("ISS", "25544", Base.AddMinutes(1), Base.AddMinutes(10));
        var started = Base; // one minute before AOS

        var match = PassSidebarMerge.FindPassForRecording(
            [upcoming],
            "25544",
            Base,
            recordingStartedUtc: started);

        Assert.Equal(upcoming.AosUtc, match!.AosUtc);
    }

    [Fact]
    public void IsPassRecordingTarget_shows_rec_in_pre_aos_grace_window()
    {
        var pass = Pass("ISS", "25544", Base.AddMinutes(1), Base.AddMinutes(10));
        var started = Base;

        Assert.True(PassSidebarMerge.IsPassRecordingTarget(
            pass,
            "25544",
            pass.AosUtc,
            started,
            Base.AddSeconds(30),
            isRecording: true));
    }

    [Fact]
    public void IsPassRecordingTarget_hides_rec_after_los()
    {
        var pass = Pass("ISS", "25544", Base.AddMinutes(-10), Base.AddMinutes(-1));
        var started = Base.AddMinutes(-8);

        Assert.False(PassSidebarMerge.IsPassRecordingTarget(
            pass,
            "25544",
            pass.AosUtc,
            started,
            Base,
            isRecording: true));
    }

    [Fact]
    public void WithinPredictionWindow_hides_short_passes_and_passes_beyond_the_sidebar_window()
    {
        var soon = Pass("ISS", "25544", Base.AddHours(2), Base.AddHours(2).AddMinutes(1.5));
        var longEnough = Pass("AO-91", "43017", Base.AddHours(3), Base.AddHours(3).AddMinutes(8));
        var later = Pass("SO-50", "27607", Base.AddHours(50), Base.AddHours(50).AddMinutes(10));

        var visible = PassSidebarMerge.WithinPredictionWindow(
            [soon, longEnough, later],
            Base,
            predictionHours: 48,
            minimumDurationMinutes: 2);

        Assert.Single(visible);
        Assert.Equal("43017", visible[0].NoradId);
    }

    private static PassInfo Pass(string name, string norad, DateTime aos, DateTime los) => new()
    {
        SatelliteName = name,
        NoradId = norad,
        AosUtc = aos,
        LosUtc = los,
        MaxElevationUtc = aos + (los - aos) / 2,
        MaxElevationDeg = 30
    };
}
