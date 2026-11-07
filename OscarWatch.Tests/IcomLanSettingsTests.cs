using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;
using OscarWatch.Diagnostics;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class IcomLanSettingsTests
{
    [Fact]
    public void Ic9700_is_the_only_single_radio_with_network_control()
    {
        Assert.True(RigSettings.IsIcomLanSingleRadio(RigType.IcomIc9700));
        Assert.False(RigSettings.IsIcomLanSingleRadio(RigType.IcomIc9100));
        Assert.False(RigSettings.IsIcomLanSingleRadio(RigType.IcomIc705));
    }

    [Theory]
    [InlineData(RigType.IcomIc705)]
    [InlineData(RigType.IcomIc905)]
    [InlineData(RigType.IcomIc7300)]
    public void Network_capable_legs(RigType type)
    {
        Assert.True(RigSettings.IsIcomLanEndpoint(type));
        Assert.True(RigSettings.IsIcomLanCapable(type));
    }

    [Fact]
    public void Ic9700_lan_needs_host_port_user_and_password()
    {
        var rig = new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc9700,
            Connection = RigConnectionKind.Lan,
            Port = "COM5",
            NetworkHost = "192.168.1.60",
            NetworkPort = RigSettings.IcomLanDefaultPort,
            NetworkUser = "oscar"
        };
        Assert.True(rig.UsesIcomLan);
        Assert.False(rig.IsIcomLanConfigured);

        rig.NetworkPassword = "secret";

        Assert.True(rig.IsIcomLanConfigured);
        Assert.True(rig.UsesNetwork);
        Assert.Equal("", rig.SerialPortInUse);
    }

    [Fact]
    public void Ic9700_serial_keeps_its_com_port()
    {
        var rig = new RigSettings { Type = RigType.IcomIc9700, Port = "COM5" };

        Assert.False(rig.UsesIcomLan);
        Assert.Equal("COM5", rig.SerialPortInUse);
    }

    [Fact]
    public void Lan_connection_is_ignored_for_radios_without_network_control()
    {
        var rig = new RigSettings { Type = RigType.IcomIc9100, Connection = RigConnectionKind.Lan, Port = "COM5" };

        Assert.False(rig.UsesIcomLan);
        Assert.Equal("COM5", rig.SerialPortInUse);
    }

    [Theory]
    [InlineData(RigType.IcomIc705)]
    [InlineData(RigType.IcomIc905)]
    [InlineData(RigType.IcomIc7300)]
    public void Lan_leg_needs_host_port_user_and_password(RigType type)
    {
        var endpoint = new RigEndpointSettings
        {
            Type = type,
            Connection = RigConnectionKind.Lan,
            NetworkHost = "192.168.1.61",
            NetworkPort = RigSettings.IcomLanDefaultPort,
            NetworkUser = "oscar"
        };
        Assert.False(endpoint.IsConfigured);

        endpoint.NetworkPassword = "secret";

        Assert.True(endpoint.UsesIcomLan);
        Assert.True(endpoint.IsConfigured);
        Assert.Equal("", endpoint.SerialPortInUse);
    }

    [Fact]
    public void Lan_legs_do_not_hold_com_ports()
    {
        var rig = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.IcomIc705,
                Connection = RigConnectionKind.Lan,
                Port = "COM3",
                NetworkHost = "192.168.1.61",
                NetworkPort = RigSettings.IcomLanDefaultPort
            },
            Uplink = new RigEndpointSettings { Type = RigType.IcomIc7300, Port = "COM3" }
        };

        Assert.False(SerialPortConflictHelper.HasConflict(new RotatorSettings(), rig));
    }

    [Fact]
    public void Ic9700_lan_does_not_clash_with_rotator_port()
    {
        var rig = new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc9700,
            Connection = RigConnectionKind.Lan,
            Port = "COM7",
            NetworkHost = "192.168.1.60"
        };

        Assert.False(SerialPortConflictHelper.HasConflict(new RotatorSettings { Enabled = true, Port = "COM7" }, rig));
    }

    [Fact]
    public void Factory_creates_network_driver_for_ic9700_without_connecting()
    {
        using var driver = RigDriverFactory.Create(new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc9700,
            Connection = RigConnectionKind.Lan,
            NetworkHost = "192.0.2.1",
            NetworkPort = RigSettings.IcomLanDefaultPort,
            NetworkUser = "oscar",
            NetworkPassword = "secret",
            CivAddress = "A2"
        });

        Assert.IsType<IcomIc9700Driver>(driver);
        Assert.False(driver.IsConnected);
    }

    [Fact]
    public void Diagnostics_bundle_hides_rig_network_passwords()
    {
        var settings = new AppSettings
        {
            Rig = new RigSettings
            {
                NetworkPassword = "single-secret",
                Downlink = new RigEndpointSettings { NetworkPassword = "down-secret" },
                Uplink = new RigEndpointSettings { NetworkPassword = "up-secret" }
            }
        };

        var redacted = DiagnosticsBundleBuilder.RedactSettings(settings);

        Assert.DoesNotContain("single-secret", redacted);
        Assert.DoesNotContain("down-secret", redacted);
        Assert.DoesNotContain("up-secret", redacted);
    }
}
