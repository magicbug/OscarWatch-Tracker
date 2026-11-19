using System.Net.Sockets;
using System.Text;
using OscarWatch.Core.Models;
using OscarWatch.Core.Wsjtx;
using OscarWatch.Wsjtx;

namespace OscarWatch.Tests;

public class WsjtxUdpMessageTests
{
    private static readonly DateTime QsoTime = new(2000, 1, 1, 18, 42, 3, 250, DateTimeKind.Utc);

    [Fact]
    public void Julian_day_matches_qt_for_the_year_2000()
    {
        Assert.Equal(2451545, WsjtxUdpMessage.JulianDay(2000, 1, 1));
    }

    [Fact]
    public void Heartbeat_uses_the_wsjt_header_and_schema_3()
    {
        var frame = Read(WsjtxUdpMessage.Heartbeat("1.2.3", "r9"));

        Assert.Equal(WsjtxUdpMessage.Magic, frame.Magic);
        Assert.Equal(WsjtxUdpMessage.Schema, frame.Schema);
        Assert.Equal(WsjtxUdpMessage.HeartbeatType, frame.Type);
        Assert.Equal(WsjtxUdpMessage.ClientId, frame.Id);
        Assert.Equal(WsjtxUdpMessage.Schema, frame.ReadUInt32());
        Assert.Equal("1.2.3", frame.ReadString());
        Assert.Equal("r9", frame.ReadString());
        Assert.Equal(frame.Length, frame.Offset);
    }

    [Fact]
    public void Qso_logged_carries_the_contact_time_call_and_frequency()
    {
        var frame = Read(WsjtxUdpMessage.QsoLogged(SampleLogbook(), SampleQso()));

        Assert.Equal(WsjtxUdpMessage.QsoLoggedType, frame.Type);
        Assert.Equal(WsjtxUdpMessage.ClientId, frame.Id);

        var off = frame.ReadDateTime();
        Assert.Equal(2451545, off.JulianDay);
        Assert.Equal((uint)((18 * 3600 + 42 * 60 + 3) * 1000 + 250), off.Milliseconds);
        Assert.Equal(1, off.TimeSpec);

        Assert.Equal("K2MO", frame.ReadString());
        Assert.Equal("FN31", frame.ReadString());
        Assert.Equal(145_950_000UL, frame.ReadUInt64());
        Assert.Equal("FT4", frame.ReadString());
        Assert.Equal("-12", frame.ReadString());
        Assert.Equal("-14", frame.ReadString());
    }

    [Fact]
    public void Logged_adif_wraps_one_satellite_record()
    {
        var packet = WsjtxUdpMessage.LoggedAdif(SampleLogbook(), SampleQso());
        var frame = Read(packet);

        Assert.Equal(WsjtxUdpMessage.LoggedAdifType, frame.Type);
        Assert.Equal(new byte[] { 0xAD, 0xBC, 0xCB, 0xDA }, packet.Take(4));

        var adif = frame.ReadString();
        Assert.Contains("<PROGRAMID:10>OscarWatch", adif, StringComparison.Ordinal);
        Assert.Contains("<EOH>", adif, StringComparison.Ordinal);
        Assert.Contains("<CALL:4>K2MO", adif, StringComparison.Ordinal);
        Assert.Contains("<SAT_NAME:5>RS-44", adif, StringComparison.Ordinal);
        Assert.Contains("<PROP_MODE:3>SAT", adif, StringComparison.Ordinal);
        Assert.Contains("<EOR>", adif, StringComparison.Ordinal);
    }

