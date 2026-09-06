using System.Collections.Concurrent;
using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Rotator;
using OscarWatch.Core.Services;
using Serilog;

namespace OscarWatch.Rotator;

/// <summary>
/// All rotator I/O runs on a dedicated background thread; the UI only enqueues commands and reads status.
/// </summary>
public sealed class RotatorController : IRotatorController, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<RotatorController>();
    private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CommandWaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Consecutive below-threshold / missing-target ticks required before park-after-pass
    /// once this pass has already been tracking. Prevents a single glitch tick at AOS from
    /// slewing home and then resuming.
    /// </summary>
    internal const int ParkAfterPassConfirmTicks = 3;

    private readonly Func<RotatorSettings, IRotatorDriver>? _driverFactory;
    private readonly IOrbitPropagator? _propagator;
    private readonly ISettingsService? _settingsService;
    private readonly object _statusLock = new();
    private readonly object _workerStartLock = new();

    private BlockingCollection<RotatorCommand>? _commands;
    private Thread? _worker;
    private int _disposed;
    private volatile bool _shutdownRequested;

    private IRotatorDriver? _rotator;
    private string? _connectedPort;
    private string? _connectedElevationPort;
    private int _connectedBaudRate;
    private string? _connectedNetworkHost;
    private int _connectedNetworkPort;
    private bool _connectedUsesNetworkEndpoint;
    private RotatorType _connectedType;
    private string? _lastTargetNoradId;
    private double? _lastAzimuth;
    private double? _lastElevation;
    private bool _parked;
    private bool _manualParkActive;
    private bool _standbyActive;
    private bool _standbyManualActive;
    private bool _trackingHoldAfterStop;
    /// <summary>True after we have commanded track for the current pass (el ≥ track start).</summary>
    private bool _passTrackingActive;
    private int _parkAfterPassStreak;
    private int? _displayAzimuth;
    private int? _displayElevation;
    private int? _displayCommandedAzimuth;
    private int? _displayCommandedElevation;
    private int? _displayCompassAzimuth;

    private RotatorSettings _cachedSettings = new();
    private SatelliteTrackState? _cachedTarget;
    private RotatorConnectionKind _connectionKind = RotatorConnectionKind.Disconnected;
    private string? _connectionDetail;
    private RotatorPositionStatus _positionStatus = new(false, null, null);

    // Keyhole avoidance state
    private KeyholePlan? _keyholePlan;
    private bool _keyholeFlippedActive;
    private bool _isPrePositioning;
    private PassInfo? _activePassInfo;

    public RotatorController(
        Func<RotatorSettings, IRotatorDriver>? driverFactory = null,
        IOrbitPropagator? propagator = null,
        ISettingsService? settingsService = null)
    {
        _driverFactory = driverFactory;
        _propagator = propagator;
        _settingsService = settingsService;
    }

    public RotatorPositionStatus GetPositionStatus()
    {
        lock (_statusLock)
            return _positionStatus;
    }

    /// <summary>Enqueue latest pass/settings for the rotator thread (~1 Hz from UI).</summary>
    public void Update(RotatorSettings settings, SatelliteTrackState? target) =>
        Enqueue(new RotatorCommand(RotatorCommandKind.PublishTarget, settings, target));

    public void Park(RotatorSettings settings) =>
        Enqueue(new RotatorCommand(RotatorCommandKind.Park, settings));

    public void MoveTo(double azimuthDeg, double elevationDeg, RotatorSettings settings) =>
        Enqueue(new RotatorCommand(RotatorCommandKind.ManualMove, settings, azimuthDeg: azimuthDeg, elevationDeg: elevationDeg));

    public void Stop(RotatorSettings settings) =>
        Enqueue(new RotatorCommand(RotatorCommandKind.Stop, settings));

    public void ResumeTracking(RotatorSettings settings) =>
        Enqueue(new RotatorCommand(RotatorCommandKind.ResumeTracking, settings));

    public void SetStandby(bool active, RotatorSettings settings) =>
        Enqueue(new RotatorCommand(RotatorCommandKind.SetStandby, settings, standbyActive: active));

    public void Disconnect() =>
        Enqueue(new RotatorCommand(RotatorCommandKind.Disconnect));

    /// <summary>Disconnect and block until the rotator worker has closed the COM port.</summary>
    public void DisconnectAndWait() =>
        EnqueueAndWait(new RotatorCommand(RotatorCommandKind.Disconnect), TimeSpan.FromSeconds(3));

    /// <summary>Supply the active pass for keyhole planning and Smart450 north-cross detection.</summary>
    public void SetActivePass(PassInfo? pass) =>
        Enqueue(new RotatorCommand(RotatorCommandKind.SetActivePass, passInfo: pass));

    /// <summary>Synchronous tracking tick (unit tests).</summary>
    internal void UpdateSynchronously(RotatorSettings settings, SatelliteTrackState? target) =>
        EnqueueAndWait(new RotatorCommand(RotatorCommandKind.UpdateSynchronously, settings, target));

    /// <summary>Supply the active pass synchronously (unit tests).</summary>
    internal void SetActivePassSynchronously(PassInfo? pass) =>
        EnqueueAndWait(new RotatorCommand(RotatorCommandKind.SetActivePass, passInfo: pass));

    /// <summary>The current keyhole plan computed on target change (exposed for testing).</summary>
    internal KeyholePlan? CurrentKeyholePlan => _keyholePlan;

    /// <summary>Inject a keyhole plan directly for unit testing (bypasses planner).</summary>
    internal void SetKeyholePlanForTests(KeyholePlan? plan) => _keyholePlan = plan;

    /// <summary>Whether flipped tracking is currently active (exposed for testing).</summary>
    internal bool IsKeyholeFlippedActive => _keyholeFlippedActive;

    /// <summary>Whether the controller is currently pre-positioning for a flipped pass (exposed for testing).</summary>
    internal bool IsPrePositioning => _isPrePositioning;

    /// <summary>Blocks until queued commands are processed (unit tests).</summary>
    internal void DrainCommandQueueForTests() =>
        EnqueueAndWait(new RotatorCommand(RotatorCommandKind.Drain));

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
                var command = new RotatorCommand(RotatorCommandKind.Shutdown) { Completed = done };
                _commands.Add(command);
                if (!done.Wait(TimeSpan.FromSeconds(3)))
                    Log.Warning("Rotator worker shutdown did not complete in time");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Rotator worker shutdown did not complete cleanly");
        }

        _worker?.Join(TimeSpan.FromSeconds(2));
        _commands?.Dispose();
        _commands = null;
    }

    private void Enqueue(RotatorCommand command)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        EnsureWorker();
        _commands!.Add(command);
    }

    private void EnqueueAndWait(RotatorCommand command, TimeSpan? timeout = null)
    {
        using var done = new ManualResetEventSlim(false);
        command.Completed = done;
        Enqueue(command);
        if (!done.Wait(timeout ?? CommandWaitTimeout))
            throw new TimeoutException("Rotator worker did not complete the command in time.");
    }

    private void EnsureWorker()
    {
        lock (_workerStartLock)
        {
            if (_worker is { IsAlive: true })
                return;

            _shutdownRequested = false;
            _commands = new BlockingCollection<RotatorCommand>();
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "OscarWatch.Rotator"
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
                var ranSynchronousTracking = false;
                if (_commands!.TryTake(out var command, LoopInterval))
                {
                    ranSynchronousTracking = ProcessCommand(command);
                    ranSynchronousTracking |= DrainPendingCommands();
                }

                if (_shutdownRequested)
                    break;

                // UpdateSynchronously already ran tracking for this wake; do not double-count
                // park-after-pass confirm ticks (or send a duplicate track/park).
                if (!ranSynchronousTracking)
                    RunTrackingIteration();
                RefreshPositionSnapshot();
            }
        }
        finally
        {
            TearDownRotator();
            RefreshPositionSnapshot();
        }
    }

    private bool DrainPendingCommands()
    {
        var ranSynchronousTracking = false;
        while (_commands!.TryTake(out var command, 0))
            ranSynchronousTracking |= ProcessCommand(command);
        return ranSynchronousTracking;
    }

    /// <returns>True when this command already ran <see cref="RunTrackingIteration"/>.</returns>
    private bool ProcessCommand(RotatorCommand command)
    {
        var ranSynchronousTracking = false;
        try
        {
            switch (command.Kind)
            {
                case RotatorCommandKind.PublishTarget:
                    _cachedSettings = command.Settings;
                    _cachedTarget = command.Target;
                    break;

                case RotatorCommandKind.UpdateSynchronously:
                    _cachedSettings = command.Settings;
                    _cachedTarget = command.Target;
                    RunTrackingIteration();
                    ranSynchronousTracking = true;
                    break;

                case RotatorCommandKind.Park:
                    _cachedSettings = command.Settings;
                    ParkOnWorker(command.Settings);
                    break;

                case RotatorCommandKind.ManualMove:
                    _cachedSettings = command.Settings;
                    ManualMoveOnWorker(command.Settings, command.AzimuthDeg!.Value, command.ElevationDeg!.Value);
                    break;

                case RotatorCommandKind.Stop:
                    _cachedSettings = command.Settings;
                    StopOnWorker(command.Settings);
                    break;

                case RotatorCommandKind.ResumeTracking:
                    _cachedSettings = command.Settings;
                    _trackingHoldAfterStop = false;
                    break;

                case RotatorCommandKind.SetStandby:
                    _cachedSettings = command.Settings;
                    SetStandbyOnWorker(command.StandbyActive!.Value, command.Settings);
                    break;

                case RotatorCommandKind.SetActivePass:
                    _activePassInfo = command.PassInfo;
                    RecomputeKeyholePlan(_cachedSettings);
                    break;

                case RotatorCommandKind.Disconnect:
                    // Clear cached settings so the tracking loop does not reopen the port.
                    _cachedSettings = new RotatorSettings();
                    _cachedTarget = null;
                    TearDownRotator();
                    ResetTrackingState();
                    _connectionKind = RotatorConnectionKind.Disconnected;
                    _connectionDetail = null;
                    break;

                case RotatorCommandKind.Drain:
                    break;

                case RotatorCommandKind.Shutdown:
                    _shutdownRequested = true;
                    break;
            }
        }
        finally
        {
            RefreshPositionSnapshot();
            command.Completed?.Set();
        }

        return ranSynchronousTracking;
    }

    private void RunTrackingIteration()
    {
        var settings = _cachedSettings;
        if (!settings.Enabled)
        {
            TearDownRotator();
            ResetTrackingState();
            _connectionKind = RotatorConnectionKind.Disabled;
            _connectionDetail = null;
            return;
        }

        if (!settings.HasConfiguredEndpoint)
        {
            TearDownRotator();
            ResetTrackingState();
            _connectionKind = RotatorConnectionKind.NoPortSelected;
            _connectionDetail = null;
            return;
        }

        if (!EnsureConnected(settings))
            return;

        _connectionKind = RotatorConnectionKind.Connected;
        _connectionDetail = null;

        PollPosition();

        if (_trackingHoldAfterStop && !_standbyActive)
            return;

        if (_standbyActive)
        {
            if (!_standbyManualActive && settings.ParkAfterPass)
                TryPark(settings);
            return;
        }

        var target = _cachedTarget;
        if (target?.NoradId != _lastTargetNoradId)
        {
            var switchedSatellite = target?.NoradId is { } newId
                && _lastTargetNoradId is { } oldId
                && !string.Equals(newId, oldId, StringComparison.Ordinal);
            var acquiredFocus = target?.NoradId is not null && _lastTargetNoradId is null;

            _lastTargetNoradId = target?.NoradId;
            _lastAzimuth = null;
            _lastElevation = null;
            _parked = false;
            ClearTrackingAzimuthDisplay();
            RecomputeKeyholePlan(settings);

            // Do not clear pass-tracking when the target briefly becomes null (glitch tick);
            // that is handled by park-after-pass confirm streak instead.
            if (switchedSatellite || acquiredFocus)
            {
                _passTrackingActive = false;
                _parkAfterPassStreak = 0;
            }
        }

        if (_manualParkActive)
        {
            if (target?.LookAngles is { } look && look.ElevationDeg >= settings.TrackStartElevationDeg)
            {
                TryPark(settings);
                return;
            }

            // Pass ended (or idle manual park): release the hold so tracking can resume on the next rise.
            // Keep _parked true — the rotator is still at the park position.
            _manualParkActive = false;
        }

        // Keyhole pre-positioning: if we have a flipped plan and are in the pre-position window,
        // slew to flipped start azimuth before normal tracking begins.
        if (TryHandleKeyholePrePosition(settings, target))
            return;

        if (target?.LookAngles is { } lookAngles)
        {
            if (lookAngles.ElevationDeg >= settings.TrackStartElevationDeg)
            {
                _parkAfterPassStreak = 0;
                _passTrackingActive = true;

                var az = lookAngles.AzimuthDeg;
                var ahead = target.AheadAzimuthDeg;

                // Apply flipped tracking if plan is FlippedStart and we should be flipped
                if (ShouldTrackFlipped(settings, lookAngles.ElevationDeg))
                {
                    az = RotatorAzimuthPlanner.Normalize360(az + 180.0);
                    ahead = null; // ahead azimuth not meaningful when flipped
                    _keyholeFlippedActive = true;
                    _isPrePositioning = false;
                }
                else if (_keyholeFlippedActive && lookAngles.ElevationDeg < settings.KeyholeThresholdDeg)
                {
                    // Dropped below threshold after being flipped — transition to normal
                    _keyholeFlippedActive = false;
                }

                TryTrack(settings, az, lookAngles.ElevationDeg, ahead);
            }
            else
                ConsiderParkAfterPass(settings);
        }
        else
            ConsiderParkAfterPass(settings);
    }

    /// <summary>
    /// Parks when the satellite is below track-start (or target is missing).
    /// Before this pass has tracked, parks immediately (idle / waiting for AOS).
    /// After tracking has started, requires <see cref="ParkAfterPassConfirmTicks"/> consecutive
    /// end-of-pass ticks so a single bad sample cannot interrupt the pass.
    /// </summary>
    private void ConsiderParkAfterPass(RotatorSettings settings)
    {
        if (!settings.ParkAfterPass)
        {
            _parkAfterPassStreak = 0;
            return;
        }

        if (!_passTrackingActive)
        {
            TryPark(settings, afterPass: true);
            return;
        }

        _parkAfterPassStreak++;
        if (_parkAfterPassStreak < ParkAfterPassConfirmTicks)
            return;

        TryPark(settings, afterPass: true);
        _passTrackingActive = false;
        _parkAfterPassStreak = 0;
    }

    private void ParkOnWorker(RotatorSettings settings)
    {
        if (!settings.Enabled || !settings.HasConfiguredEndpoint)
            return;

        if (!EnsureConnected(settings))
            return;

        _trackingHoldAfterStop = false;
        _lastAzimuth = null;
        _lastElevation = null;

        if (_standbyActive)
        {
            _standbyManualActive = false;
            _parked = false;
            TryPark(settings, force: true);
            PollPosition();
            return;
        }

        _manualParkActive = true;
        _parked = false;
        _passTrackingActive = false;
        _parkAfterPassStreak = 0;
        TryPark(settings, force: true);
        PollPosition();
    }

    private void ManualMoveOnWorker(RotatorSettings settings, double azimuthDeg, double elevationDeg)
    {
        if (!_standbyActive)
            return;

        if (!settings.Enabled || !settings.HasConfiguredEndpoint)
            return;

        if (!EnsureConnected(settings))
            return;

        if (_rotator is null)
            return;

        var (az, el) = RotatorCalibration.ApplyOffsets(azimuthDeg, elevationDeg, settings);

        _standbyManualActive = true;
        _parked = false;
        _displayCommandedAzimuth = (int)Math.Round(az);
        _displayCommandedElevation = (int)Math.Round(el);
        _displayCompassAzimuth = (int)Math.Round(RotatorAzimuthPlanner.Normalize360(az));

        try
        {
            _rotator.SetPosition(az, el, settings);
            _lastAzimuth = Math.Round(az);
            _lastElevation = Math.Round(el);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Rotator manual move failed at Az={Az} El={El}", az, el);
            TearDownRotator();
        }

        PollPosition();
    }

    private void StopOnWorker(RotatorSettings settings)
    {
        if (!settings.Enabled || !settings.HasConfiguredEndpoint)
            return;

        if (!EnsureConnected(settings))
            return;

        if (_rotator is null)
            return;

        try
        {
            _rotator.Stop();
            if (!_standbyActive)
                _trackingHoldAfterStop = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Rotator stop failed");
            TearDownRotator();
        }

        PollPosition();
    }

    private void SetStandbyOnWorker(bool active, RotatorSettings settings)
    {
        _cachedSettings = settings;
        _standbyActive = active;

        if (!active)
        {
            _lastTargetNoradId = null;
            _lastAzimuth = null;
            _lastElevation = null;
            _parked = false;
            _manualParkActive = false;
            _standbyManualActive = false;
            _trackingHoldAfterStop = false;
            _passTrackingActive = false;
            _parkAfterPassStreak = 0;
            return;
        }

        _manualParkActive = false;
        _standbyManualActive = false;

        if (!settings.Enabled || !settings.HasConfiguredEndpoint)
            return;

        if (!EnsureConnected(settings))
            return;

        _parked = false;
        if (settings.ParkAfterPass)
            TryPark(settings);
        PollPosition();
    }

    private void TearDownRotator()
    {
        _rotator?.Dispose();
        _rotator = null;
        _connectedPort = null;
        _connectedElevationPort = null;
        _connectedBaudRate = 0;
        _connectedNetworkHost = null;
        _connectedNetworkPort = 0;
        _connectedUsesNetworkEndpoint = false;
        _connectedType = default;
    }

    private void ResetTrackingState()
    {
        _lastTargetNoradId = null;
        _lastAzimuth = null;
        _lastElevation = null;
        _parked = false;
        _manualParkActive = false;
        _standbyActive = false;
        _standbyManualActive = false;
        _trackingHoldAfterStop = false;
        _passTrackingActive = false;
        _parkAfterPassStreak = 0;
        _displayAzimuth = null;
        _displayElevation = null;
        _displayCommandedAzimuth = null;
        _displayCommandedElevation = null;
        _displayCompassAzimuth = null;
        _keyholePlan = null;
        _keyholeFlippedActive = false;
        _isPrePositioning = false;
    }

    private void RefreshPositionSnapshot()
    {
        lock (_statusLock)
            _positionStatus = new(
                _rotator is not null,
                _displayAzimuth,
                _displayElevation,
                _displayCommandedAzimuth,
                _displayCompassAzimuth,
                _parked,
                _connectionKind,
                _connectionDetail,
                IsKeyholeAvoidanceActive: _keyholeFlippedActive,
                IsPrePositioning: _isPrePositioning,
                IsTrackingHeld: _trackingHoldAfterStop,
                CommandedElevationDeg: _displayCommandedElevation);
    }

    private void ClearTrackingAzimuthDisplay()
    {
        _displayCommandedAzimuth = null;
        _displayCommandedElevation = null;
        _displayCompassAzimuth = null;
    }

    /// <summary>
    /// Recomputes the keyhole plan based on current settings, active pass, and target.
    /// If keyhole avoidance is disabled, the propagator is unavailable, or no active pass is set,
    /// the plan is cleared and normal tracking proceeds.
    /// </summary>
    private void RecomputeKeyholePlan(RotatorSettings settings)
    {
        _keyholeFlippedActive = false;

        if (!settings.KeyholeAvoidanceEnabled
            || settings.ElevationRange != RotatorElevationRange.Deg180
            || _propagator is null
            || _activePassInfo is null)
        {
            _keyholePlan = null;
            return;
        }

        var target = _cachedTarget;
        if (target is null)
        {
            _keyholePlan = null;
            return;
        }

        var site = _settingsService?.Current.GroundStation ?? new GroundStation();

        try
        {
            var profile = PassProfileBuilder.Build(_activePassInfo, target.NoradId, site, _propagator);
            if (profile is null)
            {
                Log.Information("Keyhole planning: profile build returned null (too many propagation failures), falling back to normal tracking");
                _keyholePlan = null;
                return;
            }

            var plannerSettings = new KeyholePlannerSettings(
                settings.KeyholeThresholdDeg,
                settings.SlewRateDegPerSec,
                settings.ParkAzimuthDeg);

            _keyholePlan = KeyholePlanner.Analyse(profile, plannerSettings);

            if (_keyholePlan.Strategy == KeyholeStrategy.Normal)
            {
                Log.Debug("Keyhole planning: pass classified as Normal (flipped not beneficial)");
            }
            else
            {
                Log.Information(
                    "Keyhole planning: FlippedStart recommended for {NoradId}, flipped az={FlippedAz:F1}°, lead time={LeadTime}",
                    target.NoradId,
                    _keyholePlan.FlippedStartAzimuthDeg,
                    _keyholePlan.PrePositionLeadTime);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Keyhole planning failed, falling back to normal tracking");
            _keyholePlan = null;
        }
    }

    /// <summary>
    /// Handles the keyhole pre-positioning branch. If a FlippedStart plan is active and the
    /// current time is within the pre-position window (AOS − PrePositionLeadTime to AOS),
    /// commands the rotator to slew to the flipped start azimuth at the track-start elevation
    /// (not below 0°).
    /// Returns true if pre-positioning was handled (caller should return early), false otherwise.
    /// </summary>
    private bool TryHandleKeyholePrePosition(RotatorSettings settings, SatelliteTrackState? target)
    {
        // Only applies if we have a flipped plan with pre-position data
        if (_keyholePlan?.Strategy != KeyholeStrategy.FlippedStart
            || _keyholePlan.FlippedStartAzimuthDeg is null
            || _keyholePlan.PrePositionLeadTime is null
            || _activePassInfo is null)
        {
            _isPrePositioning = false;
            return false;
        }

        var now = DateTime.UtcNow;
        var aosUtc = _activePassInfo.AosUtc;
        var prePositionStart = aosUtc - _keyholePlan.PrePositionLeadTime.Value;

        // If we are past AOS, pre-positioning window has closed — let normal/flipped tracking handle it
        if (now >= aosUtc)
        {
            _isPrePositioning = false;
            return false;
        }

        // If we are before the pre-position start time, nothing to do yet
        if (now < prePositionStart)
        {
            _isPrePositioning = false;
            return false;
        }

        // We are in the pre-position window: AOS - PrePositionLeadTime ≤ now < AOS
        // Check if there is insufficient lead time (already past the point where we could complete the slew)
        var remainingTime = aosUtc - now;
        if (remainingTime < TimeSpan.FromSeconds(1))
        {
            // Insufficient lead time — fall back to normal tracking
            Log.Information("Keyhole pre-positioning: insufficient lead time ({Remaining}s remaining), falling back to normal tracking",
                remainingTime.TotalSeconds);
            _isPrePositioning = false;
            return false;
        }

        // Slew azimuth at the rotator floor (track-start), never below the horizon.
        _isPrePositioning = true;
        TryTrack(settings, _keyholePlan.FlippedStartAzimuthDeg.Value, KeyholePrePositionElevationDeg(settings), null);
        return true;
    }

    /// <summary>
    /// Elevation used when slewing to a flipped start azimuth before AOS.
    /// Follows <see cref="RotatorSettings.TrackStartElevationDeg"/>, clamped to 0° so a
    /// default below-horizon track-start still pre-positions on the horizon.
    /// </summary>
    internal static double KeyholePrePositionElevationDeg(RotatorSettings settings) =>
        Math.Max(0, settings.TrackStartElevationDeg);

    /// <summary>
    /// Determines whether the rotator should currently be tracking in flipped mode.
    /// Returns true if the plan is FlippedStart and either:
    ///   - The satellite elevation is at or above the keyhole threshold (entering flipped zone), or
    ///   - We are already flipped and the satellite hasn't dropped below the threshold yet.
    /// Once flipped tracking starts, it stays active until elevation drops below the threshold.
    /// The descent below threshold naturally occurs after TCA (time of closest approach).
    /// </summary>
    private bool ShouldTrackFlipped(RotatorSettings settings, double elevationDeg)
    {
        if (_keyholePlan?.Strategy != KeyholeStrategy.FlippedStart)
            return false;

        // Already flipped: stay flipped until elevation drops below threshold
        if (_keyholeFlippedActive)
            return elevationDeg >= settings.KeyholeThresholdDeg;

        // Not yet flipped: start flipping when elevation reaches the threshold
        return elevationDeg >= settings.KeyholeThresholdDeg;
    }

    private bool EnsureConnected(RotatorSettings settings)
    {
        if (_rotator is not null
            && _connectedType == settings.Type
            && _connectedUsesNetworkEndpoint == settings.UsesNetworkEndpoint
            && ConnectionIdentityMatches(settings))
            return true;

        TearDownRotator();

        var endpoint = FormatEndpoint(settings);
        try
        {
            _rotator = _driverFactory?.Invoke(settings) ?? RotatorDriverFactory.Create(settings);
            _rotator.Open();
            _connectedPort = settings.Port;
            _connectedElevationPort = settings.ElevationPort;
            _connectedBaudRate = settings.BaudRate;
            _connectedNetworkHost = settings.NetworkHost;
            _connectedNetworkPort = settings.NetworkPort;
            _connectedUsesNetworkEndpoint = settings.UsesNetworkEndpoint;
            _connectedType = settings.Type;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Rotator connect failed on {Endpoint}", endpoint);
            _connectionKind = RotatorConnectionKind.ConnectFailed;
            _connectionDetail = ex.Message;
            TearDownRotator();
            return false;
        }
    }

    private bool ConnectionIdentityMatches(RotatorSettings settings)
    {
        if (settings.UsesNetworkEndpoint)
        {
            return string.Equals(_connectedNetworkHost, settings.NetworkHost, StringComparison.OrdinalIgnoreCase)
                && _connectedNetworkPort == settings.NetworkPort;
        }

        if (settings.UsesDualSerialPorts)
        {
            return string.Equals(_connectedPort, settings.Port, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_connectedElevationPort, settings.ElevationPort, StringComparison.OrdinalIgnoreCase)
                && _connectedBaudRate == settings.BaudRate;
        }

        return _connectedPort == settings.Port
            && _connectedBaudRate == settings.BaudRate;
    }

    private static string FormatEndpoint(RotatorSettings settings)
    {
        if (settings.UsesNetworkEndpoint)
            return $"{settings.NetworkHost.Trim()}:{settings.NetworkPort}";

        if (settings.UsesDualSerialPorts)
            return $"{settings.Port.Trim()} + {settings.ElevationPort.Trim()}";

        return settings.Port;
    }

    private void TryTrack(
        RotatorSettings settings,
        double azimuthDeg,
        double elevationDeg,
        double? aheadAzimuthDeg = null)
    {
        if (_rotator is null)
            return;

        var commandAzInput = RotatorCalibration.ApplyAzimuthOffset(azimuthDeg, settings)
            ?? RotatorAzimuthPlanner.Normalize360(azimuthDeg);
        var commandEl = Math.Clamp(
            elevationDeg + settings.ElevationOffsetDeg,
            0,
            settings.MaxElevationDeg);
        var aheadForPlanner = RotatorCalibration.ApplyAzimuthOffset(aheadAzimuthDeg, settings);
        var remainingPathCrossesNorth = false;
        if (_activePassInfo is not null)
        {
            var losCompass = RotatorCalibration.ApplyAzimuthOffset(
                _activePassInfo.LosAzimuthDeg, settings);
            remainingPathCrossesNorth = losCompass is { } losAz
                && RotatorAzimuthPlanner.IndicatesEastToWestNorthCrossing(commandAzInput, losAz);
        }

        var useSmartAzimuth = settings.SmartAzimuth450 && settings.MaxAzimuthDeg > 360;
        var effectiveLastAzimuth = _lastAzimuth ?? _displayAzimuth;
        var commandAz = useSmartAzimuth
            ? RotatorAzimuthPlanner.ResolveCommandAz(
                effectiveLastAzimuth,
                commandAzInput,
                settings.MaxAzimuthDeg,
                aheadForPlanner,
                remainingPathCrossesNorth)
            : commandAzInput;

        _displayCommandedAzimuth = (int)Math.Round(commandAz);
        _displayCommandedElevation = (int)Math.Round(commandEl);
        _displayCompassAzimuth = (int)Math.Round(RotatorAzimuthPlanner.Normalize360(azimuthDeg));

        var send = _lastAzimuth is null || _lastElevation is null
            || Math.Abs(commandAz - _lastAzimuth.Value) >= settings.MovementThresholdDeg
            || Math.Abs(commandEl - _lastElevation.Value) >= settings.MovementThresholdDeg;

        if (!send)
            return;

        try
        {
            _rotator.SetPosition(commandAz, commandEl, settings);
            _lastAzimuth = Math.Round(commandAz);
            _lastElevation = Math.Round(commandEl);
            _parked = false;
            // Poll before the worker signals command completion so GetPositionStatus
            // reflects commanded az/el without waiting for the next loop iteration.
            PollPosition();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Rotator track failed at Az={Az} El={El}", commandAz, commandEl);
            TearDownRotator();
        }
    }

    private void TryPark(RotatorSettings settings, bool afterPass = false, bool force = false)
    {
        if (afterPass && !settings.ParkAfterPass)
            return;

        if (_rotator is null || (_parked && !force))
            return;

        try
        {
            var az = settings.ParkAzimuthDeg;
            var el = settings.ParkElevationDeg;
            _rotator.SetPosition(az, el, settings);
            _lastAzimuth = az;
            _lastElevation = el;
            _parked = true;
            ClearTrackingAzimuthDisplay();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Rotator park failed");
            TearDownRotator();
        }
    }

    private void PollPosition()
    {
        if (_rotator is null)
            return;

        try
        {
            var (az, el) = _rotator.GetPosition();
            if (az is not null)
                _displayAzimuth = az;
            if (el is not null)
                _displayElevation = el;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Rotator position poll failed");
            TearDownRotator();
        }
    }

    private enum RotatorCommandKind
    {
        PublishTarget,
        UpdateSynchronously,
        Park,
        ManualMove,
        Stop,
        ResumeTracking,
        SetStandby,
        SetActivePass,
        Disconnect,
        Drain,
        Shutdown
    }

    private sealed class RotatorCommand
    {
        public RotatorCommand(
            RotatorCommandKind kind,
            RotatorSettings? settings = null,
            SatelliteTrackState? target = null,
            bool? standbyActive = null,
            double? azimuthDeg = null,
            double? elevationDeg = null,
            PassInfo? passInfo = null)
        {
            Kind = kind;
            Settings = settings ?? new RotatorSettings();
            Target = target;
            StandbyActive = standbyActive;
            AzimuthDeg = azimuthDeg;
            ElevationDeg = elevationDeg;
            PassInfo = passInfo;
        }

        public RotatorCommandKind Kind { get; }
        public RotatorSettings Settings { get; }
        public SatelliteTrackState? Target { get; }
        public bool? StandbyActive { get; }
        public double? AzimuthDeg { get; }
        public double? ElevationDeg { get; }
        public PassInfo? PassInfo { get; }
        public ManualResetEventSlim? Completed { get; set; }
    }
}
