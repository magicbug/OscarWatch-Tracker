using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

/// <summary>
/// Validates link-hold polling behaviour: one FA; per interval (SatPC32-style ~1/s)
/// while SATL tracking is active.
/// </summary>
public class Ts2000LinkHoldTests : Ts2000TestBase
{
    [Fact]
    public void LinkHoldPollNow_sends_one_FA_command_in_satellite_mode()
    {
        EnterSatelliteMode();
        ClearCommandLog();

        Driver.SendSatelliteLinkHoldPollNow();

        var cmds = GetSentCommands();
        Assert.Single(cmds);
        Assert.Equal("FA;", cmds[0]);
    }

    [Fact]
    public void LinkHoldPollIfDue_rate_limits_to_one_FA_per_interval()
    {
        EnterSatelliteMode();
        ClearCommandLog();

        Driver.SendSatelliteLinkHoldPollIfDue();
        Assert.Single(GetSentCommands());

        ClearCommandLog();
        Driver.SendSatelliteLinkHoldPollIfDue();
        Assert.Empty(GetSentCommands());
    }

    [Fact]
    public void LinkHoldPollIfDue_sends_again_after_interval_elapses()
    {
        var transport = Ts2000TransportFactory.CreateRecordingTransport();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var driver = new KenwoodTs2000Driver(
            transport,
            catDelayMs: 0,
            satModeSettlingDelayMs: 0,
            linkHoldPollIntervalMs: 50,
            utcNow: () => now);
        driver.Open();
        driver.SetSatelliteMode(true);
        transport.SentCommands.Clear();

        driver.SendSatelliteLinkHoldPollIfDue();
        now = now.AddMilliseconds(50);
        driver.SendSatelliteLinkHoldPollIfDue();

        Assert.Equal(2, transport.SentCommands.Count(c => c == "FA;"));
        driver.Dispose();
    }

    [Fact]
    public void LinkHoldPoll_sends_no_commands_when_not_in_satellite_mode()
    {
        ClearCommandLog();

        Driver.SendSatelliteLinkHoldPollNow();

        Assert.Empty(GetSentCommands());
    }

    [Fact]
    public void LinkHoldPoll_sends_no_commands_when_transport_not_open()
    {
        var transport = Ts2000TransportFactory.CreateRecordingTransport();
        var driver = new KenwoodTs2000Driver(
            transport,
            catDelayMs: 0,
            satModeSettlingDelayMs: 0,
            satModeRetryCount: 3,
            satModeRetryDelayMs: 0);

        driver.SetSatelliteMode(true);
        transport.SentCommands.Clear();

        driver.SendSatelliteLinkHoldPollNow();

        Assert.Empty(transport.SentCommands);
        driver.Dispose();
    }
}
