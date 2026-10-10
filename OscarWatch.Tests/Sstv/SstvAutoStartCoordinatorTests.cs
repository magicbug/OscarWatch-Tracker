using OscarWatch.Core.Sstv;

namespace OscarWatch.Tests.Sstv;

public sealed class SstvAutoStartCoordinatorTests
{
    private const string Norad = "25544";

    private static SstvAutoStartInput Input(
        double? elevation,
        bool running = false,
        bool auto = false,
        bool enabled = true,
        string? norad = Norad,
        double start = 5) =>
        new(enabled, norad, elevation, start, running, auto);

    [Fact]
    public void Rise_above_start_elevation_starts_once()
    {
        var c = new SstvAutoStartCoordinator();

        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(-2)));
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(3)));
        Assert.Equal(SstvAutoStartAction.Start, c.Process(Input(6)));
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(8, running: true, auto: true)));
    }

    [Fact]
    public void First_sample_already_above_start_elevation_starts()
    {
        var c = new SstvAutoStartCoordinator();

        Assert.Equal(SstvAutoStartAction.Start, c.Process(Input(30)));
    }

    [Fact]
    public void Does_not_start_again_within_the_same_pass_after_a_manual_stop()
    {
        var c = new SstvAutoStartCoordinator();
        Assert.Equal(SstvAutoStartAction.Start, c.Process(Input(6)));

        // User stops by hand while the satellite is still up.
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(8, running: false)));
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(10, running: false)));
    }

    [Fact]
    public void Does_not_start_when_the_receiver_is_already_running()
    {
        var c = new SstvAutoStartCoordinator();

        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(3)));
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(6, running: true)));
    }

    [Fact]
    public void Stops_after_three_consecutive_samples_below_zero()
    {
        var c = new SstvAutoStartCoordinator();
        Assert.Equal(SstvAutoStartAction.Start, c.Process(Input(6)));

        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(-1, running: true, auto: true)));
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(-1, running: true, auto: true)));
        Assert.Equal(SstvAutoStartAction.Stop, c.Process(Input(-1, running: true, auto: true)));
    }

    [Fact]
    public void A_single_noisy_sample_below_zero_does_not_stop_the_session()
    {
        var c = new SstvAutoStartCoordinator();
        Assert.Equal(SstvAutoStartAction.Start, c.Process(Input(6)));

        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(30, running: true, auto: true)));
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(-0.5, running: true, auto: true)));
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(20, running: true, auto: true)));
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(-0.5, running: true, auto: true)));
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(-0.5, running: true, auto: true)));
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(20, running: true, auto: true)));
    }

    [Fact]
    public void Manual_session_is_not_stopped_at_loss_of_signal()
    {
        var c = new SstvAutoStartCoordinator();

        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(-1, running: true, auto: false)));
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(-1, running: true, auto: false)));
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(-1, running: true, auto: false)));
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(-1, running: true, auto: false)));
    }

    [Fact]
    public void Rearms_after_the_satellite_sets_so_the_next_pass_starts()
    {
        var c = new SstvAutoStartCoordinator();
        Assert.Equal(SstvAutoStartAction.Start, c.Process(Input(6)));
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(-1, running: true, auto: true)));
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(-1, running: true, auto: true)));
        Assert.Equal(SstvAutoStartAction.Stop, c.Process(Input(-1, running: true, auto: true)));

        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(2)));
        Assert.Equal(SstvAutoStartAction.Start, c.Process(Input(6)));
    }

    [Fact]
    public void Disabling_the_option_stops_an_automatic_session()
    {
        var c = new SstvAutoStartCoordinator();
        Assert.Equal(SstvAutoStartAction.Start, c.Process(Input(6)));

        Assert.Equal(SstvAutoStartAction.Stop, c.Process(Input(10, running: true, auto: true, enabled: false)));
    }

    [Fact]
    public void Disabling_the_option_does_not_stop_a_manual_session()
    {
        var c = new SstvAutoStartCoordinator();

        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(10, running: true, auto: false, enabled: false)));
    }

    [Fact]
    public void Changing_focus_stops_an_automatic_session_and_tracks_the_new_satellite()
    {
        var c = new SstvAutoStartCoordinator();
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(10, running: true, auto: true, norad: Norad)));

        Assert.Equal(SstvAutoStartAction.Stop, c.Process(Input(10, running: true, auto: true, norad: "40967")));

        // The new satellite is already above the threshold, so it starts once the old session is gone.
        Assert.Equal(SstvAutoStartAction.Start, c.Process(Input(10, running: false, norad: "40967")));
    }

    [Fact]
    public void Missing_focus_does_nothing()
    {
        var c = new SstvAutoStartCoordinator();

        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(30, norad: null)));
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(30, norad: "  ")));
    }

    [Fact]
    public void Missing_or_non_finite_elevation_is_ignored_and_keeps_history()
    {
        var c = new SstvAutoStartCoordinator();
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(3)));

        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(null)));
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(double.NaN)));

        // Previous sample was 3, so this crossing still counts as a rise.
        Assert.Equal(SstvAutoStartAction.Start, c.Process(Input(6)));
    }

    [Fact]
    public void Reset_tracking_lets_a_window_reopened_mid_pass_start_straight_away()
    {
        var c = new SstvAutoStartCoordinator();
        Assert.Equal(SstvAutoStartAction.Start, c.Process(Input(6)));
        Assert.Equal(SstvAutoStartAction.None, c.Process(Input(8, running: false)));

        c.ResetTracking();

        Assert.Equal(SstvAutoStartAction.Start, c.Process(Input(8, running: false)));
    }
}
