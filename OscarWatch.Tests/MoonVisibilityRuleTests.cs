using OscarWatch.Core.Eme;
using OscarWatch.Core.Models;

namespace OscarWatch.Tests;

public sealed class MoonVisibilityRuleTests
{
    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.001, true)]
    [InlineData(-0.001, false)]
    public void The_default_rule_counts_the_moon_at_or_above_zero(double elevation, bool up) =>
        Assert.Equal(up, MoonVisibilityRule.Default.IsUp(180, elevation));

    [Fact]
    public void A_negative_limit_counts_a_moon_below_the_horizon()
    {
        var rule = new MoonVisibilityRule(-3, 90, null);
        Assert.True(rule.IsUp(0, -2.9));
        Assert.True(rule.IsUp(0, -3.0));
        Assert.False(rule.IsUp(0, -3.1));
    }

    [Fact]
    public void The_maximum_elevation_excludes_a_moon_above_it()
    {
        var rule = new MoonVisibilityRule(5, 60, null);
        Assert.False(rule.IsUp(0, 4.9));
        Assert.True(rule.IsUp(0, 5));
        Assert.True(rule.IsUp(0, 60));
        Assert.False(rule.IsUp(0, 60.1));
    }

    [Fact]
    public void The_mask_raises_the_floor_only_where_it_is_higher_than_the_limit()
    {
        var mask = new HorizonMask { Points = [new(0, 0), new(90, 15), new(180, 15), new(270, 0)] };
        var rule = new MoonVisibilityRule(5, 90, mask);
        Assert.Equal(15, rule.Floor(135), 0.001);
        Assert.False(rule.IsUp(135, 10));
        Assert.True(rule.IsUp(135, 16));
        // Where the mask is lower than the limit, the limit holds.
        Assert.Equal(5, rule.Floor(315 + 20), 0.5);
        Assert.False(rule.IsUp(0, 4));
        Assert.True(rule.IsUp(0, 6));
    }

    [Fact]
    public void No_mask_and_a_maximum_of_90_is_a_plain_limit()
    {
        Assert.True(MoonVisibilityRule.Default.IsPlainLimit);
        Assert.False(new MoonVisibilityRule(0, 80, null).IsPlainLimit);
        Assert.False(new MoonVisibilityRule(0, 90, new HorizonMask { Points = [new(0, 5)] }).IsPlainLimit);
        Assert.True(new MoonVisibilityRule(0, 90, new HorizonMask()).IsPlainLimit);
    }
}
