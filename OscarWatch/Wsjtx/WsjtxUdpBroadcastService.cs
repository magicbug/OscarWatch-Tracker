using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Core.Wsjtx;
using Serilog;

namespace OscarWatch.Wsjtx;

public sealed class WsjtxUdpBroadcastService : IWsjtxUdpBroadcastService, IDisposable
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private static readonly ILogger Log = Serilog.Log.ForContext<WsjtxUdpBroadcastService>();

    private readonly IWsjtxUdpSender _sender;
    private readonly Action<Action> _queue;
    private readonly string _version;
    private readonly object _gate = new();
    private WsjtxUdpSettings _settings = new();
    private Timer? _timer;
    private bool _advertised;
    private string? _advertisedHost;
    private int _advertisedPort;

    public WsjtxUdpBroadcastService(IWsjtxUdpSender sender)
        : this(sender, static work => { _ = Task.Run(work); })
    {
    }

    internal WsjtxUdpBroadcastService(IWsjtxUdpSender sender, Action<Action> queue)
    {
        _sender = sender;
        _queue = queue;
        _version = typeof(WsjtxUdpBroadcastService).Assembly.GetName().Version?.ToString(3) ?? "1.0";
    }

    public void ApplySettings(WsjtxUdpSettings settings)
    {
        var next = Copy(settings);
        byte[]? close = null;
        var closeHost = "";
        var closePort = 0;
        var announce = false;
        var announceHost = "";
        var announcePort = 0;
        Timer? timerToDispose = null;

        lock (_gate)
        {
            var endpointChanged = _advertised
                && (_advertisedHost is null
                    || !next.Enabled
                    || !string.Equals(_advertisedHost, next.Host, StringComparison.OrdinalIgnoreCase)
                    || _advertisedPort != next.Port);

            if (endpointChanged && _advertisedHost is not null)
            {
                close = WsjtxUdpMessage.Close();
                closeHost = _advertisedHost;
                closePort = _advertisedPort;
                _advertised = false;
            }

            _settings = next;

            if (next.Enabled)
            {
                _advertised = true;
                _advertisedHost = next.Host;
                _advertisedPort = next.Port;
                announce = true;
                announceHost = next.Host;
                announcePort = next.Port;
                _timer ??= new Timer(OnHeartbeat, null, HeartbeatInterval, HeartbeatInterval);
            }
            else
            {
                timerToDispose = _timer;
                _timer = null;
                _advertised = false;
            }
        }

        timerToDispose?.Dispose();

        if (close is null && !announce)
            return;

        var closePacket = close;
        var beat = announce ? WsjtxUdpMessage.Heartbeat(_version) : null;
        Queue(() =>
        {
            if (closePacket is not null)
                SendQuiet(closePacket, closeHost, closePort);
            if (beat is not null)
                SendQuiet(beat, announceHost, announcePort);
        });
    }

    public void PublishLoggedQso(QsoRecord record, QsoLogbook logbook)
    {
        if (record is null || logbook is null)
            return;

        string host;
        int port;
        lock (_gate)
        {
            if (!_settings.Enabled)
                return;

            host = _settings.Host;
            port = _settings.Port;
        }

        byte[] qsoLogged;
        byte[] adif;
        try
        {
            qsoLogged = WsjtxUdpMessage.QsoLogged(logbook, record);
            adif = WsjtxUdpMessage.LoggedAdif(logbook, record);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not build WSJT-X UDP log packets for {Call}", record.Call);
            return;
        }

        Queue(() =>
        {
            lock (_gate)
            {
                if (!_settings.Enabled)
                    return;
            }

            SendQuiet(qsoLogged, host, port);
            SendQuiet(adif, host, port);
        });
    }

    public Task StopAsync()
    {
        byte[]? close = null;
        var host = "";
        var port = 0;
        Timer? timer;

        lock (_gate)
        {
            timer = _timer;
            _timer = null;
            if (_advertised && _advertisedHost is not null)
            {
                close = WsjtxUdpMessage.Close();
                host = _advertisedHost;
                port = _advertisedPort;
                _advertised = false;
            }

            _settings = new WsjtxUdpSettings
            {
                Enabled = false,
                Host = _settings.Host,
                Port = _settings.Port
            };
        }

        timer?.Dispose();

        if (close is null)
            return Task.CompletedTask;

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var packet = close;
        Queue(() =>
        {
            try
            {
                SendQuiet(packet, host, port);
            }
            finally
            {
                done.TrySetResult();
            }
        });
        return done.Task;
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    private void OnHeartbeat(object? state)
    {
        string host;
        int port;
        lock (_gate)
        {
            if (!_settings.Enabled || _timer is null)
                return;

            host = _settings.Host;
            port = _settings.Port;
        }

        SendQuiet(WsjtxUdpMessage.Heartbeat(_version), host, port);
    }

    private void Queue(Action work)
    {
        _queue(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WSJT-X UDP broadcast failed");
            }
        });
    }

    private void SendQuiet(byte[] datagram, string host, int port)
    {
        try
        {
            _sender.Send(datagram, host, port);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WSJT-X UDP send to {Host}:{Port} failed", host, port);
        }
    }

    private static WsjtxUdpSettings Copy(WsjtxUdpSettings? settings)
    {
        settings ??= new WsjtxUdpSettings();
        var host = settings.Host?.Trim() ?? "";
        if (host.Length == 0)
            host = WsjtxUdpSettings.DefaultHost;

        var port = WsjtxUdpSettings.IsValidPort(settings.Port)
            ? settings.Port
            : WsjtxUdpSettings.DefaultPort;

        return new WsjtxUdpSettings
        {
            Enabled = settings.Enabled,
            Host = host,
            Port = port
        };
    }
}
