using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4CallDtTests
{
    [Fact]
    public void Hint_at_a_different_delay_from_the_same_station_is_rejected()
    {
        var timing = new Ft4CallDt();
        timing.NoteReliable("ON8NT", 0.4, -12);

        Assert.False(timing.AllowsHint("ON8NT", -0.5));
        Assert.True(timing.AllowsHint("ON8NT", 0.4));
        Assert.True(timing.AllowsHint("EA3EA", -0.3));
    }

    [Theory]
    [InlineData(-0.3)]
    [InlineData(-0.1)]
    public void Early_satellite_dt_is_remembered_for_hints(double timeSec)
    {
        var timing = new Ft4CallDt();
        timing.NoteReliable("VU2LBW", timeSec, -10);

        Assert.True(timing.AllowsHint("VU2LBW", timeSec));
    }

    [Fact]
    public void R_plus_35_raw_dt_is_not_remembered_for_hints()
    {
        var timing = new Ft4CallDt();
        timing.NoteReliable("ON8NT", -0.5, -12);

        Assert.True(timing.AllowsHint("ON8NT", -0.5));
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(-0.6)]
    public void Outside_satellite_dt_is_not_publishable_as_hint(double timeSec)
    {
        Assert.False(Ft4DecodeDepth.IsPublishableHint(timeSec, -12));
    }

    [Fact]
    public void Snr_floor_does_not_set_the_station_delay()
    {
        var timing = new Ft4CallDt();
        timing.NoteReliable("ON8NT", 0.4, Ft4DecodeDepth.ApSnrFloorDb);

        Assert.True(timing.AllowsHint("ON8NT", 0.4));
    }
}
