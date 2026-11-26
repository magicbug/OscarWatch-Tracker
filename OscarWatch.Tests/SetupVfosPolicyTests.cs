using OscarWatch.Core.Radio;

namespace OscarWatch.Tests;

/// <summary>
/// **Validates: Requirements 9.1, 9.2, 9.3, 9.6**
///
/// Example-based tests verifying that <see cref="SetupVfosPolicy"/>
/// returns the correct threshold and interactivity flag for each
/// recognised downlink mode branch.
/// </summary>
public sealed class SetupVfosPolicyBranchTests
{
    private const int FmThreshold = 500;
    private const int LinearThreshold = 100;

    /// <summary>
    /// FM and FMN modes return the FM Doppler threshold with Interactive=false.
    /// </summary>
    [Theory]
    [InlineData("FM")]
    [InlineData("FMN")]
    [InlineData("DV")]
    public void Fm_modes_return_fm_threshold_with_interactive_false(string mode)
    {
        var result = SetupVfosPolicy.Evaluate(mode, FmThreshold, LinearThreshold);

        Assert.Equal(FmThreshold, result.ThresholdHz);
        Assert.False(result.Interactive);
    }

    /// <summary>
    /// LSB, USB, and CW modes return the linear Doppler threshold with Interactive=true.
    /// </summary>
    [Theory]
    [InlineData("LSB")]
    [InlineData("USB")]
    [InlineData("CW")]
    public void Linear_modes_return_linear_threshold_with_interactive_true(string mode)
    {
        var result = SetupVfosPolicy.Evaluate(mode, FmThreshold, LinearThreshold);

        Assert.Equal(LinearThreshold, result.ThresholdHz);
        Assert.True(result.Interactive);
    }

    /// <summary>
    /// DATA-LSB and DATA-USB modes return threshold 0 with Interactive=false.
    /// </summary>
    [Theory]
    [InlineData("DATA-LSB")]
    [InlineData("DATA-USB")]
    public void Data_modes_return_zero_threshold_with_interactive_false(string mode)
    {
        var result = SetupVfosPolicy.Evaluate(mode, FmThreshold, LinearThreshold);

        Assert.Equal(0, result.ThresholdHz);
        Assert.False(result.Interactive);
    }

    /// <summary>
    /// IsLinearMode returns true for LSB, USB, and CW.
    /// </summary>
    [Theory]
    [InlineData("LSB")]
    [InlineData("USB")]
    [InlineData("CW")]
    public void IsLinearMode_returns_true_for_linear_modes(string mode)
    {
        Assert.True(SetupVfosPolicy.IsLinearMode(mode));
    }
}
