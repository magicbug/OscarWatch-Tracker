using System.Net;
using System.Net.Sockets;

namespace OscarWatch.Wsjtx;

/// <summary>Sends one unicast UDP datagram. Prefers IPv4 when a name has both kinds of address.</summary>
public sealed class UdpWsjtxSender : IWsjtxUdpSender
{
    public void Send(ReadOnlyMemory<byte> datagram, string host, int port)
    {
        var address = Resolve(host);
        using var client = new UdpClient(address.AddressFamily);
        client.Send(datagram.Span, new IPEndPoint(address, port));
    }

    private static IPAddress Resolve(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("A server address is required.", nameof(host));

        if (IPAddress.TryParse(host.Trim(), out var literal))
            return literal;

        var addresses = Dns.GetHostAddresses(host);
        var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (v4 is not null)
            return v4;

        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        return addresses[0];
    }
}