    [Fact]
    public void Close_is_only_the_header()
    {
        var frame = Read(WsjtxUdpMessage.Close());
        Assert.Equal(WsjtxUdpMessage.CloseType, frame.Type);
        Assert.Equal(WsjtxUdpMessage.ClientId, frame.Id);
        Assert.Equal(frame.Length, frame.Offset);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2237, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    public void Port_range_is_1_to_65535(int port, bool valid)
    {
        Assert.Equal(valid, WsjtxUdpSettings.IsValidPort(port));
    }

    private static QsoLogbook SampleLogbook() => new()
    {
        MyCallsign = "mm9sql",
        MyGridSquare = "io86"
    };

    private static QsoRecord SampleQso() => new()
    {
        Call = "k2mo",
        GridSquare = "fn31",
        Mode = "FT4",
        RstSent = "-12",
        RstRcvd = "-14",
        SatName = "RS-44",
        PropMode = "SAT",
        FreqHz = 145_950_000,
        QsoUtc = QsoTime
    };

    private static Frame Read(byte[] packet) => new(packet);

    private sealed class Frame
    {
        private readonly byte[] _data;
        private int _offset;

        public Frame(byte[] data)
        {
            _data = data;
            Magic = ReadUInt32();
            Schema = ReadUInt32();
            Type = ReadUInt32();
            Id = ReadString();
        }

        public uint Magic { get; }
        public uint Schema { get; }
        public uint Type { get; }
        public string Id { get; }
        public int Offset => _offset;
        public int Length => _data.Length;

        public uint ReadUInt32()
        {
            var value = ((uint)_data[_offset] << 24)
                | ((uint)_data[_offset + 1] << 16)
                | ((uint)_data[_offset + 2] << 8)
                | _data[_offset + 3];
            _offset += 4;
            return value;
        }

        public ulong ReadUInt64()
        {
            var high = ReadUInt32();
            var low = ReadUInt32();
            return ((ulong)high << 32) | low;
        }

        public string ReadString()
        {
            var length = ReadUInt32();
            if (length == 0xFFFFFFFFu || length == 0)
                return "";

            var text = Encoding.UTF8.GetString(_data, _offset, (int)length);
            _offset += (int)length;
            return text;
        }

        public (long JulianDay, uint Milliseconds, byte TimeSpec) ReadDateTime()
        {
            var julian = unchecked((long)ReadUInt64());
            var milliseconds = ReadUInt32();
            var timeSpec = _data[_offset];
            _offset++;
            return (julian, milliseconds, timeSpec);
        }
    }
}

public class WsjtxUdpBroadcastServiceTests
{
    [Fact]
    public void Disabled_feed_sends_nothing()
    {
        var sender = new RecordingSender();
        using var service = new WsjtxUdpBroadcastService(sender, work => work());
        service.ApplySettings(new WsjtxUdpSettings { Enabled = false, Host = "127.0.0.1", Port = 2237 });
        service.PublishLoggedQso(SampleQso(), SampleLogbook());

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public void Enabled_feed_sends_a_heartbeat_then_both_log_packets()
    {
        var sender = new RecordingSender();
        using var service = new WsjtxUdpBroadcastService(sender, work => work());
        service.ApplySettings(new WsjtxUdpSettings { Enabled = true, Host = "127.0.0.1", Port = 2237 });

        Assert.Equal(WsjtxUdpMessage.HeartbeatType, TypeOf(sender.Sent[0].Datagram));
        Assert.Equal("127.0.0.1", sender.Sent[0].Host);
        Assert.Equal(2237, sender.Sent[0].Port);

        sender.Sent.Clear();
        service.PublishLoggedQso(SampleQso(), SampleLogbook());

        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal(WsjtxUdpMessage.QsoLoggedType, TypeOf(sender.Sent[0].Datagram));
        Assert.Equal(WsjtxUdpMessage.LoggedAdifType, TypeOf(sender.Sent[1].Datagram));
    }

    [Fact]
    public void A_failed_send_does_not_escape()
    {
        var sender = new RecordingSender { Error = new SocketException((int)SocketError.ConnectionReset) };
        using var service = new WsjtxUdpBroadcastService(sender, work => work());
        service.ApplySettings(new WsjtxUdpSettings { Enabled = true, Host = "127.0.0.1", Port = 2237 });

        var thrown = Record.Exception(() => service.PublishLoggedQso(SampleQso(), SampleLogbook()));

        Assert.Null(thrown);
        Assert.True(sender.Sent.Count >= 2);
    }

    [Fact]
    public void Turning_the_feed_off_sends_close()
    {
        var sender = new RecordingSender();
        using var service = new WsjtxUdpBroadcastService(sender, work => work());
        service.ApplySettings(new WsjtxUdpSettings { Enabled = true, Host = "10.0.0.4", Port = 2237 });
        sender.Sent.Clear();

        service.ApplySettings(new WsjtxUdpSettings { Enabled = false, Host = "10.0.0.4", Port = 2237 });

        Assert.Single(sender.Sent);
        Assert.Equal(WsjtxUdpMessage.CloseType, TypeOf(sender.Sent[0].Datagram));
        Assert.Equal("10.0.0.4", sender.Sent[0].Host);
    }

    private static uint TypeOf(byte[] datagram) =>
        ((uint)datagram[8] << 24) | ((uint)datagram[9] << 16) | ((uint)datagram[10] << 8) | datagram[11];

    private static QsoLogbook SampleLogbook() => new() { MyCallsign = "MM9SQL", MyGridSquare = "IO86" };

    private static QsoRecord SampleQso() => new()
    {
        Call = "K2MO",
        GridSquare = "FN31",
        Mode = "FM",
        SatName = "SO-50",
        FreqHz = 145_900_000,
        QsoUtc = DateTime.UnixEpoch
    };

    private sealed class RecordingSender : IWsjtxUdpSender
    {
        public List<SentDatagram> Sent { get; } = [];
        public Exception? Error { get; set; }

        public void Send(ReadOnlyMemory<byte> datagram, string host, int port)
        {
            Sent.Add(new SentDatagram(datagram.ToArray(), host, port));
            if (Error is not null)
                throw Error;
        }
    }

    private sealed record SentDatagram(byte[] Datagram, string Host, int Port);
}
