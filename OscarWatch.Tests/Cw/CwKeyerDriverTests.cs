using OscarWatch.Core.Cw;
using OscarWatch.Core.Models;
using OscarWatch.Rig;

namespace OscarWatch.Tests.Cw;

public sealed class CwKeyerDriverTests
{
    private static CwMessage Text(string text) => CwMessage.FromText(text);

    [Fact]
    public void Icom_text_is_sent_once_even_without_an_ack()
    {
        var transport = new RecordingIcomCivTransport();
        using var driver = new IcomIc9700Driver(transport);
        driver.Open();
        transport.CommandResponses.Enqueue([]);

        Assert.True(driver.TrySendCwText(Text("CQ")));

        Assert.Single(transport.SentCommandBodies, b => b.StartsWith("17", StringComparison.Ordinal));
        Assert.Equal("17" + Convert.ToHexString("CQ"u8).ToLowerInvariant(), transport.SentCommandBodies[^1]);
    }

    [Fact]
    public void Icom_text_is_refused_on_a_nak()
    {
        var transport = new RecordingIcomCivTransport();
        using var driver = new IcomIc7300Driver(transport);
        driver.Open();
        transport.CommandResponses.Enqueue([0xFE, 0xFE, 0x60, 0x00, 0xFA, 0xFD]);

        Assert.False(driver.TrySendCwText(Text("TEST")));
    }

    [Fact]
    public void Icom_stop_and_speed_commands()
    {
        var transport = new RecordingIcomCivTransport();
        using var driver = new IcomIc9700Driver(transport);
        driver.Open();

        Assert.True(driver.TryStopCwText());
        Assert.True(driver.TrySetCwSpeed(20));

        Assert.Contains("17ff", transport.SentCommandBodies);
        Assert.Contains("140c0085", transport.SentCommandBodies);
    }

    [Fact]
    public void Icom_radio_without_0x17_does_not_offer_text()
    {
        var transport = new RecordingIcomCivTransport();
        using var driver = new IcomIc821hDriver(transport);
        driver.Open();

        Assert.False(driver.SupportsCwText);
        Assert.False(driver.TrySendCwText(Text("CQ")));
        Assert.Empty(transport.SentCommandBodies);
    }

    [Fact]
    public void Kenwood_hf_sends_ky_ks_and_stop()
    {
        var transport = new RecordingKenwoodHfTransport("021");
        using var driver = new KenwoodHfDriver(RigType.KenwoodTs590sg, transport, catDelayMs: 0);
        driver.Open();

        Assert.True(driver.TrySetCwSpeed(25));
        Assert.True(driver.TrySendCwText(Text("CQ")));
        Assert.True(driver.TryStopCwText());

        Assert.Contains("KS025;", transport.SentCommands);
        Assert.Contains("KY CQ                      ;", transport.SentCommands);
        Assert.Contains("KY0;", transport.SentCommands);
    }

    [Fact]
    public void Ts2000_stops_with_a_blank_buffer()
    {
        var transport = new RecordingKenwoodCatTransport();
        using var driver = new KenwoodTs2000Driver(transport);
        driver.Open();

        Assert.True(driver.TryStopCwText());

        Assert.Contains("KY " + new string(' ', 24) + ";", transport.SentCommands);
    }
}
