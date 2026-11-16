using OscarWatch.Core.Radio;

namespace OscarWatch.Tests;

public sealed class YaesuFt817CatCodecTests
{
    [Fact]
    public void DecodeFrequency_round_trip()
    {
        var response = new byte[] { 0x14, 0x25, 0x00, 0x00, 0x01 };
        var hz = YaesuFt817CatCodec.DecodeFrequency10Hz(response);
        Assert.Equal(142_500_000, hz);

        var cmd = YaesuFt817CatCodec.BuildSetFrequencyCommand(hz);
        Assert.Equal(142_500_000, YaesuFt817CatCodec.DecodeFrequency10Hz(cmd));
        Assert.Equal(0x01, cmd[4]);
    }

    [Fact]
    public void DecodeFrequency_round_trip_10m()
    {
        const long hz = 29_450_000;
        var cmd = YaesuFt817CatCodec.BuildSetFrequencyCommand(hz);
        Assert.Equal(hz, YaesuFt817CatCodec.DecodeFrequency10Hz(cmd));
    }

    [Fact]
    public void BuildSetMode_FM_uses_wide_byte()
    {
        var cmd = YaesuFt817CatCodec.BuildSetModeCommand("FM");
        Assert.Equal(0x08, cmd[0]);
        Assert.Equal(0x07, cmd[4]);
    }

    [Theory]
    [InlineData("USB", 0x01)]
    [InlineData("LSB", 0x00)]
    [InlineData("DATA-USB", 0x0A)]
    [InlineData("DATA-LSB", 0x0A)]
    [InlineData("DATA-FM", 0x0C)]
    [InlineData("FM-DATA", 0x0C)]
    public void BuildSetMode_maps_catalogue_modes(string mode, byte expectedModeByte)
    {
        var cmd = YaesuFt817CatCodec.BuildSetModeCommand(mode);
        Assert.Equal(expectedModeByte, cmd[0]);
        Assert.Equal(0x07, cmd[4]);
    }

    [Theory]
    [InlineData(0x0A, "DIG")]
    [InlineData(0x0C, "PKT")]
    [InlineData(0x01, "USB")]
    [InlineData(0x00, "LSB")]
    public void DecodeMode_includes_dig_and_pkt(byte modeByte, string expected) =>
        Assert.Equal(expected, YaesuFt817CatCodec.DecodeMode(modeByte));

    [Fact]
    public void BuildCtcss_67Hz_uses_bcd_tone_bytes()
    {
        Assert.True(YaesuFt817CatCodec.IsSupportedCtcssTone(67.0));
        var cmd = YaesuFt817CatCodec.BuildCtcssFrequencyCommand(67.0);
        Assert.Equal([0x06, 0x70, 0x06, 0x70, 0x0b], cmd);
    }

    [Fact]
    public void BuildCtcss_192_8Hz_uses_bcd_tone_bytes()
    {
        var cmd = YaesuFt817CatCodec.BuildCtcssFrequencyCommand(192.8);
        Assert.Equal([0x19, 0x28, 0x19, 0x28, 0x0b], cmd);
    }

    [Fact]
    public void Split_commands()
    {
        Assert.Equal(0x02, YaesuFt817CatCodec.SplitOn[4]);
        Assert.Equal(0x82, YaesuFt817CatCodec.SplitOff[4]);
    }

    [Theory]
    [InlineData("FM", true)]
    [InlineData("FMN", true)]
    [InlineData("USB", false)]
    [InlineData("LSB", false)]
    [InlineData("CW", false)]
    public void IsFmMode(string mode, bool expected) =>
        Assert.Equal(expected, YaesuFt817CatCodec.IsFmMode(mode));
}
