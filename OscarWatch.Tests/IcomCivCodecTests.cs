using OscarWatch.Core.Radio;

namespace OscarWatch.Tests;

public class IcomCivCodecTests
{
    public static IEnumerable<object[]> FrequencyEncodeRows()
    {
        foreach (var row in GoldenFixtureLoader.Load().FrequencyEncode)
            yield return new object[] { row.Hz, row.PayloadHex };
    }

    [Theory]
    [MemberData(nameof(FrequencyEncodeRows))]
    public void EncodeSetFrequencyHz_matches_golden(long hz, string expectedHex)
    {
        var body = IcomCivCodec.EncodeSetFrequencyHz(hz);
        Assert.Equal(expectedHex, Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void ParseCivAddressHex_parses_hex_values()
    {
        Assert.Equal(0x60, IcomCivCodec.ParseCivAddressHex("60"));
        Assert.Equal(0x60, IcomCivCodec.ParseCivAddressHex(null));
        Assert.Equal(0x7C, IcomCivCodec.ParseCivAddressHex("7C"));
        Assert.Equal(0xA2, IcomCivCodec.ParseCivAddressHex("A2"));
    }

    [Theory]
    [InlineData(145_950_000)]
    [InlineData(435_659_900)]
    [InlineData(432_146_000)]
    [InlineData(29_450_000)]
    public void DecodeFrequencyFromResponse_parses_bcd_digits_as_decimal_hz(long hz)
    {
        var body = IcomCivCodec.EncodeSetFrequencyHz(hz);
        var response = new byte[] { 0xFE, 0xFE, 0x60, 0x00, 0x00, body[1], body[2], body[3], body[4], body[5], 0xFB, 0xFD };
        var decoded = IcomCivCodec.DecodeFrequencyFromResponse(response);
        Assert.Equal(hz, decoded);
    }

    [Fact]
    public void DecodeFrequencyFromResponse_does_not_treat_digit_string_as_hex()
    {
        var decoded = IcomCivCodec.DecodeFrequencyFromResponse(
            BuildReadResponseFromHz(435_659_900));
        Assert.Equal(435_659_900, decoded);
        Assert.NotEqual(18_075_719_936, decoded);
    }

    [Theory]
    [InlineData(67.0, false, "1b000670")]
    [InlineData(67.0, true, "1b010670")]
    [InlineData(74.4, true, "1b010744")]
    [InlineData(141.3, false, "1b001413")]
    public void EncodeToneHz_matches_icom_civ_layout(double hz, bool squelchTone, string expectedHex)
    {
        var body = IcomCivCodec.EncodeToneHz(hz, squelchTone);
        Assert.Equal(expectedHex, Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void BuildCommandFrame_wraps_body()
    {
        var frame = IcomCivCodec.BuildCommandFrame(0x60, [0x03]);
        Assert.Equal(0xFE, frame[0]);
        Assert.Equal(0x60, frame[2]);
        Assert.Equal(0x03, frame[4]);
        Assert.Equal(0xFD, frame[^1]);
    }

    [Theory]
    [InlineData("FM", "0605")]
    [InlineData("FMN", "0605")]
    [InlineData("fm", "0605")]
    [InlineData("USB", "0601")]
    [InlineData("LSB", "0600")]
    [InlineData("DATA-USB", "0601")]
    [InlineData("DATA-LSB", "0600")]
    [InlineData("DATA-FM", "0605")]
    [InlineData("FM-DATA", "0605")]
    [InlineData("CW", "0603")]
    public void EncodeSetModeCommand_maps_modes(string mode, string expectedHex)
    {
        var body = IcomCivCodec.EncodeSetModeCommand(mode);
        Assert.NotNull(body);
        Assert.Equal(expectedHex, Convert.ToHexString(body).ToLowerInvariant());
    }

    [Theory]
    [InlineData("FM")]
    [InlineData("FMN")]
    public void EncodeSetModeCommand_fm_never_uses_rtty_byte(string mode)
    {
        var body = IcomCivCodec.EncodeSetModeCommand(mode);
        Assert.NotNull(body);
        Assert.DoesNotContain((byte)0x04, body);
        Assert.Equal(0x05, body[1]);
    }

    [Theory]
    [InlineData("FMN", "060502")]
    [InlineData("FM", "060501")]
    [InlineData("DATA-FM", "060501")]
    [InlineData("USB", "0601")]
    [InlineData("CW", "0603")]
    public void EncodeIc910SetModeCommand_maps_fm_filter_width(string mode, string expectedHex)
    {
        var body = IcomCivCodec.EncodeIc910SetModeCommand(mode);
        Assert.NotNull(body);
        Assert.Equal(expectedHex, Convert.ToHexString(body).ToLowerInvariant());
    }

    [Theory]
    [InlineData("DATA-USB", new[] { "0601", "1a060101" })]
    [InlineData("DATA-LSB", new[] { "0600", "1a060101" })]
    [InlineData("USB", new[] { "0601", "1a060000" })]
    [InlineData("LSB", new[] { "0600", "1a060000" })]
    [InlineData("FM", new[] { "0605" })]
    [InlineData("CW", new[] { "0603" })]
    [InlineData("DATA-FM", new[] { "0605", "1a060101" })]
    [InlineData("FM-DATA", new[] { "0605", "1a060101" })]
    public void Encode9700SetModeCommands_includes_data_mode_for_digital_sideband(string mode, string[] expectedHex)
    {
        var commands = IcomCivCodec.Encode9700SetModeCommands(mode);
        Assert.Equal(expectedHex.Length, commands.Length);
        for (var i = 0; i < expectedHex.Length; i++)
            Assert.Equal(expectedHex[i], Convert.ToHexString(commands[i]).ToLowerInvariant());
    }

    [Theory]
    [InlineData(145_950_000, true)]
    [InlineData(29_450_000, true)]
    [InlineData(435_659_900, true)]
    [InlineData(1_269_500_000, true)]
    [InlineData(2_400_000_000, true)]
    [InlineData(5_760_000_000, true)]
    [InlineData(10_368_000_000, true)]
    [InlineData(100_000_000, false)]
    [InlineData(300_000_000, false)]
    [InlineData(2_000_000_000, false)]
    public void IsValidSatelliteFrequencyHz_accepts_amateur_satellite_bands(long hz, bool expected) =>
        Assert.Equal(expected, IcomCivCodec.IsValidSatelliteFrequencyHz(hz));

    [Fact]
    public void EncodeSetFrequencyHz_10ghz_uses_six_bcd_bytes()
    {
        var body = IcomCivCodec.EncodeSetFrequencyHz(10_368_000_000);
        Assert.Equal(7, body.Length);
        Assert.Equal(0x05, body[0]);
        Assert.Equal("05000000680301", Convert.ToHexString(body).ToLowerInvariant());
    }

    [Theory]
    [InlineData(0, 0x00, 0x00)]
    [InlineData(128, 0x01, 0x28)]
    [InlineData(255, 0x02, 0x55)]
    public void EncodeLevel255_matches_packed_bcd(int level, byte high, byte low)
    {
        var (h, l) = IcomCivCodec.EncodeLevel255(level);
        Assert.Equal(high, h);
        Assert.Equal(low, l);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(255)]
    public void DecodeLevel255FromResponse_round_trips(int level)
    {
        var (high, low) = IcomCivCodec.EncodeLevel255(level);
        var response = new byte[] { 0xFE, 0xFE, 0xE0, 0xA2, 0x14, 0x0A, high, low, 0xFD };
        Assert.Equal(level, IcomCivCodec.DecodeLevel255FromResponse(response));
    }

    private static byte[] BuildReadResponseFromHz(long hz)
    {
        var body = IcomCivCodec.EncodeSetFrequencyHz(hz);
        var frame = new byte[4 + body.Length + 2];
        frame[0] = 0xFE;
        frame[1] = 0xFE;
        frame[2] = 0x60;
        frame[3] = 0x00;
        frame[4] = 0x00;
        body.AsSpan(1).CopyTo(frame.AsSpan(5));
        frame[^2] = 0xFB;
        frame[^1] = 0xFD;
        return frame;
    }
}
