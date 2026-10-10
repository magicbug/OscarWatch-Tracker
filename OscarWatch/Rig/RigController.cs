using System.Collections.Concurrent;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Radio;
using OscarWatch.Core.Services;
using Serilog;

namespace OscarWatch.Rig;

/// <summary>
/// All rig I/O runs on a dedicated background thread; the UI only enqueues commands and reads status.
/// </summary>
public sealed class RigController : IRigController, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<RigController>();
    /// <summary>Ring buffer of recent Main dial reads used to detect operator movement.</summary>
    private const int DialHistoryLength = 8;
    /// <summary>After operator moves the Main dial, defer Sub (uplink) CAT so brief pauses while scanning do not select Sub.</summary>
    private static int InteractiveSubWriteCooldownMs(RigSettings settings) =>
        InteractiveDialResumePolicy.ResolveUplinkResumeMs(settings.InteractiveUplinkResumeMs);

    /// <summary>
    /// When Main still matches the last CAT receive write, allow Sub (uplink) CAT immediately —
    /// Doppler moved the target, not the operator (IC-910/9700 post-TCA uplink lag).
    /// </summary>
    private const int InteractiveSubDopplerCooldownMs = 0;
    private const int FmCompanionLegHz = 10;
    /// <summary>After a CAT frequency write, ignore dial stability briefly so reads settle.</summary>
    private const int PostCatWriteDialSettleMs = 350;
    /// <summary>
    /// Flex SmartSDR can push a lagging slice frequency after our CAT write. Treat a dial that still
    /// shows the pre-write frequency as status lag, not a Main-dial passband hunt.
    /// </summary>
    private const int FlexPostWriteStatusLagMs = 2_000;
    private static readonly TimeSpan LoopInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CommandWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DopplerLogSnapshotInterval = TimeSpan.FromSeconds(1);
    private const int ConnectFailureBackoffSeconds = 3;
    /// <summary>Consecutive failed Kenwood FA/FB (or beacon FA) writes before suspending doppler briefly.</summary>
    private const int KenwoodFaFbFailBackoffThreshold = 3;
    private const int KenwoodFaFbFailBackoffMs = 1500;

    private readonly Func<RigSettings, IRigDriver>? _driverFactory;
    private readonly Func<RigEndpointSettings, IRigDriver>? _endpointFactory;
    private readonly IOrbitPropagator? _propagator;
    private readonly ISettingsService? _settingsService;
    private readonly IDopplerPassLogger _dopplerPassLogger;
    private readonly long[] _rxDialHistory = new long[DialHistoryLength];
    private readonly object _statusLock = new();
    private readonly object _workerStartLock = new();

    private BlockingCollection<RigCommand>? _commands;
    private Thread? _worker;
    private int _disposed;
    private volatile bool _shutdownRequested;
    private volatile bool _disconnectRequested;

    private IRigDriver? _driver;
    private IRigDriver? _downlinkDriver;
    private IRigDriver? _uplinkDriver;
    private string? _connectedKey;
    private string? _downlinkConnectedKey;
    private string? _uplinkConnectedKey;
    private string? _passKey;
    private long _lastRigRxHz;
    private long _lastRigTxHz;
    /// <summary>RX frequency immediately before the last successful CAT write (Flex stale-status detection).</summary>
    private long _rxHzBeforeLastCatWrite;
    private long _displayRxHz;
    private long _displayTxHz;
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private DateTime _lastRxWriteUtc = DateTime.MinValue;
    private DateTime _lastTxWriteUtc = DateTime.MinValue;
    private int _thresholdHz;
    private int _knobTuneThresholdHz = KnobTuneCapturePolicy.LinearThresholdHz;
    private bool _interactive;
    private bool _useMainSub;
    private bool _isBeaconOnly;
    private RigVfo _receiveVfo = RigVfo.VfoA;
    private int _rxDialHistoryCount;
    private bool _vfoNotMoving;
    private bool _receiveDialMatchesLastCatWrite;
    private double _passbandDownlinkAdjustKHz;
    private double _passbandUplinkAdjustKHz;
    private RigStatusKind _statusKind = RigStatusKind.None;
    private string? _statusPort;
    private string? _statusDetail;
    private bool _isTracking;
    private int _missingLookAnglesTicks;
    private bool _catUpdatesPaused;
    private bool _passInitPending;
    private double? _lastAppliedCtcssHz;
    private bool? _lastAppliedCtcssSquelch;
    private double _lastContextRxOffsetKHz;
    private double _lastContextTxOffsetKHz;
    private DopplerStrategy _lastContextDopplerStrategy = DopplerStrategy.Full;
    private bool _forceFrequencyApply;
    private bool _blockKnobCapture;
    /// <summary>FT4 modem holds CAT Doppler between explicit slot-boundary steps.</summary>
    private bool _ft4SlotGatedDoppler;
    private bool _ft4ForceDopplerStep;
    /// <summary>True while a forced FT4 step should write frequencies and skip the dial read.</summary>
    private bool _ft4DirectDopplerStep;
    /// <summary>When set, the forced step uses range rate at this instant rather than "now".</summary>
    private DateTime? _ft4DopplerInstantUtc;
    /// <summary>Set when CAT PTT (or handshake PTT) interrupts a Doppler write. Skip further CI-V.</summary>
    private bool _dopplerPreempted;
    private readonly object _urgentLock = new();
    private const int UrgentNone = 0;
    private const int UrgentPttOn = 1;
    private const int UrgentPttOff = 2;
    private const int UrgentHandshake = 3;
    private int _urgentKind;
    private int _urgentGeneration;
    private int _appliedUrgentGeneration;
    private bool _urgentHandshakeUseRts;
    private bool _urgentHandshakeAssert;
    private DateTime _ignoreDialUntilUtc = DateTime.MinValue;
    private DateTime _lastDialChangeUtc = DateTime.MinValue;
    /// <summary>When the receive dial last became still (or first sampled). MinValue means not yet observed.</summary>
    private DateTime _dialStableSinceUtc = DateTime.MinValue;
    private int _identicalDialSampleCount;
    private DateTime _suspendDopplerUntilUtc = DateTime.MinValue;
    /// <summary>Kenwood-only: after consecutive FA/FB rejects, block further Doppler writes (including force/offset).</summary>
    private DateTime _kenwoodFaFbBackoffUntilUtc = DateTime.MinValue;
    private int _kenwoodFaFbFailCount;
    private DateTime _suspendConnectUntilUtc = DateTime.MinValue;
    private string? _lastConnectError;
    private string? _identityMismatchDevice;
    private volatile bool _forceIdentityRecheck;
    private SerialPortConnectErrorKind _lastConnectErrorKind = SerialPortConnectErrorKind.None;
    private string? _lastConnectErrorPort;
    private string? _lastConnectEndpoint;
    private bool? _lastPassDownlinkOnVhf;
    /// <summary>Last Flex/sat pass identity (<see cref="SatelliteTrackState.Name"/>); used to force pan rebind on sat change.</summary>
    private string? _lastPassSatelliteKey;
    private DateTime _lastDopplerLogUtc = DateTime.MinValue;
    private string? _flexSatelliteSetupError;
    /// <summary>Prior loop horizon state; used to detect orbital AOS (below → above 0°).</summary>
    private bool? _wasAboveHorizon;

    /// <summary>
    /// Consecutive publishes without look angles before clearing Doppler tracking.
    /// Matches rotator / pass-recording grace so a single propagation miss does not flap CAT.
    /// </summary>
    internal const int MissingLookAnglesClearTicks = 3;

    private RigSettings _cachedSettings = new();
    private RigTrackingContext? _cachedContext;
    private bool? _cachedCatPausedOverride;
    private RigConnectionStatus _status = new(false, false, RigStatusKind.None, null, null, null, null, false, 0, 0);

    public RigController(
        Func<RigSettings, IRigDriver>? driverFactory = null,
        Func<RigEndpointSettings, IRigDriver>? endpointFactory = null,
        IOrbitPropagator? propagator = null,
        ISettingsService? settingsService = null,
        IDopplerPassLogger? dopplerPassLogger = null)
    {
        _driverFactory = driverFactory;
        _endpointFactory = endpointFactory;
        _propagator = propagator;
        _settingsService = settingsService;
        _dopplerPassLogger = dopplerPassLogger ?? NullDopplerPassLogger.Instance;
    }

    public RigConnectionStatus GetStatus()
    {
        lock (_statusLock)
            return _status;
    }

    /// <summary>Enqueue latest pass/settings for the rig thread (~1–4 Hz from UI).</summary>
    public void PublishContext(RigSettings settings, RigTrackingContext? context, bool reinitializePass = false, bool? catPausedOverride = null) =>
        Enqueue(new RigCommand(RigCommandKind.PublishContext, settings, context, reinitializePass, catPausedOverride));

    /// <summary>Runs one doppler iteration on the rig thread (unit tests).</summary>
    public void RunTrackingLoopOnce() =>
        EnqueueAndWait(new RigCommand(RigCommandKind.RunTrackingLoopOnce));

    /// <summary>Synchronous publish + doppler tick (unit tests).</summary>
    public void Update(RigSettings settings, RigTrackingContext? context) =>
        EnqueueAndWait(new RigCommand(RigCommandKind.UpdateSynchronously, settings, context));

    public void ApplySelectedCtcss(RigSettings settings, RigTrackingContext? context)
    {
        if (context is null)
            return;

        Enqueue(new RigCommand(RigCommandKind.ApplySelectedCtcss, settings, context));
    }

    public void SetPtt(bool transmit)
    {
        var command = new RigCommand(RigCommandKind.SetPtt, pttTransmit: transmit);
        lock (_urgentLock)
        {
            command.Generation = ++_urgentGeneration;
            _urgentKind = transmit ? UrgentPttOn : UrgentPttOff;
        }

        Enqueue(command);
    }

    public void SetHandshakePtt(bool useRts, bool assert)
    {
        var command = new RigCommand(
            RigCommandKind.SetHandshakePtt,
            handshakeUseRts: useRts,
            handshakeAssert: assert);
        lock (_urgentLock)
        {
            command.Generation = ++_urgentGeneration;
            _urgentKind = UrgentHandshake;
            _urgentHandshakeUseRts = useRts;
            _urgentHandshakeAssert = assert;
        }

        Enqueue(command);
    }

    public void SetFt4SlotGatedDoppler(bool hold) =>
        Enqueue(new RigCommand(RigCommandKind.SetFt4SlotGatedDoppler, ft4HoldDoppler: hold));

    public void ForceFt4DopplerStep() =>
        Enqueue(new RigCommand(RigCommandKind.ForceFt4DopplerStep));

    public bool TryForceFt4DopplerStep(DateTime frequencyAtUtc, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return false;

        using var done = new ManualResetEventSlim(false);
        var command = new RigCommand(RigCommandKind.ForceFt4DopplerStep)
        {
            Completed = done,
            DopplerAtUtc = frequencyAtUtc
        };
        try
        {
            Enqueue(command);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return done.Wait(timeout);
    }

    public bool TryGetUplinkRfPowerWatts(out double watts)
    {
        watts = 0;
        var command = new RigCommand(RigCommandKind.ReadUplinkRfPower);
        try
        {
            EnqueueAndWait(command, TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException ex)
        {
            Log.Debug(ex, "RF power read timed out");
            return false;
        }

        if (command.RfPowerWatts is not { } value)
            return false;

        watts = value;
        return true;
    }

    public bool TrySetUplinkRfPowerWatts(double watts)
    {
        var command = new RigCommand(RigCommandKind.SetUplinkRfPower)
        {
            RfPowerWatts = watts
        };
        try
        {
            EnqueueAndWait(command, TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException ex)
        {
            Log.Debug(ex, "RF power set timed out");
            return false;
        }

        return command.Succeeded;
    }

    public void RequireIdentityRecheck()
    {
        _forceIdentityRecheck = true;
        _suspendConnectUntilUtc = DateTime.MinValue;
    }

    public void Disconnect()
    {
        _disconnectRequested = true;
        Enqueue(new RigCommand(RigCommandKind.Disconnect));
    }

    /// <summary>Disconnect and block until the rig worker has torn down drivers and cleared tracking state.</summary>
    public void DisconnectAndWait()
    {
        _disconnectRequested = true;
        EnqueueAndWait(new RigCommand(RigCommandKind.Disconnect), TimeSpan.FromSeconds(30));
    }

    /// <summary>Blocks until queued commands are processed (unit tests).</summary>
    internal void DrainCommandQueueForTests() =>
        EnqueueAndWait(new RigCommand(RigCommandKind.Drain));

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
                var command = new RigCommand(RigCommandKind.Shutdown) { Completed = done };
                _commands.Add(command);
                if (!done.Wait(TimeSpan.FromSeconds(3)))
                    Log.Warning("Rig worker shutdown did not complete in time");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Rig worker shutdown did not complete cleanly");
        }

        _worker?.Join(TimeSpan.FromSeconds(2));
        _commands?.Dispose();
        _commands = null;
    }

    private void Enqueue(RigCommand command)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        EnsureWorker();
        _commands!.Add(command);
    }

    private void EnqueueAndWait(RigCommand command, TimeSpan? timeout = null)
    {
        using var done = new ManualResetEventSlim(false);
        command.Completed = done;
        Enqueue(command);
        if (!done.Wait(timeout ?? CommandWaitTimeout))
            throw new TimeoutException("Rig worker did not complete the command in time.");
    }

    private void EnsureWorker()
    {
        lock (_workerStartLock)
        {
            if (_worker is { IsAlive: true })
                return;

            _shutdownRequested = false;
            _commands = new BlockingCollection<RigCommand>();
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "OscarWatch.Rig"
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
                if (_commands!.TryTake(out var command, LoopInterval))
                {
                    ProcessCommand(command);
                    try
                    {
                        DrainPendingCommands();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Rig worker drain failed; continuing");
                    }
                }

                if (_shutdownRequested)
                    break;

                try
                {
                    RunLoopIteration(ignoreDopplerSuspend: false);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Rig tracking loop iteration failed; continuing");
                }

                RefreshStatusSnapshot();
            }
        }
        finally
        {
            TearDownRig();
            RefreshStatusSnapshot();
        }
    }

    private void DrainPendingCommands()
    {
        while (_commands!.TryTake(out var command, 0))
            ProcessCommand(command);
    }

    private void ProcessCommand(RigCommand command)
    {
        try
        {
            switch (command.Kind)
            {
                case RigCommandKind.PublishContext:
                    _cachedSettings = command.Settings;
                    _cachedCatPausedOverride = command.CatPausedOverride;
                    ApplyPublishState(_cachedSettings, command.Context, command.ReinitializePass, command.CatPausedOverride);
                    if (!command.ReinitializePass && _forceFrequencyApply)
                        RunLoopIteration(ignoreDopplerSuspend: true);
                    break;

                case RigCommandKind.UpdateSynchronously:
                    _cachedSettings = command.Settings;
                    _cachedCatPausedOverride = command.CatPausedOverride;
                    ApplyPublishState(_cachedSettings, command.Context, catPausedOverride: command.CatPausedOverride);
                    RunLoopIteration(ignoreDopplerSuspend: true);
                    break;

                case RigCommandKind.RunTrackingLoopOnce:
                    RunLoopIteration(ignoreDopplerSuspend: true);
                    break;

                case RigCommandKind.ApplySelectedCtcss:
                    ApplySelectedCtcssOnWorker(command.Settings, command.Context!);
                    break;

                case RigCommandKind.SetPtt:
                case RigCommandKind.SetHandshakePtt:
                    TryConsumeUrgentRigAction();
                    if (command.Generation > _appliedUrgentGeneration)
                    {
                        ApplyQueuedRigAction(command);
                        _appliedUrgentGeneration = command.Generation;
                    }

                    break;

                case RigCommandKind.SetFt4SlotGatedDoppler:
                    _ft4SlotGatedDoppler = command.Ft4HoldDoppler;
                    if (!_ft4SlotGatedDoppler)
                        _ft4ForceDopplerStep = false;
                    Log.Information("FT4 slot-gated Doppler hold={Hold}", _ft4SlotGatedDoppler);
                    break;

                case RigCommandKind.ForceFt4DopplerStep:
                    if (_ft4SlotGatedDoppler)
                    {
                        _ft4ForceDopplerStep = true;
                        _forceFrequencyApply = true;
                        _ft4DirectDopplerStep = true;
                        _ft4DopplerInstantUtc = command.DopplerAtUtc;
                        try
                        {
                            RunLoopIteration(ignoreDopplerSuspend: true);
                        }
                        finally
                        {
                            _ft4DirectDopplerStep = false;
                            _ft4DopplerInstantUtc = null;
                        }
                    }
                    break;

                case RigCommandKind.ReadUplinkRfPower:
                    command.RfPowerWatts = TryReadUplinkRfPowerWattsOnWorker();
                    break;

                case RigCommandKind.SetUplinkRfPower:
                    command.Succeeded = command.RfPowerWatts is { } target
                        && TrySetUplinkRfPowerWattsOnWorker(target);
                    break;

                case RigCommandKind.Disconnect:
                    _cachedContext = null;
                    TearDownRig();
                    ResetTrackingState();
                    _suspendConnectUntilUtc = DateTime.MinValue;
                    _disconnectRequested = false;
                    break;

                case RigCommandKind.Drain:
                    break;

                case RigCommandKind.Shutdown:
                    _shutdownRequested = true;
                    break;
            }
        }
        catch (FlexSatelliteSetupException ex)
        {
            _isTracking = false;
            _passInitPending = false;
            _flexSatelliteSetupError = ex.Message;
            SetRigStatus(RigStatusKind.FlexControlFailed, detail: ex.Message);
            Log.Warning(ex, "FlexRadio satellite setup failed while processing {Kind}", command.Kind);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Rig worker failed processing {Kind}", command.Kind);
        }
        finally
        {
            RefreshStatusSnapshot();
            try
            {
                command.Completed?.Set();
            }
            catch (ObjectDisposedException)
            {
                // The waiter timed out and released the event. The command itself has finished.
            }
        }
    }

    private void ApplySelectedCtcssOnWorker(RigSettings settings, RigTrackingContext context)
    {
        if (!RigIsConfigured(settings))
            return;

        if (!HasRequiredPorts(settings))
            return;

        if (!EnsureConnected(settings))
            return;

        if (context.SelectedCtcssHz is not { } hz || hz <= 0 || context.Mode.IsBeaconOnly)
            return;

        if (!settings.DualRadioEnabled)
            _useMainSub = ShouldUseMainSubLayout(settings, context);

        ApplyCtcss(settings, context, force: true);
        RestoreOperatorVfo();
    }

    private void RefreshStatusSnapshot()
    {
        var snapshot = new RigConnectionStatus(
            IsRigConnected(),
            _isTracking,
            _statusKind,
            _statusPort,
            _statusDetail,
            DisplayHz(_displayRxHz, _lastRigRxHz),
            DisplayHz(_displayTxHz, _lastRigTxHz),
            _catUpdatesPaused,
            _passbandDownlinkAdjustKHz,
            _passbandUplinkAdjustKHz);

        lock (_statusLock)
            _status = snapshot;
    }

    private void ApplyPublishState(RigSettings settings, RigTrackingContext? context, bool reinitializePass = false, bool? catPausedOverride = null)
    {
        if (!RigIsConfigured(settings))
        {
            TearDownRig();
            SetRigStatus(RigStatusKind.None);
            return;
        }

        if (!HasRequiredPorts(settings))
        {
            TearDownRig();
            SetRigStatus(settings.DualRadioEnabled
                ? RigStatusKind.SelectDualComPorts
                : RigStatusKind.NoComPort);
            return;
        }

        if (!EnsureConnected(settings))
        {
            SetRigStatus(DescribeConnectionFailure(settings));
            return;
        }

        var effectivePaused = catPausedOverride ?? settings.CatUpdatesPaused;
        var wasPaused = _catUpdatesPaused;
        var resumingFromCatPause = wasPaused && !effectivePaused;
        _catUpdatesPaused = effectivePaused;

        if (!TryResolveTrackingContext(ref context))
        {
            EndDopplerPassLog("context_cleared");
            _isTracking = false;
            _cachedContext = null;
            SetRigStatus(effectivePaused ? RigStatusKind.CatPaused : RigStatusKind.Connected);
            return;
        }

        // Non-null look angles guaranteed by TryResolveTrackingContext.
        var resolved = context!;
        SyncDisplayFrequencies(ComputeDoppler(resolved));

        _isBeaconOnly = resolved.Mode.IsBeaconOnly;

        if (!SupportsTracking())
            return;

        var newPassKey = PassKey(resolved);
        var passKeyChanged = !string.Equals(_passKey, newPassKey, StringComparison.Ordinal);
        if (passKeyChanged || reinitializePass || resumingFromCatPause)
            _flexSatelliteSetupError = null;

        if (passKeyChanged)
            BeginNewPass(settings, resolved, newPassKey, effectivePaused);

        if (_flexSatelliteSetupError is not null)
        {
            _isTracking = false;
            SetRigStatus(RigStatusKind.FlexControlFailed, detail: _flexSatelliteSetupError);
            return;
        }

        if (effectivePaused)
        {
            _isTracking = false;
            if (!wasPaused)
            {
                if (settings.Type == RigType.FlexSmartSdr)
                    Log.Information("FlexRadio CAT updates paused; Doppler control is suspended");
                LogDopplerPauseTransition(settings, resolved, "cat_pause_start");
            }
            SetRigStatus(RigStatusKind.CatPaused);
            return;
        }

        if (resumingFromCatPause)
        {
            if (settings.Type == RigType.FlexSmartSdr)
                Log.Information("FlexRadio CAT updates resumed; reinitialising satellite control");
            LogDopplerPauseTransition(settings, resolved, "cat_pause_end");
        }

        if (resumingFromCatPause || _passInitPending)
        {
            RunPassInit(settings, resolved);
            _passInitPending = false;
        }
        else if (reinitializePass && !passKeyChanged)
            RunPassInit(settings, resolved);
        else if (resolved.SelectedCtcssHz is > 0)
            ApplyCtcss(settings, resolved, force: false);

        NoteContextOffsetChange(resolved);
        NoteContextDopplerStrategyChange(resolved);
        _isTracking = true;
        SetRigStatus(ResolveTrackingStatusKind(settings));
        TryClearPassbandOnOrbitalAos(settings, resolved);
        UpdateDopplerPassLogHorizon(settings, resolved);
    }

    /// <summary>
    /// Accepts a fresh context with look angles, or holds the last good context for a few
    /// missing-angle publishes so Doppler CAT does not flap on a single propagation miss.
    /// </summary>
    /// <returns>True when <paramref name="context"/> is usable (non-null look angles).</returns>
    private bool TryResolveTrackingContext(ref RigTrackingContext? context)
    {
        if (context?.TrackState.LookAngles is not null)
        {
            _missingLookAnglesTicks = 0;
            _cachedContext = context;
            return true;
        }

        if (_isTracking
            && _cachedContext?.TrackState.LookAngles is not null
            && _missingLookAnglesTicks + 1 < MissingLookAnglesClearTicks)
        {
            _missingLookAnglesTicks++;
            context = _cachedContext;
            return true;
        }

        _missingLookAnglesTicks = 0;
        return false;
    }

    private RigStatusKind ResolveTrackingStatusKind(RigSettings settings) =>
        IsKenwoodSatlUnconfirmed(settings)
            ? RigStatusKind.Ts2000SatlUnconfirmed
            : RigStatusKind.Tracking;

    private bool IsKenwoodSatlUnconfirmed(RigSettings settings) =>
        settings.Type == RigType.KenwoodTs2000
        && _useMainSub
        && _driver is KenwoodTs2000Driver kenwood
        && kenwood.UsesFaFbSatelliteTracking
        && !kenwood.IsSatelliteModeActive;

    private void SetRigStatus(RigStatusKind kind, string? port = null, string? detail = null)
    {
        _statusKind = kind;
        _statusPort = port;
        _statusDetail = detail;
    }

    private void SetRigStatus((RigStatusKind Kind, string? Port, string? Detail) status) =>
        SetRigStatus(status.Kind, status.Port, status.Detail);

    private void BeginNewPass(RigSettings settings, RigTrackingContext context, string newPassKey, bool effectivePaused)
    {
        EndDopplerPassLog("pass_changed");
        _passKey = newPassKey;
        _passbandDownlinkAdjustKHz = 0;
        _passbandUplinkAdjustKHz = 0;
        _wasAboveHorizon = context.TrackState.LookAngles is null ? null : IsAboveHorizon(context);
        ClearDialHistory();
        _lastAppliedCtcssHz = null;
        _lastAppliedCtcssSquelch = null;
        _flexSatelliteSetupError = null;
        _lastContextRxOffsetKHz = context.ReceiveOffsetKHz;
        _lastContextTxOffsetKHz = context.TransmitOffsetKHz;
        _lastContextDopplerStrategy = context.DopplerStrategy;
        _forceFrequencyApply = false;

        if (effectivePaused)
            _passInitPending = true;
        else
            RunPassInit(settings, context);
    }

    private void TearDownRig()
    {
        _driver?.Dispose();
        _driver = null;
        _downlinkDriver?.Dispose();
        _downlinkDriver = null;
        _uplinkDriver?.Dispose();
        _uplinkDriver = null;
        _connectedKey = null;
        _downlinkConnectedKey = null;
        _uplinkConnectedKey = null;
        ResetTrackingState();
    }

    private void ResetTrackingState()
    {
        EndDopplerPassLog("tracking_reset");
        _passKey = null;
        _wasAboveHorizon = null;
        _passbandDownlinkAdjustKHz = 0;
        _passbandUplinkAdjustKHz = 0;
        _lastRigRxHz = 0;
        _lastRigTxHz = 0;
        _rxHzBeforeLastCatWrite = 0;
        _displayRxHz = 0;
        _displayTxHz = 0;
        _isTracking = false;
        _missingLookAnglesTicks = 0;
        ClearDialHistory();
        _passInitPending = false;
        _catUpdatesPaused = false;
        _cachedCatPausedOverride = null;
        _flexSatelliteSetupError = null;
        _lastAppliedCtcssHz = null;
        _lastAppliedCtcssSquelch = null;
        _lastPassDownlinkOnVhf = null;
        _lastPassSatelliteKey = null;
        _receiveVfo = RigVfo.VfoA;
        _suspendDopplerUntilUtc = DateTime.MinValue;
        _kenwoodFaFbBackoffUntilUtc = DateTime.MinValue;
        _kenwoodFaFbFailCount = 0;
        // The hold belongs to the FT4 session, which does not resend it after a reconnect.
        _ft4ForceDopplerStep = false;
    }

    private void RunLoopIteration(bool ignoreDopplerSuspend = false)
    {
        if (!IsRigConnected() || _cachedContext is null)
            return;

        TryLogOperationalSnapshot();

        if (!_cachedSettings.Enabled || (_cachedCatPausedOverride ?? _cachedSettings.CatUpdatesPaused) || !_isTracking)
            return;

        if (!ignoreDopplerSuspend && DateTime.UtcNow < _suspendDopplerUntilUtc)
        {
            TryLogDopplerSuspendSnapshot();
            return;
        }

        // OrbitDeck-style FT4: hold the dial within a slot; only write on ForceFt4DopplerStep.
        if (_ft4SlotGatedDoppler && !_ft4ForceDopplerStep)
            return;
        var directStep = _ft4DirectDopplerStep;
        _ft4ForceDopplerStep = false;
        _dopplerPreempted = false;

        // CAT PTT shares this thread. Send it before a frequency write so a slow
        // CI-V step cannot keep the radio in receive for the first seconds of an FT4 slot.
        if (TryConsumeUrgentRigAction(duringDoppler: true))
            return;

        if (_cachedContext.TrackState.LookAngles is null)
            return;

        if (directStep)
        {
            // Skip the dial read and the second VFO select. Those extra CI-V turns are what
            // pushed IC-9700 CAT PTT several seconds into the FT4 transmit slot.
            WriteDopplerFrequencies(_cachedSettings, _cachedContext);
            TryLogPeriodicSnapshot(_cachedSettings, _cachedContext);
            return;
        }

        TryClearPassbandOnOrbitalAos(_cachedSettings, _cachedContext);

        if (_interactive && SetupVfosPolicy.IsLinearMode(_cachedContext.Mode.DownlinkMode))
        {
            if (_cachedSettings.DualRadioEnabled)
                ProcessInteractiveLinearDual(_cachedSettings, _cachedContext);
            else
                ProcessInteractiveLinear(_cachedSettings, _cachedContext);
        }
        else
            ProcessAutomaticDoppler(_cachedSettings, _cachedContext);

        TrySendKenwoodSatelliteLinkHoldPoll();
        TryLogPeriodicSnapshot(_cachedSettings, _cachedContext);
    }

    private void TrySendKenwoodSatelliteLinkHoldPoll()
    {
        if (_cachedSettings.Type != RigType.KenwoodTs2000
            || !_useMainSub
            || _driver is not KenwoodTs2000Driver kenwood
            || !kenwood.UsesFaFbSatelliteTracking)
        {
            return;
        }

        kenwood.SendSatelliteLinkHoldPollIfDue();
    }

    private void ProcessInteractiveLinear(RigSettings settings, RigTrackingContext context)
    {
        SampleReceiveDial();

        if (ShouldTrackDopplerAutomatically(context))
        {
            // Still run passband sync so phantom trim clears when Main returns to the pure Doppler baseline.
            SyncManualFromMainDial(context);
            ProcessAutomaticDoppler(settings, context);
            return;
        }

        SyncManualFromMainDial(context);

        if (!_vfoNotMoving)
        {
            // Offset / strategy changes must apply immediately; failed doppler retries wait for a stable dial.
            if (!_forceFrequencyApply || !_blockKnobCapture)
                return;

            if (WriteDopplerFrequencies(settings, context))
                RestoreOperatorVfo();
            return;
        }

        if (WriteDopplerFrequencies(settings, context))
            RestoreOperatorVfo();
    }

    /// <summary>
    /// Dual linear: downlink dial sets NOR/REV passband trim on both legs (same as single radio);
    /// while RX dial moves, hold downlink CAT but keep writing uplink Doppler + trim.
    /// </summary>
    private void ProcessInteractiveLinearDual(RigSettings settings, RigTrackingContext context)
    {
        SampleReceiveDial();

        if (ShouldTrackDopplerAutomatically(context))
        {
            // Still run passband sync so phantom trim clears when Main returns to the pure Doppler baseline.
            SyncManualFromMainDial(context);
            ProcessAutomaticDoppler(settings, context);
            return;
        }

        SyncManualFromMainDial(context);

        if (!_vfoNotMoving)
        {
            if (!_forceFrequencyApply || !_blockKnobCapture)
            {
                WriteDopplerFrequencies(settings, context, holdDownlinkCatWrites: true);
                return;
            }

            if (WriteDopplerFrequencies(settings, context))
                RestoreOperatorVfo();
            return;
        }

        if (WriteDopplerFrequencies(settings, context))
            RestoreOperatorVfo();
    }

    /// <summary>
    /// Hands-off linear passes: when Main still shows the last CAT RX write, track doppler every loop
    /// instead of waiting for dial-stability (which our own writes reset). Passband trim from an earlier
    /// Main hunt may stay non-zero; that only records where the operator listens, not active tuning.
    /// </summary>
    private bool ShouldTrackDopplerAutomatically(RigTrackingContext context)
    {
        if (_lastRigRxHz <= 0)
            return false;

        if (!TryReadReceiveDialHz(out var dialHz))
            return false;

        if (DateTime.UtcNow < _ignoreDialUntilUtc)
            return DialMatchesLastCatWrite(dialHz, KnobTuneThresholdHz());

        if (!_vfoNotMoving)
            return false;

        return DialMatchesLastCatWrite(dialHz, AutomaticDialMatchToleranceHz());
    }

    /// <summary>Match window for CAT-only tracking: within doppler threshold so display jitter is not passband trim.</summary>
    private int AutomaticDialMatchToleranceHz() =>
        _thresholdHz > 0 ? Math.Max(KnobTuneThresholdHz(), _thresholdHz) : KnobTuneThresholdHz();

    private string ResolveDialTrackingMode(RigTrackingContext context) =>
        DopplerDialTrackingMode.Resolve(
            _interactive,
            _interactive && ShouldTrackDopplerAutomatically(context),
            _vfoNotMoving);

    private bool DialMatchesLastCatWrite(long dialHz, int toleranceHz) =>
        _lastRigRxHz > 0 && Math.Abs(dialHz - _lastRigRxHz) < toleranceHz;

    private bool HasNeutralPassbandTrim() =>
        Math.Abs(_passbandDownlinkAdjustKHz) < 0.0001 && Math.Abs(_passbandUplinkAdjustKHz) < 0.0001;

    /// <summary>
    /// Largest manual passband trim (per leg) that knob tuning can add. Larger values mean the
    /// dial reading is not a real tune, and the trim would push the CAT frequency out of range.
    /// </summary>
    internal const double PassbandTrimLimitKHz = 100.0;

    internal static double ClampPassbandTrimKHz(double trimKHz) =>
        Math.Clamp(trimKHz, -PassbandTrimLimitKHz, PassbandTrimLimitKHz);

    private void ProcessAutomaticDoppler(RigSettings settings, RigTrackingContext context)
    {
        if (WriteDopplerFrequencies(settings, context))
            RestoreOperatorVfo();
    }

    /// <summary>
    /// Derive manual RX/TX adjust from Main dial vs doppler baseline (not vs last CAT write).
    /// Clears phantom manual state when the dial matches the computed target.
    /// </summary>
    private void SyncManualFromMainDial(RigTrackingContext context)
    {
        if (_blockKnobCapture || DateTime.UtcNow < _ignoreDialUntilUtc || !_vfoNotMoving
            || context.TrackState.LookAngles is null)
            return;

        RxDriver()?.SelectVfo(ReceiveVfo(), force: true);
        if (!TryReadReceiveDialHz(out var dialHz))
            return;

        // Lagging SmartSDR status after an offset/Doppler write must not become passband trim.
        if (IsStalePostWriteDialEcho(dialHz))
            return;

        var (rxRangeRate, txRangeRate) = ResolveRangeRatesForDoppler(context);
        var baseline = DopplerFrequencyCalculator.Compute(
            context.Mode,
            rxRangeRate,
            context.ReceiveOffsetKHz,
            context.TransmitOffsetKHz,
            _passbandDownlinkAdjustKHz,
            _passbandUplinkAdjustKHz,
            context.DopplerStrategy,
            txRangeRate);
        var pureBaselineHz = ToHz(DopplerFrequencyCalculator.Compute(
            context.Mode,
            rxRangeRate,
            context.ReceiveOffsetKHz,
            context.TransmitOffsetKHz,
            0,
            0,
            context.DopplerStrategy,
            txRangeRate).RadioReceiveKHz);
        var expectedMainHz = ToHz(baseline.RadioReceiveKHz);
        var deltaFromBaselineHz = dialHz - expectedMainHz;
        var threshold = KnobTuneThresholdHz();

        if (Math.Abs(dialHz - pureBaselineHz) < threshold
            && (Math.Abs(_passbandDownlinkAdjustKHz) > 0.0001
                || Math.Abs(_passbandUplinkAdjustKHz) > 0.0001))
        {
            _passbandDownlinkAdjustKHz = 0;
            _passbandUplinkAdjustKHz = 0;
            _forceFrequencyApply = true;
            return;
        }

        if (Math.Abs(deltaFromBaselineHz) < threshold)
            return;

        // Dial still matches the last CAT write — baseline moved (doppler lag), not a knob tune.
        if (_lastRigRxHz > 0 && Math.Abs(dialHz - _lastRigRxHz) < threshold)
            return;

        var deltaKhz = deltaFromBaselineHz / 1000.0;
        double newDown;
        double newUp;
        if (context.Mode.DopplerCorrection == DopplerCorrection.Reverse)
        {
            // REV: Main dial up → downlink nominal up, uplink nominal down (single and dual radio).
            newDown = _passbandDownlinkAdjustKHz + deltaKhz;
            newUp = _passbandUplinkAdjustKHz - deltaKhz;
        }
        else
        {
            // NOR: both nominals move with Main dial (single and dual radio).
            newDown = _passbandDownlinkAdjustKHz + deltaKhz;
            newUp = _passbandUplinkAdjustKHz + deltaKhz;
        }

        newDown = ClampPassbandTrimKHz(newDown);
        newUp = ClampPassbandTrimKHz(newUp);

        if (NearlyEqual(newDown, _passbandDownlinkAdjustKHz) && NearlyEqual(newUp, _passbandUplinkAdjustKHz))
            return;

        _passbandDownlinkAdjustKHz = newDown;
        _passbandUplinkAdjustKHz = newUp;
        if (KnobTuneCapturePolicy.UsesImmediateStatusCapture(_cachedSettings.Type))
        {
            Log.Information(
                "FlexRadio manual passband tune captured: dialHz={DialHz}, deltaKHz={DeltaKHz:F3}, downlinkAdjustKHz={DownlinkAdjustKHz:F3}, uplinkAdjustKHz={UplinkAdjustKHz:F3}",
                dialHz,
                deltaKhz,
                newDown,
                newUp);
        }
        LogDopplerEvent(
            _cachedSettings,
            context,
            ComputeDoppler(context),
            _thresholdHz,
            ResolveWriteThresholdHz(_cachedSettings, context),
            "passband_knob",
            notes: $"delta_khz={deltaKhz:0.000}");
        SeedDialHistoryStable(dialHz);
        _vfoNotMoving = true;
    }

    private void SeedDialHistoryStable(long dialHz)
    {
        for (var i = 0; i < DialHistoryLength; i++)
            _rxDialHistory[i] = dialHz;

        _rxDialHistoryCount = DialHistoryLength;
        var settleMs = InteractiveDialResumePolicy.ResolveSettleMs(_cachedSettings.InteractiveDialSettleMs);
        _dialStableSinceUtc = DateTime.UtcNow.AddMilliseconds(-settleMs);
        _identicalDialSampleCount = Math.Max(2, (settleMs + 99) / 100);
    }

    private int KnobTuneThresholdHz() => _knobTuneThresholdHz;

    private void SampleReceiveDial()
    {
        if (!TryReadReceiveDialHz(out var dialHz))
        {
            _vfoNotMoving = false;
            _receiveDialMatchesLastCatWrite = false;
            return;
        }

        _receiveDialMatchesLastCatWrite = _lastRigRxHz > 0
            && DialMatchesLastCatWrite(dialHz, AutomaticDialMatchToleranceHz());

        // SmartSDR pushes external slice tuning to our cache. Unlike serial CAT polling, a changed
        // Flex frequency is already an authoritative operator action, so capture it immediately
        // instead of waiting for the dial-settle timer while Doppler can pull the slice back.
        // Never do that during post-CAT settle / offset block, and never treat a lagging pre-write
        // frequency as a hunt (that was snapping RS-44 ~tens of kHz after offset clicks).
        if (KnobTuneCapturePolicy.UsesImmediateStatusCapture(_cachedSettings.Type)
            && !_blockKnobCapture
            && DateTime.UtcNow >= _ignoreDialUntilUtc
            && IsOperatorDialMovement(dialHz))
        {
            if (IsStalePostWriteDialEcho(dialHz))
            {
                MarkProgrammaticFrequencySettle();
                _forceFrequencyApply = true;
                _vfoNotMoving = false;
                return;
            }

            _lastDialChangeUtc = DateTime.UtcNow;
            SeedDialHistoryStable(dialHz);
            _vfoNotMoving = true;
            return;
        }

        if (DateTime.UtcNow < _ignoreDialUntilUtc)
        {
            if (DialMatchesLastCatWrite(dialHz, KnobTuneThresholdHz())
                && _rxDialHistoryCount > 0
                && _rxDialHistory[Math.Min(_rxDialHistoryCount, DialHistoryLength) - 1] == _lastRigRxHz)
            {
                _vfoNotMoving = true;
                return;
            }

            ShiftDialHistory(dialHz);
            _vfoNotMoving = IsDialHistoryStable();
            return;
        }

        ShiftDialHistory(dialHz);
        _vfoNotMoving = IsDialHistoryStable();
    }

    private bool IsDialHistoryStable()
    {
        if (_rxDialHistoryCount < 2)
            return false;

        return InteractiveDialResumePolicy.IsDialSettled(
            _dialStableSinceUtc,
            DateTime.UtcNow,
            _cachedSettings.InteractiveDialSettleMs,
            _identicalDialSampleCount,
            (int)LoopInterval.TotalMilliseconds);
    }

    private bool CanWriteInteractiveSub()
    {
        var cooldownMs = _vfoNotMoving && _receiveDialMatchesLastCatWrite
            ? InteractiveSubDopplerCooldownMs
            : InteractiveSubWriteCooldownMs(_cachedSettings);
        return (DateTime.UtcNow - _lastDialChangeUtc).TotalMilliseconds >= cooldownMs;
    }

    private void ShiftDialHistory(long dialHz)
    {
        if (_rxDialHistoryCount > 0)
        {
            var previous = _rxDialHistory[Math.Min(_rxDialHistoryCount, DialHistoryLength) - 1];
            if (previous != dialHz)
            {
                _identicalDialSampleCount = 1;
                _dialStableSinceUtc = DateTime.UtcNow;
                if (DateTime.UtcNow >= _ignoreDialUntilUtc && IsOperatorDialMovement(dialHz))
                    _lastDialChangeUtc = DateTime.UtcNow;
            }
            else
            {
                _identicalDialSampleCount++;
            }
        }
        else
        {
            _dialStableSinceUtc = DateTime.UtcNow;
            _identicalDialSampleCount = 1;
        }

        if (_rxDialHistoryCount < DialHistoryLength)
        {
            _rxDialHistory[_rxDialHistoryCount++] = dialHz;
            return;
        }

        for (var i = 0; i < DialHistoryLength - 1; i++)
            _rxDialHistory[i] = _rxDialHistory[i + 1];

        _rxDialHistory[DialHistoryLength - 1] = dialHz;
    }

    private void ClearDialHistory()
    {
        _rxDialHistoryCount = 0;
        _vfoNotMoving = false;
        _receiveDialMatchesLastCatWrite = false;
        _lastDialChangeUtc = DateTime.MinValue;
        _dialStableSinceUtc = DateTime.MinValue;
        _identicalDialSampleCount = 0;
        Array.Clear(_rxDialHistory);
    }

    /// <summary>Main dial moved away from the last CAT RX write — operator passband hunt, not Doppler stepping.</summary>
    private bool IsOperatorDialMovement(long dialHz) =>
        _lastRigRxHz <= 0 || Math.Abs(dialHz - _lastRigRxHz) >= KnobTuneThresholdHz();

    /// <summary>
    /// True when Flex status still reports the RX frequency we just tuned away from (lagging status),
    /// rather than a genuine operator Main-dial move to a new spot in the passband.
    /// </summary>
    private bool IsStalePostWriteDialEcho(long dialHz)
    {
        if (!KnobTuneCapturePolicy.UsesImmediateStatusCapture(_cachedSettings.Type))
            return false;

        if (_rxHzBeforeLastCatWrite <= 0 || _lastRigRxHz <= 0 || _lastRxWriteUtc == DateTime.MinValue)
            return false;

        if ((DateTime.UtcNow - _lastRxWriteUtc).TotalMilliseconds > FlexPostWriteStatusLagMs)
            return false;

        if (DialMatchesLastCatWrite(dialHz, KnobTuneThresholdHz()))
            return false;

        return Math.Abs(dialHz - _rxHzBeforeLastCatWrite) < KnobTuneThresholdHz();
    }

    private bool WriteDopplerFrequencies(RigSettings settings, RigTrackingContext context, bool holdDownlinkCatWrites = false)
    {
        var corrected = ComputeDoppler(context);

        SyncDisplayFrequencies(corrected);

        var rxHz = ToHz(corrected.RadioReceiveKHz);
        var txHz = ToHz(corrected.RadioTransmitKHz);

        var forceApply = _forceFrequencyApply;
        _forceFrequencyApply = false;
        var thresholdHz = ResolveWriteThresholdHz(settings, context);
        var strategy = context.DopplerStrategy;
        var catPaused = _cachedCatPausedOverride ?? settings.CatUpdatesPaused;
        if (!forceApply && !ShouldWrite(thresholdHz, rxHz, txHz, strategy))
            return false;

        // Kenwood FA/FB reject storms: honour failure backoff even when offset/force ignores loop settle.
        if (settings.Type == RigType.KenwoodTs2000 && DateTime.UtcNow < _kenwoodFaFbBackoffUntilUtc)
        {
            if (forceApply)
                _forceFrequencyApply = true;
            return false;
        }

        var rxDelta = Math.Abs(rxHz - _lastRigRxHz);
        var txDelta = _isBeaconOnly ? 0 : Math.Abs(txHz - _lastRigTxHz);
        var correctRx = strategy != DopplerStrategy.UplinkOnly;
        var correctTx = !_isBeaconOnly && strategy != DopplerStrategy.DownlinkOnly;
        var writeRx = correctRx && (forceApply || rxDelta > thresholdHz || thresholdHz == 0);
        var writeTx = correctTx && (forceApply || txDelta > thresholdHz || thresholdHz == 0);

        if (holdDownlinkCatWrites)
            writeRx = false;

        // Cross-band: keep RX/TX CAT in sync when either leg triggers (FM automatic, or linear interactive).
        if (!holdDownlinkCatWrites && !forceApply && strategy == DopplerStrategy.Full)
        {
            var crossBand = settings.DualRadioEnabled
                || RigSatModeHelper.UseMainSubLayout(context.Mode.DownlinkKHz, context.Mode.UplinkKHz);
            if (crossBand)
            {
                var companionHz = _interactive ? 0 : FmCompanionLegHz;
                if (writeRx && !writeTx && txDelta > companionHz)
                    writeTx = true;
                if (writeTx && !writeRx && rxDelta > companionHz)
                    writeRx = true;
            }
        }

        if (writeTx && _interactive && !settings.DualRadioEnabled && !CanWriteInteractiveSub())
        {
            _forceFrequencyApply = true;
            writeTx = false;
        }

        if (!writeRx && !writeTx)
            return false;

        if (!CanWriteDoppler(settings, writeRx, writeTx))
            return false;

        if (TryConsumeUrgentRigAction(duringDoppler: true))
            return false;

        var wroteRx = false;
        var wroteTx = false;
        if (settings.Type == RigType.KenwoodTs2000 && _useMainSub && _driver is KenwoodTs2000Driver kenwoodDoppler
            && kenwoodDoppler.UsesFaFbSatelliteTracking && (writeRx || writeTx))
        {
            var ok = _isBeaconOnly
                ? kenwoodDoppler.ApplySatelliteBeaconDopplerStep(rxHz)
                : kenwoodDoppler.ApplySatelliteDopplerStep(rxHz, txHz);

            if (ok)
            {
                NoteKenwoodFrequencyWriteSuccess();
                if (writeRx)
                {
                    _lastRigRxHz = rxHz;
                    wroteRx = true;
                }

                if (writeTx)
                {
                    _lastRigTxHz = txHz;
                    wroteTx = true;
                }
            }
            else
            {
                NoteKenwoodFrequencyWriteFailure();
            }

            if (_interactive && wroteTx)
                RestoreOperatorVfo();
        }
        else
        {
            if (writeRx)
                wroteRx = WriteRx(settings, rxHz);
            if (TryConsumeUrgentRigAction(duringDoppler: true))
                writeTx = false;
            if (writeTx)
            {
                wroteTx = WriteTx(settings, txHz);
                if (_interactive && wroteTx)
                    RestoreOperatorVfo();
            }
        }

        if (wroteRx || wroteTx)
        {
            if (wroteRx)
                _lastRxWriteUtc = DateTime.UtcNow;
            if (wroteTx)
                _lastTxWriteUtc = DateTime.UtcNow;
            _lastWriteUtc = DateTime.UtcNow;
            // Dual uplink-only writes cannot echo on the RX dial. Refreshing dial settle here
            // permanently blocks linear passband capture while Doppler keeps moving the uplink.
            if (wroteRx || !settings.DualRadioEnabled)
                MarkProgrammaticFrequencySettle();
            FinishOffsetKnobCaptureBlock();
        }

        if ((writeRx && !wroteRx) || (writeTx && !wroteTx))
            _forceFrequencyApply = true;

        if (wroteRx || wroteTx)
        {
            LogDopplerEvent(
                settings,
                context,
                corrected,
                _thresholdHz,
                thresholdHz,
                "cat_write",
                wroteRx: wroteRx,
                wroteTx: wroteTx,
                catPaused: catPaused);
        }

        return wroteRx || wroteTx;
    }

    private void FinishOffsetKnobCaptureBlock()
    {
        // Keep _lastRigRxHz from WriteRx — an immediate dial read often still shows the pre-offset
        // frequency and leaves linear tracking matched to the wrong baseline until the knob moves.
        _blockKnobCapture = false;
    }

    private static long? DisplayHz(long displayHz, long lastWrittenHz) =>
        displayHz > 0 ? displayHz : lastWrittenHz > 0 ? lastWrittenHz : null;

    private bool EnsureConnected(RigSettings settings) =>
        settings.DualRadioEnabled ? EnsureDualConnected(settings) : EnsureSingleConnected(settings);

    private void ConfigureFlexPassInitCancellation(IRigDriver driver)
    {
        if (driver is FlexRadioDriver flex)
            flex.PassInitCancelled = () => _disconnectRequested || _shutdownRequested;
    }

    private bool PassInitAborted => _disconnectRequested || _shutdownRequested;

    private bool EnsureSingleConnected(RigSettings settings)
    {
        if (DateTime.UtcNow < _suspendConnectUntilUtc)
            return _driver?.IsConnected == true;

        var key = RigSettings.IsFlexNetworkRadio(settings.Type)
            ? $"{settings.Type}|{settings.NetworkHost}|{settings.NetworkPort}|{settings.FlexRadioSerial}|{settings.CatDelayMs}"
            : $"{settings.Type}|{settings.Port}|{settings.BaudRate}|{settings.CivAddress}";
        if (_forceIdentityRecheck)
        {
            _forceIdentityRecheck = false;
            if (_driver is not null && _connectedKey == key && _driver.IsConnected)
            {
                if (ConfirmDriver(_driver, settings.Port, endpointLabel: null, settings.Type))
                    return true;

                TearDownRig();
                return false;
            }
        }

        if (_driver is not null && _connectedKey == key && _driver.IsConnected)
            return true;

        TearDownRig();
        try
        {
            _driver = (_driverFactory ?? RigDriverFactory.Create)(settings);
            _driver.Open();
            _connectedKey = key;
            ConfigureFlexPassInitCancellation(_driver);
            if (ConfirmDriver(_driver, settings.Port, endpointLabel: null, settings.Type))
            {
                Log.Information("Rig connected: type={RigType}, endpoint={Endpoint}", settings.Type, FormatSingleEndpoint(settings));
                _lastConnectError = null;
                _lastConnectErrorKind = SerialPortConnectErrorKind.None;
                _lastConnectErrorPort = null;
                _lastConnectEndpoint = null;
                _identityMismatchDevice = null;
                return true;
            }

            if (_identityMismatchDevice is not null)
            {
                TearDownRig();
                return false;
            }

            var endpoint = FormatSingleEndpoint(settings);
            _lastConnectError = RigSettings.IsFlexNetworkRadio(settings.Type)
                ? $"Opened {endpoint} but SmartSDR is not responding"
                : $"Opened {settings.Port} but CI-V is not responding";
            Log.Warning("Rig opened {Endpoint} for {RigType} but link is not active", endpoint, settings.Type);
            TearDownRig();
            RecordConnectFailure(
                SerialPortConnectErrorKind.Generic,
                endpoint,
                endpointLabel: null,
                englishDetail: _lastConnectError);
            return false;
        }
        catch (Exception ex)
        {
            var endpoint = FormatSingleEndpoint(settings);
            RecordConnectFailure(
                ClassifyConnectError(ex),
                endpoint,
                endpointLabel: null,
                englishDetail: ex.Message);
            Log.Warning(ex, "Rig connect failed for {RigType} on {Endpoint}", settings.Type, endpoint);
            _driver?.Dispose();
            _driver = null;
            return false;
        }
    }

    private bool EnsureDualConnected(RigSettings settings)
    {
        if (DateTime.UtcNow < _suspendConnectUntilUtc)
            return _downlinkDriver?.IsConnected == true && _uplinkDriver?.IsConnected == true;

        var downKey = EndpointConnectionKey(settings.Downlink);
        var upKey = EndpointConnectionKey(settings.Uplink);
        var downOk = _downlinkDriver is not null
            && _downlinkConnectedKey == downKey
            && _downlinkDriver.IsConnected;
        var upOk = _uplinkDriver is not null
            && _uplinkConnectedKey == upKey
            && _uplinkDriver.IsConnected;

        if (_forceIdentityRecheck)
        {
            _forceIdentityRecheck = false;
            if (downOk && upOk)
            {
                if (ConfirmDriver(
                        _downlinkDriver!,
                        settings.Downlink.Port,
                        SerialPortConnectErrorHelper.EndpointDownlink,
                        settings.Downlink.Type)
                    && ConfirmDriver(
                        _uplinkDriver!,
                        settings.Uplink.Port,
                        SerialPortConnectErrorHelper.EndpointUplink,
                        settings.Uplink.Type))
                {
                    _identityMismatchDevice = null;
                    return true;
                }

                TearDownRig();
                return false;
            }
        }

        if (downOk && upOk)
            return true;

        var upPort = settings.Uplink.Type == RigType.Dummy ? "" : settings.Uplink.Port;
        if (SerialPortConnectErrorHelper.TryDescribeDualSamePort(settings.Downlink.Port, upPort, out var sharedPort))
        {
            RecordConnectFailure(SerialPortConnectErrorKind.DualSamePort, sharedPort, endpointLabel: null);
            TearDownRig();
            return false;
        }

        TearDownRig();

        try
        {
            _downlinkDriver = CreateEndpointDriver(settings.Downlink);
            _downlinkDriver.Open();
            _downlinkConnectedKey = downKey;
            if (!ConfirmDriver(
                    _downlinkDriver,
                    settings.Downlink.Port,
                    SerialPortConnectErrorHelper.EndpointDownlink,
                    settings.Downlink.Type))
            {
                if (_identityMismatchDevice is null)
                {
                    RecordConnectFailure(
                        SerialPortConnectErrorKind.Generic,
                        settings.Downlink.Port,
                        SerialPortConnectErrorHelper.EndpointDownlink,
                        $"Opened {FormatEndpointLabel(settings.Downlink)} but the link is not active");
                }

                TearDownRig();
                return false;
            }

            _uplinkDriver = CreateEndpointDriver(settings.Uplink);
            _uplinkDriver.Open();
            _uplinkConnectedKey = upKey;
            if (!ConfirmDriver(
                    _uplinkDriver,
                    settings.Uplink.Port,
                    SerialPortConnectErrorHelper.EndpointUplink,
                    settings.Uplink.Type))
            {
                if (_identityMismatchDevice is null)
                {
                    RecordConnectFailure(
                        SerialPortConnectErrorKind.Generic,
                        settings.Uplink.Port,
                        SerialPortConnectErrorHelper.EndpointUplink,
                        $"Opened {FormatEndpointLabel(settings.Uplink)} but the link is not active");
                }

                TearDownRig();
                return false;
            }

            _lastConnectError = null;
            _lastConnectErrorKind = SerialPortConnectErrorKind.None;
            _lastConnectErrorPort = null;
            _lastConnectEndpoint = null;
            _identityMismatchDevice = null;
            return true;
        }
        catch (Exception ex)
        {
            var failedPort = settings.Downlink.Port;
            var failedEndpoint = SerialPortConnectErrorHelper.EndpointDownlink;
            if (_uplinkDriver is not null)
            {
                failedPort = settings.Uplink.Port;
                failedEndpoint = SerialPortConnectErrorHelper.EndpointUplink;
            }

            RecordConnectFailure(ClassifyConnectError(ex), failedPort, failedEndpoint, ex.Message);
            Log.Warning(ex, "Dual rig connect failed (down {DownEndpoint}, up {UpEndpoint})",
                FormatEndpointLabel(settings.Downlink), FormatEndpointLabel(settings.Uplink));
            TearDownRig();
            return false;
        }
    }

    private bool ConfirmDriver(IRigDriver driver, string? port, string? endpointLabel, RigType type)
    {
        bool confirmed;
        try
        {
            confirmed = driver.IsConnected && driver.TryConfirmIdentity();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Rig identity check failed on {Port}", port);
            confirmed = false;
        }

        if (confirmed)
            return true;

        if (!driver.IsConnected)
            return false;

        var name = DeviceIdentityNames.Radio(type);
        Log.Warning("Rig on {Port} did not answer as {Radio}", port, name);
        RecordConnectFailure(
            SerialPortConnectErrorKind.Generic,
            port,
            endpointLabel,
            $"Opened {port} but it did not answer as {name}.");
        _identityMismatchDevice = name;
        return false;
    }

    private void RecordConnectFailure(
        SerialPortConnectErrorKind kind,
        string? port,
        string? endpointLabel,
        string? englishDetail = null)
    {
        _identityMismatchDevice = null;
        _lastConnectErrorKind = kind;
        _lastConnectErrorPort = port;
        _lastConnectEndpoint = endpointLabel;
        _lastConnectError = englishDetail ?? SerialPortConnectErrorHelper.ToEnglish(kind, port ?? "", endpointLabel);
        _suspendConnectUntilUtc = DateTime.UtcNow.AddSeconds(ConnectFailureBackoffSeconds);
    }

    private static SerialPortConnectErrorKind ClassifyConnectError(Exception ex) =>
        SerialPortConnectErrorHelper.Classify(ex);

    private IRigDriver CreateEndpointDriver(RigEndpointSettings endpoint) =>
        _endpointFactory?.Invoke(endpoint) ?? RigDriverFactory.Create(endpoint);

    private static string EndpointConnectionKey(RigEndpointSettings endpoint) =>
        endpoint.Type == RigType.Dummy
            ? "Dummy"
            : RigSettings.IsSdrDownlinkEndpoint(endpoint.Type)
                ? $"{endpoint.Type}|{endpoint.NetworkHost}|{endpoint.NetworkPort}|{endpoint.CatDelayMs}"
                : $"{endpoint.Type}|{endpoint.Port}|{endpoint.BaudRate}|{endpoint.CatDelayMs}|{endpoint.CivAddress}";

    private void RunPassInit(RigSettings settings, RigTrackingContext context)
    {
        if (settings.DualRadioEnabled)
        {
            RunPassInitDual(settings, context);
            return;
        }

        if (_driver is null)
            return;

        _useMainSub = !_isBeaconOnly && ShouldUseMainSubLayout(settings, context);
        if (settings.Type == RigType.FlexSmartSdr)
        {
            Log.Information(
                "Initialising FlexRadio pass: satellite={Satellite}, beaconOnly={BeaconOnly}, mainSub={MainSub}, downlinkKHz={DownlinkKHz}, uplinkKHz={UplinkKHz}",
                context.TrackState.Name,
                _isBeaconOnly,
                _useMainSub,
                context.Mode.DownlinkKHz,
                context.Mode.UplinkKHz);
        }
        AssignReceiveVfo(settings, context);

        if (_isBeaconOnly)
        {
            if (settings.Type == RigType.KenwoodTs2000)
            {
                // TS-2000: keep SATL and Doppler-track FA only (exiting SATL causes FR/tone reject beeps).
                _useMainSub = true;
                if (_driver is KenwoodTs2000Driver kenwoodBeacon
                    && kenwoodBeacon.UsesFaFbSatelliteTracking)
                {
                    kenwoodBeacon.ReaffirmSatelliteLayout();
                }
                else
                {
                    _driver.SetSatelliteMode(true);
                    if (!_driver.IsSatelliteModeActive)
                    {
                        Log.Warning(
                            "TS-2000 SATL not confirmed via SA; — continuing FA tracking for beacon (no FR/split in SAT).");
                    }

                    Thread.Sleep(150);
                }
            }
            else
            {
                _driver.SetSatelliteMode(false);
                _driver.SetSplitOn(false);
                ClearCtcssLeavingSatelliteMode(settings, context);
                EnsureBeaconDownlinkOnMain(context);
            }
        }
        else if (!_useMainSub)
        {
            _driver.SetSatelliteMode(false);
            _driver.SetSplitOn(true);
            ClearCtcssLeavingSatelliteMode(settings, context);
            if (UsesIcomSplitAbLayout(settings, context))
                _driver.SelectVfo(RigVfo.VfoA, force: true);
        }
        else
        {
            if (settings.Type == RigType.KenwoodTs2000
                && _driver is KenwoodTs2000Driver kenwoodDriver
                && kenwoodDriver.UsesFaFbSatelliteTracking)
            {
                // Mid-pass re-init (offsets, CAT resume): keep SATL, reaffirm layout, reprogram FA/FB.
                kenwoodDriver.ReaffirmSatelliteLayout();
            }
            else
            {
                if (settings.Type == RigType.FlexSmartSdr && _driver is FlexRadioDriver flexBeforeSat)
                    flexBeforeSat.ConfigureAntennaPorts(settings);

                _driver.SetSatelliteMode(true);
                if (settings.Type == RigType.KenwoodTs2000 && !_driver.IsSatelliteModeActive)
                {
                    Log.Warning(
                        "TS-2000 SATL not confirmed via SA; — continuing FA/FB tracking (no FR/split in SAT).");
                }

                Thread.Sleep(150);
            }

            // IC-910/9100/9700 reject split CI-V in satellite (Main/Sub) mode with NAK.
            // Kenwood SATL uses FA/FB only — never FR/FT (driver no-ops split while in SAT).
            if (_useMainSub && !IsIcomSatelliteLayoutRig(settings.Type) && settings.Type != RigType.KenwoodTs2000)
                _driver.SetSplitOn(false);
            if (settings.Type == RigType.IcomIc821h && _driver is IcomIc821hDriver ic821)
                ic821.EstablishSatelliteVfoState();
        }

        TryBandSwap(settings, context);

        var setup = SetupVfosPolicy.Evaluate(
            context.EffectiveDownlinkMode,
            settings.DopplerThresholdFmHz,
            settings.DopplerThresholdLinearHz);
        _thresholdHz = setup.ThresholdHz;
        _interactive = setup.Interactive;
        _knobTuneThresholdHz = KnobTuneCapturePolicy.Resolve(context.EffectiveDownlinkMode);

        // FT-847 can revert to narrow FM when SAT frequencies or the CTCSS tone are programmed after mode.
        // Flex SmartSDR must defer modes until after slice pan bind, tune, and pan centre.
        var deferModeSetup = settings.Type is RigType.YaesuFt847 or RigType.FlexSmartSdr;
        var isKenwoodSat = settings.Type == RigType.KenwoodTs2000 && _useMainSub;
        var isFlexSatPass = settings.Type == RigType.FlexSmartSdr && _useMainSub && !_isBeaconOnly;
        if (!deferModeSetup && !isKenwoodSat)
            ConfigureVfoModes(context);

        var corrected = ComputeDoppler(context);
        var rxHz = ToHz(corrected.RadioReceiveKHz);
        var txHz = ToHz(corrected.RadioTransmitKHz);
        _lastRigRxHz = 0;
        _lastRigTxHz = 0;
        _rxHzBeforeLastCatWrite = 0;

        if (isFlexSatPass && _driver is FlexRadioDriver flexPreTune)
        {
            if (PassInitAborted)
                return;

            flexPreTune.ConfigureAntennaPorts(settings);
            var downlinkOnVhf = RigSatModeHelper.IsVhfCenterKHz(context.Mode.DownlinkKHz);
            var layoutFlipped = _lastPassDownlinkOnVhf is bool previousDownlinkOnVhf
                && previousDownlinkOnVhf != downlinkOnVhf;
            var satelliteChanged = _lastPassSatelliteKey is string previousSatellite
                && !string.Equals(previousSatellite, context.TrackState.Name, StringComparison.OrdinalIgnoreCase);
            // Layout flips always request recreate. Same-layout sat switches also request it, but
            // BindDuplexSlicesToBandPans still short-circuits when slices are already on healthy
            // dual-band pans (avoids the single-pan bootstrap lock-up from tearing down a good bind).
            var forcePanRebind = layoutFlipped || satelliteChanged;
            flexPreTune.EnsureDualBandPanLayout(rxHz, txHz);
            flexPreTune.BindDuplexSlicesToBandPans(rxHz, txHz, forcePanRebind);
            flexPreTune.ApplyBandAntennaPorts(settings, rxHz, txHz);
        }

        var initResult = isKenwoodSat && _driver is KenwoodTs2000Driver kenwoodInit
            ? InitializeKenwoodSatellitePass(settings, kenwoodInit, context, rxHz, txHz)
            : WriteInitialFrequencies(settings, rxHz, txHz);

        if (!isFlexSatPass)
            ApplyCtcss(settings, context, force: true);

        if (deferModeSetup && settings.Type == RigType.YaesuFt847)
        {
            ConfigureVfoModes(context);
            // The SAT TX mode command clears the encoder. Tone frequency stays before mode
            // (programming it afterwards can force narrow FM); only the encoder is repeated.
            ReassertFt847CtcssEncoder(settings, context);
        }

        if (initResult.RxWritten)
            _lastRigRxHz = rxHz;
        if (initResult.TxWritten)
            _lastRigTxHz = txHz;

        if (initResult.RequiresRetry(_isBeaconOnly))
        {
            _forceFrequencyApply = true;
            WriteDopplerFrequencies(settings, context);
        }

        if (initResult.RxWritten || initResult.TxWritten)
            _lastWriteUtc = DateTime.UtcNow;

        if (isFlexSatPass && _driver is FlexRadioDriver flexPostInit)
        {
            if (PassInitAborted)
                return;

            flexPostInit.CenterBandPanadapters(rxHz, txHz);
            ConfigureVfoModes(context);
            flexPostInit.EnsureDuplexPassFrequencies(
                rxHz,
                txHz,
                FlexModeMapper.ToSmartSdrMode(context.EffectiveDownlinkMode),
                FlexModeMapper.ToSmartSdrMode(context.EffectiveUplinkMode));
            // EnsureDuplexPassFrequencies may recreate slices after the pre-tune apply; set ports again.
            flexPostInit.ApplyBandAntennaPorts(settings, rxHz, txHz);
            ApplyCtcss(settings, context, force: true);
        }
        else if (settings.Type == RigType.FlexSmartSdr && _driver is FlexRadioDriver flexBeaconDriver)
        {
            flexBeaconDriver.ConfigureAntennaPorts(settings);
            if (rxHz > 0)
                flexBeaconDriver.ApplyBandAntennaPorts(settings, rxHz, 0);
            if (rxHz > 0)
                flexBeaconDriver.CenterBandPanadapters(rxHz, 0);
            flexBeaconDriver.EnsureReceiveSliceActive();
        }

        _lastPassDownlinkOnVhf = RigSatModeHelper.IsVhfCenterKHz(context.Mode.DownlinkKHz);
        _lastPassSatelliteKey = context.TrackState.Name;
        MarkProgrammaticFrequencySettle();
        ExtendDopplerSuspendMs(500);
        RestoreOperatorVfo();
    }

    private void RunPassInitDual(RigSettings settings, RigTrackingContext context)
    {
        if (_downlinkDriver is null || _uplinkDriver is null)
            return;

        _useMainSub = false;
        _receiveVfo = RigVfo.Main;

        _downlinkDriver.SelectVfo(RigVfo.Main, force: true);
        if (settings.ReceiveRegion() == RigRegion.USA)
            _downlinkDriver.SetToneSquelchOn(false);
        else
            _downlinkDriver.SetToneOn(false);

        if (settings.Uplink.Type != RigType.Dummy)
        {
            _uplinkDriver.SelectVfo(RigVfo.Main, force: true);
            _uplinkDriver.SetToneOn(false);
            _uplinkDriver.SetToneSquelchOn(false);
        }

        var setup = SetupVfosPolicy.Evaluate(
            context.EffectiveDownlinkMode,
            settings.DopplerThresholdFmHz,
            settings.DopplerThresholdLinearHz);
        _thresholdHz = setup.ThresholdHz;
        _interactive = setup.Interactive;
        _knobTuneThresholdHz = KnobTuneCapturePolicy.Resolve(context.EffectiveDownlinkMode);

        var corrected = ComputeDoppler(context);
        var rxHz = ToHz(corrected.RadioReceiveKHz);
        var txHz = ToHz(corrected.RadioTransmitKHz);
        _lastRigRxHz = 0;
        _lastRigTxHz = 0;
        _rxHzBeforeLastCatWrite = 0;

        _downlinkDriver.SelectVfo(RigVfo.Main);
        _downlinkDriver.SetMode(context.EffectiveDownlinkMode);
        var rxWritten = _downlinkDriver.SetFrequencyHz(rxHz);

        var txWritten = true;
        if (!_isBeaconOnly && settings.Uplink.Type != RigType.Dummy)
        {
            if (UsesYaesuNewCatSplitUplink(settings))
                _uplinkDriver.SetSplitOn(true);

            _uplinkDriver.SelectVfo(RigVfo.Main);
            _uplinkDriver.SetMode(context.EffectiveUplinkMode);
            if (UsesYaesuNewCatSplitUplink(settings))
            {
                _uplinkDriver.SetFrequencyHz(txHz);
                _uplinkDriver.SelectVfo(RigVfo.VfoB);
                txWritten = _uplinkDriver.SetFrequencyHz(txHz);
            }
            else
            {
                txWritten = _uplinkDriver.SetFrequencyHz(txHz);
            }
        }

        ApplyCtcss(settings, context, force: true);

        if (rxWritten)
            _lastRigRxHz = rxHz;
        if (txWritten)
            _lastRigTxHz = txHz;

        var initResult = new InitialFrequencyWriteResult(rxWritten, txWritten);
        if (initResult.RequiresRetry(_isBeaconOnly))
        {
            _forceFrequencyApply = true;
            WriteDopplerFrequencies(settings, context);
        }

        if (initResult.RxWritten || initResult.TxWritten)
            _lastWriteUtc = DateTime.UtcNow;

        _lastPassDownlinkOnVhf = RigSatModeHelper.IsVhfCenterKHz(context.Mode.DownlinkKHz);
        _lastPassSatelliteKey = context.TrackState.Name;
        MarkProgrammaticFrequencySettle();
        ExtendDopplerSuspendMs(500);
        RestoreOperatorVfo();
    }

    private void TryBandSwap(RigSettings settings, RigTrackingContext context)
    {
        if (_driver is null)
            return;

        // TS-2000 SATL assigns downlink/uplink via FA/FB and SA P3 — not ICOM-style Main/Sub exchange.
        if (settings.Type == RigType.KenwoodTs2000)
            return;

        var canExchange = _driver.SupportsVfoExchange;
        var downlinkOnVhf = RigSatModeHelper.IsVhfCenterKHz(context.Mode.DownlinkKHz);
        if (canExchange
            && _lastPassDownlinkOnVhf is bool previousDownlinkOnVhf
            && previousDownlinkOnVhf != downlinkOnVhf)
        {
            _driver.ExchangeVfos();
            return;
        }

        _driver.SelectVfo(RigVfo.Main);
        Thread.Sleep(50);

        var mainHz = _driver.ReadFrequencyHz(RigVfo.Main);
        if (mainHz is > 0)
        {
            if (canExchange && RigSatModeHelper.NeedsMainSubBandSwap(mainHz.Value, context.Mode.DownlinkKHz))
                _driver.ExchangeVfos();

            return;
        }

        // CI-V read failed — infer from Sub when downlink/uplink are on opposite bands.
        if (!canExchange || !downlinkOnVhf || !RigSatModeHelper.IsUhfCenterKHz(context.Mode.UplinkKHz))
            return;

        var subHz = _driver.ReadFrequencyHz(RigVfo.Sub);
        if (subHz is > 0 and < 200_000_000)
            _driver.ExchangeVfos();
    }

    private static string PassKey(RigTrackingContext context) =>
        $"{context.TrackState.NoradId}|{context.Mode.Type}|{context.Mode.DownlinkKHz}|{context.Mode.UplinkKHz}";

    private static bool IsIcomSatelliteLayoutRig(RigType type) =>
        type is RigType.IcomIc910 or RigType.IcomIc9100 or RigType.IcomIc9700 or RigType.IcomIc821h;

    private static bool UsesMainSubSatelliteLayout(RigType type) =>
        type is RigType.IcomIc910 or RigType.IcomIc9100 or RigType.IcomIc9700 or RigType.IcomIc821h
            or RigType.YaesuFt847 or RigType.KenwoodTs2000 or RigType.FlexSmartSdr or RigType.Dummy;

    private static bool UsesIcomSatelliteOnlyLayout(RigType type) =>
        type == RigType.IcomIc821h;

    private static bool ShouldUseMainSubLayout(RigSettings settings, RigTrackingContext context) =>
        settings.Type == RigType.FlexSmartSdr
            ? context.Mode.DownlinkKHz > 0 && context.Mode.UplinkKHz > 0
            : UsesMainSubSatelliteLayout(settings.Type)
              && (RigSatModeHelper.UseMainSubLayout(context.Mode.DownlinkKHz, context.Mode.UplinkKHz)
                  || UsesIcomSatelliteOnlyLayout(settings.Type));

    private static bool UsesIcomSplitAbLayout(RigSettings settings, RigTrackingContext context) =>
        IsIcomSatelliteLayoutRig(settings.Type)
        && !UsesIcomSatelliteOnlyLayout(settings.Type)
        && !context.Mode.IsBeaconOnly
        && !RigSatModeHelper.UseMainSubLayout(context.Mode.DownlinkKHz, context.Mode.UplinkKHz);

    private void AssignReceiveVfo(RigSettings settings, RigTrackingContext context)
    {
        if (_useMainSub)
            _receiveVfo = RigVfo.Main;
        else if (_isBeaconOnly && IsIcomSatelliteLayoutRig(settings.Type))
            _receiveVfo = RigVfo.Main;
        else
            _receiveVfo = RigVfo.VfoA;
    }

    private void EnsureBeaconDownlinkOnMain(RigTrackingContext context)
    {
        if (_driver is null || _receiveVfo != RigVfo.Main || !_driver.SupportsVfoExchange)
            return;

        _driver.SelectVfo(RigVfo.Main);
        Thread.Sleep(50);
        var mainHz = _driver.ReadFrequencyHz(RigVfo.Main);
        if (mainHz is > 0 && RigSatModeHelper.NeedsMainSubBandSwap(mainHz.Value, context.Mode.DownlinkKHz))
            _driver.ExchangeVfos();
    }

    private void ClearCtcssLeavingSatelliteMode(RigSettings settings, RigTrackingContext context)
    {
        if (_driver is null)
            return;

        foreach (var vfo in VfosForSatelliteCtcssClear(settings, context))
            SetCtcssOffOnVfo(vfo);

        _lastAppliedCtcssHz = null;
        _lastAppliedCtcssSquelch = null;
    }

    private static IEnumerable<RigVfo> VfosForSatelliteCtcssClear(RigSettings settings, RigTrackingContext context)
    {
        if (UsesIcomSplitAbLayout(settings, context))
        {
            yield return RigVfo.VfoA;
            yield return RigVfo.VfoB;
            yield break;
        }

        if (settings.Type is RigType.IcomIc910 or RigType.IcomIc9100 or RigType.IcomIc9700 or RigType.IcomIc821h
            or RigType.YaesuFt847 or RigType.KenwoodTs2000)
        {
            yield return RigVfo.Main;
            yield return RigVfo.Sub;
            yield break;
        }

        yield return RigVfo.VfoA;
        yield return RigVfo.VfoB;
    }

    private void SetCtcssOffOnVfo(RigVfo vfo)
    {
        if (_driver is null)
            return;

        _driver.SelectVfo(vfo, force: true);
        _driver.SetToneOn(false);
        _driver.SetToneSquelchOn(false);
    }

    private InitialFrequencyWriteResult InitializeKenwoodSatellitePass(
        RigSettings settings,
        KenwoodTs2000Driver kenwood,
        RigTrackingContext context,
        long downlinkHz,
        long uplinkHz)
    {
        _driver!.SelectVfo(RigVfo.Main);
        if (settings.ReceiveRegion() == RigRegion.USA)
            _driver.SetToneSquelchOn(false);
        else
            _driver.SetToneOn(false);

        if (_isBeaconOnly)
        {
            if (!KenwoodCatCodec.TryGetModeCode(context.EffectiveDownlinkMode, out var beaconMode))
                return new InitialFrequencyWriteResult(false, TxWritten: true);

            var beaconOk = kenwood.ApplySatelliteBeaconPassFrequenciesWithBandRecovery(
                downlinkHz,
                context.Mode.DownlinkKHz,
                beaconMode);
            if (beaconOk)
                NoteKenwoodFrequencyWriteSuccess();
            else
                NoteKenwoodFrequencyWriteFailure();
            return new InitialFrequencyWriteResult(beaconOk, TxWritten: true);
        }

        if (!KenwoodCatCodec.TryGetModeCode(context.EffectiveDownlinkMode, out var downlinkMode)
            || !KenwoodCatCodec.TryGetModeCode(context.EffectiveUplinkMode, out var uplinkMode))
        {
            return new InitialFrequencyWriteResult(false, false);
        }

        var ok = kenwood.ApplySatellitePassFrequenciesWithBandRecovery(
            downlinkHz,
            uplinkHz,
            context.Mode.DownlinkKHz,
            downlinkMode,
            uplinkMode);
        if (ok)
            NoteKenwoodFrequencyWriteSuccess();
        else
            NoteKenwoodFrequencyWriteFailure();
        return new InitialFrequencyWriteResult(ok, ok && !_isBeaconOnly);
    }

    private void NoteKenwoodFrequencyWriteSuccess() => _kenwoodFaFbFailCount = 0;

    private void NoteKenwoodFrequencyWriteFailure()
    {
        _kenwoodFaFbFailCount++;
        if (_kenwoodFaFbFailCount < KenwoodFaFbFailBackoffThreshold)
            return;

        _kenwoodFaFbFailCount = 0;
        _kenwoodFaFbBackoffUntilUtc = DateTime.UtcNow.AddMilliseconds(KenwoodFaFbFailBackoffMs);
        Log.Warning(
            "TS-2000 FA/FB writes failing; backing off Doppler CAT for {BackoffMs} ms",
            KenwoodFaFbFailBackoffMs);
    }

    private void ExtendDopplerSuspendMs(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        if (until > _suspendDopplerUntilUtc)
            _suspendDopplerUntilUtc = until;
    }

    private void ConfigureVfoModes(RigTrackingContext context)
    {
        if (_driver is null)
            return;

        if (_isBeaconOnly)
        {
            _driver.SelectVfo(ReceiveVfo());
            _driver.SetMode(context.EffectiveDownlinkMode);
            return;
        }

        if (_useMainSub)
        {
            // Satellite layout: downlink mode on Main, uplink mode on Sub.
            _driver.SelectVfo(RigVfo.Main);
            _driver.SetMode(context.EffectiveDownlinkMode);
            _driver.SelectVfo(RigVfo.Sub);
            _driver.SetMode(context.EffectiveUplinkMode);
            return;
        }

        _driver.SelectVfo(RigVfo.VfoA);
        _driver.SetMode(context.EffectiveDownlinkMode);
        _driver.SelectVfo(RigVfo.VfoB);
        _driver.SetMode(context.EffectiveUplinkMode);
    }

    /// <summary>
    /// FT-847 SAT mode setup runs after the tone frequency so wide FM sticks.
    /// That mode command turns the SAT TX encoder off, so switch it back on.
    /// </summary>
    private void ReassertFt847CtcssEncoder(RigSettings settings, RigTrackingContext context)
    {
        var driver = TxDriver();
        if (driver is null
            || settings.Uplink.Type == RigType.Dummy
            || _isBeaconOnly
            || context.SelectedCtcssHz is not > 0)
        {
            return;
        }

        var uplinkType = settings.DualRadioEnabled ? settings.Uplink.Type : settings.Type;
        var squelch = !UsesEncodeOnlyUplinkCtcss(uplinkType) && settings.TransmitRegion() == RigRegion.USA;
        driver.SelectVfo(UplinkVfoForCtcss(settings, context), force: true);
        if (squelch)
            driver.SetToneSquelchOn(true);
        else
            driver.SetToneOn(true);
    }

    private void ApplyCtcss(RigSettings settings, RigTrackingContext context, bool force)
    {
        var driver = TxDriver();
        if (driver is null || settings.Uplink.Type == RigType.Dummy || context.SelectedCtcssHz is not { } hz || hz <= 0)
            return;

        var uplinkType = settings.DualRadioEnabled ? settings.Uplink.Type : settings.Type;
        if (uplinkType == RigType.FlexSmartSdr
            && !context.Mode.IsFmMode
            && !IsFmUplinkMode(context.EffectiveUplinkMode))
        {
            return;
        }

        // ICOM (and most others): USA region uses TSQL so the tone is actually programmed/enabled.
        // TS-2000 and FT-847: encode-only; tone decode mutes receive because satellite downlinks rarely carry a tone.
        var squelch = !UsesEncodeOnlyUplinkCtcss(uplinkType) && settings.TransmitRegion() == RigRegion.USA;
        if (!force && _lastAppliedCtcssHz == hz && _lastAppliedCtcssSquelch == squelch)
            return;

        driver.SelectVfo(UplinkVfoForCtcss(settings, context), force: true);
        if (squelch)
        {
            driver.SetToneHz(hz, squelchTone: true);
            driver.SetToneSquelchOn(true);
        }
        else
        {
            driver.SetToneHz(hz, squelchTone: false);
            driver.SetToneOn(true);
        }

        _lastAppliedCtcssHz = hz;
        _lastAppliedCtcssSquelch = squelch;
    }

    private static bool IsFmUplinkMode(string mode) =>
        mode.Equals("FM", StringComparison.OrdinalIgnoreCase)
        || mode.Equals("FMN", StringComparison.OrdinalIgnoreCase)
        || mode.Equals("NFM", StringComparison.OrdinalIgnoreCase);

    private static bool UsesEncodeOnlyUplinkCtcss(RigType rigType) =>
        rigType is RigType.KenwoodTs2000 or RigType.YaesuFt847;

    private static RigVfo UplinkVfoForCtcss(RigSettings settings, RigTrackingContext context)
    {
        if (settings.DualRadioEnabled)
        {
            if (RigSettings.IsYaesuNewCatDualEndpoint(settings.Uplink.Type))
                return RigVfo.VfoB;

            return RigVfo.Main;
        }

        if (settings.Type is RigType.IcomIc910 or RigType.IcomIc9100 or RigType.IcomIc9700 or RigType.IcomIc821h)
        {
            return RigSatModeHelper.UseMainSubLayout(context.Mode.DownlinkKHz, context.Mode.UplinkKHz)
                || UsesIcomSatelliteOnlyLayout(settings.Type)
                ? RigVfo.Sub
                : RigVfo.VfoB;
        }

        if (settings.Type is RigType.YaesuFt847 or RigType.KenwoodTs2000)
            return RigVfo.Sub;

        return RigSatModeHelper.UseMainSubLayout(context.Mode.DownlinkKHz, context.Mode.UplinkKHz)
            ? RigVfo.Sub
            : RigVfo.VfoB;
    }

    private InitialFrequencyWriteResult WriteInitialFrequencies(RigSettings settings, long rxHz, long txHz)
    {
        if (settings.DualRadioEnabled)
            return WriteInitialFrequenciesDual(settings, rxHz, txHz);

        if (_driver is null)
            return new InitialFrequencyWriteResult(false, false);

        if (_useMainSub)
        {
            // Pass init: clear tone on Main, set RX, then set TX on Sub (CTCSS applied after).
            // Flex SmartSDR encodes tone on the TX slice only; skip pre-clear (ApplyCtcss gates FM).
            _driver.SelectVfo(RigVfo.Main);
            if (settings.Type != RigType.FlexSmartSdr)
            {
                if (settings.ReceiveRegion() == RigRegion.USA)
                    _driver.SetToneSquelchOn(false);
                else
                    _driver.SetToneOn(false);
            }

            var rxWritten = _driver.SetFrequencyHz(rxHz);
            if (_isBeaconOnly)
                return new InitialFrequencyWriteResult(rxWritten, TxWritten: true);

            _driver.SelectVfo(RigVfo.Sub, force: true);
            var txWritten = _driver.SetFrequencyHz(txHz);
            return new InitialFrequencyWriteResult(rxWritten, txWritten);
        }

        _driver.SelectVfo(ReceiveVfo());
        var rxOk = _driver.SetFrequencyHz(rxHz);
        if (_isBeaconOnly)
            return new InitialFrequencyWriteResult(rxOk, TxWritten: true);

        _driver.SelectVfo(RigVfo.VfoB);
        var txOk = _driver.SetFrequencyHz(txHz);
        return new InitialFrequencyWriteResult(rxOk, txOk);
    }

    private InitialFrequencyWriteResult WriteInitialFrequenciesDual(RigSettings settings, long rxHz, long txHz)
    {
        if (_downlinkDriver is null || _uplinkDriver is null)
            return new InitialFrequencyWriteResult(false, false);

        _downlinkDriver.SelectVfo(RigVfo.Main);
        if (settings.ReceiveRegion() == RigRegion.USA)
            _downlinkDriver.SetToneSquelchOn(false);
        else
            _downlinkDriver.SetToneOn(false);

        var rxWritten = _downlinkDriver.SetFrequencyHz(rxHz);
        if (_isBeaconOnly || settings.Uplink.Type == RigType.Dummy)
            return new InitialFrequencyWriteResult(rxWritten, TxWritten: true);

        if (UsesYaesuNewCatSplitUplink(settings))
            _uplinkDriver.SetSplitOn(true);

        _uplinkDriver.SelectVfo(TransmitVfoForWrite(settings));
        var txWritten = _uplinkDriver.SetFrequencyHz(txHz);
        return new InitialFrequencyWriteResult(rxWritten, txWritten);
    }

    private readonly record struct InitialFrequencyWriteResult(bool RxWritten, bool TxWritten)
    {
        public bool RequiresRetry(bool isBeaconOnly) =>
            !RxWritten || (!isBeaconOnly && !TxWritten);
    }

    private void NoteContextOffsetChange(RigTrackingContext context)
    {
        if (NearlyEqual(context.ReceiveOffsetKHz, _lastContextRxOffsetKHz)
            && NearlyEqual(context.TransmitOffsetKHz, _lastContextTxOffsetKHz))
            return;

        var notes =
            $"rx={_lastContextRxOffsetKHz:0.000}->{context.ReceiveOffsetKHz:0.000};tx={_lastContextTxOffsetKHz:0.000}->{context.TransmitOffsetKHz:0.000}";
        _lastContextRxOffsetKHz = context.ReceiveOffsetKHz;
        _lastContextTxOffsetKHz = context.TransmitOffsetKHz;
        _forceFrequencyApply = true;
        _blockKnobCapture = true;
        ClearDialHistory();
        MarkProgrammaticFrequencySettle();
        LogDopplerEvent(
            _cachedSettings,
            context,
            ComputeDoppler(context),
            _thresholdHz,
            ResolveWriteThresholdHz(_cachedSettings, context),
            "offset_change",
            notes: notes);
    }

    private void NoteContextDopplerStrategyChange(RigTrackingContext context)
    {
        if (context.DopplerStrategy == _lastContextDopplerStrategy)
            return;

        _lastContextDopplerStrategy = context.DopplerStrategy;
        _forceFrequencyApply = true;
        MarkProgrammaticFrequencySettle();
    }

    private void MarkProgrammaticFrequencySettle()
    {
        _ignoreDialUntilUtc = DateTime.UtcNow.AddMilliseconds(PostCatWriteDialSettleMs);
        if (_lastRigRxHz > 0)
            SeedDialHistoryStable(_lastRigRxHz);
        else
            ClearDialHistory();
    }

    private bool ShouldWrite(int thresholdHz, long rxHz, long txHz, DopplerStrategy strategy)
    {
        var rxDelta = Math.Abs(rxHz - _lastRigRxHz);
        var txDelta = Math.Abs(txHz - _lastRigTxHz);
        if (thresholdHz == 0)
        {
            return strategy switch
            {
                DopplerStrategy.UplinkOnly => txDelta > 0,
                DopplerStrategy.DownlinkOnly => rxDelta > 0,
                _ => rxDelta > 0 || txDelta > 0
            };
        }

        return strategy switch
        {
            DopplerStrategy.UplinkOnly => txDelta > thresholdHz,
            DopplerStrategy.DownlinkOnly => rxDelta > thresholdHz,
            _ => rxDelta > thresholdHz || txDelta > thresholdHz
        };
    }

    private bool TryReadReceiveDialHz(out long hz)
    {
        hz = 0;
        var driver = RxDriver();
        if (driver is null)
            return false;

        var dial = driver.ReadFrequencyHz(ReceiveVfo());
        if (dial is null or <= 0)
            return false;

        if (!RigFrequencyBands.IsPlausibleReceiveRead(_lastRigRxHz, dial.Value))
            return false;

        hz = dial.Value;
        return true;
    }

    private RigVfo ReceiveVfo() => _receiveVfo;

    private RigVfo TransmitVfo() => _useMainSub ? RigVfo.Sub : RigVfo.VfoB;

    private void RestoreOperatorVfo()
    {
        if (_dopplerPreempted)
            return;

        var driver = RxDriver();
        if (driver is null)
            return;

        driver.SelectVfo(ReceiveVfo(), force: _interactive);
        // The second select is a settle for the operator's dial. During an FT4 slot step it
        // only adds CI-V time on the same port CAT PTT has to use.
        if (!_interactive || _ft4DirectDopplerStep)
            return;

        var delayMs = Math.Clamp(_cachedSettings.ReceiveCatDelayMs(), 50, 200);
        Thread.Sleep(delayMs);
        driver.SelectVfo(ReceiveVfo(), force: true);
    }

    private bool CanWriteDoppler(RigSettings settings, bool writeRx, bool writeTx)
    {
        if (writeRx && !CanWriteRx(settings))
            return false;

        if (writeTx && !CanWriteTx(settings))
            return false;

        return true;
    }

    private bool CanWriteRx(RigSettings settings) =>
        (DateTime.UtcNow - _lastRxWriteUtc).TotalMilliseconds >= settings.ReceiveCatDelayMs();

    private bool CanWriteTx(RigSettings settings) =>
        (DateTime.UtcNow - _lastTxWriteUtc).TotalMilliseconds >= settings.TransmitCatDelayMs();

    private bool WriteRx(RigSettings settings, long hz)
    {
        var driver = RxDriver();
        if (driver is null)
            return false;

        driver.SelectVfo(ReceiveVfoForWrite(settings), force: true);
        if (driver.SetFrequencyHz(hz))
        {
            if (_lastRigRxHz > 0 && _lastRigRxHz != hz)
                _rxHzBeforeLastCatWrite = _lastRigRxHz;
            _lastRigRxHz = hz;
            return true;
        }

        return false;
    }

    private bool WriteTx(RigSettings settings, long hz)
    {
        if (settings.Uplink.Type == RigType.Dummy)
        {
            _lastRigTxHz = hz;
            return true;
        }

        var driver = TxDriver();
        if (driver is null)
            return false;

        driver.SelectVfo(TransmitVfoForWrite(settings), force: true);
        if (driver.SetFrequencyHz(hz))
        {
            _lastRigTxHz = hz;
            return true;
        }

        return false;
    }

    private RigVfo ReceiveVfoForWrite(RigSettings settings) =>
        settings.DualRadioEnabled ? RigVfo.Main : ReceiveVfo();

    private RigVfo TransmitVfoForWrite(RigSettings settings)
    {
        if (settings.DualRadioEnabled)
        {
            if (RigSettings.IsYaesuNewCatDualEndpoint(settings.Uplink.Type))
                return RigVfo.VfoB;

            return RigVfo.Main;
        }

        return TransmitVfo();
    }

    private static bool UsesYaesuNewCatSplitUplink(RigSettings settings) =>
        settings.DualRadioEnabled && RigSettings.IsYaesuNewCatDualEndpoint(settings.Uplink.Type);

    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 0.0001;

    private void SyncDisplayFrequencies(CorrectedFrequencies corrected)
    {
        _displayRxHz = ToHz(corrected.RadioReceiveKHz);
        _displayTxHz = ToHz(corrected.RadioTransmitKHz);
    }

    private CorrectedFrequencies ComputeDoppler(RigTrackingContext context)
    {
        var (rxRangeRate, txRangeRate) = ResolveRangeRatesForDoppler(context);
        return DopplerFrequencyCalculator.Compute(
            context.Mode,
            rxRangeRate,
            context.ReceiveOffsetKHz,
            context.TransmitOffsetKHz,
            _passbandDownlinkAdjustKHz,
            _passbandUplinkAdjustKHz,
            context.DopplerStrategy,
            txRangeRate);
    }

    private DopplerLeadRangeRates ResolveRangeRatesForDoppler(RigTrackingContext context)
    {
        var site = _settingsService?.Current.GroundStation ?? new GroundStation();
        if (_ft4DopplerInstantUtc is { } at && _propagator is not null)
        {
            try
            {
                var rate = _propagator.GetLookAngles(context.TrackState.NoradId, site, at).RangeRateKmPerSec;
                return new DopplerLeadRangeRates(rate, rate, 0);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "FT4 Doppler step could not propagate range rate at {Utc:o}", at);
            }
        }

        return DopplerCatLead.ResolveRangeRates(
            _propagator,
            _cachedSettings,
            site,
            context.TrackState,
            DateTime.UtcNow,
            context.Mode.DownlinkMode,
            context.Mode.UplinkMode);
    }

    private int ResolveWriteThresholdHz(RigSettings settings, RigTrackingContext context)
    {
        if (!SetupVfosPolicy.IsLinearMode(context.Mode.DownlinkMode))
            return _thresholdHz;

        var baseThresholdHz = settings.DopplerThresholdLinearHz;
        if (!settings.DopplerAdaptiveThresholdEnabled || baseThresholdHz <= 0)
            return baseThresholdHz;

        return DopplerAdaptiveThreshold.Resolve(
            baseThresholdHz,
            EstimateDopplerSlewHzPerSec(context),
            enabled: true);
    }

    private double EstimateDopplerSlewHzPerSec(RigTrackingContext context)
    {
        if (_propagator is null || context.TrackState.LookAngles is null)
            return 0;

        var site = _settingsService?.Current.GroundStation ?? new GroundStation();
        return DopplerAdaptiveThreshold.EstimateMaxSlewHzPerSec(
            _propagator,
            context.TrackState.NoradId,
            site,
            DateTime.UtcNow,
            context.TrackState.LookAngles.RangeRateKmPerSec,
            context.Mode.DownlinkKHz,
            context.Mode.UplinkKHz,
            context.DopplerStrategy,
            _isBeaconOnly);
    }

    private GroundStation CurrentSite() =>
        _settingsService?.Current.GroundStation ?? new GroundStation();

    private void EndDopplerPassLog(string reason)
    {
        if (_dopplerPassLogger.ActiveLogPath is null)
            return;

        _dopplerPassLogger.EndPass(DateTime.UtcNow, reason);
        _lastDopplerLogUtc = DateTime.MinValue;
    }

    private void TryLogPeriodicSnapshot(RigSettings settings, RigTrackingContext context)
    {
        if (!settings.DopplerPassLogEnabled)
            return;

        if (!IsAboveHorizon(context))
        {
            EndDopplerPassLog("below_horizon");
            return;
        }

        EnsureDopplerPassLogStarted(settings, context);
        if (_dopplerPassLogger.ActiveLogPath is null)
            return;

        var utc = DateTime.UtcNow;
        if (utc - _lastDopplerLogUtc < DopplerLogSnapshotInterval)
            return;

        _lastDopplerLogUtc = utc;
        var effectiveThreshold = ResolveWriteThresholdHz(settings, context);
        var corrected = ComputeDoppler(context);
        var belowThreshold = !ShouldWrite(
            effectiveThreshold,
            ToHz(corrected.RadioReceiveKHz),
            ToHz(corrected.RadioTransmitKHz),
            context.DopplerStrategy);
        LogDopplerEvent(
            settings,
            context,
            corrected,
            _thresholdHz,
            effectiveThreshold,
            "snapshot",
            belowThreshold: belowThreshold,
            catPaused: _cachedCatPausedOverride ?? settings.CatUpdatesPaused,
            skipReason: ResolveTrackingSkipReason(settings, belowThreshold));
    }

    private void TryLogOperationalSnapshot()
    {
        if (_cachedContext is null || !IsRigConnected())
            return;

        var settings = _cachedSettings;
        if (!settings.DopplerPassLogEnabled || !settings.Enabled)
            return;

        var catPaused = _cachedCatPausedOverride ?? settings.CatUpdatesPaused;
        if (!catPaused && _isTracking)
            return;

        if (!IsAboveHorizon(_cachedContext))
        {
            EndDopplerPassLog("below_horizon");
            return;
        }

        EnsureDopplerPassLogStarted(settings, _cachedContext);
        if (_dopplerPassLogger.ActiveLogPath is null)
            return;

        var utc = DateTime.UtcNow;
        if (utc - _lastDopplerLogUtc < DopplerLogSnapshotInterval)
            return;

        _lastDopplerLogUtc = utc;
        var corrected = ComputeDoppler(_cachedContext);
        LogDopplerEvent(
            settings,
            _cachedContext,
            corrected,
            _thresholdHz,
            ResolveWriteThresholdHz(settings, _cachedContext),
            catPaused ? "cat_paused" : "operational_hold",
            catPaused: catPaused,
            skipReason: ResolveOperationalSkipReason(settings, catPaused));
    }

    private void TryLogDopplerSuspendSnapshot()
    {
        var settings = _cachedSettings;
        if (!settings.DopplerPassLogEnabled || _cachedContext is null)
            return;

        if (!IsAboveHorizon(_cachedContext))
            return;

        EnsureDopplerPassLogStarted(settings, _cachedContext);
        if (_dopplerPassLogger.ActiveLogPath is null)
            return;

        var utc = DateTime.UtcNow;
        if (utc - _lastDopplerLogUtc < DopplerLogSnapshotInterval)
            return;

        _lastDopplerLogUtc = utc;
        LogDopplerEvent(
            settings,
            _cachedContext,
            ComputeDoppler(_cachedContext),
            _thresholdHz,
            ResolveWriteThresholdHz(settings, _cachedContext),
            "doppler_suspend",
            skipReason: "doppler_suspend");
    }

    private void LogDopplerPauseTransition(RigSettings settings, RigTrackingContext context, string eventName)
    {
        if (!settings.DopplerPassLogEnabled || !IsAboveHorizon(context))
            return;

        EnsureDopplerPassLogStarted(settings, context);
        if (_dopplerPassLogger.ActiveLogPath is null)
            return;

        _lastDopplerLogUtc = DateTime.UtcNow;
        var catPaused = eventName == "cat_pause_start";
        LogDopplerEvent(
            settings,
            context,
            ComputeDoppler(context),
            _thresholdHz,
            ResolveWriteThresholdHz(settings, context),
            eventName,
            catPaused: catPaused,
            skipReason: catPaused ? "cat_paused" : null);
    }

    private string? ResolveOperationalSkipReason(RigSettings settings, bool catPaused)
    {
        if (catPaused)
            return "cat_paused";

        if (!settings.Enabled)
            return "rig_disabled";

        if (!_isTracking)
            return "not_tracking";

        return null;
    }

    private string? ResolveTrackingSkipReason(RigSettings settings, bool belowThreshold)
    {
        if (DateTime.UtcNow < _suspendDopplerUntilUtc)
            return "doppler_suspend";

        if (_interactive && !_vfoNotMoving)
            return "vfo_unstable";

        if (belowThreshold)
            return "below_threshold";

        if (_interactive && !settings.DualRadioEnabled && !CanWriteInteractiveSub())
            return "sub_cooldown";

        return null;
    }

    private (long MainDialHz, long DialVsCatHz) ReadDialDiagnostics()
    {
        if (!TryReadReceiveDialHz(out var dialHz))
            return (0, 0);

        if (_lastRigRxHz <= 0)
            return (dialHz, 0);

        return (dialHz, dialHz - _lastRigRxHz);
    }

    private void LogDopplerEvent(
        RigSettings settings,
        RigTrackingContext context,
        CorrectedFrequencies corrected,
        int baseThresholdHz,
        int effectiveThresholdHz,
        string eventName,
        bool wroteRx = false,
        bool wroteTx = false,
        bool belowThreshold = false,
        bool catPaused = false,
        string? skipReason = null,
        string? notes = null)
    {
        if (!settings.DopplerPassLogEnabled)
            return;

        if (!IsAboveHorizon(context))
        {
            EndDopplerPassLog("below_horizon");
            return;
        }

        EnsureDopplerPassLogStarted(settings, context);
        if (_dopplerPassLogger.ActiveLogPath is null)
            return;

        var (mainDialHz, dialVsCatHz) = ReadDialDiagnostics();

        _dopplerPassLogger.Append(DopplerDiagnostics.Capture(
            _propagator,
            settings,
            CurrentSite(),
            context,
            DateTime.UtcNow,
            baseThresholdHz,
            effectiveThresholdHz,
            corrected,
            _lastRigRxHz,
            _lastRigTxHz,
            _passbandDownlinkAdjustKHz,
            _passbandUplinkAdjustKHz,
            eventName,
            wroteRx,
            wroteTx,
            belowThreshold,
            _interactive,
            ResolveDialTrackingMode(context),
            mainDialHz,
            dialVsCatHz,
            _vfoNotMoving,
            _isTracking,
            catPaused,
            skipReason,
            notes));
    }

    private static bool IsAboveHorizon(RigTrackingContext context) =>
        context.TrackState.LookAngles?.ElevationDeg is >= 0;

    /// <summary>
    /// Clears runtime passband trim when the satellite rises above the horizon again on the same
    /// satellite/mode — stored RX/TX offsets in settings are unchanged.
    /// </summary>
    private void TryClearPassbandOnOrbitalAos(RigSettings settings, RigTrackingContext context)
    {
        if (context.TrackState.LookAngles is null)
            return;

        var above = IsAboveHorizon(context);
        if (_wasAboveHorizon == false && above)
            ClearPassbandTrim(settings, context, "passband_aos_reset");

        _wasAboveHorizon = above;
    }

    private void ClearPassbandTrim(RigSettings settings, RigTrackingContext context, string eventName)
    {
        if (Math.Abs(_passbandDownlinkAdjustKHz) < 0.0001 && Math.Abs(_passbandUplinkAdjustKHz) < 0.0001)
            return;

        _passbandDownlinkAdjustKHz = 0;
        _passbandUplinkAdjustKHz = 0;
        _forceFrequencyApply = true;
        LogDopplerEvent(
            settings,
            context,
            ComputeDoppler(context),
            _thresholdHz,
            ResolveWriteThresholdHz(settings, context),
            eventName,
            catPaused: _cachedCatPausedOverride ?? settings.CatUpdatesPaused);
    }

    private void UpdateDopplerPassLogHorizon(RigSettings settings, RigTrackingContext context)
    {
        if (!IsAboveHorizon(context))
            EndDopplerPassLog("below_horizon");
        else
            EnsureDopplerPassLogStarted(settings, context);
    }

    private void EnsureDopplerPassLogStarted(RigSettings settings, RigTrackingContext context)
    {
        if (!settings.DopplerPassLogEnabled || _passKey is null || _dopplerPassLogger.ActiveLogPath is not null)
            return;

        _lastDopplerLogUtc = DateTime.MinValue;
        _dopplerPassLogger.BeginPass(settings, context, DateTime.UtcNow);
    }

    private static long ToHz(double kHz) => (long)Math.Round(kHz * 1000.0);

    private static string FormatEndpointLabel(RigEndpointSettings endpoint) =>
        RigSettings.IsSdrDownlinkEndpoint(endpoint.Type)
            ? $"{endpoint.NetworkHost}:{endpoint.NetworkPort}"
            : endpoint.Port;

    private static string FormatSingleEndpoint(RigSettings settings) =>
        RigSettings.IsFlexNetworkRadio(settings.Type)
            ? $"{settings.NetworkHost}:{settings.NetworkPort}"
            : settings.Port;

    private static bool RigIsConfigured(RigSettings settings) =>
        settings.Enabled && (settings.DualRadioEnabled || settings.Type != RigType.None);

    private static bool HasRequiredPorts(RigSettings settings)
    {
        if (settings.DualRadioEnabled)
            return settings.Downlink.IsConfigured && settings.Uplink.IsConfigured;

        if (RigSettings.IsFlexNetworkRadio(settings.Type))
            return settings.IsFlexNetworkConfigured;

        return !string.IsNullOrWhiteSpace(settings.Port) || settings.Type == RigType.Dummy;
    }

    private bool IsRigConnected() =>
        _cachedSettings.DualRadioEnabled
            ? _downlinkDriver?.IsConnected == true
              && (_cachedSettings.Uplink.Type == RigType.Dummy || _uplinkDriver?.IsConnected == true)
            : _driver?.IsConnected == true;

    private bool SupportsTracking() =>
        _cachedSettings.DualRadioEnabled
            ? _downlinkDriver?.SupportsTracking == true
              && (_cachedSettings.Uplink.Type == RigType.Dummy || _uplinkDriver?.SupportsTracking == true)
            : _driver?.SupportsTracking == true;

    private IRigDriver? RxDriver() =>
        _cachedSettings.DualRadioEnabled ? _downlinkDriver : _driver;

    private IRigDriver? TxDriver() =>
        _cachedSettings.DualRadioEnabled ? _uplinkDriver : _driver;

    /// <summary>
    /// Send a CAT or handshake PTT that arrived while this thread was inside a Doppler write.
    /// Returns true when something was sent. The caller should not start another CI-V command:
    /// many rigs ignore tuning while keyed, and the extra commands delay the unkey.
    /// </summary>
    private bool TryConsumeUrgentRigAction(bool duringDoppler = false)
    {
        int kind;
        int generation;
        bool useRts;
        bool assert;
        lock (_urgentLock)
        {
            kind = _urgentKind;
            if (kind == UrgentNone)
                return false;
            generation = _urgentGeneration;
            useRts = _urgentHandshakeUseRts;
            assert = _urgentHandshakeAssert;
            _urgentKind = UrgentNone;
        }

        if (generation <= _appliedUrgentGeneration)
            return false;

        switch (kind)
        {
            case UrgentPttOn:
                ApplyPttOnWorker(true);
                break;
            case UrgentPttOff:
                ApplyPttOnWorker(false);
                break;
            case UrgentHandshake:
                ApplyHandshakePttOnWorker(useRts, assert);
                break;
            default:
                return false;
        }

        _appliedUrgentGeneration = generation;
        if (duringDoppler)
        {
            _dopplerPreempted = true;
            Log.Information("FT4 PTT went out during a Doppler write, so the rest of that write was skipped");
        }

        return true;
    }

    private void ApplyQueuedRigAction(RigCommand command)
    {
        if (command.Kind == RigCommandKind.SetHandshakePtt)
            ApplyHandshakePttOnWorker(command.HandshakeUseRts, command.HandshakeAssert);
        else
            ApplyPttOnWorker(command.PttTransmit);
    }

    private void ApplyPttOnWorker(bool transmit)
    {
        try
        {
            TxDriver()?.SetPtt(transmit);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Rig PTT {State} failed", transmit ? "on" : "off");
        }
    }

    private void ApplyHandshakePttOnWorker(bool useRts, bool assert)
    {
        try
        {
            var driver = TxDriver();
            if (driver is null)
                return;
            if (!driver.TrySetHandshakeLine(useRts, assert))
                Log.Debug("Handshake PTT not available on {Rig}", driver.RigType);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Handshake PTT failed");
        }
    }

    private double? TryReadUplinkRfPowerWattsOnWorker()
    {
        try
        {
            var driver = TxDriver();
            if (driver is null || !driver.SupportsRfPowerRead)
                return null;

            // Yaesu FT-991 / FT-991A and Kenwood TS-2000: PC; returns watts directly.
            if (driver.TryReadRfPowerWatts(out var wattsDirect))
                return wattsDirect;

            // ICOM CI-V: relative 0–255 mapped via band maximum.
            if (!WithTransmitVfoSelected(driver, d => (d.TryReadRfPowerLevel(out var l), l), out var level))
                return null;

            var hz = _lastRigTxHz;
            if (_cachedContext is { Corrected.RadioTransmitKHz: var txKHz } && txKHz > 0)
                hz = (long)Math.Round(txKHz * 1000.0);

            if (!IcomRfPowerEstimator.TryEstimateWatts(
                    driver.RigType,
                    hz,
                    level,
                    out var watts,
                    _cachedSettings.Ic910PowerClass,
                    _cachedSettings.Ic9700PowerClass))
                return null;

            return watts;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Uplink RF power read failed");
            return null;
        }
    }

    /// <summary>
    /// ICOM level commands act on the selected band, and the operator VFO is the receive band
    /// on Main/Sub satellite layouts, so select the uplink first and restore afterwards.
    /// </summary>
    private bool WithTransmitVfoSelected<T>(IRigDriver driver, Func<IRigDriver, (bool Ok, T Value)> action, out T value)
    {
        driver.SelectVfo(TransmitVfoForWrite(_cachedSettings), force: true);
        try
        {
            var (ok, result) = action(driver);
            value = result;
            return ok;
        }
        finally
        {
            if (!_cachedSettings.DualRadioEnabled)
                driver.SelectVfo(ReceiveVfo(), force: true);
        }
    }

    private bool TrySetUplinkRfPowerWattsOnWorker(double watts)
    {
        try
        {
            var driver = TxDriver();
            if (driver is null || !driver.SupportsRfPowerWrite)
                return false;

            if (driver.TrySetRfPowerWatts(watts))
            {
                Log.Information("Set uplink RF power to {Watts:0} W on {Rig}", watts, driver.RigType);
                return true;
            }

            var hz = _lastRigTxHz;
            if (_cachedContext is { Corrected.RadioTransmitKHz: var txKHz } && txKHz > 0)
                hz = (long)Math.Round(txKHz * 1000.0);

            if (!IcomRfPowerEstimator.TryLevelForWatts(
                    driver.RigType,
                    hz,
                    watts,
                    out var level,
                    _cachedSettings.Ic910PowerClass,
                    _cachedSettings.Ic9700PowerClass))
                return false;

            if (!WithTransmitVfoSelected(driver, d => (d.TrySetRfPowerLevel(level), level), out _))
                return false;

            Log.Information(
                "Set uplink RF power to {Watts:0} W (CI-V level {Level}) on {Rig}",
                watts,
                level,
                driver.RigType);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Uplink RF power set failed");
            return false;
        }
    }

    private (RigStatusKind Kind, string? Port, string? Detail) DescribeConnectionFailure(RigSettings settings)
    {
        if (_identityMismatchDevice is not null)
            return (RigStatusKind.IdentityMismatch, _lastConnectErrorPort, _identityMismatchDevice);

        if (_lastConnectErrorKind == SerialPortConnectErrorKind.DualSamePort)
            return (RigStatusKind.DualRadioSamePort, _lastConnectErrorPort, null);

        if (_lastConnectErrorKind is SerialPortConnectErrorKind.PortNotFound or SerialPortConnectErrorKind.PortBusy)
        {
            var kind = _lastConnectErrorKind == SerialPortConnectErrorKind.PortNotFound
                ? RigStatusKind.SerialPortNotFound
                : RigStatusKind.SerialPortBusy;
            return (kind, _lastConnectErrorPort, _lastConnectEndpoint);
        }

        if (settings.DualRadioEnabled)
            return (RigStatusKind.DualNotConnected, null, _lastConnectError);

        if (RigSettings.IsFlexNetworkRadio(settings.Type))
        {
            var endpoint = FormatSingleEndpoint(settings);
            return string.IsNullOrWhiteSpace(settings.NetworkHost)
                ? (RigStatusKind.NotConnected, null, _lastConnectError)
                : (RigStatusKind.NotConnected, endpoint, _lastConnectError);
        }

        var port = settings.Port;
        return string.IsNullOrWhiteSpace(port)
            ? (RigStatusKind.NotConnected, null, _lastConnectError)
            : (RigStatusKind.NotConnected, port, _lastConnectError);
    }

    private enum RigCommandKind
    {
        PublishContext,
        UpdateSynchronously,
        RunTrackingLoopOnce,
        ApplySelectedCtcss,
        SetPtt,
        SetHandshakePtt,
        SetFt4SlotGatedDoppler,
        ForceFt4DopplerStep,
        ReadUplinkRfPower,
        SetUplinkRfPower,
        Disconnect,
        Drain,
        Shutdown
    }

    private sealed class RigCommand
    {
        public RigCommand(
            RigCommandKind kind,
            RigSettings? settings = null,
            RigTrackingContext? context = null,
            bool reinitializePass = false,
            bool? catPausedOverride = null,
            bool pttTransmit = false,
            bool handshakeUseRts = true,
            bool handshakeAssert = false,
            bool ft4HoldDoppler = false)
        {
            Kind = kind;
            Settings = settings ?? new RigSettings();
            Context = context;
            ReinitializePass = reinitializePass;
            CatPausedOverride = catPausedOverride;
            PttTransmit = pttTransmit;
            HandshakeUseRts = handshakeUseRts;
            HandshakeAssert = handshakeAssert;
            Ft4HoldDoppler = ft4HoldDoppler;
        }

        public RigCommandKind Kind { get; }
        public RigSettings Settings { get; }
        public RigTrackingContext? Context { get; }
        public bool ReinitializePass { get; }
        public bool? CatPausedOverride { get; }
        public bool PttTransmit { get; }
        public bool HandshakeUseRts { get; }
        public bool HandshakeAssert { get; }
        public bool Ft4HoldDoppler { get; }
        public int Generation { get; set; }
        public DateTime? DopplerAtUtc { get; set; }
        public double? RfPowerWatts { get; set; }
        public bool Succeeded { get; set; }
        public ManualResetEventSlim? Completed { get; set; }
    }
}
