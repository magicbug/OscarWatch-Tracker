using OscarWatch.Core.Ft4;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4OscarWatchSpotTests
{
    private const long UplinkHz = 145_995_635;
    private const long DownlinkHz = 435_612_000;
    private static readonly DateTime Slot = new(2026, 10, 1, 17, 47, 0, DateTimeKind.Utc);

    private static LiveTrackerSnapshot Snapshot(
        string satellite = "RS-44",
        long uplinkHz = UplinkHz,
        long downlinkHz = DownlinkHz,
        string uplinkMode = "USB",
        string downlinkMode = "USB") =>
        new(satellite, uplinkMode, downlinkMode, uplinkHz, downlinkHz, "2m", "70cm", "FT4");

    private static Ft4DecodedMessage Decode(string text, float snr = -9.4f, bool own = false, bool tx = false)
    {
        Ft4MessageCodec.TryParse(text, out var to, out var de, out var extra);
        return new Ft4DecodedMessage(Slot, text, 1500, 0.1f, snr, to, de, extra, own, tx);
    }

    [Fact]
    public void Spot_uses_heard_station_grid_and_both_frequencies()
    {
        Assert.True(Ft4OscarWatchSpots.TryCreate(
            Decode("CQ EA3ZZ JN11"),
            Snapshot(),
            "MM9SQL",
            "OscarWatch-Tracker/1.6.0",
            out var spot));

        Assert.Equal("RS-44", spot.Satellite);
        Assert.Equal("EA3ZZ", spot.HeardCallsign);
        Assert.Equal("JN11", spot.HeardGrid);
        Assert.Equal(-9, spot.SnrDb);
        Assert.Equal(UplinkHz, spot.UplinkHz);
        Assert.Equal(DownlinkHz, spot.DownlinkHz);
        Assert.Equal(Slot, spot.HeardAtUtc);
        Assert.Equal("CQ EA3ZZ JN11", spot.Message);
        Assert.Equal("OscarWatch-Tracker/1.6.0", spot.Client);
    }

    [Fact]
    public void Report_and_rr73_do_not_become_a_grid()
    {
        Assert.True(Ft4OscarWatchSpots.TryCreate(Decode("MM9SQL W1AW -12"), Snapshot(), "MM9SQL", "client", out var report));
        Assert.Null(report.HeardGrid);
        Assert.Equal("W1AW", report.HeardCallsign);

        Assert.True(Ft4OscarWatchSpots.TryCreate(Decode("MM9SQL W1AW RR73"), Snapshot(), "MM9SQL", "client", out var rr73));
        Assert.Null(rr73.HeardGrid);
    }

    [Fact]
    public void Six_character_grid_keeps_a_lower_case_subsquare()
    {
        Assert.True(Ft4OscarWatchSpots.TryCreate(Decode("CQ W1AW FN31pr"), Snapshot(), "MM9SQL", "client", out var spot));
        Assert.Equal("FN31pr", spot.HeardGrid);
    }

    [Fact]
    public void Own_station_transmit_echo_and_rejected_hint_are_not_spotted()
    {
        Assert.False(Ft4OscarWatchSpots.TryCreate(Decode("CQ MM9SQL IO85"), Snapshot(), "mm9sql", "client", out _));
        Assert.False(Ft4OscarWatchSpots.TryCreate(Decode("CQ W1AW FN31", own: true), Snapshot(), "MM9SQL", "client", out _));
        Assert.False(Ft4OscarWatchSpots.TryCreate(Decode("CQ W1AW FN31", tx: true), Snapshot(), "MM9SQL", "client", out _));
        Assert.False(Ft4OscarWatchSpots.TryCreate(
            Decode("GM4VXE JW7XK -05") with { IsApriori = true, IsRejected = true },
            Snapshot(),
            "GM4VXE",
            "client",
            out _));
    }

    [Fact]
    public void Portable_suffix_is_not_treated_as_this_station()
    {
        Assert.True(Ft4OscarWatchSpots.TryCreate(Decode("CQ MM9SQL/P IO85"), Snapshot(), "MM9SQL", "client", out var spot));
        Assert.Equal("MM9SQL/P", spot.HeardCallsign);
    }

    [Fact]
    public void Cq_modifier_is_not_a_callsign()
    {
        Assert.False(Ft4OscarWatchSpots.TryCreate(Decode("CQ POTA"), Snapshot(), "MM9SQL", "client", out _));
        Assert.True(Ft4OscarWatchSpots.TryCreate(Decode("CQ DX W1AW"), Snapshot(), "MM9SQL", "client", out var spot));
        Assert.Equal("W1AW", spot.HeardCallsign);
    }

    [Theory]
    [InlineData("ISS", "FM", "FM", null)]
    [InlineData("ISS (ZARYA)", "FM", "FM", "25544")]
    [InlineData("FO-29", "LSB", "USB", null)]
    [InlineData("JAS 2", "LSB", "USB", "24278")]
    [InlineData("AO-07", "LSB", "USB", null)]
    [InlineData("OSCAR 7", "LSB", "USB", "07530")]
    [InlineData("RS-44", "", "", null)]
    public void Blocked_or_unknown_radio_mode_is_not_spotted(
        string satellite,
        string uplinkMode,
        string downlinkMode,
        string? noradId)
    {
        Assert.False(Ft4OscarWatchSpots.TryCreate(
            Decode("CQ W1AW FN31"),
            Snapshot(satellite, uplinkMode: uplinkMode, downlinkMode: downlinkMode),
            "MM9SQL",
            "client",
            noradId,
            out _));
    }

    [Fact]
    public void Missing_satellite_is_not_spotted() =>
        Assert.False(Ft4OscarWatchSpots.TryCreate(Decode("CQ W1AW FN31"), Snapshot(satellite: "  "), "MM9SQL", "client", out _));

    [Fact]
    public void Missing_frequencies_are_omitted()
    {
        Assert.True(Ft4OscarWatchSpots.TryCreate(Decode("CQ W1AW FN31"), Snapshot(uplinkHz: 0, downlinkHz: 0), "MM9SQL", "client", out var spot));
        Assert.Null(spot.UplinkHz);
        Assert.Null(spot.DownlinkHz);
    }

    [Fact]
    public void Snr_is_clamped_and_message_is_trimmed()
    {
        var message = "CQ W1AW FN31" + new string('X', 70);
        var client = new string('C', 140);
        Assert.True(Ft4OscarWatchSpots.TryCreate(Decode(message, snr: 80), Snapshot(), "MM9SQL", client, out var spot));
        Assert.Equal(50, spot.SnrDb);
        Assert.Equal(64, spot.Message!.Length);
        Assert.Equal(128, spot.Client!.Length);
        Assert.StartsWith("CQ W1AW", spot.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("pat", true)]
    public void Token_must_be_present(string? token, bool expected) =>
        Assert.Equal(expected, Ft4OscarWatchSpots.HasApiToken(token));

    [Fact]
    public async Task Reporter_drops_a_repeat_of_the_same_slot()
    {
        var calls = 0;
        var done = new TaskCompletionSource();
        using var reporter = Reporter(
            (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                done.TrySetResult();
                return Task.FromResult(new SatelliteSpotResult(true, "ok", 202));
            });

        var spot = Sample();
        Assert.True(reporter.TryEnqueue(spot));
        Assert.False(reporter.TryEnqueue(spot));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Reporter_does_not_queue_when_reporting_is_off()
    {
        using var reporter = Reporter((_, _, _) => throw new InvalidOperationException("should not send"), enabled: false);
        Assert.False(reporter.TryEnqueue(Sample()));
    }

    [Fact]
    public async Task Reporter_does_not_post_without_a_token()
    {
        var calls = 0;
        var noticed = new TaskCompletionSource<string>();
        using var reporter = Reporter(
            (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new SatelliteSpotResult(true, "ok", 202));
            },
            token: "");
        reporter.Diagnostic += (message, _) => noticed.TrySetResult(message);

        Assert.True(reporter.TryEnqueue(Sample()));
        var message = await noticed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Contains("API token", message, StringComparison.Ordinal);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Reporter_retries_a_transient_failure()
    {
        var calls = 0;
        var done = new TaskCompletionSource();
        using var reporter = Reporter((_, _, _) =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1)
                return Task.FromResult(new SatelliteSpotResult(false, "HTTP 500", 500));
            done.TrySetResult();
            return Task.FromResult(new SatelliteSpotResult(true, "ok", 202));
        });

        Assert.True(reporter.TryEnqueue(Sample()));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, calls);
    }

    private static OscarWatchSatelliteSpot Sample() =>
        new("RS-44", "W1AW", "FN31", -7, UplinkHz, DownlinkHz, Slot, "CQ W1AW FN31", "OscarWatch-Tracker/1.6.0");

    private static OscarWatchSpotReporter Reporter(
        Func<SatelliteStatusSettings, OscarWatchSatelliteSpot, CancellationToken, Task<SatelliteSpotResult>> submit,
        bool enabled = true,
        string token = "token") =>
        new(
            submit,
            () => enabled,
            () => new SatelliteStatusSettings { BaseUrl = "https://oscarwatch.org", ApiToken = token },
            (_, _) => TimeSpan.Zero);
}
