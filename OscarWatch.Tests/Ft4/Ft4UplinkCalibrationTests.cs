using OscarWatch.Core.Ft4;
using OscarWatch.Core.Models;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4UplinkCalibrationTests
{
    private static SatelliteTransponderMode Ft4Mode(string type, double dl, double ul) => new()
    {
        Type = type,
        DownlinkKHz = dl,
        UplinkKHz = ul,
    };

    [Fact]
    public void Transponder_key_names_the_custom_row()
    {
        var mode = Ft4Mode("FT4 Asia", 435_612, 145_960);
        var key = Ft4UplinkCalibration.FormatTransponderKey("RS-44", mode);
        Assert.Equal("RS-44|FT4 Asia|435612.0000|145960.0000", key);
        Assert.Equal("RS-44 · FT4 Asia · 435.612 MHz", Ft4UplinkCalibration.FormatDisplayLabel(key));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(0.0004, "0")]
    [InlineData(0.35, "+350")]
    [InlineData(-1.2, "-1200")]
    [InlineData(12.5, "+12500")]
    [InlineData(-20, "-20000")]
    public void Signed_hertz_keeps_the_sign_and_the_full_magnitude(double khz, string expected) =>
        Assert.Equal(expected, Ft4UplinkCalibration.FormatSignedHz(khz));

    [Fact]
    public void Signed_hertz_treats_a_non_finite_value_as_zero()
    {
        Assert.Equal("0", Ft4UplinkCalibration.FormatSignedHz(double.NaN));
        Assert.Equal("0", Ft4UplinkCalibration.FormatSignedHz(double.PositiveInfinity));
    }

    [Fact]
    public void Trim_on_FT4_Asia_leaves_the_stock_transponder_and_legacy_value()
    {
        var settings = new Ft4Settings();
        settings.SetUplinkCalibrationKHz("RS-44", 0.35);
        var stock = Ft4Mode("FT4", 435_611, 145_960);
        var asia = Ft4Mode("FT4 Asia", 435_612, 145_961);

        Assert.Equal(0.35, settings.GetUplinkCalibrationKHz("RS-44", stock));
        Assert.Equal(0.35, settings.GetUplinkCalibrationKHz("RS-44", asia));

        settings.SetUplinkCalibrationKHz("RS-44", 0.55, asia);
        Assert.Equal(0.35, settings.GetUplinkCalibrationKHz("RS-44", stock));
        Assert.Equal(0.55, settings.GetUplinkCalibrationKHz("RS-44", asia));
        Assert.Equal(0.35, settings.UplinkCalibrationKHzBySatellite["RS-44"]);

        settings.SetUplinkCalibrationKHz("RS-44", 0, asia);
        Assert.Equal(0, settings.GetUplinkCalibrationKHz("RS-44", asia));
        Assert.Equal(0.35, settings.GetUplinkCalibrationKHz("RS-44", stock));
    }
}
