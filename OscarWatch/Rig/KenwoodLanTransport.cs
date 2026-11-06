using System.Net.Sockets;
using System.Text;
using OscarWatch.Core.Radio;
using Serilog;

namespace OscarWatch.Rig;

public enum KenwoodLanFailure
{
    NoReply,
    ConnectionDenied,
    LoginRejected,
    InvalidCredentials
}

/// <summary>Kenwood LAN session could not be established.</summary>
public sealed class KenwoodLanException(KenwoodLanFailure failure, string message) : InvalidOperationException(message)
{
    public KenwoodLanFailure Failure { get; } = failure;
}

/// <summary>
/// Kenwood KNS LAN CAT on TCP 60000 (TS-990S / TS-890S): <c>##CN;</c>, then <c>##ID…;</c> login.
/// The radio drops the socket after 10 s without traffic, so an idle <c>ID;</c> is sent about every 5 s.
/// A dropped session is re-established before the next command.
/// </summary>
internal sealed class KenwoodLanTransport : IKenwoodCatTransport
{
    private static readonly ILogger Log = Serilog.Log.ForContext<KenwoodLanTransport>();

    internal const int DefaultKeepAliveIdleMs = 5000;
    private const int ConnectTimeoutMs = 3000;
    private const int LoginReplyTimeoutMs = 2000;
    private const int MinimumReplyTimeoutMs = 600;

    private readonly string _host;
    private readonly int _port;
    private readonly string _account;
    private readonly string _password;
    private readonly bool _administratorAccount;
    private readonly KenwoodHfProfile _profile;
    private readonly Encoding _encoding;
    private readonly int _keepAliveIdleMs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _rx = new();
    private readonly byte[] _readBuffer = new byte[1024];
    private readonly char[] _charBuffer = new char[1024];
    private Decoder _decoder;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Timer? _keepAliveTimer;
    private long _lastSendTicks;
    private bool _disposed;

    public KenwoodLanTransport(
        KenwoodHfProfile profile,
        string host,
        int port,
        string account,
        string password,
        bool administratorAccount = true,
        int keepAliveIdleMs = DefaultKeepAliveIdleMs)
    {
        if (!profile.LanCapable)
            throw new ArgumentException($"{profile.Type} has no LAN CAT socket.", nameof(profile));

        _profile = profile;
        _host = host.Trim();
        _port = port;
        _account = account;
        _password = password;
        _administratorAccount = administratorAccount;
        _encoding = KenwoodHfCatCodec.GetLanEncoding(profile);
        _decoder = _encoding.GetDecoder();
        _keepAliveIdleMs = Math.Max(500, keepAliveIdleMs);
    }

    public bool IsOpen => _client?.Connected == true && _stream is not null;

    public void Open()
    {
        _gate.Wait();
        try
        {
            ConnectAndLoginUnlocked();
        }
        finally
        {
            _gate.Release();
        }

        _keepAliveTimer ??= new Timer(_ => KeepAlive(), null, 1000, 1000);
    }

    public bool SendCommand(string command, int postDelayMs = 50) =>
        SendFireAndForget(command, postDelayMs);

