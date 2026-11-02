using System.Collections.Concurrent;
using System.Diagnostics;
using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

/// <summary>
/// Propagates enabled satellites on a dedicated thread; the UI reads snapshots without blocking.
/// </summary>
public sealed class LiveTrackingService : ILiveTrackingService
{
    private static readonly TimeSpan LoopInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CommandWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GpsJumpWarningInterval = TimeSpan.FromSeconds(10);

    private readonly TrackingOrchestrator _orchestrator;
    private readonly IGpsService? _gps;
    private readonly Func<DateTime, IReadOnlyList<SatelliteTrackState>>? _computeOverride;
    private readonly object _snapshotLock = new();
    private readonly object _workerStartLock = new();

    private BlockingCollection<LiveTrackingCommand>? _commands;
    private Thread? _worker;
    private int _disposed;
    private volatile bool _shutdownRequested;

    private IReadOnlyList<SatelliteTrackState> _snapshot = Array.Empty<SatelliteTrackState>();
    private DateTime _snapshotUtc = DateTime.MinValue;
    private IReadOnlyList<SatelliteTrackState> _liveNowSnapshot = Array.Empty<SatelliteTrackState>();
    private DateTime _liveNowSnapshotUtc = DateTime.MinValue;
    private DateTime? _lastTrackingUtc;
    private DateTime? _lastWallUtc;
    private DateTime _lastGpsJumpWarningUtc = DateTime.MinValue;
    private long _mapTimeOffsetTicks;
    private string? _focusedNoradId;

    public TimeSpan MapTimeOffset
    {
        get => new(Interlocked.Read(ref _mapTimeOffsetTicks));
        set => Interlocked.Exchange(ref _mapTimeOffsetTicks, value.Ticks);
    }

    public string? FocusedNoradId
    {
        get => Volatile.Read(ref _focusedNoradId);
        set => Volatile.Write(ref _focusedNoradId, value);
    }

    public LiveTrackingService(TrackingOrchestrator orchestrator, IGpsService? gps = null)
        : this(orchestrator, gps, computeOverride: null)
    {
    }

    internal LiveTrackingService(
        TrackingOrchestrator orchestrator,
        IGpsService? gps,
        Func<DateTime, IReadOnlyList<SatelliteTrackState>>? computeOverride)
    {
        _orchestrator = orchestrator;
        _gps = gps;
        _computeOverride = computeOverride;
    }

    public DateTime SnapshotUtc
    {
        get { lock (_snapshotLock) return _snapshotUtc; }
    }

    public IReadOnlyList<SatelliteTrackState> GetSnapshot()
    {
        lock (_snapshotLock)
            return _snapshot;
    }

    public (IReadOnlyList<SatelliteTrackState> States, DateTime Utc) GetSnapshotWithUtc()
    {
        lock (_snapshotLock)
            return (_snapshot, _snapshotUtc);
    }

    public DateTime LiveNowSnapshotUtc
    {
        get { lock (_snapshotLock) return _liveNowSnapshotUtc; }
    }

    public IReadOnlyList<SatelliteTrackState> GetLiveNowSnapshot()
    {
        lock (_snapshotLock)
            return _liveNowSnapshot;
    }

    /// <summary>Returns display and live-now snapshots under one lock (unit tests).</summary>
    internal (IReadOnlyList<SatelliteTrackState> Display, IReadOnlyList<SatelliteTrackState> LiveNow) GetSnapshotsForTests()
    {
        lock (_snapshotLock)
            return (_snapshot, _liveNowSnapshot);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        EnsureWorker();
    }

    public void RequestReload() =>
        Enqueue(new LiveTrackingCommand(LiveTrackingCommandKind.Reload));

    /// <summary>Blocks until queued commands are processed (unit tests).</summary>
    internal void DrainCommandQueueForTests() =>
        EnqueueAndWait(new LiveTrackingCommand(LiveTrackingCommandKind.Drain));

    /// <summary>Runs one propagation tick on the worker (unit tests).</summary>
    internal void RefreshSnapshotSynchronously() =>
        EnqueueAndWait(new LiveTrackingCommand(LiveTrackingCommandKind.RefreshSynchronously));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _shutdownRequested = true;
        try
        {
            if (_commands is not null)
            {
                try
                {
                    _commands.Add(new LiveTrackingCommand(LiveTrackingCommandKind.Shutdown));
                }
                catch (InvalidOperationException)
                {
                    // collection completed
                }
            }
        }
        catch
        {
            // best effort
        }

