using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

[Collection(NetworkRadioCollection.Name)]
public sealed class KenwoodLanTransportTests
{
    [Fact]
    public void Ts890s_logs_in_with_utf8_and_confirms_identity()
    {
        using var radio = new FakeKenwoodLanRadio(KenwoodHfCatCodec.Ts890s, "024");
        using var driver = new KenwoodHfDriver(
            RigType.KenwoodTs890s,
            new KenwoodLanTransport(KenwoodHfCatCodec.Ts890s, "127.0.0.1", radio.Port, "admin", "secret12"),
            catDelayMs: 0);

        driver.Open();

        Assert.True(driver.TryConfirmIdentity());
        Assert.Equal(["##CN;", "##ID00508adminsecret12;", "AI0;", "ID;"], radio.Received.Take(4));
    }

    [Fact]
    public void Ts990s_logs_in_with_utf16()
    {
        using var radio = new FakeKenwoodLanRadio(KenwoodHfCatCodec.Ts990s, "022");
        using var transport = new KenwoodLanTransport(KenwoodHfCatCodec.Ts990s, "127.0.0.1", radio.Port, "admin", "secret12");

        transport.Open();

        Assert.Equal("ID022;", transport.Transact("ID;"));
        Assert.Equal(["##CN;", "##ID58adminsecret12;", "ID;"], radio.Received.Take(3));
    }

    [Fact]
    public void Busy_radio_reports_connection_denied()
    {
        using var radio = new FakeKenwoodLanRadio(KenwoodHfCatCodec.Ts890s, "024") { AcceptConnection = false };
        using var transport = new KenwoodLanTransport(KenwoodHfCatCodec.Ts890s, "127.0.0.1", radio.Port, "admin", "secret12");

        var ex = Assert.Throws<KenwoodLanException>(transport.Open);
        Assert.Equal(KenwoodLanFailure.ConnectionDenied, ex.Failure);
    }

    [Fact]
    public void Wrong_password_reports_login_rejected()
    {
        using var radio = new FakeKenwoodLanRadio(KenwoodHfCatCodec.Ts890s, "024") { AcceptLogin = false };
        using var transport = new KenwoodLanTransport(KenwoodHfCatCodec.Ts890s, "127.0.0.1", radio.Port, "admin", "wrong");

        var ex = Assert.Throws<KenwoodLanException>(transport.Open);
        Assert.Equal(KenwoodLanFailure.LoginRejected, ex.Failure);
    }

    [Fact]
    public void Idle_session_sends_keepalive_before_the_radio_timeout()
    {
        using var radio = new FakeKenwoodLanRadio(KenwoodHfCatCodec.Ts890s, "024");
        using var transport = new KenwoodLanTransport(
            KenwoodHfCatCodec.Ts890s, "127.0.0.1", radio.Port, "admin", "secret12", keepAliveIdleMs: 500);

        transport.Open();

        Assert.True(SpinWait.SpinUntil(() => radio.Received.Contains("ID;"), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Dropped_session_reconnects_and_logs_in_again()
    {
        using var radio = new FakeKenwoodLanRadio(KenwoodHfCatCodec.Ts890s, "024");
        using var transport = new KenwoodLanTransport(KenwoodHfCatCodec.Ts890s, "127.0.0.1", radio.Port, "admin", "secret12");
        transport.Open();
        Assert.Equal("ID024;", transport.Transact("ID;"));

        radio.DropClient();
        Assert.True(SpinWait.SpinUntil(() => radio.ActiveClients == 0, TimeSpan.FromSeconds(2)));

        Assert.Equal("ID024;", transport.Transact("ID;"));
        Assert.Equal(2, radio.Connections);
        Assert.Equal(2, radio.Received.Count(c => c == "##CN;"));
    }

    /// <summary>Loopback stand-in for the radio's KNS LAN port.</summary>
    private sealed class FakeKenwoodLanRadio : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Encoding _encoding;
        private readonly string _id;
        private readonly CancellationTokenSource _cts = new();
        private TcpClient? _client;
        private int _connections;
        private int _activeClients;

        public FakeKenwoodLanRadio(KenwoodHfProfile profile, string id)
        {
            _encoding = KenwoodHfCatCodec.GetLanEncoding(profile);
            _id = id;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }
        public bool AcceptConnection { get; init; } = true;
        public bool AcceptLogin { get; init; } = true;
        public ConcurrentQueue<string> Received { get; } = new();
        public int Connections => Volatile.Read(ref _connections);
        public int ActiveClients => Volatile.Read(ref _activeClients);

        public void DropClient() => Interlocked.Exchange(ref _client, null)?.Close();

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            DropClient();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception)
                {
                    return;
                }

                Interlocked.Increment(ref _connections);
                Interlocked.Increment(ref _activeClients);
                _client = client;
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            var decoder = _encoding.GetDecoder();
            var buffer = new byte[512];
            var chars = new char[512];
            var pending = new StringBuilder();
            try
            {
                var stream = client.GetStream();
                while (!_cts.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, _cts.Token);
                    if (read <= 0)
                        break;

                    pending.Append(chars, 0, decoder.GetChars(buffer, 0, read, chars, 0));
                    int end;
                    while ((end = pending.ToString().IndexOf(';')) >= 0)
                    {
                        var command = pending.ToString(0, end + 1);
                        pending.Remove(0, end + 1);
                        Received.Enqueue(command);
                        var reply = Reply(command);
                        if (reply is not null)
                        {
                            var bytes = _encoding.GetBytes(reply);
                            await stream.WriteAsync(bytes, _cts.Token);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // client dropped
            }
            finally
            {
                client.Dispose();
                Interlocked.Decrement(ref _activeClients);
            }
        }

        private string? Reply(string command)
        {
            if (command == "##CN;")
                return AcceptConnection ? "##CN1;" : "##CN0;";
            if (command.StartsWith("##ID", StringComparison.Ordinal))
                return AcceptLogin ? "##ID1;" : "##ID0;";
            return command switch
            {
                "ID;" => $"ID{_id};",
                "FA;" => "FA00014074000;",
                _ => null
            };
        }
    }
}