    public bool SendFireAndForget(string command, int postDelayMs = 50)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var cmd = Normalize(command);
        _gate.Wait();
        try
        {
            if (!WriteWithReconnectUnlocked(cmd))
                return false;

            Thread.Sleep(Math.Max(postDelayMs, 0));
            foreach (var reply in DrainMessagesUnlocked())
            {
                if (IsError(reply))
                {
                    Log.Warning("Kenwood LAN set rejected on {Host}: {Cmd} → {Reply}", _host, cmd, reply);
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Log.Warning(ex, "Kenwood LAN send failed on {Host} for {Cmd}", _host, cmd);
            DisconnectUnlocked();
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? Transact(string command, int postDelayMs = 50)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var cmd = Normalize(command);
        _gate.Wait();
        try
        {
            return TransactUnlocked(cmd, Math.Max(MinimumReplyTimeoutMs, postDelayMs + 400), reconnect: true);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Log.Debug(ex, "Kenwood LAN transaction failed on {Host} for {Cmd}", _host, cmd);
            DisconnectUnlocked();
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;
        _gate.Wait();
        try
        {
            DisconnectUnlocked();
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    private void KeepAlive()
    {
        if (_disposed || !IsOpen)
            return;

        if (Environment.TickCount64 - Interlocked.Read(ref _lastSendTicks) < _keepAliveIdleMs)
            return;

        if (!_gate.Wait(0))
            return;

        try
        {
            if (!_disposed && IsOpen)
                TransactUnlocked(KenwoodHfCatCodec.BuildIdQuery(), MinimumReplyTimeoutMs, reconnect: false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Kenwood LAN keepalive failed on {Host}", _host);
            DisconnectUnlocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? TransactUnlocked(string cmd, int timeoutMs, bool reconnect)
    {
        DrainMessagesUnlocked();
        if (reconnect)
        {
            if (!WriteWithReconnectUnlocked(cmd))
                return null;
        }
        else
        {
            WriteUnlocked(cmd);
        }

        return ReadReplyUnlocked(ReplyPrefix(cmd), timeoutMs);
    }

    private bool WriteWithReconnectUnlocked(string cmd)
    {
        if (!IsOpen || RemoteClosedUnlocked())
        {
            if (_client is not null)
                Log.Information("Kenwood LAN session on {Host} dropped; reconnecting", _host);
            ConnectAndLoginUnlocked();
        }

        try
        {
            WriteUnlocked(cmd);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Log.Information("Kenwood LAN session on {Host} dropped; reconnecting", _host);
            DisconnectUnlocked();
            ConnectAndLoginUnlocked();
            WriteUnlocked(cmd);
            return true;
        }
    }

    private void ConnectAndLoginUnlocked()
    {
        DisconnectUnlocked();
        if (!KenwoodHfCatCodec.TryBuildLanLoginCommand(_profile, _account, _password, _administratorAccount, out var login))
        {
            throw new KenwoodLanException(
                KenwoodLanFailure.InvalidCredentials,
                $"{_profile.Type} LAN account or password is missing or too long.");
        }

        _client = new TcpClient { NoDelay = true };
        using (var cts = new CancellationTokenSource(ConnectTimeoutMs))
        {
            try
            {
                _client.ConnectAsync(_host, _port, cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                DisconnectUnlocked();
                throw new TimeoutException($"Kenwood LAN connect to {_host}:{_port} timed out.");
            }
        }

        _stream = _client.GetStream();
        _stream.WriteTimeout = ConnectTimeoutMs;
        _decoder = _encoding.GetDecoder();
        _rx.Clear();

        WriteUnlocked(KenwoodHfCatCodec.BuildLanConnectCommand());
        var cn = ReadReplyUnlocked("##CN", LoginReplyTimeoutMs);
        if (cn is null || !KenwoodHfCatCodec.TryParseLanConnect(cn, out var accepted))
        {
            DisconnectUnlocked();
            throw new KenwoodLanException(KenwoodLanFailure.NoReply, $"{_host}:{_port} did not answer ##CN.");
        }

        if (!accepted)
        {
            DisconnectUnlocked();
            throw new KenwoodLanException(
                KenwoodLanFailure.ConnectionDenied,
                $"{_host}:{_port} refused the LAN session. Another program may already be connected.");
        }

        WriteUnlocked(login);
        var id = ReadReplyUnlocked("##ID", LoginReplyTimeoutMs);
        if (id is null || !KenwoodHfCatCodec.TryParseLanLogin(id, out var loggedIn) || !loggedIn)
        {
            DisconnectUnlocked();
            throw new KenwoodLanException(
                KenwoodLanFailure.LoginRejected,
                $"{_host}:{_port} rejected the LAN account or password.");
        }

        Log.Information("Kenwood LAN session open to {RigType} at {Host}:{Port}", _profile.Type, _host, _port);
    }

    private void WriteUnlocked(string cmd)
    {
        if (_stream is null)
            throw new IOException("Kenwood LAN stream is not open.");

        var bytes = _encoding.GetBytes(cmd);
        _stream.Write(bytes, 0, bytes.Length);
        _stream.Flush();
        Interlocked.Exchange(ref _lastSendTicks, Environment.TickCount64);
    }

    private string? ReadReplyUnlocked(string prefix, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            while (TryTakeMessage(out var message))
            {
                if (IsError(message))
                    return null;
                if (message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return message;
                Log.Debug("Kenwood LAN ignored unsolicited {Message} while waiting for {Prefix}", message, prefix);
            }

            if (Environment.TickCount64 >= deadline)
                return null;

            if (!PumpUnlocked(waitMs: 15))
                return null;
        }
    }

    private List<string> DrainMessagesUnlocked()
    {
        var messages = new List<string>();
        if (_stream is null)
            return messages;

        while (_stream.DataAvailable)
        {
            if (!PumpUnlocked(waitMs: 0))
                break;
        }

        while (TryTakeMessage(out var message))
            messages.Add(message);
        return messages;
    }

    /// <summary>Read whatever is available. Returns false when the socket closed.</summary>
    private bool PumpUnlocked(int waitMs)
    {
        if (_stream is null)
            return false;

        if (!_stream.DataAvailable)
        {
            if (waitMs > 0)
                Thread.Sleep(waitMs);
            return true;
        }

        var read = _stream.Read(_readBuffer, 0, _readBuffer.Length);
        if (read <= 0)
        {
            DisconnectUnlocked();
            return false;
        }

        var chars = _decoder.GetChars(_readBuffer, 0, read, _charBuffer, 0);
        _rx.Append(_charBuffer, 0, chars);
        return true;
    }

    private bool TryTakeMessage(out string message)
    {
        message = "";
        for (var i = 0; i < _rx.Length; i++)
        {
            if (_rx[i] != ';')
                continue;

            message = _rx.ToString(0, i + 1).Trim('\0', ' ', '\r', '\n');
            _rx.Remove(0, i + 1);
            return true;
        }

        return false;
    }

    /// <summary>True when the radio has closed the socket (readable with no data pending).</summary>
    private bool RemoteClosedUnlocked()
    {
        var socket = _client?.Client;
        if (socket is null)
            return true;

        try
        {
            return socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return true;
        }
    }

    private void DisconnectUnlocked()
    {
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            _client?.Dispose();
        }
        catch
        {
            // ignore
        }

        _stream = null;
        _client = null;
        _rx.Clear();
    }

    internal static string ReplyPrefix(string cmd)
    {
        var body = cmd.TrimEnd(';');
        if (body.StartsWith("##", StringComparison.Ordinal))
            return body.Length >= 4 ? body[..4] : body;
        return body.Length >= 2 ? body[..2] : body;
    }

    private static string Normalize(string command)
    {
        var cmd = command.Trim();
        return cmd.EndsWith(';') ? cmd : cmd + ";";
    }

    private static bool IsError(string reply) =>
        reply is "?;" or "E;" or "O;";
}
