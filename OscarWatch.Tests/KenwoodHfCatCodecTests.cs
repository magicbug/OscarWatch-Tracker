using System.Text;
using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;

namespace OscarWatch.Tests;

public sealed class KenwoodHfCatCodecTests
{
    [Theory]
    [InlineData("USB", "OM02;")]
    [InlineData("LSB", "OM01;")]
    [InlineData("CW", "OM03;")]
    [InlineData("FM", "OM04;")]
    [InlineData("AM", "OM05;")]
    [InlineData("DATA-USB", "OM0D;")]
    [InlineData("unknown", "OM02;")]
    public void Ts890s_and_ts990s_use_operating_mode_command(string mode, string expected)
    {
        Assert.Equal(expected, KenwoodHfCatCodec.BuildSetModeCommand(KenwoodHfCatCodec.Ts890s, mode));
        Assert.Equal(expected, KenwoodHfCatCodec.BuildSetModeCommand(KenwoodHfCatCodec.Ts990s, mode));
    }

    [Theory]
    [InlineData("USB", "MD2;")]
    [InlineData("LSB", "MD1;")]
    [InlineData("CW", "MD3;")]
    [InlineData("FM", "MD4;")]
    [InlineData("unknown", "MD2;")]
    public void Ts590_uses_md_command(string mode, string expected)
    {
        Assert.Equal(expected, KenwoodHfCatCodec.BuildSetModeCommand(KenwoodHfCatCodec.Ts590s, mode));
        Assert.Equal(expected, KenwoodHfCatCodec.BuildSetModeCommand(KenwoodHfCatCodec.Ts590sg, mode));
    }

    [Fact]
    public void Main_simplex_commands_differ_by_family()
    {
        Assert.Equal(["CB0;", "TB0;"], KenwoodHfCatCodec.BuildMainSimplexCommands(KenwoodHfCatCodec.Ts890s));
        Assert.Equal(["FR0;"], KenwoodHfCatCodec.BuildMainSimplexCommands(KenwoodHfCatCodec.Ts590sg));
    }

    [Theory]
    [InlineData(RigType.KenwoodTs990s, "022")]
    [InlineData(RigType.KenwoodTs890s, "024")]
    [InlineData(RigType.KenwoodTs590s, "021")]
    [InlineData(RigType.KenwoodTs590sg, "023")]
    public void Profiles_expect_documented_ids(RigType type, string id)
    {
        Assert.Equal(id, KenwoodHfCatCodec.GetProfile(type).ExpectedId);
        Assert.True(KenwoodHfCatCodec.TryParseId($"ID{id};", out var parsed));
        Assert.Equal(id, parsed);
    }

    [Fact]
    public void Ts890s_login_uses_account_type_and_two_digit_lengths()
    {
        Assert.True(KenwoodHfCatCodec.TryBuildLanLoginCommand(KenwoodHfCatCodec.Ts890s, "admin", "secret12", true, out var admin));
        Assert.Equal("##ID00508adminsecret12;", admin);
        Assert.True(KenwoodHfCatCodec.TryBuildLanLoginCommand(KenwoodHfCatCodec.Ts890s, "op", "pw", false, out var user));
        Assert.Equal("##ID10202oppw;", user);
    }

    [Fact]
    public void Ts990s_login_uses_one_digit_lengths_and_max_eight_characters()
    {
        Assert.True(KenwoodHfCatCodec.TryBuildLanLoginCommand(KenwoodHfCatCodec.Ts990s, "admin", "secret12", true, out var command));
        Assert.Equal("##ID58adminsecret12;", command);
        Assert.False(KenwoodHfCatCodec.TryBuildLanLoginCommand(KenwoodHfCatCodec.Ts990s, "admin", "toolong12", true, out _));
    }

    [Fact]
    public void Login_rejects_semicolons_empty_values_and_ts590()
    {
        Assert.False(KenwoodHfCatCodec.TryBuildLanLoginCommand(KenwoodHfCatCodec.Ts890s, "a;b", "pw", true, out _));
        Assert.False(KenwoodHfCatCodec.TryBuildLanLoginCommand(KenwoodHfCatCodec.Ts890s, "admin", "", true, out _));
        Assert.False(KenwoodHfCatCodec.TryBuildLanLoginCommand(KenwoodHfCatCodec.Ts590sg, "admin", "pw", true, out _));
    }

