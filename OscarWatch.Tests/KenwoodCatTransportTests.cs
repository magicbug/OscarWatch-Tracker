using System.IO.Ports;
using System.Reflection;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class KenwoodCatTransportTests
{
    [Fact]
    public void Serial_port_asserts_RTS_for_radio_replies()
    {
        using var transport = new KenwoodCatTransport("COM99", 57600);
        var port = GetSerialPort(transport);

        Assert.True(port.RtsEnable);
        Assert.Equal(Handshake.RequestToSend, port.Handshake);
        Assert.False(port.DtrEnable);
        Assert.Equal(StopBits.One, port.StopBits);
    }

    [Fact]
    public void Serial_port_disables_RTS_when_hardware_flow_control_off()
    {
        using var transport = new KenwoodCatTransport("COM99", 57600, hardwareRtsEnabled: false);
        var port = GetSerialPort(transport);

        Assert.False(port.RtsEnable);
        Assert.Equal(Handshake.None, port.Handshake);
        Assert.False(port.DtrEnable);
        Assert.Equal(StopBits.One, port.StopBits);
    }

    [Fact]
    public void Serial_port_accepts_two_stop_bits_for_ts790()
    {
        using var transport = new KenwoodCatTransport("COM99", 4800, hardwareRtsEnabled: true, StopBits.Two);
        var port = GetSerialPort(transport);

        Assert.Equal(4800, port.BaudRate);
        Assert.Equal(StopBits.Two, port.StopBits);
        Assert.True(port.RtsEnable);
    }

    private static SerialPort GetSerialPort(KenwoodCatTransport transport)
    {
        var field = typeof(KenwoodCatTransport).GetField("_port", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<SerialPort>(field!.GetValue(transport));
    }
}
