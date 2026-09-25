namespace OscarWatch.Core.PskReporter;

/// <summary>
/// Queues spots for one receiver and sends them straight to PSK Reporter over UDP/IPFIX,
/// no more than one datagram about every five minutes unless it fills first.
/// </summary>
public sealed class PskReporterClient : IDisposable
{
    public const string DefaultHost = "report.pskreporter.info";
    public const int DefaultPort = 4739;

    public static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan TemplateInterval = TimeSpan.FromHours(1);
    private const int InitialTemplateDatagrams = 3;

    private readonly Func<string, int, IPskReporterTransport> _transportFactory;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private readonly List<PskReporterSpot> _pending = new();
    private readonly uint _observationDomainId = (uint)Random.Shared.Next(1, int.MaxValue);
    private readonly Timer? _timer;

    private IPskReporterTransport? _transport;
    private string _host = DefaultHost;
    private int _port = DefaultPort;
    private PskReporterReceiver? _receiver;
    private DateTime _nextFlushUtc;
    private DateTime _lastTemplateUtc = DateTime.MinValue;
    private int _datagramsSent;
    private uint _recordsSent;
    private bool _disposed;

    public PskReporterClient()
        : this((host, port) => new UdpPskReporterTransport(host, port), () => DateTime.UtcNow, startTimer: true)
    {
    }

    public PskReporterClient(
        Func<string, int, IPskReporterTransport> transportFactory,
        Func<DateTime> utcNow,
        bool startTimer = false)
    {
        _transportFactory = transportFactory;
        _utcNow = utcNow;
        if (startTimer)
            _timer = new Timer(_ => FlushIfDue(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    /// <summary>Operator-facing diagnostics (message, optional exception) for the host application to log.</summary>
    public event Action<string, Exception?>? Diagnostic;

    public bool IsEnabled
    {
        get
        {
            lock (_gate)
                return _transport is not null;
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _pending.Count;
        }
    }

    /// <summary>Turn reporting on (opens the socket) or off (closes it and discards anything queued).</summary>
    public void Configure(bool enabled, string? host, int port)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            host = string.IsNullOrWhiteSpace(host) ? DefaultHost : host.Trim();
            port = port is > 0 and <= 65535 ? port : DefaultPort;

            if (!enabled)
            {
                if (_transport is not null)
                    Diagnostic?.Invoke($"PSK Reporter reporting turned off; {_pending.Count} queued spots discarded", null);
                CloseTransport();
                _pending.Clear();
                _receiver = null;
                return;
            }

            if (_transport is not null && string.Equals(_host, host, StringComparison.OrdinalIgnoreCase) && _port == port)
                return;

            CloseTransport();
            _pending.Clear();
            _receiver = null;
            try
            {
                _transport = _transportFactory(host, port);
                _host = host;
                _port = port;
                _datagramsSent = 0;
                _lastTemplateUtc = DateTime.MinValue;
                _nextFlushUtc = _utcNow() + FlushInterval + TimeSpan.FromSeconds(Random.Shared.Next(0, 60));
                Diagnostic?.Invoke($"PSK Reporter reporting on, sending to {host}:{port}", null);
            }
            catch (Exception ex)
            {
                _transport = null;
                Diagnostic?.Invoke($"PSK Reporter could not open a UDP socket for {host}:{port}", ex);
            }
        }
    }

    /// <summary>Queue a spot. Dropped when reporting is off.</summary>
    public void Enqueue(PskReporterReceiver receiver, PskReporterSpot spot)
    {
        lock (_gate)
        {
            if (_transport is null || _disposed)
                return;

            // One datagram carries one receiver record, so a new satellite, callsign, or grid starts a new batch.
            if (_receiver is not null && _receiver != receiver)
                FlushLocked();
            _receiver = receiver;

            var existing = _pending.FindIndex(s =>
                string.Equals(s.SenderCallsign, spot.SenderCallsign, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                var locator = string.IsNullOrEmpty(spot.SenderLocator) ? _pending[existing].SenderLocator : spot.SenderLocator;
                _pending[existing] = spot with { SenderLocator = locator };
            }
            else
            {
                _pending.Add(spot);
            }

            if (PskReporterPacketBuilder.EstimateSize(receiver, _pending) >= PskReporterPacketBuilder.MaxDatagramBytes)
                FlushLocked();
        }
    }

    /// <summary>Send the queue when the five-minute window has passed.</summary>
    public void FlushIfDue()
    {
        lock (_gate)
        {
            if (_transport is null || _disposed || _utcNow() < _nextFlushUtc)
                return;
            FlushLocked();
        }
    }

    /// <summary>Send whatever is queued now.</summary>
    public void Flush()
    {
        lock (_gate)
            FlushLocked();
    }

    private void FlushLocked()
    {
        var now = _utcNow();
        _nextFlushUtc = now + FlushInterval;
        if (_transport is null || _receiver is null || _pending.Count == 0)
            return;

        var includeTemplates = _datagramsSent < InitialTemplateDatagrams || now - _lastTemplateUtc >= TemplateInterval;
        var datagram = PskReporterPacketBuilder.Build(
            _receiver,
            _pending,
            includeTemplates,
            PskReporterPacketBuilder.ToUnixSeconds(now),
            _recordsSent,
            _observationDomainId);
        _recordsSent += (uint)_pending.Count;

        try
        {
            _transport.Send(datagram);
            _datagramsSent++;
            if (includeTemplates)
                _lastTemplateUtc = now;
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"PSK Reporter send failed; {_pending.Count} spots dropped", ex);
        }

        _pending.Clear();
    }

    private void CloseTransport()
    {
        try
        {
            _transport?.Dispose();
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke("PSK Reporter socket close failed", ex);
        }

        _transport = null;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        lock (_gate)
        {
            if (_disposed)
                return;
            FlushLocked();
            CloseTransport();
            _disposed = true;
        }
    }
}
