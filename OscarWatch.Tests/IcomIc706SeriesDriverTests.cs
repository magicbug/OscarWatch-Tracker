using OscarWatch.Core.Models;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class IcomIc706SeriesDriverTests
{
    [Theory]
    [InlineData(RigType.IcomIc706)]
    [InlineData(RigType.IcomIc706Mkii)]
    [InlineData(RigType.IcomIc706MkiiG)]
    public void SelectVfo_Main_uses_vfo_a_civ_selector(RigType rigType)
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc706SeriesDriver(rigType, transport);
        driver.Open();
        transport.SentCommandBodies.Clear();

        driver.SelectVfo(RigVfo.Main, force: true);

        Assert.Contains(transport.SentCommandBodies, body => body == "0700");
        Assert.Equal(rigType, driver.RigType);
    }

    [Fact]
    public void SetSatelliteMode_is_no_op()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc706SeriesDriver(RigType.IcomIc706MkiiG, transport);
        driver.Open();
        var countBefore = transport.CommandCount;

        driver.SetSatelliteMode(true);
        driver.SetSatelliteMode(false);

        Assert.Equal(countBefore, transport.CommandCount);
    }

    [Theory]
    [InlineData(RigType.IcomIc706, "48")]
    [InlineData(RigType.IcomIc706Mkii, "4C")]
    [InlineData(RigType.IcomIc706MkiiG, "58")]
    public void Default_civ_address_matches_model(RigType rigType, string expected)
    {
        Assert.Equal(expected, RigSettings.DefaultCivAddressFor(rigType));
    }

    [Theory]
    [InlineData(RigType.IcomIc706)]
    [InlineData(RigType.IcomIc706Mkii)]
    [InlineData(RigType.IcomIc706MkiiG)]
    public void IsDualCapableSerialEndpoint_includes_model(RigType rigType) =>
        Assert.True(RigSettings.IsDualCapableSerialEndpoint(rigType));

    [Fact]
    public void SetFrequencyHz_on_Main_updates_cached_read_for_mkiig_70cm()
    {
        var transport = new RecordingIcomCivTransport { MainHz = 435_750_000 };
        var driver = new IcomIc706SeriesDriver(RigType.IcomIc706MkiiG, transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        Assert.True(driver.SetFrequencyHz(435_751_000));
        transport.NextReadResponse = [];
        Assert.Null(driver.ReadFrequencyHz(RigVfo.Main));
        transport.NextReadResponse = null;
        Assert.Equal(435_751_000, driver.ReadFrequencyHz(RigVfo.Main));
    }

    [Theory]
    [InlineData(RigType.IcomIc706)]
    [InlineData(RigType.IcomIc706Mkii)]
    public void SetFrequencyHz_rejects_70cm_on_hf_vhf_models(RigType rigType)
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc706SeriesDriver(rigType, transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        Assert.False(driver.SetFrequencyHz(435_750_000));
        Assert.Equal(0, transport.SetFrequencyCommandCount);
    }

    [Fact]
    public void SetFrequencyHz_accepts_2m_on_all_models()
    {
        foreach (var rigType in new[] { RigType.IcomIc706, RigType.IcomIc706Mkii, RigType.IcomIc706MkiiG })
        {
            var transport = new RecordingIcomCivTransport();
            var driver = new IcomIc706SeriesDriver(rigType, transport);
            driver.Open();
            driver.SelectVfo(RigVfo.Main);

            Assert.True(driver.SetFrequencyHz(145_960_000));
        }
    }

    [Fact]
    public void SetFrequencyHz_accepts_10m_on_all_models()
    {
        foreach (var rigType in new[] { RigType.IcomIc706, RigType.IcomIc706Mkii, RigType.IcomIc706MkiiG })
        {
            var transport = new RecordingIcomCivTransport();
            var driver = new IcomIc706SeriesDriver(rigType, transport);
            driver.Open();
            driver.SelectVfo(RigVfo.Main);

            Assert.True(driver.SetFrequencyHz(29_450_000));
        }
    }

    [Fact]
    public void SetFrequencyHz_accepts_70cm_only_on_mkiig()
    {
        var transport = new RecordingIcomCivTransport();
        var driver = new IcomIc706SeriesDriver(RigType.IcomIc706MkiiG, transport);
        driver.Open();
        driver.SelectVfo(RigVfo.Main);

        Assert.True(driver.SetFrequencyHz(435_750_000));
    }
}

public sealed class IcomIc706SeriesEndpointSettingsTests
{
    [Theory]
    [InlineData(RigType.IcomIc706)]
    [InlineData(RigType.IcomIc706Mkii)]
    [InlineData(RigType.IcomIc706MkiiG)]
    public void IsConfigured_with_port(RigType rigType)
    {
        var endpoint = new RigEndpointSettings
        {
            Type = rigType,
            Port = "COM706"
        };

        Assert.True(endpoint.IsConfigured);
    }

    [Fact]
    public void IsConfigured_without_port()
    {
        var endpoint = new RigEndpointSettings { Type = RigType.IcomIc706 };

        Assert.False(endpoint.IsConfigured);
    }
}
