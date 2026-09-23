using OscarWatch.Core.Ft4;
using OscarWatch.Core.Models;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4SatelliteEligibilityTests
{
    [Fact]
    public void Allows_linear_ssb_modes()
    {
        var mode = new SatelliteTransponderMode
        {
            Type = "SSB Transponder",
            UplinkMode = "LSB",
            DownlinkMode = "USB"
        };

        Assert.Equal(
            Ft4SatelliteEligibility.BlockReason.None,
            Ft4SatelliteEligibility.Evaluate("RS-44", "53384", mode));
    }

    [Fact]
    public void Allows_data_usb_ft4_catalogue_mode()
    {
        var mode = new SatelliteTransponderMode
        {
            Type = "FT4",
            UplinkMode = "DATA-USB",
            DownlinkMode = "DATA-USB"
        };

        Assert.True(Ft4SatelliteEligibility.IsAllowed("RS-44", "53384", mode));
    }

    [Fact]
    public void Blocks_fm_modes()
    {
        var mode = new SatelliteTransponderMode
        {
            Type = "FM Voice",
            UplinkMode = "FM",
            DownlinkMode = "FM"
        };

        Assert.Equal(
            Ft4SatelliteEligibility.BlockReason.FmMode,
            Ft4SatelliteEligibility.Evaluate("SO-50", "27607", mode));
        Assert.Equal(
            "Ft4.Blocked.FmSatellite",
            Ft4SatelliteEligibility.StatusKey(Ft4SatelliteEligibility.BlockReason.FmMode));
    }

    [Fact]
    public void Blocks_fo29_by_norad_even_on_ssb()
    {
        var mode = new SatelliteTransponderMode
        {
            Type = "SSB Transponder",
            UplinkMode = "LSB",
            DownlinkMode = "USB"
        };

        Assert.Equal(
            Ft4SatelliteEligibility.BlockReason.Fo29,
            Ft4SatelliteEligibility.Evaluate("FO-29", "24278", mode));
        Assert.Equal(
            Ft4SatelliteEligibility.BlockReason.Fo29,
            Ft4SatelliteEligibility.Evaluate("Something Else", "24278", mode));
    }

    [Fact]
    public void Blocks_fo29_by_name_when_norad_missing()
    {
        var mode = new SatelliteTransponderMode
        {
            Type = "SSB Transponder",
            UplinkMode = "LSB",
            DownlinkMode = "USB"
        };

        Assert.Equal(
            Ft4SatelliteEligibility.BlockReason.Fo29,
            Ft4SatelliteEligibility.Evaluate("FO-29", null, mode));
    }

    [Fact]
    public void Fo29_takes_priority_over_fm()
    {
        var mode = new SatelliteTransponderMode
        {
            Type = "FM",
            UplinkMode = "FM",
            DownlinkMode = "FM"
        };

        Assert.Equal(
            Ft4SatelliteEligibility.BlockReason.Fo29,
            Ft4SatelliteEligibility.Evaluate("FO-29", "24278", mode));
    }

    [Fact]
    public void Blocks_ao7_by_norad_and_catalogue_name()
    {
        var mode = new SatelliteTransponderMode
        {
            Type = "SSB Transponder",
            UplinkMode = "LSB",
            DownlinkMode = "USB"
        };

        Assert.Equal(
            Ft4SatelliteEligibility.BlockReason.Ao7,
            Ft4SatelliteEligibility.Evaluate("AO-07", "07530", mode));
        Assert.Equal(
            Ft4SatelliteEligibility.BlockReason.Ao7,
            Ft4SatelliteEligibility.Evaluate("AO-7", null, mode));
        Assert.Equal(
            Ft4SatelliteEligibility.BlockReason.Ao7,
            Ft4SatelliteEligibility.Evaluate("Something Else", "7530", mode));
        Assert.Equal(
            "Ft4.Blocked.Ao7",
            Ft4SatelliteEligibility.StatusKey(Ft4SatelliteEligibility.BlockReason.Ao7));
    }

    [Fact]
    public void Ao7_takes_priority_over_fm()
    {
        var mode = new SatelliteTransponderMode
        {
            Type = "FM",
            UplinkMode = "FM",
            DownlinkMode = "FM"
        };

        Assert.Equal(
            Ft4SatelliteEligibility.BlockReason.Ao7,
            Ft4SatelliteEligibility.Evaluate("AO-07", "07530", mode));
    }
}
