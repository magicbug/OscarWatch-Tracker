using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using OscarWatch.Core.Net;
using OscarWatch.Core.Terminal;
using Serilog;

namespace OscarWatch.Terminal;

public enum AprsIsState
{
    Off,
    Connecting,
    Connected,
    Unverified,
    Error
}

/// <summary>
/// Receive-only APRS-IS gateway: logs in and forwards heard RF frames with <c>qAR</c>. It never reads
/// traffic for transmission, so nothing from the internet reaches RF.
/// </summary>
public sealed class AprsIsClient : IAsyncDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<AprsIsClient>();
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan StableSession = TimeSpan.FromSeconds(30);

    /// <summary>
    /// APRS-IS drops a client that sends nothing. aprsc's default is long, but many servers use a
    /// much shorter limit, so a quiet receive-only gateway has to say something on its own.
    /// </summary>
    internal static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(20);

    private readonly AprsIsDupeFilter _dupes = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _gate = new();

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private NetworkStream? _stream;
    private string _callsign = "";
    private AprsIsState _state = AprsIsState.Off;

    public AprsIsState State => _state;
    public int GatedCount { get; private set; }

    public event Action<AprsIsState>? StateChanged;

    public void Start(TerminalAprsIsSettings settings, string callsign)
    {
        Stop();
        _callsign = TerminalText.BaseCallsign(callsign);
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Server) || _callsign.Length == 0)
            return;

        var cts = new CancellationTokenSource();
        lock (_gate)
            _runCts = cts;
        var server = settings.Server.Trim();
        var port = settings.Port;
        var passcode = settings.Passcode ?? "";
        _runTask = Task.Run(() => RunAsync(server, port, passcode, cts));
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _runCts;
            _runCts = null;
            _stream = null;
        }

        if (cts is null)
            return;
        cts.Cancel();
        SetState(AprsIsState.Off);
    }

    /// <summary>Queues a heard frame for the gateway. Nothing is sent until the server has verified the passcode.</summary>
    public void Gate(Ax25Frame frame, DateTime utc)
    {
        if (_state != AprsIsState.Connected)
            return;
        var line = AprsIsFormatter.FormatGateLine(frame, _callsign);
        if (line is null || !_dupes.TryAccept(AprsIsFormatter.DupeKey(frame), utc))
            return;
        _ = WriteLineAsync(line);
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_runTask is not null)
        {
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }
    }

    private async Task RunAsync(string server, int port, string passcode, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var backoff = InitialBackoff;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                SetState(AprsIsState.Connecting);
                var started = Stopwatch.GetTimestamp();
                var opened = false;
                try
                {
                    using var client = new TcpClient { NoDelay = true };
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        timeout.CancelAfter(ConnectTimeout);
                        await client.ConnectAsync(server, port, timeout.Token).ConfigureAwait(false);
                    }

                    var stream = client.GetStream();
                    lock (_gate)
                        _stream = stream;
                    var login = AprsIsFormatter.LoginLine(_callsign, passcode, "OscarWatch-Tracker", OscarWatchHttpClients.GetProductVersion());
                    await WriteRawAsync(stream, login, ct).ConfigureAwait(false);
                    opened = true;
                    await ReadUntilClosedAsync(stream, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
                {
                    if (ct.IsCancellationRequested)
                        break;
                    Log.Information(ex, "APRS-IS connection to {Server}:{Port} failed", server, port);
                    SetState(AprsIsState.Error);
                }

                lock (_gate)
                    _stream = null;
                if (ct.IsCancellationRequested)
                    break;

                var uptime = Stopwatch.GetElapsedTime(started);
                if (opened && !ct.IsCancellationRequested)
                    Log.Information("APRS-IS connection to {Server}:{Port} closed after {UptimeSeconds:0}s", server, port, uptime.TotalSeconds);

                var plan = PlanReconnect(backoff, uptime);
                backoff = plan.Next;
                try
                {
                    await Task.Delay(plan.Delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_runCts, cts))
                    _runCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>How long to wait before the next attempt, and the delay to keep for the one after that.</summary>
    internal readonly record struct ReconnectPlan(TimeSpan Delay, TimeSpan Next);

    internal static ReconnectPlan PlanReconnect(TimeSpan current, TimeSpan uptime)
    {
        if (uptime >= StableSession)
            return new ReconnectPlan(InitialBackoff, InitialBackoff);

        var doubledTicks = Math.Min(MaxBackoff.Ticks, current.Ticks * 2);
        return new ReconnectPlan(current, TimeSpan.FromTicks(doubledTicks));
    }

    private async Task ReadUntilClosedAsync(NetworkStream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
        var pending = reader.ReadLineAsync(ct).AsTask();
        while (!ct.IsCancellationRequested)
        {
            var pause = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                var wait = Task.Delay(KeepaliveInterval, pause.Token);
                var finished = await Task.WhenAny(pending, wait).ConfigureAwait(false);
                if (finished != pending)
                {
                    if (ct.IsCancellationRequested)
                        break;
                    await WriteRawAsync(stream, "#", ct).ConfigureAwait(false);
                    continue;
                }
            }
            finally
            {
                pause.Cancel();
                pause.Dispose();
            }

            var line = await pending.ConfigureAwait(false);
            if (line is null)
                break;
            NoteServerLine(line);
            pending = reader.ReadLineAsync(ct).AsTask();
        }

        if (!ct.IsCancellationRequested)
            SetState(AprsIsState.Error);
    }

    private void NoteServerLine(string line)
    {
        if (!line.StartsWith("# logresp", StringComparison.OrdinalIgnoreCase))
            return;

        var verified = line.Contains(" verified", StringComparison.OrdinalIgnoreCase)
                       && !line.Contains("unverified", StringComparison.OrdinalIgnoreCase);
        SetState(verified ? AprsIsState.Connected : AprsIsState.Unverified);
        Log.Information("APRS-IS passcode {Result}", verified ? "verified" : "not verified");
    }

    private async Task WriteLineAsync(string line)
    {
        NetworkStream? stream;
        lock (_gate)
            stream = _stream;
        if (stream is null)
            return;
        try
        {
            await WriteRawAsync(stream, line, CancellationToken.None).ConfigureAwait(false);
            GatedCount++;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            Log.Debug(ex, "APRS-IS write failed");
        }
    }

    private async Task WriteRawAsync(NetworkStream stream, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void SetState(AprsIsState state)
    {
        if (_state == state)
            return;
        _state = state;
        StateChanged?.Invoke(state);
    }
}
