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
        Assert.Equal(Ft4DecodeLayout.Basic, settings.DecodeLayout);
        Assert.Equal(Ft4Settings.DefaultDecodeColumnShare, settings.DecodeColumnShare);
        Assert.False(settings.WindowMaximised);
        Assert.Equal(Ft4Settings.DefaultWaterfallRangeDb, settings.WaterfallRangeDb);
        Assert.Equal(Ft4Settings.FastWaterfallSpeed, settings.WaterfallSpeed);
        Assert.True(settings.WaterfallFlatten);
        Assert.True(settings.WaterfallSlotLines);
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

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0.1, Ft4Settings.MinDecodeColumnShare)]
    [InlineData(0.9, Ft4Settings.MaxDecodeColumnShare)]
    [InlineData(double.NaN, Ft4Settings.DefaultDecodeColumnShare)]
    public void Decode_column_share_is_kept_within_range(double stored, double expected)
    {
        Assert.Equal(expected, Ft4Settings.ClampDecodeColumnShare(stored));
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

    [Fact]
    public void Waterfall_speed_clamps_and_keeps_more_history_when_slower()
    {
        Assert.Equal(Ft4Settings.SlowWaterfallSpeed, Ft4Settings.ClampWaterfallSpeed(0));
        Assert.Equal(Ft4Settings.FastWaterfallSpeed, Ft4Settings.ClampWaterfallSpeed(9));
        Assert.Equal(4, Ft4Settings.WaterfallTicksPerRow(Ft4Settings.SlowWaterfallSpeed));
        Assert.Equal(2, Ft4Settings.WaterfallTicksPerRow(Ft4Settings.MediumWaterfallSpeed));
        Assert.Equal(1, Ft4Settings.WaterfallTicksPerRow(Ft4Settings.FastWaterfallSpeed));
        Assert.Equal(48, Ft4Settings.WaterfallHistorySeconds(Ft4Settings.SlowWaterfallSpeed, 120));
        Assert.Equal(24, Ft4Settings.WaterfallHistorySeconds(Ft4Settings.MediumWaterfallSpeed, 120));
        Assert.Equal(12, Ft4Settings.WaterfallHistorySeconds(Ft4Settings.FastWaterfallSpeed, 120));
        Assert.Equal(0, Ft4Settings.WaterfallHistorySeconds(Ft4Settings.FastWaterfallSpeed, 0));
    }

    [Fact]
    public void Missing_waterfall_speed_keeps_the_original_rate()
    {
        var settings = JsonSerializer.Deserialize<Ft4Settings>("{}", new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        })!;

        Assert.Equal(Ft4Settings.DefaultWaterfallSpeed, settings.WaterfallSpeed);
    }
}
