using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4DecodeDepthTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(19.9)]
    public void Low_elevation_uses_deep_decode(double elevationDeg)
    {
        Assert.True(Ft4DecodeDepth.UseDeep(elevationDeg));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(45)]
    [InlineData(90)]
    public void Mid_pass_stays_on_the_fast_budget(double elevationDeg)
    {
        Assert.False(Ft4DecodeDepth.UseDeep(elevationDeg));
    }

    [Fact]
    public void Below_the_horizon_stays_fast()
    {
        Assert.False(Ft4DecodeDepth.UseDeep(-2));
    }

    [Fact]
    public void Missing_elevation_stays_fast()
    {
        Assert.False(Ft4DecodeDepth.UseDeep(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2.5)]
    [InlineData(4.9)]
    public void First_few_degrees_decode_the_full_slot(double elevationDeg)
    {
        Assert.True(Ft4DecodeDepth.UseFullSlotDecode(elevationDeg));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(-0.5)]
    public void Full_slot_decode_is_off_outside_the_first_few_degrees(double elevationDeg)
    {
        Assert.False(Ft4DecodeDepth.UseFullSlotDecode(elevationDeg));
    }

    [Fact]
    public void Full_slot_decode_is_off_when_elevation_is_unknown()
    {
        Assert.False(Ft4DecodeDepth.UseFullSlotDecode(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(40)]
    public void Hinted_replies_are_tried_while_the_satellite_is_up(double elevationDeg)
    {
        Assert.True(Ft4DecodeDepth.UseApriori(elevationDeg));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(-3)]
    public void Hinted_replies_stop_once_the_satellite_has_set(double elevationDeg)
    {
        Assert.False(Ft4DecodeDepth.UseApriori(elevationDeg));
    }

    [Fact]
    public void Hinted_replies_stop_when_elevation_is_unknown()
    {
        Assert.False(Ft4DecodeDepth.UseApriori(null));
    }

    [Theory]
    [InlineData(-0.4)]
    [InlineData(-0.3)]
    [InlineData(-0.1)]
    [InlineData(0)]
    [InlineData(0.4)]
    [InlineData(0.5)]
    [InlineData(0.6)]
    [InlineData(1.2)]
    [InlineData(1.5)]
    public void Satellite_dt_keeps_a_normal_ft4_start(double timeSec)
    {
        Assert.True(Ft4DecodeDepth.IsPlausibleSatelliteDt(timeSec));
    }

    [Theory]
    [InlineData(-0.41)]
    [InlineData(-0.5)]
    [InlineData(-0.6)]
    [InlineData(1.6)]
    [InlineData(2.3)]
    [InlineData(2.5)]
    public void Moonbounce_dt_is_not_a_satellite_copy(double timeSec)
    {
        Assert.False(Ft4DecodeDepth.IsPlausibleSatelliteDt(timeSec));
    }

    [Theory]
    [InlineData(-0.3)]
    [InlineData(-0.1)]
    public void Early_rs44_copies_are_accepted_on_the_satellite_path(double timeSec)
    {
        Assert.True(Ft4DecodeDepth.IsPlausibleSatelliteDt(timeSec));
    }

    [Fact]
    public void R_plus_35_hint_at_raw_minus_half_second_stays_rejected()
    {
        Assert.False(Ft4DecodeDepth.IsPlausibleSatelliteDt(-0.5));
    }

    [Fact]
    public void Hinted_reply_on_the_snr_floor_is_not_published()
    {
        Assert.False(Ft4DecodeDepth.IsPublishableHint(0.4, Ft4DecodeDepth.ApSnrFloorDb));
        Assert.True(Ft4DecodeDepth.IsPublishableHint(0.4, -18));
    }
}