        _worker?.Join(TimeSpan.FromSeconds(2));
        _commands?.Dispose();
        _commands = null;
    }

    private void Enqueue(LiveTrackingCommand command)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        EnsureWorker();
        _commands!.Add(command);
    }

    private void EnqueueAndWait(LiveTrackingCommand command, TimeSpan? timeout = null)
    {
        using var done = new ManualResetEventSlim(false);
        command.Completed = done;
        Enqueue(command);
        if (!done.Wait(timeout ?? CommandWaitTimeout))
            throw new TimeoutException("Live tracking worker did not complete the command in time.");
    }

    private void EnsureWorker()
    {
        lock (_workerStartLock)
        {
            if (_worker is { IsAlive: true })
                return;

            _shutdownRequested = false;
            _commands = new BlockingCollection<LiveTrackingCommand>();
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "OscarWatch.Tracking"
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
                try
                {
                    var commands = _commands;
                    if (commands is null)
                        break;

                    if (commands.TryTake(out var command, LoopInterval))
                    {
                        var refreshedByCommand = ProcessCommand(command);
                        refreshedByCommand |= DrainPendingCommands();
                        if (refreshedByCommand || _shutdownRequested)
                            continue;
                    }

                    if (_shutdownRequested)
                        break;

                    RefreshSnapshot();
                }
                catch (Exception ex)
                {
                    Trace.TraceError(
                        "Live tracking worker iteration failed; continuing (shutdown={0}): {1}",
                        _shutdownRequested,
                        ex);
                }
            }
        }
        finally
        {
            lock (_snapshotLock)
            {
                _snapshot = Array.Empty<SatelliteTrackState>();
                _snapshotUtc = DateTime.MinValue;
                _liveNowSnapshot = Array.Empty<SatelliteTrackState>();
                _liveNowSnapshotUtc = DateTime.MinValue;
            }
        }
    }

    private bool DrainPendingCommands()
    {
        var commands = _commands;
        if (commands is null)
            return false;

        var refreshed = false;
        while (commands.TryTake(out var command, 0))
            refreshed |= ProcessCommand(command);
        return refreshed;
    }

    private bool ProcessCommand(LiveTrackingCommand command)
    {
        var refreshed = false;
        try
        {
            switch (command.Kind)
            {
                case LiveTrackingCommandKind.Reload:
                    // Reload invalidates the cached enabled-satellite list (Req 2.4)
                    _orchestrator.ReloadEnabledSatellites();
                    RefreshSnapshot();
                    refreshed = true;
                    break;

                case LiveTrackingCommandKind.RefreshSynchronously:
                    RefreshSnapshot();
                    refreshed = true;
                    break;

                case LiveTrackingCommandKind.Drain:
                    break;

                case LiveTrackingCommandKind.Shutdown:
                    _shutdownRequested = true;
                    break;
            }
        }
        finally
        {
            command.Completed?.Set();
        }

        return refreshed;
    }

    private void RefreshSnapshot()
    {
        var wallUtc = DateTime.UtcNow;
        var trackingUtc = ResolveTrackingUtc(wallUtc);
        var offset = MapTimeOffset;
        var displayUtc = trackingUtc + offset;

        var focus = FocusedNoradId;
        var displayStates = _computeOverride?.Invoke(displayUtc)
            ?? _orchestrator.GetLiveStates(displayUtc, focus);
        var liveNowStates = offset == TimeSpan.Zero
            ? displayStates
            : _computeOverride?.Invoke(trackingUtc)
              ?? _orchestrator.GetLiveStates(trackingUtc, focus);

        // Copy before publish: orchestrator double-buffers mutable lists that are cleared
        // on the worker thread; UI render may still be reading the previous snapshot.
        var publishedDisplay = PublishSnapshot(displayStates);
        var publishedLiveNow = ReferenceEquals(liveNowStates, displayStates)
            ? publishedDisplay
            : PublishSnapshot(liveNowStates);

        lock (_snapshotLock)
        {
            _snapshot = publishedDisplay;
            _snapshotUtc = displayUtc;
            _liveNowSnapshot = publishedLiveNow;
            _liveNowSnapshotUtc = trackingUtc;
        }
    }

    private DateTime ResolveTrackingUtc(DateTime wallUtc)
    {
        var candidate = _gps?.GetTrackingUtc() ?? wallUtc;
        if (_lastTrackingUtc is { } previousTrackingUtc && _lastWallUtc is { } previousWallUtc)
        {
            var wallDelta = wallUtc - previousWallUtc;
            var trackingDelta = candidate - previousTrackingUtc;
            var maxTrackingDelta = TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(wallDelta.Ticks * 2);
            if (trackingDelta < TimeSpan.FromSeconds(-1) || trackingDelta > maxTrackingDelta)
            {
                if (wallUtc - _lastGpsJumpWarningUtc >= GpsJumpWarningInterval)
                {
                    Trace.TraceWarning(
                        "GPS tracking UTC jump ignored; falling back to system UTC (trackingDelta={0} ms, wallDelta={1} ms).",
                        trackingDelta.TotalMilliseconds,
                        wallDelta.TotalMilliseconds);
                    _lastGpsJumpWarningUtc = wallUtc;
                }
                candidate = wallUtc;
            }
        }

        _lastTrackingUtc = candidate;
        _lastWallUtc = wallUtc;
        return candidate;
    }

    private static IReadOnlyList<SatelliteTrackState> PublishSnapshot(IReadOnlyList<SatelliteTrackState> states) =>
        states.Count == 0 ? Array.Empty<SatelliteTrackState>() : states.ToArray();

    private enum LiveTrackingCommandKind
    {
        Reload,
        RefreshSynchronously,
        Drain,
        Shutdown
    }

    private sealed class LiveTrackingCommand
    {
        public LiveTrackingCommand(LiveTrackingCommandKind kind) => Kind = kind;

        public LiveTrackingCommandKind Kind { get; }
        public ManualResetEventSlim? Completed { get; set; }
    }
}
