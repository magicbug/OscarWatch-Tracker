using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;

namespace OscarWatch.Tests;

public sealed class DualRadioConfigHelperTests
{
    [Fact]
    public void IsIncomplete_false_when_rig_disabled()
    {
        var rig = DualRig(downPort: "COM3");
        rig.Enabled = false;
        Assert.False(DualRadioConfigHelper.IsIncomplete(rig));
    }

    [Fact]
    public void IsIncomplete_false_when_dual_disabled()
    {
        var rig = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = false,
            Type = RigType.IcomIc910,
            Port = "COM3"
        };
        Assert.False(DualRadioConfigHelper.IsIncomplete(rig));
    }

    [Fact]
    public void IsIncomplete_true_when_only_downlink_configured()
    {
        var rig = DualRig(downPort: "COM3");
        Assert.True(DualRadioConfigHelper.IsIncomplete(rig));
        Assert.Equal(DualRadioConfigHelper.MissingUplinkCode, DualRadioConfigHelper.IncompleteCode(rig));
    }

    [Fact]
    public void IsIncomplete_true_when_only_uplink_configured()
    {
        var rig = DualRig(upPort: "COM4");
        Assert.True(DualRadioConfigHelper.IsIncomplete(rig));
        Assert.Equal(DualRadioConfigHelper.MissingDownlinkCode, DualRadioConfigHelper.IncompleteCode(rig));
    }

    [Fact]
    public void IsIncomplete_true_when_neither_leg_configured()
    {
        var rig = DualRig();
        Assert.True(DualRadioConfigHelper.IsIncomplete(rig));
        Assert.Equal(DualRadioConfigHelper.MissingBothCode, DualRadioConfigHelper.IncompleteCode(rig));
    }

    [Fact]
    public void IsIncomplete_false_when_both_legs_configured()
    {
        var rig = DualRig(downPort: "COM3", upPort: "COM4");
        Assert.False(DualRadioConfigHelper.IsIncomplete(rig));
        Assert.Equal("", DualRadioConfigHelper.IncompleteCode(rig));
    }

    [Fact]
    public void IsIncomplete_false_when_downlink_is_icom_lan_and_uplink_is_dummy()
    {
        var rig = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.IcomIc705,
                Connection = RigConnectionKind.Lan,
                NetworkHost = "192.168.1.21",
                NetworkPort = RigSettings.IcomLanDefaultPort,
                NetworkUser = "user",
                NetworkPassword = "pass"
            },
            Uplink = new RigEndpointSettings { Type = RigType.Dummy }
        };

        Assert.False(DualRadioConfigHelper.IsIncomplete(rig));
    }

    [Fact]
    public void IsIncomplete_false_when_downlink_is_icom_lan_and_uplink_is_serial()
    {
        var rig = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.IcomIc705,
                Connection = RigConnectionKind.Lan,
                NetworkHost = "192.168.1.21",
                NetworkPort = RigSettings.IcomLanDefaultPort,
                NetworkUser = "user",
                NetworkPassword = "pass"
            },
            Uplink = new RigEndpointSettings { Type = RigType.YaesuFt818, Port = "COM4" }
        };

        Assert.False(DualRadioConfigHelper.IsIncomplete(rig));
    }

    [Fact]
    public void IsIncomplete_true_when_downlink_icom_lan_serial_selected_without_com_port()
    {
        var rig = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.IcomIc705,
                Connection = RigConnectionKind.Serial,
                Port = ""
            },
            Uplink = new RigEndpointSettings { Type = RigType.Dummy }
        };

        Assert.True(DualRadioConfigHelper.IsIncomplete(rig));
        Assert.Equal(DualRadioConfigHelper.MissingDownlinkCode, DualRadioConfigHelper.IncompleteCode(rig));
    }

    [Fact]
    public void IsIncomplete_false_when_uplink_is_dummy()
    {
        var rig = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.YaesuFt817,
                Port = "COM3"
            },
            Uplink = new RigEndpointSettings
            {
                Type = RigType.Dummy
            }
        };
        Assert.False(DualRadioConfigHelper.IsIncomplete(rig));
        Assert.Equal("", DualRadioConfigHelper.IncompleteCode(rig));
    }

    private static RigSettings DualRig(string? downPort = null, string? upPort = null) => new()
    {
        Enabled = true,
        DualRadioEnabled = true,
        Downlink = new RigEndpointSettings
        {
            Type = RigType.YaesuFt817,
            Port = downPort ?? ""
        },
        Uplink = new RigEndpointSettings
        {
            Type = RigType.YaesuFt818,
            Port = upPort ?? ""
        }
    };
}
