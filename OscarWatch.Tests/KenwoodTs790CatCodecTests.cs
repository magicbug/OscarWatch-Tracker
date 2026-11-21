using OscarWatch.Core.Radio;

namespace OscarWatch.Tests;

public sealed class KenwoodTs790CatCodecTests
{
    [Fact]
    public void Identity_commands_and_parse()
    {
        Assert.Equal("ID;", KenwoodTs790CatCodec.BuildIdentityQuery());
        Assert.True(KenwoodTs790CatCodec.TryParseIdentity("ID007;", out var id));
        Assert.Equal("007", id);
        Assert.True(KenwoodTs790CatCodec.IsTs790Identity("ID007;"));
        Assert.False(KenwoodTs790CatCodec.IsTs790Identity("ID019;"));
    }

    [Fact]
    public void Destination_and_frequency_commands()
    {
        Assert.Equal("DC0;", KenwoodTs790CatCodec.BuildSelectMainCommand());
        Assert.Equal("DC1;", KenwoodTs790CatCodec.BuildSelectSubCommand());
        Assert.Equal("DC;", KenwoodTs790CatCodec.BuildDestinationQuery());
        Assert.Equal("FN0;", KenwoodTs790CatCodec.BuildSelectVfoACommand());
        Assert.Equal("FA00145900000;", KenwoodTs790CatCodec.BuildSetFrequencyCommand(145_900_000));
        Assert.Equal("FA;", KenwoodTs790CatCodec.BuildReadFrequencyCommand());
        Assert.Equal("MD4;", KenwoodTs790CatCodec.BuildSetModeCommand('4'));
    }

    [Fact]
    public void TryParseDestinationIsSub_reads_dc_reply()
    {
        Assert.True(KenwoodTs790CatCodec.TryParseDestinationIsSub("DC1;", out var sub));
        Assert.True(sub);
        Assert.True(KenwoodTs790CatCodec.TryParseDestinationIsSub("DC0;", out sub));
        Assert.False(sub);
    }

    [Fact]
    public void Tone_commands_reuse_kenwood_table()
    {
        Assert.True(KenwoodCatCodec.TryGetCtcssIndex(67.0, out var index));
        Assert.Equal("TN01;", KenwoodTs790CatCodec.BuildToneFrequencyCommand(index));
        Assert.Equal("TO1;", KenwoodTs790CatCodec.BuildToneEnableCommand(true));
        Assert.Equal("CT0;", KenwoodTs790CatCodec.BuildCtcssEnableCommand(false));
    }
}
