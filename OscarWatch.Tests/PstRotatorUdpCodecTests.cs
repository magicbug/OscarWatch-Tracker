using OscarWatch.Rotator;

namespace OscarWatch.Tests;

public sealed class PstRotatorUdpCodecTests
{
    [Fact]
    public void Queries_and_stop_match_protocol_literals()
    {
        Assert.Equal("<PST>AZ?</PST>", PstRotatorUdpCodec.AzimuthQuery);
        Assert.Equal("<PST>EL?</PST>", PstRotatorUdpCodec.ElevationQuery);
        Assert.Equal("<PST><STOP>1</STOP></PST>", PstRotatorUdpCodec.StopCommand);
    }

    [Theory]
    [InlineData(123, "<PST><AZIMUTH>123</AZIMUTH></PST>")]
    [InlineData(123.44, "<PST><AZIMUTH>123.4</AZIMUTH></PST>")]
    [InlineData(370.5, "<PST><AZIMUTH>370.5</AZIMUTH></PST>")]
    [InlineData(0, "<PST><AZIMUTH>0</AZIMUTH></PST>")]
    public void BuildAzimuthCommand_formats_invariant(double az, string expected)
    {
        Assert.Equal(expected, PstRotatorUdpCodec.BuildAzimuthCommand(az));
    }

    [Fact]
    public void BuildElevationCommand_formats_invariant()
    {
        Assert.Equal("<PST><ELEVATION>45</ELEVATION></PST>", PstRotatorUdpCodec.BuildElevationCommand(45));
        Assert.Equal("<PST><ELEVATION>12.5</ELEVATION></PST>", PstRotatorUdpCodec.BuildElevationCommand(12.5));
    }

    [Fact]
    public void BuildAzimuthCommand_ignores_current_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("es-ES");
            Assert.Equal("<PST><AZIMUTH>90.5</AZIMUTH></PST>", PstRotatorUdpCodec.BuildAzimuthCommand(90.5));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("AZ:123.4", 123.4, null)]
    [InlineData("EL:45", null, 45.0)]
    [InlineData("AZ:270\r\n", 270.0, null)]
    [InlineData("AZ:10.0 EL:20.5", 10.0, 20.5)]
    [InlineData("AZ=181,5", 181.5, null)]
    public void TryParseReply_reads_axes(string reply, double? az, double? el)
    {
        Assert.True(PstRotatorUdpCodec.TryParseReply(reply, out var parsedAz, out var parsedEl));
        Assert.Equal(az, parsedAz);
        Assert.Equal(el, parsedEl);
    }

    [Fact]
    public void TryParseReply_rejects_null()
    {
        Assert.False(PstRotatorUdpCodec.TryParseReply(null, out var az, out var el));
        Assert.Null(az);
        Assert.Null(el);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<PST>OK</PST>")]
    public void TryParseReply_rejects_unrelated_text(string reply)
    {
        Assert.False(PstRotatorUdpCodec.TryParseReply(reply, out var az, out var el));
        Assert.Null(az);
        Assert.Null(el);
    }
}
