using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class RigPassbandTrimClampTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(12.5, 12.5)]
    [InlineData(-42.0, -42.0)]
    [InlineData(RigController.PassbandTrimLimitKHz, RigController.PassbandTrimLimitKHz)]
    public void Trim_within_limit_is_unchanged(double input, double expected)
    {
        Assert.Equal(expected, RigController.ClampPassbandTrimKHz(input), precision: 6);
    }

    [Theory]
    [InlineData(705_747.0, RigController.PassbandTrimLimitKHz)]
    [InlineData(-705_747.0, -RigController.PassbandTrimLimitKHz)]
    public void Runaway_trim_is_clamped(double input, double expected)
    {
        Assert.Equal(expected, RigController.ClampPassbandTrimKHz(input), precision: 6);
    }
}
