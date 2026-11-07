using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

[Collection(NetworkRadioCollection.Name)]
public sealed class IcomLanTransportTests
{
    private const int Ic9700Civ = 0xA2;

    private static readonly IcomLanTimings FastTimings = new()
    {
        HandshakeTimeoutMs = 300,
        HandshakeAttempts = 2,
        LoginTimeoutMs = 1000,
        StreamGrantTimeoutMs = 2000,
        PingIntervalMs = 200,
        LinkTimeoutMs = 1500,
        ReplyGraceMs = 300
    };

    [Fact]
    public void Passcode_shifts_each_character_by_its_position()
    {
        var encoded = IcomLanPackets.Passcode("ab");

        Assert.Equal(16, encoded.Length);
        Assert.Equal(0x38, encoded[0]);
        Assert.Equal(0x2E, encoded[1]);
        Assert.All(encoded[2..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Passcode_wraps_past_the_printable_range()
    {
        var encoded = IcomLanPackets.Passcode("a~");

        Assert.Equal(0x47, encoded[1]);
    }

    [Fact]
    public void Login_opens_civ_stream_and_reads_frequency()
    {
        using var radio = new FakeIcomLanRadio();
        using var transport = CreateTransport(radio);

        transport.Open();

        Assert.True(transport.IsOpen);
        Assert.Equal("IC-9700", transport.RadioName);
        Assert.Equal(1, radio.Logins);
        Assert.True(SpinWait.SpinUntil(() => radio.CivOpens == 1, TimeSpan.FromSeconds(2)));

        var reply = transport.WriteCommand([0x03], 0);

        Assert.Equal(145_900_000, IcomCivCodec.DecodeFrequencyFromResponse(reply));
        Assert.Contains(radio.CivFrames, f => f.SequenceEqual(new byte[] { 0xFE, 0xFE, 0xA2, 0xE0, 0x03, 0xFD }));
    }

    [Fact]
    public void Wrong_password_reports_login_rejected()
    {
        using var radio = new FakeIcomLanRadio();
        using var transport = new IcomLanTransport(
            "127.0.0.1", radio.ControlPort, radio.User, "wrong", Ic9700Civ, 0, FastTimings);

        var ex = Assert.Throws<IcomLanException>(transport.Open);

        Assert.Equal(IcomLanFailure.LoginRejected, ex.Failure);
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public void Radio_in_use_by_another_client_reports_busy()
    {
        using var radio = new FakeIcomLanRadio { BusyClient = "SHACK-PC" };
        using var transport = CreateTransport(radio);

        var ex = Assert.Throws<IcomLanException>(transport.Open);

        Assert.Equal(IcomLanFailure.Busy, ex.Failure);
        Assert.Equal("SHACK-PC", ex.OtherClient);
    }

    [Fact]
    public void Silent_address_reports_no_reply()
    {
        using var silent = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        silent.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)silent.LocalEndPoint!).Port;
        using var transport = new IcomLanTransport("127.0.0.1", port, "oscar", "secret", Ic9700Civ, 0, FastTimings);

        var ex = Assert.Throws<IcomLanException>(transport.Open);

        Assert.Equal(IcomLanFailure.NoReply, ex.Failure);
    }

    [Fact]
    public void Silent_radio_drops_the_link_and_reopen_logs_in_again()
    {
        using var radio = new FakeIcomLanRadio();
        using var transport = CreateTransport(radio);
        transport.Open();

        radio.Silent = true;
        Assert.True(SpinWait.SpinUntil(() => !transport.IsOpen, TimeSpan.FromSeconds(5)));
        Assert.Empty(transport.WriteCommand([0x03], 0));

        radio.Silent = false;
        transport.Open();

        Assert.True(transport.IsOpen);
        Assert.Equal(2, radio.Logins);
        Assert.Equal(145_900_000, IcomCivCodec.DecodeFrequencyFromResponse(transport.WriteCommand([0x03], 0)));
    }

    [Fact]
    public void Radio_disconnect_closes_the_session()
    {
        using var radio = new FakeIcomLanRadio();
        using var transport = CreateTransport(radio);
        transport.Open();

        radio.SendControlDisconnect();

        Assert.True(SpinWait.SpinUntil(() => !transport.IsOpen, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Dispose_sends_civ_close_token_remove_and_disconnect()
    {
        using var radio = new FakeIcomLanRadio();
        var transport = CreateTransport(radio);
        transport.Open();

        transport.Dispose();

        Assert.True(SpinWait.SpinUntil(
            () => radio.CivCloses == 1 && radio.TokenRemoves == 1 && radio.ControlDisconnects >= 1,
            TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Ic9700_driver_confirms_identity_over_the_network()
    {
        using var radio = new FakeIcomLanRadio { FrequencyHz = 435_800_000 };
        using var driver = new IcomIc9700Driver(
            () => CreateTransport(radio), $"127.0.0.1:{radio.ControlPort}", "A2", catDelayMs: 15);

        driver.Open();

        Assert.True(driver.IsConnected);
        Assert.True(driver.TryConfirmIdentity());
        Assert.Equal(435_800_000, driver.ReadFrequencyHz(RigVfo.Main));
    }

    [Fact]
    public void Network_driver_open_reports_connection_errors()
    {
        using var radio = new FakeIcomLanRadio();
        using var driver = new IcomIc705Driver(
            () => new IcomLanTransport("127.0.0.1", radio.ControlPort, radio.User, "wrong", 0xA4, 0, FastTimings),
            $"127.0.0.1:{radio.ControlPort}",
            "A4");

        var ex = Assert.Throws<IcomLanException>(driver.Open);

        Assert.Equal(IcomLanFailure.LoginRejected, ex.Failure);
        Assert.False(driver.IsConnected);
    }

    [Fact]
    public void Factory_builds_network_driver_for_lan_leg()
    {
        var endpoint = new RigEndpointSettings
        {
            Type = RigType.IcomIc7300,
            Connection = RigConnectionKind.Lan,
            NetworkHost = "192.168.1.60",
            NetworkPort = 50001,
            NetworkUser = "oscar",
            NetworkPassword = "secret",
            CivAddress = "94"
        };

        using var driver = RigDriverFactory.Create(endpoint);

        Assert.IsType<IcomIc7300Driver>(driver);
        Assert.Equal(RigType.IcomIc7300, driver.RigType);
        Assert.False(driver.IsConnected);
    }

    [Fact]
    public void Reply_frame_skips_broadcasts_and_echoes()
    {
        var buffer = new List<byte>
        {
            0xFE, 0xFE, 0x00, 0xA2, 0x00, 0x00, 0x00, 0x90, 0x45, 0x01, 0xFD,
            0xFE, 0xFE, 0xA2, 0xE0, 0x03, 0xFD,
            0xFE, 0xFE, 0xE0, 0xA2, 0x03, 0x00, 0x00, 0x90, 0x45, 0x01, 0xFD,
            0xFE, 0xFE, 0xE0
        };

        Assert.True(IcomLanTransport.TryTakeReplyFrame(buffer, 0xA2, out var frame));

        Assert.Equal(145_900_000, IcomCivCodec.DecodeFrequencyFromResponse(frame));
        Assert.Equal(new byte[] { 0xFE, 0xFE, 0xE0 }, buffer);
    }

    [Fact]
    public void Reply_frame_waits_for_a_complete_frame()
    {
        var buffer = new List<byte> { 0xFE, 0xFE, 0xE0, 0xA2, 0x03, 0x00 };

        Assert.False(IcomLanTransport.TryTakeReplyFrame(buffer, 0xA2, out _));
        Assert.Equal(6, buffer.Count);
    }

    [Fact]
    public void Build_frame_uses_the_network_controller_address()
    {
        Assert.Equal(
            new byte[] { 0xFE, 0xFE, 0xA2, 0xE0, 0x03, 0xFD },
            IcomLanTransport.BuildFrame(0xA2, [0x03]));
    }

    private static IcomLanTransport CreateTransport(FakeIcomLanRadio radio) =>
        new("127.0.0.1", radio.ControlPort, radio.User, radio.Password, Ic9700Civ, 0, FastTimings);

    /// <summary>Loopback stand-in for an Icom radio's network control and CI-V ports.</summary>
    private sealed class FakeIcomLanRadio : IDisposable
    {
        private const uint RadioSid = 0x0A0B0C0D;

        private readonly Endpoint _control;
        private readonly Endpoint _civ;
        private readonly byte[] _authId = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60];
        private readonly byte[] _replyId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        private volatile bool _disposed;
        private int _logins;
        private int _tokenRemoves;
        private int _controlDisconnects;
        private int _civOpens;
        private int _civCloses;

        public FakeIcomLanRadio()
        {
            _control = new Endpoint();
            _civ = new Endpoint();
            new Thread(() => Serve(_control, HandleControl)) { IsBackground = true }.Start();
            new Thread(() => Serve(_civ, HandleCiv)) { IsBackground = true }.Start();
        }

        public int ControlPort => _control.Port;
        public string User { get; init; } = "oscar";
        public string Password { get; init; } = "secret";
        public string? BusyClient { get; init; }
        public long FrequencyHz { get; init; } = 145_900_000;
        public volatile bool Silent;
        public ConcurrentQueue<byte[]> CivFrames { get; } = new();
        public int Logins => Volatile.Read(ref _logins);
        public int TokenRemoves => Volatile.Read(ref _tokenRemoves);
        public int ControlDisconnects => Volatile.Read(ref _controlDisconnects);
        public int CivOpens => Volatile.Read(ref _civOpens);
        public int CivCloses => Volatile.Read(ref _civCloses);

        public void SendControlDisconnect() =>
            _control.Send(IcomLanPackets.Control(IcomLanPackets.TypeDisconnect, 0, RadioSid, _control.ClientSid));

        public void Dispose()
        {
            _disposed = true;
            _control.Socket.Dispose();
            _civ.Socket.Dispose();
        }

        private void Serve(Endpoint endpoint, Action<Endpoint, byte[]> handleData)
        {
            var buffer = new byte[2048];
            while (!_disposed)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int read;
                try
                {
                    read = endpoint.Socket.ReceiveFrom(buffer, ref from);
                }
                catch (SocketException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (Silent || read < IcomLanPackets.HeaderLength)
                    continue;

                var p = buffer.AsSpan(0, read).ToArray();
                if (read == IcomLanPackets.HeaderLength)
                {
                    HandleControlPacket(endpoint, p, from);
                    continue;
                }

                if (read == IcomLanPackets.PingLength && IcomLanPackets.IsType(p, IcomLanPackets.TypePing))
                {
                    if (p[0x10] == 0x00)
                    {
                        endpoint.Send(IcomLanPackets.Ping(
                            IcomLanPackets.Seq(p), RadioSid, endpoint.ClientSid, reply: true, p.AsSpan(0x11, 4)));
                    }

                    continue;
                }

                if (IcomLanPackets.IsType(p, IcomLanPackets.TypeData))
                    handleData(endpoint, p);
            }
        }

        private void HandleControlPacket(Endpoint endpoint, byte[] p, EndPoint from)
        {
            switch (p[4])
            {
                case IcomLanPackets.TypeAreYouThere:
                    endpoint.Client = from;
                    endpoint.ClientSid = IcomLanPackets.SenderId(p);
                    endpoint.Send(IcomLanPackets.Control(IcomLanPackets.TypeIAmHere, 0, RadioSid, endpoint.ClientSid));
                    break;
                case IcomLanPackets.TypeAreYouReady:
                    endpoint.Send(IcomLanPackets.Control(IcomLanPackets.TypeAreYouReady, 1, RadioSid, endpoint.ClientSid));
                    break;
                case IcomLanPackets.TypeDisconnect when endpoint == _control:
                    Interlocked.Increment(ref _controlDisconnects);
                    break;
            }
        }

        private void HandleControl(Endpoint endpoint, byte[] p)
        {
            switch (p.Length)
            {
                case IcomLanPackets.LoginLength:
                {
                    Interlocked.Increment(ref _logins);
                    var reply = NewData(endpoint, IcomLanPackets.LoginReplyLength);
                    var ok = p.AsSpan(0x40, 16).SequenceEqual(IcomLanPackets.Passcode(User))
                        && p.AsSpan(0x50, 16).SequenceEqual(IcomLanPackets.Passcode(Password));
                    if (ok)
                        _authId.CopyTo(reply, 0x1A);
                    else
                        reply[0x30] = reply[0x31] = reply[0x32] = reply[0x33] = 0xFF;
                    endpoint.Send(reply);
                    break;
                }
                case IcomLanPackets.TokenLength when p[0x15] == IcomLanPackets.TokenRenew:
                {
                    var token = NewData(endpoint, IcomLanPackets.TokenLength);
                    token[0x15] = IcomLanPackets.TokenRenew;
                    endpoint.Send(token);

                    var caps = NewData(endpoint, IcomLanPackets.CapabilitiesLength);
                    _replyId.CopyTo(caps, 0x42);
                    Encoding.ASCII.GetBytes("IC-9700").CopyTo(caps, 0x52);
                    endpoint.Send(caps);

                    var status = NewData(endpoint, IcomLanPackets.StatusLength);
                    BinaryPrimitives.WriteUInt16BigEndian(status.AsSpan(0x42), (ushort)_civ.Port);
                    endpoint.Send(status);

                    if (BusyClient is not null)
                        endpoint.Send(ConnInfo(endpoint, BusyClient));
                    break;
                }
                case IcomLanPackets.TokenLength when p[0x15] == IcomLanPackets.TokenRemove:
                    Interlocked.Increment(ref _tokenRemoves);
                    break;
                case IcomLanPackets.ConnInfoLength:
                    endpoint.Send(ConnInfo(endpoint, IcomLanTransport.ClientName));
                    break;
            }
        }

        private void HandleCiv(Endpoint endpoint, byte[] p)
        {
            if (p.Length == 0x16 && p[0x10] == IcomLanPackets.CivOpenCloseMarker)
            {
                if (p[0x15] == 0x05)
                    Interlocked.Increment(ref _civOpens);
                else
                    Interlocked.Increment(ref _civCloses);
                return;
            }

            if (!IcomLanPackets.TryGetCivPayload(p, out var span))
                return;

            var frame = span.ToArray();
            CivFrames.Enqueue(frame);
            if (frame.Length < 6 || frame[3] != IcomLanPackets.ControllerAddress)
                return;

            byte[] reply = frame[4] == 0x03
                ? [0xFE, 0xFE, 0xE0, frame[2], 0x03, .. IcomCivCodec.EncodeSetFrequencyHz(FrequencyHz)[1..], 0xFD]
                : [0xFE, 0xFE, 0xE0, frame[2], 0xFB, 0xFD];
            var packet = IcomLanPackets.CivData(RadioSid, endpoint.ClientSid, 0, reply);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), endpoint.NextSeq());
            endpoint.Send(packet);
        }

        private byte[] ConnInfo(Endpoint endpoint, string client)
        {
            var p = NewData(endpoint, IcomLanPackets.ConnInfoLength);
            _authId.CopyTo(p, 0x1A);
            p[0x60] = 0x01;
            Encoding.ASCII.GetBytes(client).CopyTo(p, 0x64);
            return p;
        }

        private static byte[] NewData(Endpoint endpoint, int length)
        {
            var p = new byte[length];
            IcomLanPackets.WriteHeader(p, IcomLanPackets.TypeData, endpoint.NextSeq(), RadioSid, endpoint.ClientSid);
            return p;
        }

        private sealed class Endpoint
        {
            private int _seq;

            public Endpoint()
            {
                Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                {
                    ReceiveTimeout = 200
                };
                if (OperatingSystem.IsWindows())
                    Socket.IOControl(-1744830452, [0, 0, 0, 0], null);
                Socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                Port = ((IPEndPoint)Socket.LocalEndPoint!).Port;
            }

            public Socket Socket { get; }
            public int Port { get; }
            public volatile EndPoint? Client;
            public uint ClientSid;

            public ushort NextSeq() => (ushort)Interlocked.Increment(ref _seq);

            public void Send(byte[] packet)
            {
                var client = Client;
                if (client is null)
                    return;
                try
                {
                    Socket.SendTo(packet, client);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                }
            }
        }
    }
}
