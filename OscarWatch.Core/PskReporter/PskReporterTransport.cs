using System.Net.Sockets;

namespace OscarWatch.Core.PskReporter;

/// <summary>Sends finished datagrams. Tests swap in an in-memory sink.</summary>
public interface IPskReporterTransport : IDisposable
{
    void Send(byte[] datagram);
}

/// <summary>One UDP socket for the whole session, so PSK Reporter sees a stable source port.</summary>
public sealed class UdpPskReporterTransport : IPskReporterTransport
{
    private readonly UdpClient _client;
    private readonly string _host;
    private readonly int _port;

    public UdpPskReporterTransport(string host, int port)
    {
        _host = host;
        _port = port;
        _client = new UdpClient(0);
    }

    public void Send(byte[] datagram) => _client.Send(datagram, datagram.Length, _host, _port);

    public void Dispose() => _client.Dispose();
}
