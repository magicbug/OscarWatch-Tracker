using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using OscarWatch.Core.Radio;
using Serilog;

namespace OscarWatch.Rig;

/// <summary>Listens for FlexRadio SmartSDR UDP discovery broadcasts on port 4992.</summary>
public sealed class FlexDiscoveryService : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<FlexDiscoveryService>();

    private readonly ConcurrentDictionary<string, FlexDiscoveredRadio> _radios = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private volatile IReadOnlyList<FlexDiscoveredRadio>? _cachedRadios;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public event EventHandler? RadiosChanged;

    public IReadOnlyList<FlexDiscoveredRadio> Radios
    {
        get
        {
            // Return cached sorted list; rebuilt only when a radio is added/updated.
            var cached = _cachedRadios;
            if (cached is not null)
                return cached;

            lock (_gate)
            {
                _cachedRadios ??= _radios.Values.OrderBy(r => r.Nickname).ThenBy(r => r.IpAddress).ToList();
                return _cachedRadios;
            }
        }
    }

    /// <summary>Inject a discovery datagram (used by unit tests).</summary>
    public bool IngestDatagram(ReadOnlySpan<byte> datagram)
    {
        if (!FlexDiscoveryCodec.TryParse(datagram, out var radio))
            return false;

        return Upsert(radio);
    }

    public void Start(int port = FlexDiscoveryCodec.DefaultDiscoveryPort)
    {
        lock (_gate)
        {
            if (_listenTask is not null)
                return;

            _cts = new CancellationTokenSource();
            try
            {
                _udp = new UdpClient(AddressFamily.InterNetwork);
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Flex discovery UDP bind failed on port {Port}", port);
                _udp?.Dispose();
                _udp = null;
                _cts.Dispose();
                _cts = null;
                throw;
            }

            var token = _cts.Token;
            _listenTask = Task.Run(() => ListenLoopAsync(token), token);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            try
            {
                _udp?.Close();
            }
            catch
            {
            }

            _udp?.Dispose();
            _udp = null;

            try
            {
                _listenTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            _listenTask = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Clear()
    {
        _radios.Clear();
        lock (_gate)
        {
            _cachedRadios = null;
        }
        RadiosChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var udp = _udp;
                if (udp is null)
                    break;

                var result = await udp.ReceiveAsync(token).ConfigureAwait(false);
                IngestDatagram(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (token.IsCancellationRequested)
                    break;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Flex discovery receive error");
            }
        }
    }

    private bool Upsert(FlexDiscoveredRadio radio)
    {
        var key = !string.IsNullOrWhiteSpace(radio.Serial)
            ? radio.Serial
            : $"{radio.IpAddress}:{radio.Port}";

        var changed = true;
        _radios.AddOrUpdate(
            key,
            radio,
            (_, existing) =>
            {
                if (existing.IpAddress == radio.IpAddress
                    && existing.Port == radio.Port
                    && existing.Nickname == radio.Nickname
                    && existing.Model == radio.Model
                    && existing.Status == radio.Status)
                {
                    changed = false;
                    return existing;
                }

                return radio;
            });

        if (changed)
        {
            lock (_gate)
            {
                _cachedRadios = null; // Invalidate under same lock as rebuild
            }
            Log.Information(
                "FlexRadio discovered: model={Model}, nickname={Nickname}, serial={Serial}, endpoint={IpAddress}:{Port}, status={Status}",
                radio.Model,
                radio.Nickname,
                radio.Serial,
                radio.IpAddress,
                radio.Port,
                radio.Status);
            RadiosChanged?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }
}
