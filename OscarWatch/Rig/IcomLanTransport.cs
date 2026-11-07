using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Serilog;

namespace OscarWatch.Rig;

public enum IcomLanFailure
{
    NoReply,
    LoginRejected,
    Busy,
    Disconnected
}

/// <summary>Icom network control session could not be established.</summary>
public sealed class IcomLanException(IcomLanFailure failure, string message, string? otherClient = null)
    : InvalidOperationException(message)
{
    public IcomLanFailure Failure { get; } = failure;

    /// <summary>Computer name of the remote client already holding the radio, when the radio reported one.</summary>
    public string? OtherClient { get; } = otherClient;
}

internal sealed class IcomLanTimings
{
    public int HandshakeTimeoutMs { get; init; } = 1000;
    public int HandshakeAttempts { get; init; } = 3;
    public int LoginTimeoutMs { get; init; } = 3000;
    public int StreamGrantTimeoutMs { get; init; } = 5000;
    public int PingIntervalMs { get; init; } = 1000;
    public int LinkTimeoutMs { get; init; } = 6000;
    public int ReauthIntervalMs { get; init; } = 60_000;
    public int ReplyGraceMs { get; init; } = 800;
}

/// <summary>
/// CI-V over Icom's network control protocol (RS-BA1), as used by the IC-9700, IC-705, IC-905 and IC-7300MK2.
/// Opens the control stream (login, token, keepalive) and the CI-V stream. The audio stream is never opened.
/// A dropped session leaves <see cref="IsOpen"/> false so <see cref="RigController"/> reconnects.
/// </summary>
internal sealed class IcomLanTransport : IIcomCivTransport
{
    internal const string ClientName = "OscarWatch";

    private static readonly ILogger Log = Serilog.Log.ForContext<IcomLanTransport>();
    private const int TickMs = 100;
    private const int MaxCivBufferBytes = 4096;

    private readonly string _host;
    private readonly int _controlPort;
    private readonly string _user;
    private readonly string _password;
    private readonly int _civAddress;
    private readonly int _defaultPostDelayMs;
    private readonly IcomLanTimings _timings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _rxLock = new();
    private readonly object _authLock = new();
    private readonly List<byte> _civRx = [];
    private readonly HashSet<ushort> _recentCivSeqs = [];
    private readonly Queue<ushort> _recentCivSeqOrder = new();
    private readonly byte[] _authId = new byte[6];
    private UdpStream? _control;
    private UdpStream? _civ;
    private ushort _innerSeq;
    private ushort _civSeq;
    private Timer? _timer;
    private long _lastReauthTicks;
    private int _ticking;
    private volatile bool _open;
    private volatile bool _linkLost;
    private volatile bool _disposed;

    public IcomLanTransport(
        string host,
        int controlPort,
        string user,
        string password,
        int civAddress,
        int catDelayMs = 50,
        IcomLanTimings? timings = null)
    {
        _host = host?.Trim() ?? "";
        _controlPort = controlPort;
        _user = user?.Trim() ?? "";
        _password = password ?? "";
        _civAddress = civAddress;
        _defaultPostDelayMs = Math.Max(catDelayMs, 15);
        _timings = timings ?? new IcomLanTimings();
    }

    public bool IsOpen => _open && !_linkLost && !_disposed;

    /// <summary>Model name the radio reported during login (for example IC-9700).</summary>
    public string RadioName { get; private set; } = "";

