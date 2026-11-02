using OscarWatch.Core.Models;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class IcomIc705DriverTests
{
    [Fact]
    public void SelectVfo_Main_uses_vfo_a_civ_selector()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc705Driver(transport);
        driver.Open();
        transport.SentCommandBodies.Clear();

        driver.SelectVfo(RigVfo.Main, force: true);

        Assert.Contains(transport.SentCommandBodies, body => body == "0700");
    }

    [Fact]
    public void ReadFrequencyHz_Main_uses_vfo_a_civ_selector()
    {
        var transport = new RecordingIcomCivTransport { MainHz = 145_960_000 };
        var driver = new IcomIc705Driver(transport);
        driver.Open();
        transport.SentCommandBodies.Clear();

        Assert.Equal(145_960_000, driver.ReadFrequencyHz(RigVfo.Main));
        Assert.Contains(transport.SentCommandBodies, body => body == "0700");
    }

    [Fact]
    public void SetSatelliteMode_is_no_op()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc705Driver(transport);
        driver.Open();
        var countBefore = transport.CommandCount;

        driver.SetSatelliteMode(true);
        driver.SetSatelliteMode(false);

        Assert.Equal(countBefore, transport.CommandCount);
    }

    [Fact]
    public void SetFrequencyHz_on_Main_updates_cached_read()
    {
        var transport = new RecordingIcomCivTransport { MainHz = 435_750_000 };
        var driver = new IcomIc705Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        Assert.True(driver.SetFrequencyHz(435_751_000));
        transport.NextReadResponse = [];
        Assert.Null(driver.ReadFrequencyHz(RigVfo.Main));
        transport.NextReadResponse = null;
        Assert.Equal(435_751_000, driver.ReadFrequencyHz(RigVfo.Main));
    }
}

public sealed class RigEndpointSettingsTests
{
    [Fact]
    public void IsConfigured_ic705_with_port()
    {
        var endpoint = new RigEndpointSettings
        {
            Type = RigType.IcomIc705,
            Port = "COM705"
        };

        Assert.True(endpoint.IsConfigured);
    }

    [Fact]
    public void IsConfigured_ic705_without_port()
    {
        var endpoint = new RigEndpointSettings { Type = RigType.IcomIc705 };

        Assert.False(endpoint.IsConfigured);
    }

    [Fact]
    public void IsConfigured_ft991_with_port()
    {
        var endpoint = new RigEndpointSettings
        {
            Type = RigType.YaesuFt991,
            Port = "COM991"
        };

        Assert.True(endpoint.IsConfigured);
    }

    [Fact]
    public void IsConfigured_dummy_without_port()
    {
        var endpoint = new RigEndpointSettings { Type = RigType.Dummy };

        Assert.True(endpoint.IsConfigured);
    }
}
