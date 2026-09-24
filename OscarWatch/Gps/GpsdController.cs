using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using Serilog;

namespace OscarWatch.Gps;

/// <summary>Reads gpsd JSON watch stream over TCP on a dedicated background thread.</summary>
public sealed class GpsdController : IGpsService, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<GpsdController>();
    private static readonly TimeSpan LoopInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ClockLoopInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan CommandWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FixStaleAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private const string WatchCommand = "?WATCH={\"enable\":true,\"json\":true}";

    private readonly object _statusLock = new();
    private readonly object _workerStartLock = new();

    private BlockingCollection<GpsCommand>? _commands;
    private Thread? _worker;
    private int _disposed;
    private volatile bool _shutdownRequested;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private string? _connectedKey;
    private GpsSettings _cachedSettings = new();
    private GpsConnectionStatus _status = new(false, false, null, null, null, null, null, null);
    private DateTime? _lastFixUtc;
    private string? _lastConnectError;
    private readonly GpsClockOffsetEstimator _clock = new();

    public void Update(GpsSettings settings) =>
        Enqueue(new GpsCommand(GpsCommandKind.Update, settings));

    public void Disconnect() =>
        Enqueue(new GpsCommand(GpsCommandKind.Disconnect));

    /// <summary>Disconnect and block until the gpsd worker has closed the TCP connection.</summary>
    public void DisconnectAndWait() =>
        EnqueueAndWait(new GpsCommand(GpsCommandKind.Disconnect), TimeSpan.FromSeconds(10));

    public GpsConnectionStatus GetStatus()
    {
        lock (_statusLock)
            return _status;
    }

    public DateTime? GetTrackingUtc()
    {
        lock (_statusLock)
        {
            if (!_cachedSettings.UseGpsTimeForTracking || !_status.HasFix || _lastFixUtc is null)
                return null;

            if (DateTime.UtcNow - _lastFixUtc.Value > FixStaleAfter)
                return null;

            return _status.FixUtc ?? _lastFixUtc;
        }
    }

    public TimeSpan? GetFt4ClockOffset()
    {
        lock (_statusLock)
        {
            if (!_cachedSettings.Enabled || !_cachedSettings.UseGpsTimeForFt4 || !_status.HasFix || _lastFixUtc is null)
                return null;

            if (DateTime.UtcNow - _lastFixUtc.Value > FixStaleAfter)
                return null;
        }

        return _clock.CurrentOffset;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        try
        {
            // Bypass Enqueue (disposed check) so Shutdown can still be delivered.
            if (_commands is not null && _worker is { IsAlive: true })
            {
                using var done = new ManualResetEventSlim(false);
                var command = new GpsCommand(GpsCommandKind.Shutdown) { Completed = done };
                _commands.Add(command);
                if (!done.Wait(TimeSpan.FromSeconds(3)))
                    Log.Warning("gpsd worker shutdown did not complete in time");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "gpsd worker shutdown did not complete cleanly");
        }

        _worker?.Join(TimeSpan.FromSeconds(2));
        _commands?.Dispose();
        _commands = null;
    }

    private void Enqueue(GpsCommand command)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        EnsureWorker();
        _commands!.Add(command);
    }

    private void EnqueueAndWait(GpsCommand command, TimeSpan? timeout = null)
    {
        using var done = new ManualResetEventSlim(false);
        command.Completed = done;
        Enqueue(command);
        if (!done.Wait(timeout ?? CommandWaitTimeout))
            throw new TimeoutException("gpsd worker did not complete the command in time.");
    }

    private void EnsureWorker()
    {
        lock (_workerStartLock)
        {
            if (_worker is { IsAlive: true })
                return;

            _shutdownRequested = false;
            _commands = new BlockingCollection<GpsCommand>();
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "OscarWatch.Gpsd"
            };
            _worker.Start();
        }
    }

    private void WorkerLoop()
    {
        try
        {
            while (!_shutdownRequested)
            {
                var commands = _commands;
                if (commands is null)
                    break;

                // Poll quickly while FT4 GPS time is on so arrival stamps stay close to the report.
                var interval = _cachedSettings.UseGpsTimeForFt4 ? ClockLoopInterval : LoopInterval;
                if (commands.TryTake(out var command, interval))
                {
                    ProcessCommand(command);
                    DrainPendingCommands();
                }

                if (_shutdownRequested)
                    break;

                if (_cachedSettings.Enabled
                    && _cachedSettings.ConnectionKind == GpsConnectionKind.Gpsd
                    && !string.IsNullOrWhiteSpace(_cachedSettings.GpsdHost)
                    && _stream is null)
                    EnsureConnected(_cachedSettings);

                ReadAvailableLines();
                RefreshStatusFromFixAge();
            }
        }
        finally
        {
            TearDownConnection();
            SetStatus(new GpsConnectionStatus(false, false, null, null, null, null, null, null));
        }
    }

    private void DrainPendingCommands()
    {
        var commands = _commands;
        if (commands is null)
            return;

        while (commands.TryTake(out var command, 0))
            ProcessCommand(command);
    }

    private void ProcessCommand(GpsCommand command)
    {
        try
        {
            switch (command.Kind)
            {
                case GpsCommandKind.Update:
                    _cachedSettings = command.Settings ?? new GpsSettings();
                    if (!_cachedSettings.Enabled
                        || _cachedSettings.ConnectionKind != GpsConnectionKind.Gpsd
                        || string.IsNullOrWhiteSpace(_cachedSettings.GpsdHost))
                    {
                        TearDownConnection();
                        SetStatus(new GpsConnectionStatus(false, false, null, null, null, null, null, null));
                    }
                    else if (!EnsureConnected(_cachedSettings))
                    {
                        SetStatus(new GpsConnectionStatus(false, false, null, null, null, null, null, _lastConnectError));
                    }
                    break;

                case GpsCommandKind.Disconnect:
                    // Clear cached settings so the worker loop does not reconnect to gpsd.
                    _cachedSettings = new GpsSettings();
                    TearDownConnection();
                    SetStatus(new GpsConnectionStatus(false, false, null, null, null, null, null, null));
                    break;

                case GpsCommandKind.Shutdown:
                    _shutdownRequested = true;
                    break;
            }
        }
        finally
        {
            command.Completed?.Set();
        }
    }

    private bool EnsureConnected(GpsSettings settings)
    {
        var host = settings.GpsdHost.Trim();
        var port = Math.Clamp(settings.GpsdPort, 1, 65535);
        var key = $"{host}|{port}";
        if (_stream is not null && _connectedKey == key)
            return true;

        TearDownConnection();
        try
        {
            _client = new TcpClient();
            var connectTask = _client.ConnectAsync(host, port);
            if (!connectTask.Wait(ConnectTimeout))
                throw new TimeoutException($"Timed out connecting to gpsd at {host}:{port}.");

            _stream = _client.GetStream();
            _reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            _writer.WriteLine(WatchCommand);
            _connectedKey = key;
            _lastConnectError = null;
            SetStatus(new GpsConnectionStatus(true, false, null, null, null, null, null, null));
            return true;
        }
        catch (Exception ex)
        {
            _lastConnectError = ex.Message;
            Log.Warning(ex, "gpsd connect failed at {Host}:{Port}", host, port);
            TearDownConnection();
            return false;
        }
    }

    private void ReadAvailableLines()
    {
        if (_reader is null || !_cachedSettings.Enabled)
            return;

        try
        {
            while (_stream?.DataAvailable == true)
            {
                var line = _reader.ReadLine();
                if (line is null)
                {
                    TearDownConnection();
                    SetStatus(new GpsConnectionStatus(false, false, null, null, null, null, null, "gpsd connection closed."));
                    return;
                }

                if (line.Length > 0)
                    ProcessLine(line);
            }
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "gpsd read failed at {Host}:{Port}", _cachedSettings.GpsdHost, _cachedSettings.GpsdPort);
            TearDownConnection();
            SetStatus(new GpsConnectionStatus(false, false, null, null, null, null, null, ex.Message));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "gpsd read failed at {Host}:{Port}", _cachedSettings.GpsdHost, _cachedSettings.GpsdPort);
            TearDownConnection();
            SetStatus(new GpsConnectionStatus(false, false, null, null, null, null, null, ex.Message));
        }
    }

    private void ProcessLine(string line)
    {
        if (GpsdJsonParser.TryParseTpvLine(line, out var tpv))
        {
            ApplyTpv(tpv);
            return;
        }

        if (GpsdJsonParser.TryParseSkyLine(line, out var satellitesInUse))
            ApplySatelliteCount(satellitesInUse);
    }

    private void ApplyTpv(GpsdJsonParser.GpsFixData parsed)
    {
        var receivedUtc = DateTime.UtcNow;
        lock (_statusLock)
        {
            var lat = parsed.LatitudeDeg ?? _status.LatitudeDeg;
            var lon = parsed.LongitudeDeg ?? _status.LongitudeDeg;
            var alt = parsed.AltitudeMeters ?? _status.AltitudeMeters;
            var sats = _status.Satellites;
            var fixUtc = parsed.UtcTime ?? _status.FixUtc;
            var hasFix = parsed.HasValidPosition
                && lat is not null
                && lon is not null
                && MeetsMinSatellites(sats);

            if (hasFix)
            {
                _lastFixUtc = receivedUtc;
                if (parsed.UtcTime is { } gpsUtc)
                    _clock.AddSample(gpsUtc, receivedUtc);
            }

            _status = new GpsConnectionStatus(
                _stream is not null,
                hasFix,
                lat,
                lon,
                alt,
                sats,
                fixUtc,
                null);
        }
    }

    private void ApplySatelliteCount(int satellitesInUse)
    {
        lock (_statusLock)
        {
            var hasFix = _status.HasFix && MeetsMinSatellites(satellitesInUse);
            if (hasFix)
                _lastFixUtc = DateTime.UtcNow;

            _status = _status with
            {
                Satellites = satellitesInUse,
                HasFix = hasFix
            };
        }
    }

    private bool MeetsMinSatellites(int? satellites) =>
        satellites is null || satellites >= Math.Max(1, _cachedSettings.MinSatellites);

    private void RefreshStatusFromFixAge()
    {
        lock (_statusLock)
        {
            if (!_status.HasFix || _lastFixUtc is null)
                return;

            if (DateTime.UtcNow - _lastFixUtc.Value <= FixStaleAfter)
                return;

            _status = _status with { HasFix = false };
        }
    }

    private void TearDownConnection()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "gpsd writer dispose failed");
        }

        try
        {
            _reader?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "gpsd reader dispose failed");
        }

        try
        {
            _stream?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "gpsd stream dispose failed");
        }

        try
        {
            _client?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "gpsd client dispose failed");
        }

        _writer = null;
        _reader = null;
        _stream = null;
        _client = null;
        _connectedKey = null;
        _clock.Reset();
    }

    private void SetStatus(GpsConnectionStatus status)
    {
        lock (_statusLock)
            _status = status;
    }

    private enum GpsCommandKind
    {
        Update,
        Disconnect,
        Shutdown
    }

    private sealed class GpsCommand
    {
        public GpsCommand(GpsCommandKind kind, GpsSettings? settings = null)
        {
            Kind = kind;
            Settings = settings;
        }

        public GpsCommandKind Kind { get; }
        public GpsSettings? Settings { get; }
        public ManualResetEventSlim? Completed { get; set; }
    }
}
