using System.Collections.Concurrent;
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
    /// <summary>Consecutive identical Main dial samples before linear CAT (8 × ~100 ms loop ≈ 0.8 s).</summary>
    private const int DialHistoryLength = 8;
    /// <summary>After operator moves the Main dial, defer Sub (uplink) CAT so brief pauses while scanning do not select Sub.</summary>
    private const int InteractiveSubWriteCooldownMs = 2500;
    private const int FmCompanionLegHz = 10;
    /// <summary>Min Hz between dial and last CAT RX to treat as operator tuning (QTrigdoppler uses 1 Hz).</summary>
    private const int KnobTuneCaptureThresholdHz = 1;
    /// <summary>After a CAT frequency write, ignore dial stability briefly so reads settle.</summary>
    private const int PostCatWriteDialSettleMs = 350;
    private static readonly TimeSpan LoopInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CommandWaitTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<RigSettings, IRigDriver>? _driverFactory;
    private readonly Func<RigEndpointSettings, IRigDriver>? _endpointFactory;
    private readonly IOrbitPropagator? _propagator;
    private readonly ISettingsService? _settingsService;
    private readonly long[] _rxDialHistory = new long[DialHistoryLength];
    private readonly object _statusLock = new();
    private readonly object _workerStartLock = new();

    private BlockingCollection<RigCommand>? _commands;
    private Thread? _worker;
    private int _disposed;
    private volatile bool _shutdownRequested;

    private IRigDriver? _driver;
    private IRigDriver? _downlinkDriver;
    private IRigDriver? _uplinkDriver;
    private string? _connectedKey;
    private string? _downlinkConnectedKey;
    private string? _uplinkConnectedKey;
    private string? _passKey;
    private long _lastRigRxHz;
    private long _lastRigTxHz;
    private long _displayRxHz;
    private long _displayTxHz;
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private DateTime _lastRxWriteUtc = DateTime.MinValue;
    private DateTime _lastTxWriteUtc = DateTime.MinValue;
    private int _thresholdHz;
    private bool _interactive;
    private bool _useMainSub;
    private bool _isBeaconOnly;
    private RigVfo _receiveVfo = RigVfo.VfoA;
    private int _rxDialHistoryCount;
    private bool _vfoNotMoving;
    private double _passbandDownlinkAdjustKHz;
    private double _passbandUplinkAdjustKHz;
    private RigStatusKind _statusKind = RigStatusKind.None;
    private string? _statusPort;
    private string? _statusDetail;
    private bool _isTracking;
    private bool _catUpdatesPaused;
    private bool _passInitPending;
    private double? _lastAppliedCtcssHz;
    private bool? _lastAppliedCtcssSquelch;
    private double _lastContextRxOffsetKHz;
    private double _lastContextTxOffsetKHz;
    private DopplerStrategy _lastContextDopplerStrategy = DopplerStrategy.Full;
    private bool _forceFrequencyApply;
    private bool _blockKnobCapture;
    private DateTime _ignoreDialUntilUtc = DateTime.MinValue;
    private DateTime _lastDialChangeUtc = DateTime.MinValue;
    private int _previousRangeRateSign;
    private DateTime _suspendDopplerUntilUtc = DateTime.MinValue;
    private DateTime _suspendConnectUntilUtc = DateTime.MinValue;
    private string? _lastConnectError;
    private bool? _lastPassDownlinkOnVhf;

    private RigSettings _cachedSettings = new();
    private RigTrackingContext? _cachedContext;
    private bool? _cachedCatPausedOverride;
    private RigConnectionStatus _status = new(false, false, RigStatusKind.None, null, null, null, null, false, 0, 0);

    public RigController(
        Func<RigSettings, IRigDriver>? driverFactory = null,
        Func<RigEndpointSettings, IRigDriver>? endpointFactory = null,
        IOrbitPropagator? propagator = null,
        ISettingsService? settingsService = null)
    {
        _driverFactory = driverFactory;
        _endpointFactory = endpointFactory;
        _propagator = propagator;
        _settingsService = settingsService;
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

    public void Disconnect() =>
        Enqueue(new RigCommand(RigCommandKind.Disconnect));

    /// <summary>Blocks until queued commands are processed (unit tests).</summary>
    internal void DrainCommandQueueForTests() =>
        EnqueueAndWait(new RigCommand(RigCommandKind.Drain));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        try
        {
            if (_commands is not null && _worker is { IsAlive: true })
                EnqueueAndWait(new RigCommand(RigCommandKind.Shutdown), TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Rig worker shutdown did not complete cleanly");
        }

        _commands?.Dispose();
        _commands = null;
        _worker?.Join(TimeSpan.FromSeconds(2));
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
                    DrainPendingCommands();
                }

                if (_shutdownRequested)
                    break;

                RunLoopIteration(ignoreDopplerSuspend: false);
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
                    _cachedContext = command.Context;
                    _cachedCatPausedOverride = command.CatPausedOverride;
                    ApplyPublishState(_cachedSettings, _cachedContext, command.ReinitializePass, command.CatPausedOverride);
                    if (!command.ReinitializePass && _forceFrequencyApply)
                        RunLoopIteration(ignoreDopplerSuspend: true);
                    break;

                case RigCommandKind.UpdateSynchronously:
                    _cachedSettings = command.Settings;
                    _cachedContext = command.Context;
                    _cachedCatPausedOverride = command.CatPausedOverride;
                    ApplyPublishState(_cachedSettings, _cachedContext, catPausedOverride: command.CatPausedOverride);
                    RunLoopIteration(ignoreDopplerSuspend: true);
                    break;

                case RigCommandKind.RunTrackingLoopOnce:
                    RunLoopIteration(ignoreDopplerSuspend: true);
                    break;

                case RigCommandKind.ApplySelectedCtcss:
                    ApplySelectedCtcssOnWorker(command.Settings, command.Context!);
                    break;

                case RigCommandKind.Disconnect:
                    _cachedContext = null;
                    TearDownRig();
                    ResetTrackingState();
                    break;

                case RigCommandKind.Drain:
                    break;

                case RigCommandKind.Shutdown:
                    _shutdownRequested = true;
                    break;
            }
        }
        finally
        {
            RefreshStatusSnapshot();
            command.Completed?.Set();
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
            SetRigStatus(DescribeConnectionFailure(settings, _lastConnectError));
            return;
        }

        var effectivePaused = catPausedOverride ?? settings.CatUpdatesPaused;
        var resumingFromCatPause = _catUpdatesPaused && !effectivePaused;
        _catUpdatesPaused = effectivePaused;

        if (context is not null && context.TrackState.LookAngles is not null)
            SyncDisplayFrequencies(ComputeDoppler(context));

        if (context is null || context.TrackState.LookAngles is null)
        {
            _isTracking = false;
            SetRigStatus(effectivePaused ? RigStatusKind.CatPaused : RigStatusKind.Connected);
            return;
        }

        _isBeaconOnly = context.Mode.IsBeaconOnly;

        if (!SupportsTracking())
            return;

        var newPassKey = PassKey(context);
        var passKeyChanged = !string.Equals(_passKey, newPassKey, StringComparison.Ordinal);
        if (passKeyChanged)
            BeginNewPass(settings, context, newPassKey, effectivePaused);

        if (effectivePaused)
        {
            _isTracking = false;
            SetRigStatus(RigStatusKind.CatPaused);
            return;
        }

        if (resumingFromCatPause || _passInitPending)
        {
            RunPassInit(settings, context);
            _passInitPending = false;
        }
        else if (reinitializePass && !passKeyChanged)
            RunPassInit(settings, context);
        else if (context.SelectedCtcssHz is > 0)
            ApplyCtcss(settings, context, force: false);

        NoteContextOffsetChange(context);
        NoteContextDopplerStrategyChange(context);
        _isTracking = true;
        SetRigStatus(RigStatusKind.Tracking);
    }

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
        _passKey = newPassKey;
        _passbandDownlinkAdjustKHz = 0;
        _passbandUplinkAdjustKHz = 0;
        ClearDialHistory();
        _lastAppliedCtcssHz = null;
        _lastAppliedCtcssSquelch = null;
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
        _passKey = null;
        _lastRigRxHz = 0;
        _lastRigTxHz = 0;
        _displayRxHz = 0;
        _displayTxHz = 0;
        _isTracking = false;
        ClearDialHistory();
        _passInitPending = false;
        _catUpdatesPaused = false;
        _cachedCatPausedOverride = null;
        _lastAppliedCtcssHz = null;
        _lastAppliedCtcssSquelch = null;
        _lastPassDownlinkOnVhf = null;
        _receiveVfo = RigVfo.VfoA;
        _suspendDopplerUntilUtc = DateTime.MinValue;
        _previousRangeRateSign = 0;
    }

    private void RunLoopIteration(bool ignoreDopplerSuspend = false)
    {
        if (!IsRigConnected() || _cachedContext is null)
            return;

        if (!_cachedSettings.Enabled || (_cachedCatPausedOverride ?? _cachedSettings.CatUpdatesPaused) || !_isTracking)
            return;

        if (!ignoreDopplerSuspend && DateTime.UtcNow < _suspendDopplerUntilUtc)
            return;

        if (_cachedContext.TrackState.LookAngles is null)
            return;

        if (_interactive && SetupVfosPolicy.IsLinearMode(_cachedContext.Mode.DownlinkMode))
        {
            if (_cachedSettings.DualRadioEnabled)
                ProcessInteractiveLinearDual(_cachedSettings, _cachedContext);
            else
                ProcessInteractiveLinear(_cachedSettings, _cachedContext);
        }
        else
            ProcessAutomaticDoppler(_cachedSettings, _cachedContext);
    }

    private void ProcessInteractiveLinear(RigSettings settings, RigTrackingContext context)
    {
        SampleReceiveDial();

        if (ShouldTrackDopplerAutomatically(context))
        {
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
    /// Dual linear: downlink dial sets passband trim on the RX radio only; uplink keeps doppler while RX dial moves.
    /// </summary>
    private void ProcessInteractiveLinearDual(RigSettings settings, RigTrackingContext context)
    {
        SampleReceiveDial();

        if (ShouldTrackDopplerAutomatically(context))
        {
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
    /// Hands-off linear passes: when passband trim is neutral and Main still shows the last CAT RX write,
    /// track doppler every loop instead of waiting for dial-stability (which our own writes reset).
    /// </summary>
    private bool ShouldTrackDopplerAutomatically(RigTrackingContext context)
    {
        if (!HasNeutralPassbandTrim() || _lastRigRxHz <= 0)
            return false;

        if (DateTime.UtcNow < _ignoreDialUntilUtc)
            return true;

        if (!_vfoNotMoving)
            return false;

        if (!TryReadReceiveDialHz(out var dialHz))
            return false;

        return Math.Abs(dialHz - _lastRigRxHz) < AutomaticDialMatchToleranceHz();
    }

    /// <summary>Match window for CAT-only tracking: within doppler threshold so display jitter is not passband trim.</summary>
    private int AutomaticDialMatchToleranceHz() =>
        _thresholdHz > 0 ? Math.Max(KnobTuneThresholdHz(), _thresholdHz) : KnobTuneThresholdHz();

    private bool HasNeutralPassbandTrim() =>
        Math.Abs(_passbandDownlinkAdjustKHz) < 0.0001 && Math.Abs(_passbandUplinkAdjustKHz) < 0.0001;

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
                || (!_cachedSettings.DualRadioEnabled && Math.Abs(_passbandUplinkAdjustKHz) > 0.0001)))
        {
            _passbandDownlinkAdjustKHz = 0;
            if (!_cachedSettings.DualRadioEnabled)
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
        if (_cachedSettings.DualRadioEnabled)
        {
            newDown = _passbandDownlinkAdjustKHz + deltaKhz;
            newUp = _passbandUplinkAdjustKHz;
        }
        else if (context.Mode.DopplerCorrection == DopplerCorrection.Reverse)
        {
            // REV: Main dial up → downlink nominal up, uplink nominal down.
            newDown = _passbandDownlinkAdjustKHz + deltaKhz;
            newUp = _passbandUplinkAdjustKHz - deltaKhz;
        }
        else
        {
            // NOR: both nominals move with Main dial.
            newDown = _passbandDownlinkAdjustKHz + deltaKhz;
            newUp = _passbandUplinkAdjustKHz + deltaKhz;
        }

        if (NearlyEqual(newDown, _passbandDownlinkAdjustKHz) && NearlyEqual(newUp, _passbandUplinkAdjustKHz))
            return;

        _passbandDownlinkAdjustKHz = newDown;
        _passbandUplinkAdjustKHz = newUp;
        SeedDialHistoryStable(dialHz);
        _vfoNotMoving = true;
    }

    private void SeedDialHistoryStable(long dialHz)
    {
        for (var i = 0; i < DialHistoryLength; i++)
            _rxDialHistory[i] = dialHz;

        _rxDialHistoryCount = DialHistoryLength;
    }

    private static int KnobTuneThresholdHz() => KnobTuneCaptureThresholdHz;

    private void SampleReceiveDial()
    {
        if (DateTime.UtcNow < _ignoreDialUntilUtc)
        {
            if (_lastRigRxHz > 0
                && _rxDialHistoryCount >= DialHistoryLength
                && _rxDialHistory[0] == _lastRigRxHz)
                _vfoNotMoving = true;
            else
                _vfoNotMoving = false;

            return;
        }

        if (!TryReadReceiveDialHz(out var dialHz))
        {
            _vfoNotMoving = false;
            return;
        }

        ShiftDialHistory(dialHz);
        _vfoNotMoving = IsDialHistoryStable();
    }

    private bool IsDialHistoryStable()
    {
        if (_rxDialHistoryCount < DialHistoryLength)
            return false;

        var reference = _rxDialHistory[0];
        for (var i = 1; i < DialHistoryLength; i++)
        {
            if (_rxDialHistory[i] != reference)
                return false;
        }

        return true;
    }

    private bool CanWriteInteractiveSub() =>
        (DateTime.UtcNow - _lastDialChangeUtc).TotalMilliseconds >= InteractiveSubWriteCooldownMs;

    private void ShiftDialHistory(long dialHz)
    {
        if (_rxDialHistoryCount > 0)
        {
            var previous = _rxDialHistory[Math.Min(_rxDialHistoryCount, DialHistoryLength) - 1];
            if (previous != dialHz)
                _lastDialChangeUtc = DateTime.UtcNow;
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
        _lastDialChangeUtc = DateTime.MinValue;
        Array.Clear(_rxDialHistory);
    }

    private bool WriteDopplerFrequencies(RigSettings settings, RigTrackingContext context, bool holdDownlinkCatWrites = false)
    {
        var corrected = ComputeDoppler(context);

        SyncDisplayFrequencies(corrected);

        var rxHz = ToHz(corrected.RadioReceiveKHz);
        var txHz = ToHz(corrected.RadioTransmitKHz);

        var forceApply = _forceFrequencyApply;
        _forceFrequencyApply = false;
        var thresholdHz = _thresholdHz;
        var strategy = context.DopplerStrategy;

        if (!forceApply && settings.DopplerCatLeadEnabled && _propagator is not null && context.TrackState.LookAngles is not null)
        {
            var site = _settingsService?.Current.GroundStation ?? new GroundStation();
            var slope = DopplerCatLead.ComputeRangeRateSlopeKmPerSec2(
                _propagator, context.TrackState.NoradId, site, DateTime.UtcNow,
                context.TrackState.LookAngles.RangeRateKmPerSec);
            var timeSinceLastWrite = (DateTime.UtcNow - _lastWriteUtc).TotalMilliseconds;
            var dopplerReversed = DetectDopplerReversal(context.TrackState.LookAngles.RangeRateKmPerSec);

            if (DopplerWritePacer.ShouldDeferWrite(slope, settings.ReceiveCatDelayMs(), timeSinceLastWrite, dopplerReversed))
                return false;

            // Use adaptive threshold on steep legs
            thresholdHz = DopplerWritePacer.AdaptiveThresholdHz(thresholdHz, slope);
        }

        if (!forceApply && !ShouldWrite(thresholdHz, rxHz, txHz, strategy))
            return false;

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

        var wroteRx = false;
        var wroteTx = false;
        if (settings.Type == RigType.KenwoodTs2000 && _useMainSub && _driver is KenwoodTs2000Driver kenwoodDoppler
            && kenwoodDoppler.IsSatelliteModeActive && (writeRx || writeTx))
        {
            if (kenwoodDoppler.ApplySatelliteDopplerStep(rxHz, txHz))
            {
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

            if (_interactive && wroteTx)
                RestoreOperatorVfo();
        }
        else
        {
            if (writeRx)
                wroteRx = WriteRx(settings, rxHz);
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
            MarkProgrammaticFrequencySettle();
            FinishOffsetKnobCaptureBlock();

            if (context.TrackState.LookAngles is not null)
            {
                var rate = context.TrackState.LookAngles.RangeRateKmPerSec;
                _previousRangeRateSign = rate > 0 ? 1 : rate < 0 ? -1 : 0;
            }
        }

        if ((writeRx && !wroteRx) || (writeTx && !wroteTx))
            _forceFrequencyApply = true;

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

    private bool EnsureSingleConnected(RigSettings settings)
    {
        if (DateTime.UtcNow < _suspendConnectUntilUtc)
            return _driver?.IsConnected == true;

        var key = $"{settings.Type}|{settings.Port}|{settings.BaudRate}|{settings.CivAddress}";
        if (_driver is not null && _connectedKey == key && _driver.IsConnected)
            return true;

        TearDownRig();
        try
        {
            _driver = (_driverFactory ?? RigDriverFactory.Create)(settings);
            _driver.Open();
            _connectedKey = key;
            if (_driver.IsConnected)
            {
                _lastConnectError = null;
                return true;
            }

            _lastConnectError = $"Opened {settings.Port} but CI-V is not responding";
            Log.Warning("Rig opened {Port} for {RigType} but link is not active", settings.Port, settings.Type);
            TearDownRig();
            _suspendConnectUntilUtc = DateTime.UtcNow.AddSeconds(3);
            return false;
        }
        catch (Exception ex)
        {
            _lastConnectError = ex.Message;
            Log.Warning(ex, "Rig connect failed for {RigType} on {Port}", settings.Type, settings.Port);
            _driver?.Dispose();
            _driver = null;
            _suspendConnectUntilUtc = DateTime.UtcNow.AddSeconds(3);
            return false;
        }
    }

    private bool EnsureDualConnected(RigSettings settings)
    {
        var downKey = EndpointConnectionKey(settings.Downlink);
        var upKey = EndpointConnectionKey(settings.Uplink);
        var downOk = _downlinkDriver is not null
            && _downlinkConnectedKey == downKey
            && _downlinkDriver.IsConnected;
        var upOk = _uplinkDriver is not null
            && _uplinkConnectedKey == upKey
            && _uplinkDriver.IsConnected;

        if (downOk && upOk)
            return true;

        TearDownRig();

        try
        {
            _downlinkDriver = CreateEndpointDriver(settings.Downlink);
            _downlinkDriver.Open();
            _downlinkConnectedKey = downKey;
            if (!_downlinkDriver.IsConnected)
                return false;

            _uplinkDriver = CreateEndpointDriver(settings.Uplink);
            _uplinkDriver.Open();
            _uplinkConnectedKey = upKey;
            return _uplinkDriver.IsConnected;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Dual rig connect failed (down {DownPort}, up {UpPort})",
                settings.Downlink.Port, settings.Uplink.Port);
            TearDownRig();
            return false;
        }
    }

    private IRigDriver CreateEndpointDriver(RigEndpointSettings endpoint) =>
        _endpointFactory?.Invoke(endpoint) ?? RigDriverFactory.Create(endpoint);

    private static string EndpointConnectionKey(RigEndpointSettings endpoint) =>
        $"{endpoint.Type}|{endpoint.Port}|{endpoint.BaudRate}|{endpoint.CatDelayMs}|{endpoint.CivAddress}";

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
        AssignReceiveVfo(settings, context);

        if (_isBeaconOnly)
        {
            _driver.SetSatelliteMode(false);
            _driver.SetSplitOn(false);
            ClearCtcssLeavingSatelliteMode(settings, context);
            EnsureBeaconDownlinkOnMain(context);
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
            _driver.SetSatelliteMode(true);
            if (settings.Type == RigType.KenwoodTs2000 && !_driver.IsSatelliteModeActive)
            {
                Log.Warning(
                    "TS-2000 SATL not confirmed via SA; — continuing FA/FB tracking (no FR/split in SAT).");
            }

            // IC-910/9100/9700 reject split CI-V in satellite (Main/Sub) mode with NAK.
            // Kenwood SATL uses FA/FB only — never FR/FT (driver no-ops split while in SAT).
            if (_useMainSub && !IsIcomSatelliteLayoutRig(settings.Type) && settings.Type != RigType.KenwoodTs2000)
                _driver.SetSplitOn(false);
            if (settings.Type == RigType.IcomIc821h && _driver is IcomIc821hDriver ic821)
                ic821.EstablishSatelliteVfoState();
            Thread.Sleep(150);
        }

        if (_useMainSub)
            TryBandSwap(context);

        var setup = SetupVfosPolicy.Evaluate(
            context.EffectiveDownlinkMode,
            settings.DopplerThresholdFmHz,
            settings.DopplerThresholdLinearHz);
        _thresholdHz = setup.ThresholdHz;
        _interactive = setup.Interactive;

        // FT-847 can revert to narrow FM when SAT frequencies/CTCSS are programmed after mode.
        var deferModeSetup = settings.Type == RigType.YaesuFt847;
        var isKenwoodSat = settings.Type == RigType.KenwoodTs2000 && _useMainSub;
        if (!deferModeSetup && !isKenwoodSat)
            ConfigureVfoModes(context);

        var corrected = ComputeDoppler(context);
        var rxHz = ToHz(corrected.RadioReceiveKHz);
        var txHz = ToHz(corrected.RadioTransmitKHz);
        _lastRigRxHz = 0;
        _lastRigTxHz = 0;
        var initResult = isKenwoodSat && _driver is KenwoodTs2000Driver kenwoodInit
            ? InitializeKenwoodSatellitePass(settings, kenwoodInit, context, rxHz, txHz)
            : WriteInitialFrequencies(settings, rxHz, txHz);
        ApplyCtcss(settings, context, force: true);

        if (deferModeSetup)
            ConfigureVfoModes(context);

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
        _lastPassDownlinkOnVhf = RigSatModeHelper.IsVhfCenterKHz(context.Mode.DownlinkKHz);
        MarkProgrammaticFrequencySettle();
        _suspendDopplerUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
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

        _uplinkDriver.SelectVfo(RigVfo.Main, force: true);
        _uplinkDriver.SetToneOn(false);
        _uplinkDriver.SetToneSquelchOn(false);

        var setup = SetupVfosPolicy.Evaluate(
            context.EffectiveDownlinkMode,
            settings.DopplerThresholdFmHz,
            settings.DopplerThresholdLinearHz);
        _thresholdHz = setup.ThresholdHz;
        _interactive = setup.Interactive;

        var corrected = ComputeDoppler(context);
        var rxHz = ToHz(corrected.RadioReceiveKHz);
        var txHz = ToHz(corrected.RadioTransmitKHz);
        _lastRigRxHz = 0;
        _lastRigTxHz = 0;

        _downlinkDriver.SelectVfo(RigVfo.Main);
        _downlinkDriver.SetMode(context.EffectiveDownlinkMode);
        var rxWritten = _downlinkDriver.SetFrequencyHz(rxHz);

        var txWritten = true;
        if (!_isBeaconOnly)
        {
            _uplinkDriver.SelectVfo(RigVfo.Main);
            _uplinkDriver.SetMode(context.EffectiveUplinkMode);
            txWritten = _uplinkDriver.SetFrequencyHz(txHz);
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
        MarkProgrammaticFrequencySettle();
        _suspendDopplerUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
        RestoreOperatorVfo();
    }

    private void TryBandSwap(RigTrackingContext context)
    {
        if (_driver is null)
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

        var targetRxHz = ToHz(context.Corrected.RadioReceiveKHz);
        var mainHz = _driver.ReadFrequencyHz(RigVfo.Main);
        if (mainHz is > 0)
        {
            if (canExchange)
            {
                if (mainHz.Value > 400_000_000 && targetRxHz < 400_000_000)
                    _driver.ExchangeVfos();
                else if (mainHz.Value < 200_000_000 && targetRxHz > 200_000_000)
                    _driver.ExchangeVfos();
                else if (RigSatModeHelper.NeedsMainSubBandSwap(mainHz.Value, context.Mode.DownlinkKHz))
                    _driver.ExchangeVfos();
            }

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
            or RigType.YaesuFt847 or RigType.KenwoodTs2000 or RigType.Dummy;

    private static bool UsesIcomSatelliteOnlyLayout(RigType type) =>
        type == RigType.IcomIc821h;

    private static bool ShouldUseMainSubLayout(RigSettings settings, RigTrackingContext context) =>
        UsesMainSubSatelliteLayout(settings.Type)
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

        if (!KenwoodCatCodec.TryGetModeCode(context.EffectiveDownlinkMode, out var downlinkMode)
            || !KenwoodCatCodec.TryGetModeCode(context.EffectiveUplinkMode, out var uplinkMode))
        {
            return new InitialFrequencyWriteResult(false, false);
        }

        kenwood.ApplySatellitePassFrequencies(downlinkHz, uplinkHz, downlinkMode, uplinkMode);
        return new InitialFrequencyWriteResult(true, !_isBeaconOnly);
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

    private void ApplyCtcss(RigSettings settings, RigTrackingContext context, bool force)
    {
        var driver = TxDriver();
        if (driver is null || context.SelectedCtcssHz is not { } hz || hz <= 0)
            return;

        var squelch = settings.TransmitRegion() == RigRegion.USA;
        if (!force && _lastAppliedCtcssHz == hz && _lastAppliedCtcssSquelch == squelch)
            return;

        // CTCSS on uplink VFO: tone Hz then enable (US: TSQL, EU: repeater tone).
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

    private static RigVfo UplinkVfoForCtcss(RigSettings settings, RigTrackingContext context)
    {
        if (settings.DualRadioEnabled)
            return RigVfo.Main;

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
            _driver.SelectVfo(RigVfo.Main);
            if (settings.ReceiveRegion() == RigRegion.USA)
                _driver.SetToneSquelchOn(false);
            else
                _driver.SetToneOn(false);
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
        if (_isBeaconOnly)
            return new InitialFrequencyWriteResult(rxWritten, TxWritten: true);

        _uplinkDriver.SelectVfo(RigVfo.Main);
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

        _lastContextRxOffsetKHz = context.ReceiveOffsetKHz;
        _lastContextTxOffsetKHz = context.TransmitOffsetKHz;
        _forceFrequencyApply = true;
        _blockKnobCapture = true;
        ClearDialHistory();
        MarkProgrammaticFrequencySettle();
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
        var driver = RxDriver();
        if (driver is null)
            return;

        driver.SelectVfo(ReceiveVfo(), force: _interactive);
        if (!_interactive)
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
            _lastRigRxHz = hz;
            return true;
        }

        return false;
    }

    private bool WriteTx(RigSettings settings, long hz)
    {
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

    private RigVfo TransmitVfoForWrite(RigSettings settings) =>
        settings.DualRadioEnabled ? RigVfo.Main : TransmitVfo();

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
        return DopplerCatLead.ResolveRangeRates(
            _propagator,
            _cachedSettings,
            site,
            context.TrackState,
            DateTime.UtcNow);
    }

    /// <summary>
    /// Detects whether the Doppler direction has reversed since the last successful write.
    /// Does not update state — state is updated after a successful write.
    /// </summary>
    private bool DetectDopplerReversal(double rangeRateKmPerSec)
    {
        var currentSign = rangeRateKmPerSec > 0 ? 1 : rangeRateKmPerSec < 0 ? -1 : 0;
        if (_previousRangeRateSign == 0 || currentSign == 0)
            return false; // No previous state or zero-crossing not yet confirmed
        return currentSign != _previousRangeRateSign;
    }

    private static long ToHz(double kHz) => (long)Math.Round(kHz * 1000.0);

    private static bool RigIsConfigured(RigSettings settings) =>
        settings.Enabled && (settings.DualRadioEnabled || settings.Type != RigType.None);

    private static bool HasRequiredPorts(RigSettings settings)
    {
        if (settings.DualRadioEnabled)
            return settings.Downlink.IsConfigured && settings.Uplink.IsConfigured;

        return !string.IsNullOrWhiteSpace(settings.Port) || settings.Type == RigType.Dummy;
    }

    private bool IsRigConnected() =>
        _cachedSettings.DualRadioEnabled
            ? _downlinkDriver?.IsConnected == true && _uplinkDriver?.IsConnected == true
            : _driver?.IsConnected == true;

    private bool SupportsTracking() =>
        _cachedSettings.DualRadioEnabled
            ? _downlinkDriver?.SupportsTracking == true && _uplinkDriver?.SupportsTracking == true
            : _driver?.SupportsTracking == true;

    private IRigDriver? RxDriver() =>
        _cachedSettings.DualRadioEnabled ? _downlinkDriver : _driver;

    private IRigDriver? TxDriver() =>
        _cachedSettings.DualRadioEnabled ? _uplinkDriver : _driver;

    private static (RigStatusKind Kind, string? Port, string? Detail) DescribeConnectionFailure(
        RigSettings settings,
        string? detail = null)
    {
        if (settings.DualRadioEnabled)
            return (RigStatusKind.DualNotConnected, null, detail);

        var port = settings.Port;
        return string.IsNullOrWhiteSpace(port)
            ? (RigStatusKind.NotConnected, null, detail)
            : (RigStatusKind.NotConnected, port, detail);
    }

    private enum RigCommandKind
    {
        PublishContext,
        UpdateSynchronously,
        RunTrackingLoopOnce,
        ApplySelectedCtcss,
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
            bool? catPausedOverride = null)
        {
            Kind = kind;
            Settings = settings ?? new RigSettings();
            Context = context;
            ReinitializePass = reinitializePass;
            CatPausedOverride = catPausedOverride;
        }

        public RigCommandKind Kind { get; }
        public RigSettings Settings { get; }
        public RigTrackingContext? Context { get; }
        public bool ReinitializePass { get; }
        public bool? CatPausedOverride { get; }
        public ManualResetEventSlim? Completed { get; set; }
    }
}
