using OscarWatch.Rotator;

namespace OscarWatch.Tests;

public sealed class GrblCodecTests
{
    [Fact]
    public void TryParseStatus_reads_jog_report_from_issue()
    {
        var status = GrblStatusParser.TryParseStatus("<Jog|MPos:0.000,6.101,0.000|FS:50,0|Pn:XY>");

        Assert.NotNull(status);
        Assert.Equal("Jog", status.Value.State);
        Assert.True(status.Value.IsMoving);
        Assert.Equal(0.0, status.Value.MachineXMm, 3);
        Assert.Equal(6.101, status.Value.MachineYMm, 3);
    }

    [Fact]
    public void TryParseStatus_reads_idle_report_with_trailing_line_ending()
    {
        var status = GrblStatusParser.TryParseStatus("<Idle|MPos:0.000,9.600,0.000|FS:0,0>\r");

        Assert.NotNull(status);
        Assert.True(status.Value.IsIdle);
        Assert.Equal(9.6, status.Value.MachineYMm, 3);
    }

    [Fact]
    public void TryParseStatus_uses_MPos_and_ignores_WCO()
    {
        var status = GrblStatusParser.TryParseStatus("<Idle|MPos:10.000,2.500,0.000|FS:0,0|Pn:P|WCO:5.000,0.000,0.000>");

        Assert.NotNull(status);
        Assert.Equal(10.0, status.Value.MachineXMm, 3);
        Assert.Equal(2.5, status.Value.MachineYMm, 3);
    }

    [Fact]
    public void TryParseStatus_reads_grbl_09_comma_format()
    {
        var status = GrblStatusParser.TryParseStatus("<Run,MPos:-4.000,1.000,0.000,WPos:0.000,0.000,0.000>");

        Assert.NotNull(status);
        Assert.Equal("Run", status.Value.State);
        Assert.Equal(-4.0, status.Value.MachineXMm, 3);
    }

    [Theory]
    [InlineData("<Alarm|MPos:0.000,0.000,0.000|FS:0,0>", "Alarm")]
    [InlineData("<Hold:0|MPos:1.000,1.000,0.000|FS:0,0>", "Hold")]
    public void TryParseStatus_strips_substate(string line, string expectedState)
    {
        var status = GrblStatusParser.TryParseStatus(line);

        Assert.NotNull(status);
        Assert.Equal(expectedState, status.Value.State);
        Assert.Equal(expectedState == "Alarm", status.Value.IsAlarm);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ok")]
    [InlineData("Grbl 1.3a ['$' for help]")]
    [InlineData("<Idle|WPos:0.000,0.000,0.000>")]
    public void TryParseStatus_returns_null_for_non_status_lines(string? line)
    {
        Assert.Null(GrblStatusParser.TryParseStatus(line));
    }

    [Theory]
    [InlineData("$100=1776.000", 100, 1776.0)]
    [InlineData("$3=3 (dir port invert mask:00000011)", 3, 3.0)]
    public void TryParseSetting_reads_dollar_settings(string line, int expectedId, double expectedValue)
    {
        Assert.True(GrblStatusParser.TryParseSetting(line, out var id, out var value));
        Assert.Equal(expectedId, id);
        Assert.Equal(expectedValue, value, 3);
    }

    [Fact]
    public void Ok_and_error_replies_are_recognised()
    {
        Assert.True(GrblStatusParser.IsOk("ok\r"));
        Assert.True(GrblStatusParser.IsError("error:20"));
        Assert.False(GrblStatusParser.IsOk("error:20"));
    }

    [Theory]
    [InlineData(45, 5.0)]
    [InlineData(1, 0.111)]
    [InlineData(360, 40.0)]
    [InlineData(180, 20.0)]
    public void DegreesToMillimetres_uses_360_degrees_per_40_mm(double degrees, double expectedMm)
    {
        Assert.Equal(expectedMm, GrblCommandFormatter.DegreesToMillimetres(degrees), 3);
    }

    [Fact]
    public void FormatJog_is_absolute_metric_with_three_decimals()
    {
        var x = GrblCommandFormatter.DegreesToMillimetres(90);
        var y = GrblCommandFormatter.DegreesToMillimetres(45);

        Assert.Equal("$J=G90 G21 F50 X10.000 Y5.000", GrblCommandFormatter.FormatJog(x, y));
    }

    [Fact]
    public void FormatRapid_is_absolute_G0()
    {
        Assert.Equal("G90 G0 X-1.111 Y0.000", GrblCommandFormatter.FormatRapid(-1.1111, 0));
    }

    [Fact]
    public void ResolveMachineX_without_history_uses_primary_turn()
    {
        Assert.Equal(10.0, GrblCommandFormatter.ResolveMachineX(90, null), 3);
    }

    [Fact]
    public void ResolveMachineX_crosses_north_clockwise_without_unwinding()
    {
        var current = GrblCommandFormatter.DegreesToMillimetres(355);

        var x = GrblCommandFormatter.ResolveMachineX(5, current);

        Assert.Equal(GrblCommandFormatter.DegreesToMillimetres(365), x, 3);
    }

    [Fact]
    public void ResolveMachineX_crosses_north_anticlockwise_into_negative_turn()
    {
        var current = GrblCommandFormatter.DegreesToMillimetres(5);

        var x = GrblCommandFormatter.ResolveMachineX(355, current);

        Assert.Equal(GrblCommandFormatter.DegreesToMillimetres(-5), x, 3);
    }

    [Theory]
    [InlineData(-0.5556, 355)]
    [InlineData(40.5556, 5)]
    [InlineData(10.0, 90)]
    public void MachineXToAzimuth_wraps_to_compass(double machineXMm, double expectedAz)
    {
        Assert.Equal(expectedAz, GrblCommandFormatter.MachineXToAzimuth(machineXMm), 0);
    }
}
