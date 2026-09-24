using OscarWatch.Core.Ft4;
using Kind = OscarWatch.Core.Ft4.Ft4TuneCalibrator.ActionKind;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4TuneCalibratorTests
{
    [Fact]
    public void One_reading_alone_does_not_move_the_trim()
    {
        var cal = new Ft4TuneCalibrator();

        Assert.Equal(Kind.None, cal.Next(-240).Kind);
    }

    [Fact]
    public void Two_agreeing_slots_apply_their_average()
    {
        var cal = new Ft4TuneCalibrator();
        cal.Next(-530);

        var decision = cal.Next(-520);

        Assert.Equal(Kind.Apply, decision.Kind);
        Assert.Equal(-525, decision.CorrectionHz, 3);
    }

    [Fact]
    public void Jumping_readings_never_apply()
    {
        var cal = new Ft4TuneCalibrator();

        foreach (var reading in new double[] { -240, -504, -319, -529, -140 })
            Assert.Equal(Kind.None, cal.Next(reading).Kind);
    }

    [Fact]
    public void Tone_landing_on_the_marker_confirms_the_correction()
    {
        var cal = new Ft4TuneCalibrator();
        cal.Next(-530);
        cal.Next(-530);

        var decision = cal.Next(12);

        Assert.Equal(Kind.Confirmed, decision.Kind);
        Assert.False(cal.GaveUp);
    }

    [Fact]
    public void Tone_that_did_not_move_undoes_the_correction_and_stops()
    {
        var cal = new Ft4TuneCalibrator();
        cal.Next(-530);
        cal.Next(-530);

        var decision = cal.Next(-528);

        Assert.Equal(Kind.Undo, decision.Kind);
        Assert.Equal(530, decision.CorrectionHz, 3);
        Assert.True(cal.GaveUp);
        Assert.Equal(Kind.None, cal.Next(-530).Kind);
        Assert.Equal(Kind.None, cal.Next(-530).Kind);
    }

    [Fact]
    public void Losing_the_tone_after_a_correction_undoes_it()
    {
        var cal = new Ft4TuneCalibrator();
        cal.Next(300);
        cal.Next(310);

        var decision = cal.Next(null);

        Assert.Equal(Kind.Undo, decision.Kind);
        Assert.Equal(-305, decision.CorrectionHz, 3);
    }

    [Fact]
    public void Tone_already_on_the_marker_is_left_alone()
    {
        var cal = new Ft4TuneCalibrator();

        Assert.Equal(Kind.None, cal.Next(8).Kind);
        Assert.Equal(Kind.None, cal.Next(-10).Kind);
        Assert.Null(cal.CandidateErrorHz);
    }

    [Fact]
    public void Reset_lets_a_new_tune_try_again()
    {
        var cal = new Ft4TuneCalibrator();
        cal.Next(-530);
        cal.Next(-530);
        cal.Next(-530);
        Assert.True(cal.GaveUp);

        cal.Reset();
        cal.Next(-200);

        Assert.Equal(Kind.Apply, cal.Next(-205).Kind);
    }
}
