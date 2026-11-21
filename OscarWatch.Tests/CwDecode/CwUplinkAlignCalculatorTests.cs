using OscarWatch.Core.CwDecode;
using OscarWatch.Core.Models;
using Xunit;

namespace OscarWatch.Tests.CwDecode;

public sealed class CwUplinkAlignCalculatorTests
{
    private static SatelliteTransponderMode LinearMode(
        string doppler,
        string uplinkMode = "LSB",
        string downlinkMode = "USB",
        double uplinkKHz = 435_300,
        double downlinkKHz = 145_950) =>
        new()
        {
            Type = "Linear",
            Doppler = doppler,
            UplinkMode = uplinkMode,
            DownlinkMode = downlinkMode,
            UplinkKHz = uplinkKHz,
            DownlinkKHz = downlinkKHz
        };

    [Theory]
    [InlineData("NOR", "CW", 950.0, 700.0, -250.0)]
    [InlineData("NOR", "USB", 950.0, 700.0, -250.0)]
    [InlineData("NOR", "CW", 500.0, 700.0, +200.0)]
    [InlineData("NOR", "CW-R", 950.0, 700.0, +250.0)]
    [InlineData("NOR", "LSB", 950.0, 700.0, +250.0)]
    [InlineData("NOR", "CW-R", 500.0, 700.0, -200.0)]
    [InlineData("REV", "CW", 950.0, 700.0, +250.0)]
    [InlineData("REV", "USB", 950.0, 700.0, +250.0)]
    [InlineData("REV", "CW", 500.0, 700.0, -200.0)]
    [InlineData("REV", "CW-R", 950.0, 700.0, -250.0)]
    [InlineData("REV", "LSB", 950.0, 700.0, -250.0)]
    [InlineData("REV", "CW-R", 500.0, 700.0, +200.0)]
    public void ComputeTxDeltaHz_handles_all_transponder_senses_and_sidebands(
        string doppler,
        string receiveMode,
        double echoHz,
        double targetHz,
        double expectedDeltaHz)
    {
        var sense = doppler == "REV" ? DopplerCorrection.Reverse : DopplerCorrection.Normal;
        var delta = CwUplinkAlignCalculator.ComputeTxDeltaHz(echoHz, targetHz, sense, receiveMode);

        Assert.Equal(expectedDeltaHz, delta, precision: 6);
    }

    [Theory]
    [InlineData("LSB", "USB", 435_300, true)]
    [InlineData("USB", "USB", 145_900, true)]
    [InlineData("CW", "CW", 435_100, true)]
    [InlineData("FM", "FM", 145_850, false)]
    [InlineData("FMN", "FM", 145_850, false)]
    [InlineData("DATA-USB", "DATA-USB", 435_300, false)]
    [InlineData("DATA-LSB", "DATA-USB", 435_300, false)]
    [InlineData("USB", "USB", 0, false)] // beacon-only
    public void IsEligibleTransponder_accepts_only_two_way_linear_modes(
        string uplinkMode,
        string downlinkMode,
        double uplinkKHz,
        bool expected)
    {
        var mode = new SatelliteTransponderMode
        {
            Type = "Test",
            UplinkMode = uplinkMode,
            DownlinkMode = downlinkMode,
            UplinkKHz = uplinkKHz,
            DownlinkKHz = 145_950
        };

        Assert.Equal(expected, CwUplinkAlignCalculator.IsEligibleTransponder(mode));
    }

    [Fact]
    public void IsEligibleTransponder_rejects_null()
    {
        Assert.False(CwUplinkAlignCalculator.IsEligibleTransponder(null));
    }

    [Theory]
    [InlineData(700.0, 700.0)]
    [InlineData(704.9, 700.0)]
    [InlineData(695.2, 700.0)]
    public void Evaluate_returns_AlreadyAligned_within_deadband(double echoHz, double targetHz)
    {
        var eval = CwUplinkAlignCalculator.Evaluate(
            LinearMode("REV"),
            receiveMode: "CW",
            echoAudioHz: echoHz,
            targetPitchHz: targetHz,
            currentTransmitOffsetKHz: 0.5,
            maxCorrectionHz: 1500);

        Assert.Equal(CwUplinkAlignDecision.AlreadyAligned, eval.Decision);
        Assert.Equal(0.5, eval.NewTransmitOffsetKHz, precision: 6);
    }

    [Theory]
    [InlineData(1500, 1501.0)]
    [InlineData(500, 520.0)]
    public void Evaluate_rejects_correction_exceeding_safety_cap(double capHz, double offsetHz)
    {
        var eval = CwUplinkAlignCalculator.Evaluate(
            LinearMode("REV"),
            receiveMode: "CW",
            echoAudioHz: 700.0 + offsetHz,
            targetPitchHz: 700.0,
            currentTransmitOffsetKHz: 0.0,
            maxCorrectionHz: capHz);

        Assert.Equal(CwUplinkAlignDecision.ExceedsSafetyCap, eval.Decision);
        Assert.Equal(offsetHz, eval.TxDeltaHz, precision: 6);
    }

    [Fact]
    public void Evaluate_rejects_when_new_offset_exceeds_twenty_khz_bounds()
    {
        var eval = CwUplinkAlignCalculator.Evaluate(
            LinearMode("REV"),
            receiveMode: "CW",
            echoAudioHz: 1200.0,
            targetPitchHz: 700.0,
            currentTransmitOffsetKHz: 19.7,
            maxCorrectionHz: 1500);

        Assert.Equal(CwUplinkAlignDecision.OutOfPassbandBounds, eval.Decision);
    }

    [Fact]
    public void Evaluate_applies_in_limit_correction_with_expected_new_offset()
    {
        var eval = CwUplinkAlignCalculator.Evaluate(
            LinearMode("REV"),
            receiveMode: "CW",
            echoAudioHz: 940.0,
            targetPitchHz: 700.0,
            currentTransmitOffsetKHz: -0.100,
            maxCorrectionHz: 1500);

        Assert.Equal(CwUplinkAlignDecision.Apply, eval.Decision);
        Assert.Equal(+240.0, eval.TxDeltaHz, precision: 6);
        Assert.Equal(+0.140, eval.NewTransmitOffsetKHz, precision: 6);
    }

    [Fact]
    public void Evaluate_rejects_invalid_receive_mode()
    {
        var eval = CwUplinkAlignCalculator.Evaluate(
            LinearMode("REV"),
            receiveMode: "FM",
            echoAudioHz: 900.0,
            targetPitchHz: 700.0,
            currentTransmitOffsetKHz: 0.0,
            maxCorrectionHz: 1500);

        Assert.Equal(CwUplinkAlignDecision.InvalidMode, eval.Decision);
    }
}
