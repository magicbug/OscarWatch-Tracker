using OscarWatch.Core.Ft4;
using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4RfPowerLimitTests
{
    [Theory]
    [InlineData(30.0, false)]
    [InlineData(30.01, true)]
    [InlineData(50.0, true)]
    [InlineData(10.0, false)]
    public void ExceedsLimit_uses_strictly_greater_than_30(double watts, bool expected) =>
        Assert.Equal(expected, Ft4RfPowerLimit.ExceedsLimit(watts));
}

public sealed class IcomRfPowerEstimatorTests
{
    [Fact]
    public void Ic9700_2m_full_scale_is_100_watts()
    {
        Assert.True(IcomRfPowerEstimator.TryEstimateWatts(
            RigType.IcomIc9700, 145_900_000, 255, out var watts));
        Assert.Equal(100.0, watts, precision: 3);
    }

    [Fact]
    public void Ic9700_70cm_level_that_exceeds_30w()
    {
        // 30 W on 75 W max => 30/75 * 255 = 102
        Assert.True(IcomRfPowerEstimator.TryEstimateWatts(
            RigType.IcomIc9700, 435_800_000, 120, out var watts));
        Assert.True(Ft4RfPowerLimit.ExceedsLimit(watts));
    }

    [Fact]
    public void Ic9700_70cm_level_under_30w()
    {
        Assert.True(IcomRfPowerEstimator.TryEstimateWatts(
            RigType.IcomIc9700, 435_800_000, 80, out var watts));
        Assert.False(Ft4RfPowerLimit.ExceedsLimit(watts));
    }

    [Fact]
    public void Ic9700_23cm_max_is_10w_never_exceeds_ft4_limit()
    {
        Assert.True(IcomRfPowerEstimator.TryEstimateWatts(
            RigType.IcomIc9700, 1_269_500_000, 255, out var watts));
        Assert.Equal(10.0, watts, precision: 3);
        Assert.False(Ft4RfPowerLimit.ExceedsLimit(watts));
    }

    [Fact]
    public void Unsupported_rig_returns_false()
    {
        Assert.False(IcomRfPowerEstimator.TryEstimateWatts(
            RigType.YaesuFt991a, 145_900_000, 200, out _));
    }

    [Fact]
    public void Ic9700_70cm_level_for_30w_is_102()
    {
        Assert.True(IcomRfPowerEstimator.TryLevelForWatts(
            RigType.IcomIc9700, 435_000_000, Ft4RfPowerLimit.MaxWatts, out var level));
        Assert.Equal(102, level);
        Assert.True(IcomRfPowerEstimator.TryEstimateWatts(
            RigType.IcomIc9700, 435_000_000, level, out var setWatts));
        Assert.False(Ft4RfPowerLimit.ExceedsLimit(setWatts));
    }

    [Fact]
    public void Level_for_30w_on_100w_band_stays_at_or_under_the_limit()
    {
        Assert.True(IcomRfPowerEstimator.TryLevelForWatts(
            RigType.IcomIc9700, 145_900_000, Ft4RfPowerLimit.MaxWatts, out var level));
        Assert.True(IcomRfPowerEstimator.TryEstimateWatts(
            RigType.IcomIc9700, 145_900_000, level, out var watts));
        Assert.False(Ft4RfPowerLimit.ExceedsLimit(watts));
    }

    [Fact]
    public void Unknown_band_cannot_map_a_wattage_to_a_level() =>
        Assert.False(IcomRfPowerEstimator.TryLevelForWatts(
            RigType.YaesuFt991a, 145_900_000, 200, out _));

    [Theory]
    [InlineData(Ic910PowerClass.H, 145_900_000, 100)]
    [InlineData(Ic910PowerClass.H, 435_800_000, 75)]
    [InlineData(Ic910PowerClass.D, 145_900_000, 50)]
    [InlineData(Ic910PowerClass.D, 435_800_000, 50)]
    [InlineData(Ic910PowerClass.Base, 145_900_000, 20)]
    [InlineData(Ic910PowerClass.Base, 435_800_000, 20)]
    [InlineData(Ic910PowerClass.H, 1_269_500_000, 10)]
    [InlineData(Ic910PowerClass.D, 1_269_500_000, 10)]
    [InlineData(Ic910PowerClass.Base, 1_269_500_000, 10)]
    public void Ic910_full_scale_follows_the_power_class(Ic910PowerClass powerClass, long hz, int expectedWatts)
    {
        Assert.True(IcomRfPowerEstimator.TryEstimateWatts(
            RigType.IcomIc910, hz, 255, out var watts, powerClass));
        Assert.Equal(expectedWatts, watts, precision: 3);
    }

    [Fact]
    public void Ic910_without_a_class_keeps_the_h_scale()
    {
        Assert.True(IcomRfPowerEstimator.TryEstimateWatts(
            RigType.IcomIc910, 145_900_000, 255, out var watts));
        Assert.Equal(100.0, watts, precision: 3);
    }

    [Fact]
    public void Ic910_d_2m_level_for_30w_stays_at_or_under_the_limit()
    {
        Assert.True(IcomRfPowerEstimator.TryLevelForWatts(
            RigType.IcomIc910, 145_900_000, Ft4RfPowerLimit.MaxWatts, out var level, Ic910PowerClass.D));
        Assert.Equal(153, level);
        Assert.True(IcomRfPowerEstimator.TryEstimateWatts(
            RigType.IcomIc910, 145_900_000, level, out var watts, Ic910PowerClass.D));
        Assert.False(Ft4RfPowerLimit.ExceedsLimit(watts));
    }

    [Fact]
    public void Ic910_base_70cm_30w_request_uses_full_scale()
    {
        Assert.True(IcomRfPowerEstimator.TryLevelForWatts(
            RigType.IcomIc910, 435_800_000, Ft4RfPowerLimit.MaxWatts, out var level, Ic910PowerClass.Base));
        Assert.Equal(255, level);
        Assert.True(IcomRfPowerEstimator.TryEstimateWatts(
            RigType.IcomIc910, 435_800_000, level, out var watts, Ic910PowerClass.Base));
        Assert.Equal(20.0, watts, precision: 3);
        Assert.False(Ft4RfPowerLimit.ExceedsLimit(watts));
    }
}
