namespace OscarWatch.Wsjtx;

public interface IWsjtxUdpSender
{
    void Send(ReadOnlyMemory<byte> datagram, string host, int port);
}
