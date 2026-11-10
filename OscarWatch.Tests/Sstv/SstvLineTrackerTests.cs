using OscarWatch.Core.Sstv;

namespace OscarWatch.Tests.Sstv;

public sealed class SstvLineTrackerTests
{
    private const double Nominal = 1800;

    [Fact]
    public void False_syncs_after_a_fade_do_not_bend_the_line_grid()
    {
        var tracker = new SstvLineTracker(Nominal);
        tracker.SetAnchor(500);
        const double period = Nominal * (1 + 300e-6);
        for (var line = 0; line < 20; line++)
            tracker.Accept(line, 500 + period * line + (line % 3 - 1) * 0.5, 0.8);

        // Noise as the signal comes back: five strong pulses well off the grid, all on one side.
        for (var line = 30; line < 35; line++)
            tracker.Accept(line, 500 + period * line + 60 + line % 2 * 15, 0.9);

        var expected = 500 + period * 36;
        Assert.InRange(tracker.Predict(36), expected - 2, expected + 2);
        Assert.InRange(tracker.PeriodAt(36, centred: false), period - 0.2, period + 0.2);
    }

    [Fact]
    public void A_clean_grid_is_fitted_exactly()
    {
        var tracker = new SstvLineTracker(Nominal);
        const double period = Nominal * (1 - 150e-6);
        for (var line = 0; line < 40; line++)
            tracker.Accept(line, 1000 + period * line, 0.9);

        Assert.Equal(1000 + period * 50, tracker.Predict(50), 3);
        Assert.Equal(period, tracker.PeriodAt(20, centred: true), 4);
    }
}
