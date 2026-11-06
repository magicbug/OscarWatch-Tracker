using OscarWatch.Core.Models;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class KenwoodHfDriverTests
{
    [Theory]
    [InlineData(RigType.KenwoodTs990s, "022")]
    [InlineData(RigType.KenwoodTs890s, "024")]
    [InlineData(RigType.KenwoodTs590s, "021")]
    [InlineData(RigType.KenwoodTs590sg, "023")]
    public void Identity_matches_profile_id(RigType type, string id)
    {
        var transport = new RecordingKenwoodHfTransport(id);
        using var driver = new KenwoodHfDriver(type, transport, catDelayMs: 0);
        driver.Open();

        Assert.True(driver.TryConfirmIdentity());
        Assert.Equal(["AI0;", "ID;"], transport.SentCommands);
    }

    [Fact]
    public void Identity_rejects_another_kenwood_model()
    {
        var transport = new RecordingKenwoodHfTransport("024");
        using var driver = new KenwoodHfDriver(RigType.KenwoodTs990s, transport, catDelayMs: 0);
        driver.Open();

        Assert.False(driver.TryConfirmIdentity());
    }

    [Fact]
    public void Ts890s_pass_tune_uses_cb_tb_om_and_fa()
    {
        var transport = new RecordingKenwoodHfTransport("024");
        using var driver = new KenwoodHfDriver(RigType.KenwoodTs890s, transport, catDelayMs: 0);
        driver.Open();
        driver.SelectVfo(RigVfo.Main, force: true);
        driver.SetMode("USB");
        Assert.True(driver.SetFrequencyHz(29_400_000));

        Assert.Equal(["AI0;", "CB0;", "TB0;", "XV;", "OM02;", "FA00029400000;"], transport.SentCommands);
    }

    [Fact]
    public void Ts590sg_pass_tune_uses_fr_md_and_fa()
    {
        var transport = new RecordingKenwoodHfTransport("023") { TransverterStateReply = "EX05600000;" };
        using var driver = new KenwoodHfDriver(RigType.KenwoodTs590sg, transport, catDelayMs: 0);
        driver.Open();
        driver.SetMode("LSB");
        Assert.True(driver.SetFrequencyHz(29_400_000));

        Assert.Equal(["AI0;", "FR0;", "EX0560000;", "MD1;", "FA00029400000;"], transport.SentCommands);
    }

    [Fact]
    public void Ts890s_transverter_tunes_if_and_reads_back_sky_frequency()
    {
        var transport = new RecordingKenwoodHfTransport("024")
        {
            TransverterStateReply = "XV1;",
            TransverterOffsetReply = "XO000116000000;"
        };
        using var driver = new KenwoodHfDriver(RigType.KenwoodTs890s, transport, catDelayMs: 0);
        driver.Open();
        driver.SetMode("FM");

        Assert.True(driver.SetFrequencyHz(145_900_000));
        Assert.Contains("FA00029900000;", transport.SentCommands);
        Assert.Equal(145_900_000, driver.ReadFrequencyHz(RigVfo.Main));
        Assert.True(driver.TransverterOn);
        Assert.Equal(116_000_000, driver.TransverterOffsetHz);
    }

    [Fact]
    public void Ts590s_transverter_menu_on_applies_offset()
    {
        var transport = new RecordingKenwoodHfTransport("021")
        {
            TransverterStateReply = "EX05000001;",
            TransverterOffsetReply = "XO000406000000;"
        };
        using var driver = new KenwoodHfDriver(RigType.KenwoodTs590s, transport, catDelayMs: 0);
        driver.Open();
        driver.SetMode("USB");

        Assert.True(driver.SetFrequencyHz(435_250_000));
        Assert.Contains("EX0500000;", transport.SentCommands);
        Assert.Contains("FA00029250000;", transport.SentCommands);
    }

    [Fact]
    public void Mode_change_rereads_transverter_state()
    {
        var transport = new RecordingKenwoodHfTransport("024");
        using var driver = new KenwoodHfDriver(RigType.KenwoodTs890s, transport, catDelayMs: 0);
        driver.Open();
        driver.SetMode("FM");
        Assert.False(driver.TransverterOn);

        transport.TransverterStateReply = "XV1;";
        transport.TransverterOffsetReply = "XO000116000000;";
        driver.SetMode("FM");
        Assert.True(driver.SetFrequencyHz(145_900_000));
        Assert.Contains("FA00029900000;", transport.SentCommands);
    }

    [Fact]
    public void Untunable_frequency_rereads_transverter_and_retries()
    {
        var transport = new RecordingKenwoodHfTransport("024")
        {
            TransverterStateReply = "XV1;",
            TransverterOffsetReply = "XO000116000000;"
        };
        using var driver = new KenwoodHfDriver(RigType.KenwoodTs890s, transport, catDelayMs: 0);
        driver.Open();
        driver.SetMode("USB");

        transport.TransverterStateReply = "XV0;";
        Assert.True(driver.SetFrequencyHz(29_400_000));
        Assert.False(driver.TransverterOn);
        Assert.Contains("FA00029400000;", transport.SentCommands);
    }

    [Fact]
    public void Doppler_steps_do_not_resend_session_commands()
    {
        var transport = new RecordingKenwoodHfTransport("024");
        using var driver = new KenwoodHfDriver(RigType.KenwoodTs890s, transport, catDelayMs: 0);
        driver.Open();
        driver.SetMode("USB");
        transport.SentCommands.Clear();

        driver.SelectVfo(RigVfo.Main, force: true);
        driver.SetFrequencyHz(29_400_100);
        driver.SelectVfo(RigVfo.Main, force: true);
        driver.SetFrequencyHz(29_400_200);

        Assert.Equal(["FA00029400100;", "FA00029400200;"], transport.SentCommands);
    }
}
