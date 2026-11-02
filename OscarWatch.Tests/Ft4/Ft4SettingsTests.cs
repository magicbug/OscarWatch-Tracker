using System.Text.Json;
using System.Text.Json.Serialization;
using OscarWatch.Core.Ft4;
using OscarWatch.Core.Hardware;

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

        var mode = new OscarWatch.Core.Models.SatelliteTransponderMode
        {
            Type = "FT4",
            DownlinkKHz = 435_611,
            UplinkKHz = 145_960,
        };
        settings.SetUplinkCalibrationKHz("RS-44", 0.4, mode);
        Assert.Equal(0.4, settings.GetUplinkCalibrationKHz("RS-44", mode));
        Assert.True(
            settings.UplinkCalibrationKHzBySatellite.ContainsKey(
                Ft4UplinkCalibration.FormatTransponderKey("RS-44", mode)));
    }

    [Fact]
    public void Defaults_match_operator_expectations()
    {
        var settings = new Ft4Settings();
        Assert.True(settings.SkipRrr);
        Assert.True(settings.ApEnabled);
        Assert.Equal(PttMethod.Vox, settings.PttMethod);
        Assert.Equal(PttLine.Rts, settings.PttLine);
        Assert.Equal(200, settings.PttLeadMs);
        Assert.Equal(100, settings.PttTailMs);
        Assert.True(settings.HoldTxFrequency);
        Assert.True(settings.AutoReply);
        Assert.Empty(settings.BlockedCallsigns);
        Assert.False(settings.AutoLowerRfPower);
        Assert.True(settings.AudioDopplerTx);
        Assert.True(settings.AudioDopplerRx);
        Assert.True(settings.ParallelTxEchoDecode);
        Assert.True(settings.ShowDtWsjtxConvention);
        Assert.Equal(Ft4TxWatchdog.DefaultMinutes, settings.TxWatchdogMinutes);
        Assert.False(settings.PskReporterEnabled);
        Assert.False(settings.OscarWatchSpotsEnabled);
        Assert.Equal(12, settings.DecodeFontSize);
        Assert.Equal(Ft4Settings.DefaultWaterfallRangeDb, settings.WaterfallRangeDb);
        Assert.Equal(Ft4DecodeHighlight.DefaultCallingMeColour, settings.CallingMeColour);
        Assert.Equal(Ft4DecodeHighlight.DefaultReplyingColour, settings.ReplyingColour);
        Assert.Equal(Ft4DecodeHighlight.DefaultNewCallColour, settings.NewCallColour);
        Assert.Equal(Ft4DecodeHighlight.DefaultNewGridColour, settings.NewGridColour);
        Assert.Equal(Ft4DecodeHighlight.DefaultCqColour, settings.CqColour);
        Assert.Equal("", settings.CallingMeTextColour);
        Assert.Equal("", settings.ReplyingTextColour);
        Assert.Equal("", settings.NewCallTextColour);
        Assert.Equal("", settings.NewGridTextColour);
        Assert.Equal("", settings.CqTextColour);
        Assert.Equal(Ft4DecodeHighlight.DefaultTxTextColour, settings.TxTextColour);
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

    [Theory]
    [InlineData("vox", PttMethod.Vox)]
    [InlineData("cat", PttMethod.Cat)]
    [InlineData("catPortHandshake", PttMethod.CatPortHandshake)]
    [InlineData("separateComPort", PttMethod.SeparateComPort)]
    [InlineData("manual", PttMethod.Manual)]
    public void Ptt_settings_saved_with_the_old_ft4_enums_still_load(string savedMethod, PttMethod expected)
    {
        var json = $$"""{ "pttMethod": "{{savedMethod}}", "pttLine": "dtr", "pttInvert": true }""";
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        var settings = JsonSerializer.Deserialize<Ft4Settings>(json, options)!;

        Assert.Equal(expected, settings.PttMethod);
        Assert.Equal(PttLine.Dtr, settings.PttLine);
        Assert.True(settings.ToPttOptions().Invert);
    }

    [Fact]
    public void Waterfall_range_clamps_to_the_operator_span()
    {
        Assert.Equal(40, Ft4Settings.ClampWaterfallRangeDb(40));
        Assert.Equal(Ft4Settings.MinWaterfallRangeDb, Ft4Settings.ClampWaterfallRangeDb(0));
        Assert.Equal(Ft4Settings.MaxWaterfallRangeDb, Ft4Settings.ClampWaterfallRangeDb(200));
    }
}
