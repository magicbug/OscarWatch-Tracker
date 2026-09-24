using System.Collections.Concurrent;
using System.IO.Ports;
using System.Text;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using Serilog;

namespace OscarWatch.Gps;

/// <summary>Reads NMEA sentences from a serial GPS on a dedicated background thread.</summary>
public sealed class GpsController : IGpsService, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<GpsController>();
    private static readonly TimeSpan LoopInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ClockLoopInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan CommandWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FixStaleAfter = TimeSpan.FromSeconds(30);

    private readonly object _statusLock = new();
    private readonly object _workerStartLock = new();

    private BlockingCollection<GpsCommand>? _commands;
    private Thread? _worker;
    private int _disposed;
    private volatile bool _shutdownRequested;

    private SerialPort? _port;
    private string? _connectedKey;
    private GpsSettings _cachedSettings = new();
    private GpsConnectionStatus _status = new(false, false, null, null, null, null, null, null);
    private DateTime? _lastFixUtc;
    private DateTime? _lastSentenceUtc;
    private readonly GpsClockOffsetEstimator _clock = new();

    public void Update(GpsSettings settings) =>
        Enqueue(new GpsCommand(GpsCommandKind.Update, settings));

    public void Disconnect() =>
        Enqueue(new GpsCommand(GpsCommandKind.Disconnect));

    /// <summary>Disconnect and block until the GPS worker has closed the serial port.</summary>
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
                    Log.Warning("GPS worker shutdown did not complete in time");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GPS worker shutdown did not complete cleanly");
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
            throw new TimeoutException("GPS worker did not complete the command in time.");
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
                Name = "OscarWatch.Gps"
            };
            _worker.Start();
        }
    }

    private void WorkerLoop()
    {
        var lineBuffer = new StringBuilder();
        try
        {
            while (!_shutdownRequested)
            {
                var commands = _commands;
                if (commands is null)
                    break;

                // Poll quickly while FT4 GPS time is on so arrival stamps stay close to the sentence.
                var interval = _cachedSettings.UseGpsTimeForFt4 ? ClockLoopInterval : LoopInterval;
                if (commands.TryTake(out var command, interval))
                {
                    ProcessCommand(command);
                    DrainPendingCommands();
                }

                if (_shutdownRequested)
                    break;

                if (_cachedSettings.Enabled
                    && !string.IsNullOrWhiteSpace(_cachedSettings.Port)
                    && _port?.IsOpen != true)
                    EnsureConnected(_cachedSettings);

                ReadAvailableLines(lineBuffer);
                RefreshStatusFromFixAge();
            }
        }
        finally
        {
            TearDownPort();
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
                    if (!_cachedSettings.Enabled || string.IsNullOrWhiteSpace(_cachedSettings.Port))
                    {
                        TearDownPort();
                        SetStatus(new GpsConnectionStatus(false, false, null, null, null, null, null, null));
                    }
                    else if (!EnsureConnected(_cachedSettings))
                    {
                        SetStatus(new GpsConnectionStatus(false, false, null, null, null, null, null, _lastConnectError));
                    }
                    break;

                case GpsCommandKind.Disconnect:
                    // Clear cached settings so the worker loop does not reopen the port.
                    _cachedSettings = new GpsSettings();
                    TearDownPort();
                    SetStatus(new GpsConnectionStatus(false, false, null, null, null, null, null, null));
                    break;

                case GpsCommandKind.Drain:
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

    private string? _lastConnectError;

    private bool EnsureConnected(GpsSettings settings)
    {
        var key = $"{settings.Port}|{settings.BaudRate}";
        if (_port?.IsOpen == true && _connectedKey == key)
            return true;

        TearDownPort();
        try
        {
            _port = new SerialPort(settings.Port, settings.BaudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 500,
                WriteTimeout = 500,
                NewLine = "\r\n",
                Encoding = Encoding.ASCII
            };
            _port.Open();
            _connectedKey = key;
            _lastConnectError = null;
            SetStatus(new GpsConnectionStatus(true, false, null, null, null, null, null, null));
            return true;
        }
        catch (Exception ex)
        {
            _lastConnectError = ex.Message;
            Log.Warning(ex, "GPS connect failed on {Port}", settings.Port);
            TearDownPort();
            return false;
        }
    }

    private void ReadAvailableLines(StringBuilder lineBuffer)
    {
        if (_port?.IsOpen != true || !_cachedSettings.Enabled)
            return;

        try
        {
            while (_port.BytesToRead > 0)
            {
                var ch = (char)_port.ReadChar();
                if (ch == '\r')
                    continue;

                if (ch == '\n')
                {
                    var line = lineBuffer.ToString();
                    lineBuffer.Clear();
                    if (line.Length > 0)
                        ProcessLine(line);
                    continue;
                }

                lineBuffer.Append(ch);
                if (lineBuffer.Length > 256)
                    lineBuffer.Clear();
            }
        }
        catch (TimeoutException)
        {
            // expected with ReadTimeout
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GPS read failed on {Port}", _cachedSettings.Port);
            TearDownPort();
            SetStatus(new GpsConnectionStatus(false, false, null, null, null, null, null, ex.Message));
        }
    }

    private void ProcessLine(string line)
    {
        if (!NmeaSentenceParser.TryParseLine(line, out var parsed))
            return;

        var receivedUtc = DateTime.UtcNow;
        _lastSentenceUtc = receivedUtc;

        lock (_statusLock)
        {
            var lat = parsed.LatitudeDeg ?? _status.LatitudeDeg;
            var lon = parsed.LongitudeDeg ?? _status.LongitudeDeg;
            var alt = parsed.AltitudeMeters ?? _status.AltitudeMeters;
            var sats = parsed.SatellitesInUse ?? _status.Satellites;
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
                _port?.IsOpen == true,
                hasFix,
                lat,
                lon,
                alt,
                sats,
                fixUtc,
                null);
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

    private void TearDownPort()
    {
        try
        {
            if (_port?.IsOpen == true)
                _port.Close();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GPS port close failed");
        }

        _port?.Dispose();
        _port = null;
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
        Drain,
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
