using OscarWatch.Core.Sstv;
using Xunit.Abstractions;

namespace OscarWatch.Tests.Sstv;

/// <summary>
/// A step in the line timing (a gap or a repeat in the recording, a pause in the sender's audio) moves every
/// later sync by the same amount. The line grid is fitted over the last 48 lines, so before the tracker
/// started a new stretch at a confirmed step, the old lines kept pulling the predictions back to where the
/// lines used to be, and a picture tore for about 25 lines while the grid caught up. These tests step the timing
/// of synthetic pictures and check that the grid follows it, and that a pulse which is not a step is not
/// taken for one.
/// </summary>
public sealed class SstvLineTimingStepTests(ITestOutputHelper output)
{
    private const int Rate = 24000;
    private const double LeadSeconds = 0.5;

    private static SstvDecodedImage? Decode(SstvMode mode, float[] audio)
    {
        var images = SstvRoundTripTests.Decode(audio, new SstvDecoderOptions(), Rate);
        return images.Where(i => i.Mode == mode).OrderByDescending(i => i.LockPercent).FirstOrDefault();
    }

    /// <summary>Sample index where a line starts in the audio the test encoder writes.</summary>
    private static int LineStart(SstvMode mode, int line) =>
        (int)Math.Round(Rate * (LeadSeconds + 0.91 + (mode.LeadInMs + line * mode.LineMs) / 1000.0));

    private static float[] Step(float[] audio, int step)
    {
        var at = (int)(audio.Length * 0.55);
        var list = audio.ToList();
        if (step < 0)
            list.RemoveRange(at, -step);
        else
            list.InsertRange(at, list.GetRange(at - step, step));
        return list.ToArray();
    }

    [Fact]
    public void Tracker_follows_a_step_once_a_stretch_starts_and_keeps_the_old_lines_where_they_were()
    {
        const double period = 1000;
        var tracker = new SstvLineTracker(period);
        tracker.SetAnchor(100);
        for (var line = 0; line < 30; line++)
            tracker.Accept(line, 100 + line * period, 0.9);

        // Without a new stretch the old lines outvote one new line, so the prediction stays on the old grid.
        tracker.Accept(30, 100 + 30 * period + 165, 0.9);
        Assert.True(Math.Abs(tracker.Predict(31) - (100 + 31 * period + 165)) > 100);

        tracker.StartSegment(30);
        tracker.Accept(30, 100 + 30 * period + 165, 0.9);
        Assert.Equal(100 + 31 * period + 165, tracker.Predict(31), precision: 0);

        tracker.Accept(31, 100 + 31 * period + 165, 0.9);
        tracker.Accept(32, 100 + 32 * period + 165, 0.9);
        Assert.Equal(100 + 33 * period + 165, tracker.Predict(33), precision: 0);

        // Lines before the step keep their own fit, even when the fit is centred across the step.
        Assert.Equal(100 + 29 * period, tracker.Fitted(29), precision: 0);
        Assert.Equal(100 + 10 * period, tracker.Fitted(10), precision: 0);
    }

    [Fact]
    public void Tracker_goes_back_to_the_old_grid_when_a_step_is_undone()
    {
        const double period = 1000;
        var tracker = new SstvLineTracker(period);
        tracker.SetAnchor(100);
        for (var line = 0; line < 30; line++)
            tracker.Accept(line, 100 + line * period, 0.9);

        tracker.StartSegment(30);
        tracker.Accept(30, 100 + 30 * period + 165, 0.9);
        tracker.UndoSegment(30);

        Assert.Equal(30, tracker.AcceptedCount);
        Assert.Equal(100 + 31 * period, tracker.Predict(31), precision: 0);
    }

    [Theory]
    [InlineData(330, null)]
    [InlineData(330, 14.0)]
    [InlineData(-330, 14.0)]
    public void Decoder_regains_the_grid_after_a_step_in_the_line_timing(int step, double? cnrDb)
    {
        var mode = SstvModeTable.Pd120;
        var audio = SstvTestEncoder.Encode(mode, Rate, new SstvTestEncoder.Settings { CnrDb = cnrDb });

        var image = Decode(mode, Step(audio, step));

        Assert.NotNull(image);
        output.WriteLine($"step {step} samples, CNR {cnrDb?.ToString() ?? "clean"}: lock {image!.LockPercent:0.0}%");
        Assert.True(image.LockPercent >= 96, $"lock {image.LockPercent:0.0}%");
    }

    [Fact]
    public void A_picture_with_no_step_is_unchanged()
    {
        var mode = SstvModeTable.Pd120;
        var image = Decode(mode, SstvTestEncoder.Encode(mode, Rate));

        Assert.NotNull(image);
        Assert.True(image!.LockPercent > 99.9, $"lock {image.LockPercent:0.0}%");
        Assert.True(SstvRoundTripTests.MeanError(mode, image.Image) < 2);
    }

    [Fact]
    public void A_dropout_that_leaves_the_timing_alone_does_not_move_the_grid()
    {
        var mode = SstvModeTable.Pd120;
        var audio = SstvTestEncoder.Encode(mode, Rate);
        Array.Clear(audio, LineStart(mode, 100), LineStart(mode, 104) - LineStart(mode, 100));

        var image = Decode(mode, audio);

        Assert.NotNull(image);
        output.WriteLine($"dropout of four lines: lock {image!.LockPercent:0.0}%");
        // Four of 248 lines are gone; every other line must still be locked on the original grid.
        Assert.True(image.LockPercent >= 97, $"lock {image.LockPercent:0.0}%");
        Assert.True(image.LineFailures.Skip(106).All(f => f == SstvLineFailure.None), "lines after the dropout should be on the grid");
    }

    [Fact]
    public void A_lone_pulse_off_the_grid_after_a_dropout_is_not_taken_for_a_step()
    {
        // Four lines drop out, then one line arrives 10 ms late and the rest are on the original grid. The late
        // line is a strong sync well off the grid after several misses, which starts a trial step; the next line
        // does not agree with it, so the trial must be taken back.
        var mode = SstvModeTable.Pd120;
        var audio = SstvTestEncoder.Encode(mode, Rate).ToList();
        var a = LineStart(mode, 104);
        var b = LineStart(mode, 105);
        const int late = 240; // 10 ms
        audio.RemoveRange(b - late, late);
        audio.InsertRange(a, new float[late]);
        for (var i = LineStart(mode, 100); i < a; i++)
            audio[i] = 0;

        var image = Decode(mode, audio.ToArray());

        Assert.NotNull(image);
        output.WriteLine($"lone late pulse: lock {image!.LockPercent:0.0}%");
        Assert.True(image.LockPercent >= 95, $"lock {image.LockPercent:0.0}%");
        // The line straight after the late pulse is the one a wrongly kept step would miss.
        Assert.True(
            image.LineQuality.Skip(105).All(q => q == SstvLineQuality.Locked) && image.LineFailures.Skip(105).All(f => f == SstvLineFailure.None),
            "every line after the late pulse should be locked on the original grid");
    }
}
