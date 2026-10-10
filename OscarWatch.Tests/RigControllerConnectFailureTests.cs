using System.IO;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class RigControllerConnectFailureTests
{
    [Fact]
    public void Dual_same_port_reports_dual_radio_same_port_status()
    {
        var controller = new RigController();
        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            Downlink = new RigEndpointSettings { Type = RigType.YaesuFt817, Port = "/dev/ttyUSB0" },
            Uplink = new RigEndpointSettings { Type = RigType.YaesuFt818, Port = "/dev/ttyUSB0" }
        };

        controller.PublishContext(settings, null);
        controller.DrainCommandQueueForTests();

        var status = controller.GetStatus();
        Assert.Equal(RigStatusKind.DualRadioSamePort, status.StatusKind);
        Assert.Equal("/dev/ttyUSB0", status.StatusPort);
    }

    [Fact]
    public void Dual_missing_port_reports_serial_port_not_found_with_endpoint()
    {
        var attempts = 0;
        var controller = new RigController(endpointFactory: _ =>
        {
            attempts++;
            throw new UnauthorizedAccessException(
                "Access to the port '/dev/ttyUSB1' is denied.",
                new IOException("No such file or directory : '/dev/ttyUSB1'"));
        });

        var settings = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            Downlink = new RigEndpointSettings { Type = RigType.YaesuFt817, Port = "/dev/ttyUSB1" },
            Uplink = new RigEndpointSettings { Type = RigType.YaesuFt818, Port = "/dev/ttyUSB2" }
        };

        controller.PublishContext(settings, null);
        controller.DrainCommandQueueForTests();
        controller.PublishContext(settings, null);
        controller.DrainCommandQueueForTests();

        var status = controller.GetStatus();
        Assert.Equal(RigStatusKind.SerialPortNotFound, status.StatusKind);
        Assert.Equal("/dev/ttyUSB1", status.StatusPort);
        Assert.Equal(SerialPortConnectErrorHelper.EndpointDownlink, status.StatusDetail);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Single_missing_port_reports_serial_port_not_found()
    {
        var controller = new RigController(_ =>
            throw new UnauthorizedAccessException(
                "Access to the port '/dev/ttyUSB3' is denied.",
                new IOException("No such file or directory : '/dev/ttyUSB3'")));

        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.YaesuFt991,
            Port = "/dev/ttyUSB3"
        };

        controller.PublishContext(settings, null);
        controller.DrainCommandQueueForTests();

        var status = controller.GetStatus();
        Assert.Equal(RigStatusKind.SerialPortNotFound, status.StatusKind);
        Assert.Equal("/dev/ttyUSB3", status.StatusPort);
    }

    [Fact]
    public void Opened_port_that_is_not_the_selected_radio_does_not_send_cat()
    {
        var driver = new RecordingRigDriver { ConfirmIdentity = false };
        var controller = new RigController(_ => driver);
        var settings = new RigSettings
        {
            Enabled = true,
            Type = RigType.IcomIc9700,
            Port = "COM30"
        };

        controller.PublishContext(settings, null);
        controller.DrainCommandQueueForTests();

        var status = controller.GetStatus();
        Assert.Equal(RigStatusKind.IdentityMismatch, status.StatusKind);
        Assert.Equal("COM30", status.StatusPort);
        Assert.Equal("IC-9700", status.StatusDetail);
        Assert.False(status.IsConnected);
        Assert.Equal(0, driver.SetFrequencyCallCount);
        Assert.Equal(0, driver.SetSatelliteModeCallCount);
        Assert.Equal(1, driver.OpenCallCount);

        controller.PublishContext(settings, null);
        controller.DrainCommandQueueForTests();
        Assert.Equal(1, driver.OpenCallCount);
    }
}