    private string Endpoint => $"{_host}:{_controlPort}";

    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _timer ??= new Timer(_ => Tick(), null, TickMs, TickMs);
        _gate.Wait();
        try
        {
            CloseUnlocked();
            try
            {
                ConnectUnlocked();
            }
            catch
            {
                CloseUnlocked();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public byte[] WriteCommand(ReadOnlySpan<byte> body, int postDelayMs = 50)
    {
        if (!IsOpen)
            return [];

        var delayMs = postDelayMs > 0 ? postDelayMs : _defaultPostDelayMs;
        var frame = BuildFrame(_civAddress, body);

        _gate.Wait();
        try
        {
            var civ = _civ;
            if (!IsOpen || civ is null)
                return [];

            lock (_rxLock)
                _civRx.Clear();

            var started = Environment.TickCount64;
            civ.SendTracked(IcomLanPackets.CivData(civ.LocalSid, civ.RemoteSid, _civSeq++, frame));
            var reply = WaitForReply(started + Math.Max(delayMs, 50) + _timings.ReplyGraceMs);

            var remaining = delayMs - (Environment.TickCount64 - started);
            if (remaining > 0)
                Thread.Sleep((int)remaining);
            return reply;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            MarkLinkLost(ex.Message);
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SetHandshakeLine(bool useRts, bool assert)
    {
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer?.Dispose();
        _timer = null;
        _gate.Wait();
        try
        {
            CloseUnlocked();
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    private void ConnectUnlocked()
    {
        var address = ResolveAddress();
        _linkLost = false;
        RadioName = "";
        lock (_authLock)
            _innerSeq = 0;

        var control = new UdpStream("control", new IPEndPoint(address, _controlPort), _timings.PingIntervalMs);
        _control = control;
        if (!control.Handshake(_timings.HandshakeTimeoutMs, _timings.HandshakeAttempts))
        {
            throw new IcomLanException(
                IcomLanFailure.NoReply,
                $"{Endpoint} did not answer. Check the IP address, the control port and that Network Control is on in the radio.");
        }

        var tokenRequest = RandomNumberGenerator.GetBytes(2);
        lock (_authLock)
        {
            control.SendTracked(IcomLanPackets.Login(
                control.LocalSid, control.RemoteSid, _innerSeq++, tokenRequest, _user, _password, ClientName));
        }

        var login = control.Receive(
            p => IcomLanPackets.IsDataPacket(p, IcomLanPackets.LoginReplyLength),
            _timings.LoginTimeoutMs);
        if (login is null)
            throw new IcomLanException(IcomLanFailure.NoReply, $"{Endpoint} did not answer the login.");

        if (HasErrorCode(login))
        {
            throw new IcomLanException(
                IcomLanFailure.LoginRejected,
                $"{Endpoint} rejected the network user name or password.");
        }

        login.AsSpan(0x1A, 6).CopyTo(_authId);
        control.PingEnabled = true;
        SendToken(control, IcomLanPackets.TokenCreate);
        control.IdleEnabled = true;
        SendToken(control, IcomLanPackets.TokenRenew);

        var civPort = WaitForStreamGrant(control);

        control.Handler = OnControlPacket;
        var civ = new UdpStream("CI-V", new IPEndPoint(address, civPort), _timings.PingIntervalMs);
        _civ = civ;
        if (!civ.Handshake(_timings.HandshakeTimeoutMs, _timings.HandshakeAttempts))
        {
            throw new IcomLanException(
                IcomLanFailure.NoReply,
                $"{_host}:{civPort} did not answer on the CI-V port.");
        }

        civ.PingEnabled = true;
        civ.IdleEnabled = true;
        civ.Handler = OnCivPacket;
        _civSeq = 0;
        civ.SendTracked(IcomLanPackets.CivOpenClose(civ.LocalSid, civ.RemoteSid, _civSeq++, open: true));

        _lastReauthTicks = Environment.TickCount64;
        _open = true;
        Log.Information(
            "Icom LAN session open to {Radio} at {Endpoint} (CI-V port {CivPort})",
            string.IsNullOrEmpty(RadioName) ? "radio" : RadioName,
            Endpoint,
            civPort);
    }

    /// <summary>Token exchange and stream request. Returns the CI-V port the radio granted.</summary>
    private int WaitForStreamGrant(UdpStream control)
    {
        var civPort = _controlPort + 1;
        byte[]? replyId = null;
        var authOk = false;
        var requested = false;
        var deadline = Environment.TickCount64 + _timings.StreamGrantTimeoutMs;

        while (true)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                throw new IcomLanException(
                    IcomLanFailure.NoReply,
                    $"{Endpoint} accepted the login but did not open the CI-V stream.");
            }

            var p = control.Receive(match: null, (int)remaining);
            if (p is null)
                continue;

            if (IcomLanPackets.IsDataPacket(p, IcomLanPackets.CapabilitiesLength))
            {
                replyId = p.AsSpan(0x42, 16).ToArray();
                RadioName = IcomLanPackets.ReadAscii(p.AsSpan(0x52, 32));
            }
            else if (IcomLanPackets.IsDataPacket(p, IcomLanPackets.TokenLength))
            {
                if (p[0x15] == IcomLanPackets.TokenRenew)
                    authOk = true;
            }
            else if (IcomLanPackets.IsDataPacket(p, IcomLanPackets.StatusLength))
            {
                if (HasErrorCode(p))
                {
                    throw new IcomLanException(
                        IcomLanFailure.LoginRejected,
                        $"{Endpoint} refused the session. Check the network user name and password, or restart the radio.");
                }

                if (IsDisconnectStatus(p))
                    throw new IcomLanException(IcomLanFailure.Disconnected, $"{Endpoint} closed the session.");

                var port = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(0x42));
                if (port > 0)
                    civPort = port;
            }
            else if (IcomLanPackets.IsDataPacket(p, IcomLanPackets.ConnInfoLength) && p[0x60] == 0x01)
            {
                var client = IcomLanPackets.ReadAscii(p.AsSpan(0x64, 32));
                var ours = client.Length == 0 || string.Equals(client, ClientName, StringComparison.OrdinalIgnoreCase);
                if (!ours)
                {
                    throw new IcomLanException(
                        IcomLanFailure.Busy,
                        $"{Endpoint} is already connected to {client}. Close the other remote control application and try again.",
                        client);
                }

                if (requested)
                {
                    control.AdoptSessionIds(p);
                    if (p.AsSpan(0x1A, 6).IndexOfAnyExcept((byte)0) >= 0)
                        p.AsSpan(0x1A, 6).CopyTo(_authId);
                    return civPort;
                }
            }

            if (!requested && authOk && replyId is not null)
            {
                lock (_authLock)
                {
                    control.SendTracked(IcomLanPackets.ConnInfoRequest(
                        control.LocalSid,
                        control.RemoteSid,
                        _innerSeq++,
                        _authId,
                        replyId,
                        RadioName,
                        _user,
                        civPort,
                        civPort + 1));
                }

                requested = true;
            }
        }
    }

    private void Tick()
    {
        if (_disposed || Interlocked.Exchange(ref _ticking, 1) == 1)
            return;

        try
        {
            var control = _control;
            var civ = _civ;
            var now = Environment.TickCount64;
            control?.Tick(now);
            civ?.Tick(now);
            if (!_open || control is null || civ is null)
                return;

            if (now - _lastReauthTicks >= _timings.ReauthIntervalMs)
            {
                _lastReauthTicks = now;
                SendToken(control, IcomLanPackets.TokenRenew);
            }

            if (control.RemoteDisconnected || civ.RemoteDisconnected)
            {
                MarkLinkLost("the radio closed the session");
                return;
            }

            var lastHeard = Math.Min(control.LastReceiveTicks, civ.LastReceiveTicks);
            if (now - lastHeard > _timings.LinkTimeoutMs)
                MarkLinkLost($"no reply for {_timings.LinkTimeoutMs / 1000.0:0.#} s");
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            if (_open)
                MarkLinkLost(ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private void OnControlPacket(byte[] packet)
    {
        if (IcomLanPackets.IsDataPacket(packet, IcomLanPackets.StatusLength)
            && (HasErrorCode(packet) || IsDisconnectStatus(packet)))
        {
            MarkLinkLost("the radio ended the session");
        }
    }

    private void OnCivPacket(byte[] packet)
    {
        if (!IcomLanPackets.TryGetCivPayload(packet, out var frame))
            return;

        var seq = IcomLanPackets.Seq(packet);
        lock (_rxLock)
        {
            if (!_recentCivSeqs.Add(seq))
                return;

            _recentCivSeqOrder.Enqueue(seq);
            if (_recentCivSeqOrder.Count > 64)
                _recentCivSeqs.Remove(_recentCivSeqOrder.Dequeue());

            foreach (var b in frame)
                _civRx.Add(b);
            if (_civRx.Count > MaxCivBufferBytes)
                _civRx.RemoveRange(0, _civRx.Count - MaxCivBufferBytes);

            Monitor.PulseAll(_rxLock);
        }
    }

    private byte[] WaitForReply(long deadline)
    {
        lock (_rxLock)
        {
            while (true)
            {
                if (TryTakeReplyFrame(_civRx, _civAddress, out var reply))
                    return reply;

                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0 || _linkLost)
                    return [];

                Monitor.Wait(_rxLock, (int)remaining);
            }
        }
    }

    /// <summary>
    /// Take the first complete frame the radio addressed to the controller. Transceive broadcasts and
    /// frames for other addresses are dropped; an incomplete trailing frame is kept.
    /// </summary>
    internal static bool TryTakeReplyFrame(List<byte> buffer, int civAddress, out byte[] frame)
    {
        frame = [];
        var consumed = 0;
        var i = 0;
        while (true)
        {
            var start = -1;
            for (var j = i; j + 1 < buffer.Count; j++)
            {
                if (buffer[j] == 0xFE && buffer[j + 1] == 0xFE)
                {
                    start = j;
                    break;
                }
            }

            if (start < 0)
            {
                consumed = Math.Max(consumed, i);
                break;
            }

            while (start + 2 < buffer.Count && buffer[start + 2] == 0xFE)
                start++;

            var end = buffer.IndexOf(0xFD, start + 2);
            if (end < 0)
            {
                consumed = start;
                break;
            }

            i = end + 1;
            consumed = i;
            if (end - start + 1 >= 6
                && buffer[start + 2] == IcomLanPackets.ControllerAddress
                && buffer[start + 3] == civAddress)
            {
                frame = buffer.GetRange(start, end - start + 1).ToArray();
                buffer.RemoveRange(0, consumed);
                return true;
            }
        }

        buffer.RemoveRange(0, Math.Min(consumed, buffer.Count));
        return false;
    }

    internal static byte[] BuildFrame(int civAddress, ReadOnlySpan<byte> body)
    {
        var frame = new byte[4 + body.Length + 1];
        frame[0] = 0xFE;
        frame[1] = 0xFE;
        frame[2] = (byte)civAddress;
        frame[3] = IcomLanPackets.ControllerAddress;
        body.CopyTo(frame.AsSpan(4));
        frame[^1] = 0xFD;
        return frame;
    }

    private void SendToken(UdpStream control, byte magic)
    {
        lock (_authLock)
            control.SendTracked(IcomLanPackets.Token(control.LocalSid, control.RemoteSid, magic, _innerSeq++, _authId));
    }

    private void MarkLinkLost(string reason)
    {
        if (_linkLost || !_open)
            return;

        _linkLost = true;
        Log.Warning("Icom LAN session to {Endpoint} lost ({Reason}). OscarWatch will reconnect.", Endpoint, reason);
        lock (_rxLock)
            Monitor.PulseAll(_rxLock);
    }

    private void CloseUnlocked()
    {
        _open = false;
        var civ = _civ;
        var control = _control;
        _civ = null;
        _control = null;

        if (civ is not null)
        {
            if (civ.IsReady)
            {
                TrySend(() => civ.SendTracked(
                    IcomLanPackets.CivOpenClose(civ.LocalSid, civ.RemoteSid, _civSeq++, open: false)));
                TrySend(civ.SendDisconnect);
            }

            civ.Dispose();
        }

        if (control is not null)
        {
            if (control.IsReady)
            {
                if (_authId.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
                    TrySend(() => SendToken(control, IcomLanPackets.TokenRemove));
                TrySend(control.SendDisconnect);
            }

            control.Dispose();
        }

        Array.Clear(_authId);
        lock (_rxLock)
        {
            _civRx.Clear();
            _recentCivSeqs.Clear();
            _recentCivSeqOrder.Clear();
            Monitor.PulseAll(_rxLock);
        }
    }

    private void TrySend(Action send)
    {
        try
        {
            send();
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            Log.Debug(ex, "Icom LAN close packet to {Endpoint} failed", Endpoint);
        }
    }

    private IPAddress ResolveAddress()
    {
        if (string.IsNullOrWhiteSpace(_host))
            throw new IcomLanException(IcomLanFailure.NoReply, "No radio address is set.");

        if (IPAddress.TryParse(_host, out var literal))
            return literal;

        try
        {
            var v4 = Dns.GetHostAddresses(_host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (v4 is not null)
                return v4;
        }
        catch (SocketException ex)
        {
            Log.Debug(ex, "Icom LAN host lookup failed for {Host}", _host);
        }

        throw new IcomLanException(
            IcomLanFailure.NoReply,
            $"Could not find {_host} on the network. Check the radio's IP address.");
    }

    private static bool HasErrorCode(byte[] packet) =>
        packet[0x30] == 0xFF && packet[0x31] == 0xFF && packet[0x32] == 0xFF;

    private static bool IsDisconnectStatus(byte[] packet) =>
        packet[0x30] == 0x00 && packet[0x31] == 0x00 && packet[0x32] == 0x00 && packet[0x40] == 0x01;

    /// <summary>One UDP conversation (control or CI-V) with its own session IDs, sequence numbers and retransmit history.</summary>
    private sealed class UdpStream : IDisposable
    {
        private const int SioUdpConnReset = -1744830452;
        private const int HistorySize = 256;

        private readonly string _name;
        private readonly Socket _socket;
        private readonly int _pingIntervalMs;
        private readonly object _sendLock = new();
        private readonly Dictionary<ushort, byte[]> _history = [];
        private readonly Queue<ushort> _historyOrder = new();
        private readonly BlockingCollection<byte[]> _inbox = new();
        private readonly Thread _receiver;
        private ushort _trackedSeq = 1;
        private ushort _pingSeq = 1;
        private ushort _pingInnerSeq = 0x8304;
        private long _lastTrackedTicks;
        private long _nextIdleTicks;
        private long _nextPingTicks;
        private long _lastReceiveTicks = Environment.TickCount64;
        private volatile bool _closed;

        public UdpStream(string name, IPEndPoint remote, int pingIntervalMs)
        {
            _name = name;
            _pingIntervalMs = Math.Max(100, pingIntervalMs);
            _socket = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
            {
                ReceiveTimeout = 250
            };
            if (OperatingSystem.IsWindows())
                _socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            _socket.Bind(new IPEndPoint(
                remote.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
            _socket.Connect(remote);

            var local = (IPEndPoint)_socket.LocalEndPoint!;
            var ip = local.Address.MapToIPv4().GetAddressBytes();
            LocalSid = (uint)((ip[2] << 24) | (ip[3] << 16) | (local.Port & 0xFFFF));

            _receiver = new Thread(ReceiveLoop) { IsBackground = true, Name = $"Icom LAN {name}" };
            _receiver.Start();
        }

        public uint LocalSid { get; private set; }

        public uint RemoteSid { get; private set; }

        public bool IsReady { get; private set; }

        public volatile bool PingEnabled;

        public volatile bool IdleEnabled;

        public volatile bool RemoteDisconnected;

        /// <summary>Data packets after the handshake. Until set, packets queue for <see cref="Receive"/>.</summary>
        public volatile Action<byte[]>? Handler;

        public long LastReceiveTicks => Interlocked.Read(ref _lastReceiveTicks);

        public bool Handshake(int timeoutMs, int attempts)
        {
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                SendControlTwice(IcomLanPackets.TypeAreYouThere, 0);
                var here = Receive(
                    p => p.Length == IcomLanPackets.HeaderLength && IcomLanPackets.IsType(p, IcomLanPackets.TypeIAmHere),
                    timeoutMs);
                if (here is null)
                    continue;

                RemoteSid = IcomLanPackets.SenderId(here);
                SendControlTwice(IcomLanPackets.TypeAreYouReady, 1);
                var ready = Receive(
                    p => p.Length == IcomLanPackets.HeaderLength && IcomLanPackets.IsType(p, IcomLanPackets.TypeAreYouReady),
                    timeoutMs);
                if (ready is null)
                    continue;

                lock (_sendLock)
                    _trackedSeq = 1;
                IsReady = true;
                return true;
            }

            return false;
        }

        /// <summary>Next queued packet that matches, or null on timeout. Non-matching packets are dropped.</summary>
        public byte[]? Receive(Func<byte[], bool>? match, int timeoutMs)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0 || _closed)
                    return null;

                if (!_inbox.TryTake(out var packet, (int)remaining))
                    return null;

                if (match is null || match(packet))
                    return packet;
            }
        }

        /// <summary>The radio restates both session IDs when it grants the streams.</summary>
        public void AdoptSessionIds(byte[] packet)
        {
            RemoteSid = IcomLanPackets.SenderId(packet);
            LocalSid = IcomLanPackets.ReceiverId(packet);
        }

        public void SendTracked(byte[] packet)
        {
            lock (_sendLock)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), _trackedSeq);
                if (_history.Count >= HistorySize && _historyOrder.TryDequeue(out var oldest))
                    _history.Remove(oldest);
                if (_history.TryAdd(_trackedSeq, packet))
                    _historyOrder.Enqueue(_trackedSeq);
                else
                    _history[_trackedSeq] = packet;

                _socket.Send(packet);
                _trackedSeq++;
                if (!IcomLanPackets.IsIdle(packet))
                {
                    var now = Environment.TickCount64;
                    _lastTrackedTicks = now;
                    _nextIdleTicks = now + 100;
                }
            }
        }

        public void SendDisconnect() => SendControlTwice(IcomLanPackets.TypeDisconnect, 0);

        public void Tick(long now)
        {
            if (IdleEnabled && now >= _nextIdleTicks)
            {
                SendTracked(IcomLanPackets.Control(IcomLanPackets.TypeData, 0, LocalSid, RemoteSid));
                _nextIdleTicks = now + (now - _lastTrackedTicks >= 1000 ? 1000 : 100);
            }

            if (PingEnabled && now >= _nextPingTicks)
            {
                _nextPingTicks = now + _pingIntervalMs;
                Span<byte> replyId = stackalloc byte[4];
                replyId[0] = (byte)Random.Shared.Next(256);
                replyId[1] = (byte)_pingInnerSeq;
                replyId[2] = (byte)(_pingInnerSeq >> 8);
                replyId[3] = 0x06;
                _pingInnerSeq++;
                SendRaw(IcomLanPackets.Ping(_pingSeq++, LocalSid, RemoteSid, reply: false, replyId));
            }
        }

        public void Dispose()
        {
            if (_closed)
                return;

            _closed = true;
            try
            {
                _socket.Dispose();
            }
            catch
            {
                // ignore
            }

            if (Thread.CurrentThread != _receiver)
                _receiver.Join(500);
            _inbox.Dispose();
        }

        private void SendControlTwice(byte type, ushort seq)
        {
            var packet = IcomLanPackets.Control(type, seq, LocalSid, RemoteSid);
            SendRaw(packet);
            SendRaw(packet);
        }

        private void SendRaw(byte[] packet)
        {
            lock (_sendLock)
                _socket.Send(packet);
        }

        private void ReceiveLoop()
        {
            var buffer = new byte[2048];
            while (!_closed)
            {
                int read;
                try
                {
                    read = _socket.Receive(buffer);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut
                    or SocketError.WouldBlock or SocketError.ConnectionReset or SocketError.ConnectionRefused)
                {
                    continue;
                }
                catch (SocketException ex)
                {
                    if (_closed)
                        break;
                    Log.Debug(ex, "Icom LAN {Stream} receive failed", _name);
                    Thread.Sleep(50);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (read <= 0)
                    continue;

                Interlocked.Exchange(ref _lastReceiveTicks, Environment.TickCount64);
                try
                {
                    Dispatch(buffer.AsSpan(0, read).ToArray());
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    if (_closed)
                        break;
                    Log.Debug(ex, "Icom LAN {Stream} packet handling failed", _name);
                }
            }
        }

        private void Dispatch(byte[] packet)
        {
            if (packet.Length == IcomLanPackets.PingLength && IcomLanPackets.IsType(packet, IcomLanPackets.TypePing))
            {
                if (packet[0x10] == 0x00 && PingEnabled)
                {
                    SendRaw(IcomLanPackets.Ping(
                        IcomLanPackets.Seq(packet), LocalSid, RemoteSid, reply: true, packet.AsSpan(0x11, 4)));
                }

                return;
            }

            if (IcomLanPackets.IsType(packet, IcomLanPackets.TypeRetransmit))
            {
                HandleRetransmit(packet);
                return;
            }

            if (packet.Length == IcomLanPackets.HeaderLength && IcomLanPackets.IsType(packet, IcomLanPackets.TypeDisconnect))
            {
                if (IsReady)
                    RemoteDisconnected = true;
                return;
            }

            if (IcomLanPackets.IsIdle(packet))
                return;

            var handler = Handler;
            if (handler is not null)
                handler(packet);
            else
                _inbox.Add(packet);
        }

        private void HandleRetransmit(byte[] packet)
        {
            if (packet.Length == IcomLanPackets.HeaderLength)
            {
                Resend(IcomLanPackets.Seq(packet));
                return;
            }

            for (var offset = IcomLanPackets.HeaderLength; offset + 4 <= packet.Length; offset += 4)
            {
                var start = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(offset));
                var end = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(offset + 2));
                for (var count = 0; count < 64; count++)
                {
                    Resend(start);
                    if (start == end)
                        break;
                    start++;
                }
            }
        }

        private void Resend(ushort seq)
        {
            byte[]? packet;
            lock (_sendLock)
                _history.TryGetValue(seq, out packet);

            packet ??= IcomLanPackets.Control(IcomLanPackets.TypeData, seq, LocalSid, RemoteSid);
            SendRaw(packet);
            SendRaw(packet);
        }
    }
}