    [Fact]
    public void Only_ts990s_uses_utf16_on_lan()
    {
        var ts990 = KenwoodHfCatCodec.GetLanEncoding(KenwoodHfCatCodec.Ts990s);
        var ts890 = KenwoodHfCatCodec.GetLanEncoding(KenwoodHfCatCodec.Ts890s);
        Assert.Equal(new byte[] { (byte)'I', 0, (byte)'D', 0 }, ts990.GetBytes("ID"));
        Assert.Empty(ts990.GetPreamble());
        Assert.Equal(Encoding.ASCII.GetBytes("ID"), ts890.GetBytes("ID"));
        Assert.Empty(ts890.GetPreamble());
    }

    [Theory]
    [InlineData("##CN1;", true)]
    [InlineData("##CN0;", false)]
    public void Lan_connect_reply_parses(string reply, bool accepted)
    {
        Assert.True(KenwoodHfCatCodec.TryParseLanConnect(reply, out var result));
        Assert.Equal(accepted, result);
    }

    [Fact]
    public void Transverter_state_queries_differ_by_family()
    {
        Assert.Equal("XV;", KenwoodHfCatCodec.BuildTransverterStateQuery(KenwoodHfCatCodec.Ts890s));
        Assert.Equal("EX0500000;", KenwoodHfCatCodec.BuildTransverterStateQuery(KenwoodHfCatCodec.Ts590s));
        Assert.Equal("EX0560000;", KenwoodHfCatCodec.BuildTransverterStateQuery(KenwoodHfCatCodec.Ts590sg));
    }

    [Theory]
    [InlineData("XV1;", true)]
    [InlineData("XV0;", false)]
    public void Xv_reply_parses(string reply, bool on)
    {
        Assert.True(KenwoodHfCatCodec.TryParseTransverterOn(KenwoodHfCatCodec.Ts990s, reply, out var result));
        Assert.Equal(on, result);
    }

    [Theory]
    [InlineData("EX05600000;", false)]
    [InlineData("EX05600001;", true)]
    [InlineData("EX05600002;", true)]
    public void Ts590sg_menu_reply_parses(string reply, bool on)
    {
        Assert.True(KenwoodHfCatCodec.TryParseTransverterOn(KenwoodHfCatCodec.Ts590sg, reply, out var result));
        Assert.Equal(on, result);
    }

    [Fact]
    public void Ts590_menu_reply_for_another_menu_is_rejected()
    {
        Assert.False(KenwoodHfCatCodec.TryParseTransverterOn(KenwoodHfCatCodec.Ts590s, "EX05600001;", out _));
    }

    [Theory]
    [InlineData("XO000116000000;", false, 116_000_000L)]
    [InlineData("XO100175000000;", true, 175_000_000L)]
    public void Xo_reply_parses(string reply, bool minus, long offsetHz)
    {
        Assert.True(KenwoodHfCatCodec.TryParseTransverterOffset(reply, out var parsedMinus, out var parsedOffset));
        Assert.Equal(minus, parsedMinus);
        Assert.Equal(offsetHz, parsedOffset);
    }

    [Fact]
    public void Plus_offset_converts_sky_to_vfo_and_back()
    {
        Assert.True(KenwoodHfCatCodec.TrySkyToVfoHz(145_900_000, true, false, 116_000_000, out var vfo));
        Assert.Equal(29_900_000, vfo);
        Assert.Equal(145_900_000, KenwoodHfCatCodec.VfoToSkyHz(vfo, true, false, 116_000_000));
    }

    [Fact]
    public void Minus_offset_inverts_the_if()
    {
        Assert.True(KenwoodHfCatCodec.TrySkyToVfoHz(145_900_000, true, true, 175_000_000, out var vfo));
        Assert.Equal(29_100_000, vfo);
        Assert.Equal(145_900_000, KenwoodHfCatCodec.VfoToSkyHz(vfo, true, true, 175_000_000));
    }

    [Fact]
    public void Transverter_off_passes_frequency_through()
    {
        Assert.True(KenwoodHfCatCodec.TrySkyToVfoHz(29_400_000, false, false, 116_000_000, out var vfo));
        Assert.Equal(29_400_000, vfo);
    }

    [Fact]
    public void Sky_below_plus_offset_cannot_be_tuned()
    {
        Assert.False(KenwoodHfCatCodec.TrySkyToVfoHz(100_000_000, true, false, 116_000_000, out _));
    }
}
