using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;

namespace OscarWatch.Tests;

public class RigSatModeHelperTests
{
    public static IEnumerable<object[]> RigSatModeRows()
    {
        foreach (var row in GoldenFixtureLoader.Load().RigSatMode)
            yield return new object[] { row.DownlinkKHz, row.UplinkKHz, row.UseMainSub };
    }

    [Theory]
    [MemberData(nameof(RigSatModeRows))]
    public void UseMainSubLayout_matches_golden(double down, double up, bool expected) =>
        Assert.Equal(expected, RigSatModeHelper.UseMainSubLayout(down, up));

    [Theory]
    [InlineData(437_550, 0)]
    [InlineData(145_800, 0)]
    public void UseMainSubLayout_false_for_beacon_only_downlink(double down, double up) =>
        Assert.False(RigSatModeHelper.UseMainSubLayout(down, up));

    [Theory]
    [InlineData(145_825, 145_825, true)]
    [InlineData(145_800, 145_200, false)]
    [InlineData(145_950, 432_146, false)]
    public void IsSameBandSimplex_detects_equal_uplink_downlink(double down, double up, bool expected) =>
        Assert.Equal(expected, RigSatModeHelper.IsSameBandSimplex(down, up));

    [Theory]
    [InlineData(435_700_000, 145_865, true)]
    [InlineData(145_900_000, 145_865, false)]
    [InlineData(145_900_000, 435_667, true)]
    [InlineData(435_700_000, 435_667, false)]
    [InlineData(29_450_000, 29_450, false)]
    [InlineData(145_900_000, 29_450, true)]
    [InlineData(435_700_000, 29_450, true)]
    [InlineData(435_700_000, 1_265_000, true)]
    [InlineData(1_265_000_000, 1_268_000, false)]
    public void NeedsMainSubBandSwap_detects_wrong_main_band(long mainHz, double downlinkKHz, bool expected) =>
        Assert.Equal(expected, RigSatModeHelper.NeedsMainSubBandSwap(mainHz, downlinkKHz));

    [Theory]
    [InlineData(29_450, RigSatBandRegion.Hf)]
    [InlineData(145_865, RigSatBandRegion.Vhf)]
    [InlineData(435_667, RigSatBandRegion.UhfAndAbove)]
    public void GetSatBandRegion_classifies_hf_vhf_uhf(double downlinkKHz, RigSatBandRegion expected) =>
        Assert.Equal(expected, RigSatModeHelper.GetSatBandRegion(downlinkKHz));
}

public class SetupVfosPolicyTests
{
    [Theory]
    [InlineData("FMN", 200, 50, 200, false)]
    [InlineData("USB", 200, 50, 50, true)]
    [InlineData("DATA-USB", 200, 50, 0, false)]
    [InlineData("DATA-FM", 200, 50, 200, false)]
    [InlineData("FM-DATA", 200, 50, 200, false)]
    public void Evaluate_returns_expected_threshold(
        string mode, int fm, int linear, int expectedThreshold, bool interactive)
    {
        var result = SetupVfosPolicy.Evaluate(mode, fm, linear);
        Assert.Equal(expectedThreshold, result.ThresholdHz);
        Assert.Equal(interactive, result.Interactive);
    }
}

public class RigSettingsSerializationTests
{
    [Fact]
    public void RigSettings_defaults()
    {
        var s = new RigSettings();
        Assert.Equal(RigType.None, s.Type);
        Assert.Equal(RigRegion.EU, s.Region);
        Assert.Equal("7C", RigSettings.DefaultCivAddressFor(RigType.IcomIc910));
        Assert.Equal("7C", RigSettings.DefaultCivAddressFor(RigType.IcomIc9100));
        Assert.Equal("A2", RigSettings.DefaultCivAddressFor(RigType.IcomIc9700));
        Assert.Equal("4C", RigSettings.DefaultCivAddressFor(RigType.IcomIc821h));
        Assert.Equal(350, s.DopplerThresholdFmHz);
        Assert.Equal(50, s.DopplerThresholdLinearHz);
        Assert.True(s.DopplerCatLeadEnabled);
        Assert.True(s.DopplerAdaptiveThresholdEnabled);
        Assert.Equal(RigSettings.DefaultDopplerCatLeadMs, s.DopplerCatLeadMs);
        Assert.Equal(RigSettings.DefaultDopplerCatLeadGainPercent, s.DopplerCatLeadGainPercent);
    }
}
