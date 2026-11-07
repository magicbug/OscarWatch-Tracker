using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OscarWatch.Rotator;

/// <summary>Datagram I/O used by <see cref="PstRotatorUdpRotator"/> (replaceable in tests).</summary>
internal interface IPstRotatorUdpChannel : IDisposable
{
    void Open();
    void Send(string message);

    /// <summary>Next datagram as text, or null when nothing arrives within <paramref name="timeoutMs"/>.</summary>
    string? Receive(int timeoutMs);

    /// <summary>Drop datagrams already queued (stale replies).</summary>
    void DiscardPending();
}

/// <summary>
/// Sends to PstRotator at host:port and listens for its replies on local UDP port + 1,
/// which is where PstRotator answers <c>AZ?</c> / <c>EL?</c>.
/// </summary>
internal sealed class PstRotatorUdpChannel : IPstRotatorUdpChannel
{
    private readonly string _host;
    private readonly int _port;
    private Socket? _socket;
    private IPEndPoint? _remote;
    private readonly byte[] _buffer = new byte[1024];

    public PstRotatorUdpChannel(string host, int port)
    {
        _host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        _port = port;
    }

    public void Open()
    {
        if (_socket is not null)
            return;

        var address = ResolveIPv4(_host);
        _remote = new IPEndPoint(address, _port);

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, _port + 1));
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new IOException(
                $"PstRotator: cannot listen on UDP port {_port + 1} for replies " +
                "(is another program, e.g. a local PstRotator with UDP enabled, already using it?).",
                ex);
        }

        _socket = socket;
    }

    public void Send(string message)
    {
        var socket = _socket ?? throw new InvalidOperationException("PstRotator UDP channel is not open.");
        socket.SendTo(Encoding.ASCII.GetBytes(message), _remote!);
    }

    public string? Receive(int timeoutMs)
    {
        var socket = _socket;
        if (socket is null || timeoutMs <= 0)
            return null;

        if (!socket.Poll(timeoutMs * 1000, SelectMode.SelectRead))
            return null;

        try
        {
            var read = socket.Receive(_buffer);
            return read > 0 ? Encoding.ASCII.GetString(_buffer, 0, read) : null;
        }
        catch (SocketException)
        {
            // Windows reports ICMP port unreachable from an earlier send as ConnectionReset.
            return null;
        }
    }

    public void DiscardPending()
    {
        var socket = _socket;
        if (socket is null)
            return;

        try
        {
            while (socket.Available > 0)
                socket.Receive(_buffer);
        }
        catch (SocketException)
        {
            // ignore stale ICMP errors
        }
    }

    public void Dispose()
    {
        _socket?.Dispose();
        _socket = null;
    }

    private static IPAddress ResolveIPv4(string host)
    {
        if (IPAddress.TryParse(host, out var literal))
            return literal;

        var addresses = Dns.GetHostAddresses(host);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? throw new IOException($"PstRotator: no IPv4 address for host '{host}'.");
    }
}
