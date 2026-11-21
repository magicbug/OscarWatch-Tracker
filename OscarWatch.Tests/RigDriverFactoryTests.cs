using OscarWatch.Core.Models;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class RigDriverFactoryTests
{
    [Fact]
    public void Create_endpoint_ft817_returns_driver_not_exception()
    {
        var driver = RigDriverFactory.Create(new RigEndpointSettings
        {
            Type = RigType.YaesuFt817,
            Port = "COM_TEST",
            BaudRate = 4800
        });

        Assert.Equal(RigType.YaesuFt817, driver.RigType);
    }

    [Fact]
    public void Create_endpoint_ft857_returns_driver()
    {
        var driver = RigDriverFactory.Create(new RigEndpointSettings
        {
            Type = RigType.YaesuFt857,
            Port = "COM_TEST",
            BaudRate = RigSettings.Ft857DefaultBaudRate
        });

        Assert.IsType<YaesuFt857Driver>(driver);
        Assert.Equal(RigType.YaesuFt857, driver.RigType);
    }

    [Fact]
    public void Create_settings_ft857_when_not_dual_throws()
    {
        Assert.Throws<InvalidOperationException>(() => RigDriverFactory.Create(new RigSettings
        {
            Type = RigType.YaesuFt857,
            Port = "COM857"
        }));
    }

    [Fact]
    public void IsDualCapableSerialEndpoint_includes_ft857() =>
        Assert.True(RigSettings.IsDualCapableSerialEndpoint(RigType.YaesuFt857));

    [Fact]
    public void Create_endpoint_ic705_returns_driver_with_civ_address()
    {
        var driver = RigDriverFactory.Create(new RigEndpointSettings
        {
            Type = RigType.IcomIc705,
            Port = "COM705",
            BaudRate = 115200,
            CivAddress = "A4"
        });

        Assert.Equal(RigType.IcomIc705, driver.RigType);
    }

    [Fact]
    public void Create_settings_ic705_when_not_dual_throws()
    {
        Assert.Throws<InvalidOperationException>(() => RigDriverFactory.Create(new RigSettings
        {
            Type = RigType.IcomIc705,
            Port = "COM705"
        }));
    }

    [Fact]
    public void Create_endpoint_ic905_returns_driver_with_civ_address()
    {
        var driver = RigDriverFactory.Create(new RigEndpointSettings
        {
            Type = RigType.IcomIc905,
            Port = "COM905",
            BaudRate = 115200,
            CivAddress = "AC"
        });

        Assert.Equal(RigType.IcomIc905, driver.RigType);
    }

    [Fact]
    public void Create_settings_ic905_when_not_dual_throws()
    {
        Assert.Throws<InvalidOperationException>(() => RigDriverFactory.Create(new RigSettings
        {
            Type = RigType.IcomIc905,
            Port = "COM905"
        }));
    }

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

    [Theory]
    [InlineData(RigType.IcomIc706)]
    [InlineData(RigType.IcomIc706Mkii)]
    [InlineData(RigType.IcomIc706MkiiG)]
    public void Create_endpoint_ic706_series_returns_driver_with_civ_address(RigType rigType)
    {
        var driver = RigDriverFactory.Create(new RigEndpointSettings
        {
            Type = rigType,
            Port = "COM706",
            BaudRate = 19200,
            CivAddress = RigSettings.DefaultCivAddressFor(rigType)
        });

        Assert.Equal(rigType, driver.RigType);
    }

    [Theory]
    [InlineData(RigType.IcomIc706)]
    [InlineData(RigType.IcomIc706Mkii)]
    [InlineData(RigType.IcomIc706MkiiG)]
    public void Create_settings_ic706_series_when_not_dual_throws(RigType rigType)
    {
        Assert.Throws<InvalidOperationException>(() => RigDriverFactory.Create(new RigSettings
        {
            Type = rigType,
            Port = "COM706"
        }));
    }

    [Fact]
    public void Create_endpoint_ft991_returns_driver()
    {
        var driver = RigDriverFactory.Create(new RigEndpointSettings
        {
            Type = RigType.YaesuFt991,
            Port = "COM991",
            BaudRate = 38400
        });

        Assert.Equal(RigType.YaesuFt991, driver.RigType);
    }

    [Fact]
    public void Create_settings_ft991_when_not_dual_throws()
    {
        Assert.Throws<InvalidOperationException>(() => RigDriverFactory.Create(new RigSettings
        {
            Type = RigType.YaesuFt991,
            Port = "COM991"
        }));
    }

    [Fact]
    public void Create_endpoint_ftx1_returns_driver()
    {
        var driver = RigDriverFactory.Create(new RigEndpointSettings
        {
            Type = RigType.YaesuFtx1,
            Port = "COMFTX1",
            BaudRate = 38400
        });

        Assert.Equal(RigType.YaesuFtx1, driver.RigType);
    }

    [Fact]
    public void Create_settings_ftx1_when_not_dual_throws()
    {
        Assert.Throws<InvalidOperationException>(() => RigDriverFactory.Create(new RigSettings
        {
            Type = RigType.YaesuFtx1,
            Port = "COMFTX1"
        }));
    }

    [Theory]
    [InlineData(RigType.KenwoodThD74)]
    [InlineData(RigType.KenwoodThD75)]
    public void Create_endpoint_thd7x_returns_driver(RigType rigType)
    {
        var driver = RigDriverFactory.Create(new RigEndpointSettings
        {
            Type = rigType,
            Port = "COM_THD7X",
            BaudRate = RigSettings.KenwoodThD7xDefaultBaudRate
        });

        Assert.Equal(rigType, driver.RigType);
    }

    [Theory]
    [InlineData(RigType.KenwoodThD74)]
    [InlineData(RigType.KenwoodThD75)]
    public void Create_settings_thd7x_when_not_dual_throws(RigType rigType)
    {
        Assert.Throws<InvalidOperationException>(() => RigDriverFactory.Create(new RigSettings
        {
            Type = rigType,
            Port = "COM_THD7X"
        }));
    }

    [Fact]
    public void Create_settings_ft817_when_dual_enabled_throws()
    {
        Assert.Throws<InvalidOperationException>(() => RigDriverFactory.Create(new RigSettings
        {
            DualRadioEnabled = true,
            Type = RigType.YaesuFt817,
            Port = "COM1"
        }));
    }

    [Fact]
    public void Create_settings_flex_returns_driver()
    {
        var driver = RigDriverFactory.Create(new RigSettings
        {
            Type = RigType.FlexSmartSdr,
            NetworkHost = "192.168.1.20",
            NetworkPort = 4992
        });

        Assert.Equal(RigType.FlexSmartSdr, driver.RigType);
    }

    [Fact]
    public void Create_settings_ts790_returns_driver()
    {
        var driver = RigDriverFactory.Create(new RigSettings
        {
            Type = RigType.KenwoodTs790,
            Port = "COM3",
            BaudRate = RigSettings.Ts790DefaultBaudRate
        });

        Assert.Equal(RigType.KenwoodTs790, driver.RigType);
        Assert.False(driver.SupportsVfoExchange);
    }

    [Fact]
    public void Create_endpoint_flex_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RigDriverFactory.EnsureValidDualEndpoint(
                new RigEndpointSettings { Type = RigType.FlexSmartSdr, NetworkHost = "10.0.0.1" },
                isDownlink: true));
    }

    [Fact]
    public void IsFlexNetworkRadio_helpers()
    {
        Assert.True(RigSettings.IsFlexNetworkRadio(RigType.FlexSmartSdr));
        Assert.False(RigSettings.IsDualCapableEndpoint(RigType.FlexSmartSdr));
        Assert.True(new RigSettings
        {
            Type = RigType.FlexSmartSdr,
            NetworkHost = "10.0.0.1",
            NetworkPort = 4992
        }.IsFlexNetworkConfigured);
    }
}
