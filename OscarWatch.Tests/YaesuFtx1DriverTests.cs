using OscarWatch.Core.Models;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class YaesuFtx1DriverTests
{
    [Fact]
    public void Create_returns_ftx1_rig_type()
    {
        var driver = new YaesuFtx1Driver(new RecordingYaesuNewCatTransport());
        Assert.Equal(RigType.YaesuFtx1, driver.RigType);
    }

    [Fact]
    public void SetFrequencyHz_on_main_uses_fa_command()
    {
        var transport = new RecordingYaesuNewCatTransport();
        var driver = new YaesuFtx1Driver(transport);
        driver.Open();
        transport.SentCommands.Clear();

        driver.SelectVfo(RigVfo.Main);
        Assert.True(driver.SetFrequencyHz(435_825_000));

        Assert.Contains("FA435825000;", transport.SentCommands);
    }

    [Fact]
    public void SetMode_fm_locks_dial()
    {
        var transport = new RecordingYaesuNewCatTransport();
        var driver = new YaesuFtx1Driver(transport);
        driver.Open();
        transport.SentCommands.Clear();

        driver.SetMode("FM");

        Assert.Contains("MD04;", transport.SentCommands);
        Assert.Contains("LK1;", transport.SentCommands);
    }

    [Fact]
    public void Identity_accepts_any_four_digit_id()
    {
        var transport = new RecordingYaesuNewCatTransport();
        transport.Responses.Enqueue("ID1234;");
        var driver = new YaesuFtx1Driver(transport);
        driver.Open();

        Assert.True(driver.TryConfirmIdentity());
    }
}
