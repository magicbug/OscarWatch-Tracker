using OscarWatch.Core.Models;
using OscarWatch.Rotator;

namespace OscarWatch.Tests;

public sealed class PstRotatorUdpRotatorTests
{
    [Fact]
    public void SetPosition_sends_azimuth_then_elevation_clamped()
    {
        var channel = new FakeChannel();
        using var rotator = new PstRotatorUdpRotator(channel);
        var settings = new RotatorSettings
        {
            Type = RotatorType.PstRotatorUdp,
            AzimuthRange = RotatorAzimuthRange.Deg360,
            ElevationRange = RotatorElevationRange.Deg90
        };

        rotator.SetPosition(400, 120, settings);

        Assert.Equal(
            ["<PST><AZIMUTH>360</AZIMUTH></PST>", "<PST><ELEVATION>90</ELEVATION></PST>"],
            channel.Sent);
    }

    [Fact]
    public void Stop_sends_stop_command()
    {
        var channel = new FakeChannel();
        using var rotator = new PstRotatorUdpRotator(channel);

        rotator.Stop();

        Assert.Equal(["<PST><STOP>1</STOP></PST>"], channel.Sent);
    }

    [Fact]
    public void GetPosition_queries_both_axes_and_rounds_replies()
    {
        var channel = new FakeChannel();
        channel.Replies.Enqueue("AZ:123.6");
        channel.Replies.Enqueue("EL:44.4");
        using var rotator = new PstRotatorUdpRotator(channel);

        var (az, el) = rotator.GetPosition();

        Assert.Equal(124, az);
        Assert.Equal(44, el);
        Assert.Equal(["<PST>AZ?</PST>", "<PST>EL?</PST>"], channel.Sent);
        Assert.Equal(1, channel.DiscardCount);
    }

    [Fact]
    public void GetPosition_accepts_replies_in_any_order()
    {
        var channel = new FakeChannel();
        channel.Replies.Enqueue("EL:10");
        channel.Replies.Enqueue("AZ:200");
        using var rotator = new PstRotatorUdpRotator(channel);

        Assert.Equal((200, 10), rotator.GetPosition());
    }

    [Fact]
    public void GetPosition_returns_partial_when_one_axis_missing()
    {
        var channel = new FakeChannel();
        channel.Replies.Enqueue("AZ:90");
        using var rotator = new PstRotatorUdpRotator(channel);

        Assert.Equal((90, (int?)null), rotator.GetPosition());
    }

    [Fact]
    public void GetPosition_returns_nulls_without_replies()
    {
        var channel = new FakeChannel();
        using var rotator = new PstRotatorUdpRotator(channel);

        Assert.Equal(((int?)null, (int?)null), rotator.GetPosition());
    }

    [Fact]
    public void Settings_treat_pstrotator_as_network_endpoint()
    {
        var settings = new RotatorSettings
        {
            Type = RotatorType.PstRotatorUdp,
            TransportKind = RotatorTransportKind.Serial,
            NetworkHost = "192.168.0.50",
            NetworkPort = RotatorSettings.DefaultPstRotatorUdpPort,
            Port = "COM3"
        };

        Assert.True(settings.UsesNetworkEndpoint);
        Assert.False(settings.UsesSerialPort);
        Assert.True(settings.HasConfiguredEndpoint);
        Assert.Empty(settings.GetConfiguredSerialPorts());
    }

    [Fact]
    public void Factory_creates_pstrotator_driver()
    {
        var settings = new RotatorSettings
        {
            Type = RotatorType.PstRotatorUdp,
            NetworkHost = "127.0.0.1",
            NetworkPort = RotatorSettings.DefaultPstRotatorUdpPort
        };

        using var driver = RotatorDriverFactory.Create(settings);

        Assert.IsType<PstRotatorUdpRotator>(driver);
    }

    private sealed class FakeChannel : IPstRotatorUdpChannel
    {
        public List<string> Sent { get; } = [];
        public Queue<string> Replies { get; } = new();
        public int DiscardCount { get; private set; }

        public void Open() { }

        public void Send(string message) => Sent.Add(message);

        public string? Receive(int timeoutMs) => Replies.Count > 0 ? Replies.Dequeue() : null;

        public void DiscardPending() => DiscardCount++;

        public void Dispose() { }
    }
}
