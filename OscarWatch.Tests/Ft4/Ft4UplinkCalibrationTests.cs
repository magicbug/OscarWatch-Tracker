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
    public void Transponder_key_is_stable_and_human_readable()
    {
        var mode = Ft4Mode("FT4 Asia", 435_612, 145_960);
        var key = Ft4UplinkCalibration.FormatTransponderKey("RS-44", mode);
        Assert.Equal("RS-44|FT4 Asia|435612.0000|145960.0000", key);
        Assert.Equal("RS-44 · FT4 Asia", Ft4UplinkCalibration.FormatDisplayLabel(key));
    }

    [Fact]
    public void Per_transponder_trim_does_not_share_legacy_satellite_bucket()
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
        Assert.False(settings.UplinkCalibrationKHzBySatellite.ContainsKey("RS-44"));
    }
}
