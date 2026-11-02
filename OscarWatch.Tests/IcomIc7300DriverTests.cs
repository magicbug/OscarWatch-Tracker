using OscarWatch.Core.Models;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class IcomIc7300DriverTests
{
    [Fact]
    public void SelectVfo_Main_uses_vfo_a_civ_selector()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc7300Driver(transport);
        driver.Open();
        transport.SentCommandBodies.Clear();

        driver.SelectVfo(RigVfo.Main, force: true);

        Assert.Contains(transport.SentCommandBodies, body => body == "0700");
    }

    [Fact]
    public void ReadFrequencyHz_Main_uses_vfo_a_civ_selector()
    {
        var transport = new RecordingIcomCivTransport { MainHz = 29_450_000 };
        var driver = new IcomIc7300Driver(transport);
        driver.Open();
        transport.SentCommandBodies.Clear();

        Assert.Equal(29_450_000, driver.ReadFrequencyHz(RigVfo.Main));
        Assert.Contains(transport.SentCommandBodies, body => body == "0700");
    }

    [Fact]
    public void SetSatelliteMode_is_no_op()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc7300Driver(transport);
        driver.Open();
        var countBefore = transport.CommandCount;

        driver.SetSatelliteMode(true);
        driver.SetSatelliteMode(false);

        Assert.Equal(countBefore, transport.CommandCount);
    }

    [Fact]
    public void SetFrequencyHz_on_Main_updates_cached_read()
    {
        var transport = new RecordingIcomCivTransport { MainHz = 29_450_000 };
        var driver = new IcomIc7300Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        Assert.True(driver.SetFrequencyHz(29_451_000));
        transport.NextReadResponse = [];
        Assert.Null(driver.ReadFrequencyHz(RigVfo.Main));
        transport.NextReadResponse = null;
        Assert.Equal(29_451_000, driver.ReadFrequencyHz(RigVfo.Main));
    }

    [Fact]
    public void DefaultCivAddress_is_94() =>
        Assert.Equal("94", RigSettings.DefaultCivAddressFor(RigType.IcomIc7300));

    [Fact]
    public void IsDualCapableSerialEndpoint_includes_ic7300() =>
        Assert.True(RigSettings.IsDualCapableSerialEndpoint(RigType.IcomIc7300));

    [Fact]
    public void SetFrequencyHz_rejects_vhf_on_hf_only_radio()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc7300Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        Assert.False(driver.SetFrequencyHz(145_960_000));
        Assert.Equal(0, transport.SetFrequencyCommandCount);
    }

    [Fact]
    public void SetFrequencyHz_accepts_10m_hf()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc7300Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        Assert.True(driver.SetFrequencyHz(29_450_000));
    }
}

public sealed class IcomIc7300EndpointSettingsTests
{
    [Fact]
    public void IsConfigured_ic7300_with_port()
    {
        var endpoint = new RigEndpointSettings
        {
            Type = RigType.IcomIc7300,
            Port = "COM7300"
        };

        Assert.True(endpoint.IsConfigured);
    }

    [Fact]
    public void IsConfigured_ic7300_without_port()
    {
        var endpoint = new RigEndpointSettings { Type = RigType.IcomIc7300 };

        Assert.False(endpoint.IsConfigured);
    }
}

public sealed class IcomIc7300FactoryTests
{
    [Fact]
    public void Create_endpoint_ic7300_returns_driver_with_civ_address()
    {
        var driver = RigDriverFactory.Create(new RigEndpointSettings
        {
            Type = RigType.IcomIc7300,
            Port = "COM7300",
            BaudRate = 115200,
            CivAddress = "94"
        });

        Assert.Equal(RigType.IcomIc7300, driver.RigType);
    }

    [Fact]
    public void Create_settings_ic7300_when_not_dual_throws()
    {
        Assert.Throws<InvalidOperationException>(() => RigDriverFactory.Create(new RigSettings
        {
            Type = RigType.IcomIc7300,
            Port = "COM7300"
        }));
    }
}
