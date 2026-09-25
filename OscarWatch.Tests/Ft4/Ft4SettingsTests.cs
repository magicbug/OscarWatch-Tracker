using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4SettingsTests
{
    [Fact]
    public void Uplink_calibration_round_trips_and_clamps()
    {
        var settings = new Ft4Settings();
        settings.SetUplinkCalibrationKHz("RS-44", 0.35);
        Assert.Equal(0.35, settings.GetUplinkCalibrationKHz("rs-44"));

        settings.SetUplinkCalibrationKHz("RS-44", 50);
        Assert.Equal(20.0, settings.GetUplinkCalibrationKHz("RS-44"));

        settings.SetUplinkCalibrationKHz("RS-44", 0);
        Assert.Equal(0, settings.GetUplinkCalibrationKHz("RS-44"));
        Assert.Empty(settings.UplinkCalibrationKHzBySatellite);
    }

    [Fact]
    public void Defaults_match_operator_expectations()
    {
        var settings = new Ft4Settings();
        Assert.True(settings.SkipRrr);
        Assert.Equal(Ft4PttMethod.Vox, settings.PttMethod);
        Assert.Equal(Ft4PttLine.Rts, settings.PttLine);
        Assert.Equal(200, settings.PttLeadMs);
        Assert.Equal(100, settings.PttTailMs);
        Assert.True(settings.HoldTxFrequency);
        Assert.True(settings.AutoReply);
        Assert.True(settings.AudioDopplerTx);
        Assert.True(settings.AudioDopplerRx);
        Assert.True(settings.ParallelTxEchoDecode);
        Assert.Equal(12, settings.DecodeFontSize);
        Assert.Equal(Ft4DecodeHighlight.DefaultCallingMeColour, settings.CallingMeColour);
        Assert.Equal(Ft4DecodeHighlight.DefaultReplyingColour, settings.ReplyingColour);
        Assert.Equal(Ft4DecodeHighlight.DefaultNewCallColour, settings.NewCallColour);
        Assert.Equal(Ft4DecodeHighlight.DefaultNewGridColour, settings.NewGridColour);
    }

    [Fact]
    public void MigrateLegacyNumericDeviceIds_clears_index_ids_and_keeps_names()
    {
        var settings = new Ft4Settings
        {
            InputDeviceId = "3",
            InputDeviceDisplayName = "CABLE Output (VB-Audio Virtual Cable)",
            OutputDeviceId = "12",
            OutputDeviceDisplayName = "CABLE Input (VB-Audio Virtual Cable)",
        };

        settings.MigrateLegacyNumericDeviceIds();

        Assert.Equal("", settings.InputDeviceId);
        Assert.Equal("CABLE Output (VB-Audio Virtual Cable)", settings.InputDeviceDisplayName);
        Assert.Equal("", settings.OutputDeviceId);
        Assert.Equal("CABLE Input (VB-Audio Virtual Cable)", settings.OutputDeviceDisplayName);
    }

    [Fact]
    public void MigrateLegacyNumericDeviceIds_leaves_durable_names()
    {
        var settings = new Ft4Settings
        {
            InputDeviceId = "CABLE Output (VB-Audio Virtual Cable)",
            OutputDeviceId = "Speakers (Realtek)",
        };

        settings.MigrateLegacyNumericDeviceIds();

        Assert.Equal("CABLE Output (VB-Audio Virtual Cable)", settings.InputDeviceId);
        Assert.Equal("Speakers (Realtek)", settings.OutputDeviceId);
    }
}
