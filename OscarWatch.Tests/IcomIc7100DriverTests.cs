using OscarWatch.Core.Models;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class IcomIc7100DriverTests
{
    [Fact]
    public void SelectVfo_Main_uses_vfo_a_civ_selector()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc7100Driver(transport);
        driver.Open();
        transport.SentCommandBodies.Clear();

        driver.SelectVfo(RigVfo.Main, force: true);

        Assert.Contains(transport.SentCommandBodies, body => body == "0700");
    }

    [Fact]
    public void ReadFrequencyHz_Main_uses_vfo_a_civ_selector()
    {
        var transport = new RecordingIcomCivTransport { MainHz = 29_450_000 };
        var driver = new IcomIc7100Driver(transport);
        driver.Open();
        transport.SentCommandBodies.Clear();

        Assert.Equal(29_450_000, driver.ReadFrequencyHz(RigVfo.Main));
        Assert.Contains(transport.SentCommandBodies, body => body == "0700");
    }

    [Fact]
    public void SetSatelliteMode_is_no_op()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc7100Driver(transport);
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
        var driver = new IcomIc7100Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        Assert.True(driver.SetFrequencyHz(29_451_000));
        transport.NextReadResponse = [];
        Assert.Null(driver.ReadFrequencyHz(RigVfo.Main));
        transport.NextReadResponse = null;
        Assert.Equal(29_451_000, driver.ReadFrequencyHz(RigVfo.Main));
    }

    [Fact]
    public void DefaultCivAddress_is_88() =>
        Assert.Equal("88", RigSettings.DefaultCivAddressFor(RigType.IcomIc7100));

    [Fact]
    public void IsDualCapableSerialEndpoint_includes_ic7100() =>
        Assert.True(RigSettings.IsDualCapableSerialEndpoint(RigType.IcomIc7100));

    [Fact]
    public void SetFrequencyHz_accepts_10m_hf()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc7100Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        Assert.True(driver.SetFrequencyHz(29_450_000));
    }

    [Fact]
    public void SetFrequencyHz_accepts_2m()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc7100Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        Assert.True(driver.SetFrequencyHz(145_960_000));
    }

    [Fact]
    public void SetFrequencyHz_accepts_70cm()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc7100Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        Assert.True(driver.SetFrequencyHz(435_000_000));
    }

    [Fact]
    public void SetFrequencyHz_rejects_23cm()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc7100Driver(transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        Assert.False(driver.SetFrequencyHz(1_269_000_000));
        Assert.Equal(0, transport.SetFrequencyCommandCount);
    }
}

public sealed class IcomIc7100EndpointSettingsTests
{
    [Fact]
    public void IsConfigured_ic7100_with_port()
    {
        var endpoint = new RigEndpointSettings
        {
            Type = RigType.IcomIc7100,
            Port = "COM7100"
        };

        Assert.True(endpoint.IsConfigured);
    }

    [Fact]
    public void IsConfigured_ic7100_without_port()
    {
        var endpoint = new RigEndpointSettings { Type = RigType.IcomIc7100 };

        Assert.False(endpoint.IsConfigured);
    }
}

public sealed class IcomIc7100FactoryTests
{
    [Fact]
    public void Create_endpoint_ic7100_returns_driver_with_civ_address()
    {
        var driver = RigDriverFactory.Create(new RigEndpointSettings
        {
            Type = RigType.IcomIc7100,
            Port = "COM7100",
            BaudRate = 19200,
            CivAddress = "88"
        });

        Assert.Equal(RigType.IcomIc7100, driver.RigType);
    }

    [Fact]
    public void Create_settings_ic7100_when_not_dual_throws()
    {
        Assert.Throws<InvalidOperationException>(() => RigDriverFactory.Create(new RigSettings
        {
            Type = RigType.IcomIc7100,
            Port = "COM7100"
        }));
    }
}
