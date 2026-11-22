using OscarWatch.Core.Models;

namespace OscarWatch.Tests;

public sealed class RotatorSettingsTests
{
    [Fact]
    public void UsesNetworkEndpoint_true_for_urc()
    {
        var settings = new RotatorSettings
        {
            Type = RotatorType.UrcTcp,
            TransportKind = RotatorTransportKind.Serial
        };
        Assert.True(settings.UsesNetworkEndpoint);
        Assert.False(settings.UsesSerialPort);
    }

    [Fact]
    public void UsesNetworkEndpoint_true_for_spid_md01_even_when_transport_serial()
    {
        var settings = new RotatorSettings
        {
            Type = RotatorType.SpidMd01,
            TransportKind = RotatorTransportKind.Serial,
            NetworkHost = "192.168.0.10",
            NetworkPort = 23,
            Port = "COM3"
        };
        Assert.True(settings.UsesNetworkEndpoint);
        Assert.False(settings.UsesSerialPort);
        Assert.True(settings.HasConfiguredEndpoint);
    }

    [Fact]
    public void HasConfiguredEndpoint_requires_host_for_spid_md01()
    {
        var settings = new RotatorSettings
        {
            Type = RotatorType.SpidMd01,
            NetworkHost = "",
            NetworkPort = 23,
            Port = "COM3"
        };
        Assert.False(settings.HasConfiguredEndpoint);

        settings.NetworkHost = "192.168.0.10";
        Assert.True(settings.HasConfiguredEndpoint);
    }

    [Fact]
    public void UsesNetworkEndpoint_true_for_tcp_serial_transport()
    {
        var settings = new RotatorSettings
        {
            Type = RotatorType.YaesuGs232,
            TransportKind = RotatorTransportKind.Tcp,
            NetworkHost = "192.168.1.20",
            NetworkPort = 4001
        };
        Assert.True(settings.UsesNetworkEndpoint);
        Assert.False(settings.UsesSerialPort);
        Assert.True(settings.HasConfiguredEndpoint);
    }

    [Fact]
    public void HasConfiguredEndpoint_requires_host_for_tcp_serial()
    {
        var settings = new RotatorSettings
        {
            Type = RotatorType.EasyComm,
            TransportKind = RotatorTransportKind.Tcp,
            NetworkHost = "",
            NetworkPort = 4001,
            Port = "COM3"
        };
        Assert.False(settings.HasConfiguredEndpoint);

        settings.NetworkHost = "127.0.0.1";
        Assert.True(settings.HasConfiguredEndpoint);
    }

    [Fact]
    public void UsesSerialPort_when_serial_transport()
    {
        var settings = new RotatorSettings
        {
            Type = RotatorType.Spid,
            TransportKind = RotatorTransportKind.Serial,
            Port = "COM5"
        };
        Assert.True(settings.UsesSerialPort);
        Assert.False(settings.UsesNetworkEndpoint);
        Assert.True(settings.HasConfiguredEndpoint);
    }

    [Fact]
    public void GreenHeron_requires_both_ports()
    {
        var settings = new RotatorSettings
        {
            Type = RotatorType.GreenHeronRt21,
            Port = "COM3",
            ElevationPort = ""
        };
        Assert.True(settings.UsesDualSerialPorts);
        Assert.True(settings.UsesSerialPort);
        Assert.False(settings.UsesNetworkEndpoint);
        Assert.False(settings.HasConfiguredEndpoint);

        settings.ElevationPort = "COM4";
        Assert.True(settings.HasConfiguredEndpoint);
        Assert.Equal(new[] { "COM3", "COM4" }, settings.GetConfiguredSerialPorts());
    }

    [Fact]
    public void ClampParkElevation_allows_minus_10_for_spid_only()
    {
        Assert.Equal(-10, RotatorSettings.ClampParkElevationDeg(
            RotatorType.Spid, RotatorElevationRange.Deg180, -10));
        Assert.Equal(-10, RotatorSettings.ClampParkElevationDeg(
            RotatorType.SpidMd01, RotatorElevationRange.Deg90, -40));
        Assert.Equal(90, RotatorSettings.ClampParkElevationDeg(
            RotatorType.Spid, RotatorElevationRange.Deg90, 120));
        Assert.Equal(0, RotatorSettings.ClampParkElevationDeg(
            RotatorType.YaesuGs232, RotatorElevationRange.Deg180, -10));
        Assert.Equal(0, RotatorSettings.ClampParkElevationDeg(
            RotatorType.EasyComm, RotatorElevationRange.Deg180, -10));
    }

    [Fact]
    public void GreenHeron_ignores_tcp_transport_kind()
    {
        var settings = new RotatorSettings
        {
            Type = RotatorType.GreenHeronRt21,
            TransportKind = RotatorTransportKind.Tcp,
            Port = "COM3",
            ElevationPort = "COM4",
            NetworkHost = "127.0.0.1",
            NetworkPort = 4001
        };
        Assert.False(settings.UsesNetworkEndpoint);
        Assert.True(settings.UsesSerialPort);
        Assert.True(settings.HasConfiguredEndpoint);
    }
}
