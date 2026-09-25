using System.Collections.ObjectModel;
using OscarWatch.Core.Ft4;
using OscarWatch.Core.Logbook;
using OscarWatch.Core.Models;
using OscarWatch.Core.Net;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.PskReporter;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using OscarWatch.ViewModels;
using Serilog;

namespace OscarWatch.Ft4;

/// <summary>Owns capture, decode, transmit, PTT, sequencing, calibration, and logbook save.</summary>
public sealed class Ft4ModemService : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<Ft4ModemService>();
    private readonly ISettingsService _settings;
    private readonly ILiveTrackingService _tracking;
    private readonly FrequencyOverlayViewModel _frequencies;
    private readonly IQsoLogbookRepository _logbook;
    private readonly ICloudlogQsoUploadService _cloudlogUpload;
    private readonly ILiveTrackerSnapshotProvider _snapshot;
    private readonly IOrbitPropagator _propagator;
    private readonly IRigController _rig;
    private readonly ILocalizationService _l;
    private readonly IAudioRecordingService _recording;
    private readonly IGpsService _gps;
    private readonly Ft4AudioService _audio = new();
    private readonly Ft4PttKeyer _ptt;
    private readonly PskReporterClient _pskReporter = new();
    private readonly object _gate = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private CancellationTokenSource? _txCts;
    private CancellationTokenSource? _txScheduleCts;
    private DateTime _scheduledTxSlot = DateTime.MinValue;
    private int _txRunning; // 0 idle, 1 in progress
    private int _prepareRunning;
    private int _preparedPlayed;
    private readonly object _prepareGate = new();
    private readonly object _encodeGate = new();
    private string? _preparedKey;
    private float[]? _preparedDevicePcm;
    private int _preparedSampleRate;
    private readonly List<float> _slotBuffer = new(12000 * 8);
    private int _deviceSampleRate = 48000;
    private DateTime _currentSlotStart = DateTime.MinValue;
    private bool _txThisSlot;
    private const double MaxWaterfallEchoCorrectionHz = 50;
    private long _txKickedSlotTicks; // slot start of the last KickTransmit, set from the TX timer thread
    private DateTime _lastEchoCalibrationSlot = DateTime.MinValue;
    private bool _decodeQueuedThisSlot;
    private readonly HashSet<string> _postedDecodeKeys = new(StringComparer.Ordinal);
    private readonly object _decodePostGate = new();
    private DateTime _lastRelevantDecodeUtc = DateTime.UtcNow;
    private DateTime _lastTuneCalUtc = DateTime.MinValue;
    private readonly Ft4TuneCalibrator _tuneCalibrator = new();
    private int _tuning; // 0 off, 1 on
    private Ft4QsoSequencer? _sequencer;

    public Ft4ModemService(
        ISettingsService settings,
        ILiveTrackingService tracking,
        FrequencyOverlayViewModel frequencies,
        IRigController rig,
        IQsoLogbookRepository logbook,
        ICloudlogQsoUploadService cloudlogUpload,
        ILiveTrackerSnapshotProvider snapshot,
        IOrbitPropagator propagator,
        ILocalizationService localization,
        IAudioRecordingService recording,
        IGpsService gps)
    {
        _settings = settings;
        _tracking = tracking;
        _frequencies = frequencies;
        _logbook = logbook;
        _cloudlogUpload = cloudlogUpload;
        _snapshot = snapshot;
        _propagator = propagator;
        _rig = rig;
        _l = localization;
        _recording = recording;
        _gps = gps;
        _ptt = new Ft4PttKeyer(rig, settings);
        _pskReporter.Diagnostic += (message, ex) =>
        {
            if (ex is null)
                Log.Information("{Message}", message);
            else
                Log.Warning(ex, "{Message}", message);
        };
        ApplyPskReporterSettings();
    }

    /// <summary>Open or close the PSK Reporter socket to match FT4 settings.</summary>
    public void ApplyPskReporterSettings()
    {
        var ft4 = _settings.Current.Ft4;
        _pskReporter.Configure(ft4.PskReporterEnabled, ft4.PskReporterHost, ft4.PskReporterPort);
    }

    private void ReportToPskReporter(Ft4DecodedMessage msg)
    {
        if (!_pskReporter.IsEnabled)
            return;

        try
        {
            var snap = _snapshot.GetCurrent();
            if (!Ft4PskReporterSpots.TryCreateSpot(msg, snap, out var spot))
                return;

            var station = _settings.Current.GroundStation;
            if (!Ft4PskReporterSpots.TryCreateReceiver(
                    station.Callsign,
                    station.GridSquare,
                    snap.SatelliteName,
                    $"{OscarWatchHttpClients.ProductName} {OscarWatchHttpClients.GetProductVersion()}",
                    out var receiver))
                return;

            _pskReporter.Enqueue(receiver, spot);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "PSK Reporter spot skipped");
        }
    }

    public ObservableCollection<Ft4DecodedMessage> Decodes { get; } = new();
    public string Status { get; private set; } = "";
    public string ManualPrompt { get; private set; } = "";
    public bool IsRunning { get; private set; }
    public bool IsTuning => Volatile.Read(ref _tuning) == 1;
    public bool NativeAvailable => Ft8Native.IsAvailable;
    public Ft4QsoSequencer? Sequencer => _sequencer;

    /// <summary>Green-bracket RX audio Hz used for decode search (Hold Tx can differ from TX).</summary>
    public double RxAudioHz { get; set; } = 1500;
    public double TxPlaybackPeak => _audio.PlaybackPeak;
    public event Action? Changed;

    private string? _lastLoggedKey;

    public IReadOnlyList<AudioInputDevice> GetInputDevices() => _audio.GetInputDevices();

    public IReadOnlyList<AudioInputDevice> GetOutputDevices() => _audio.GetOutputDevices();

    /// <summary>Re-open the TX output on the device currently stored in FT4 settings (while listening).</summary>
    public void RestartOutputFromSettings()
    {
        if (!IsRunning)
            return;

        _audio.StartOutput(
            _settings.Current.Ft4.OutputDeviceId,
            _settings.Current.Ft4.OutputDeviceDisplayName);
    }

    /// <summary>Re-open capture on the device currently stored in FT4 settings (while listening).</summary>
    public void RestartCaptureFromSettings()
    {
        if (!IsRunning)
            return;
        try
        {
            _audio.StartCapture(
                _settings.Current.Ft4.InputDeviceId,
                _settings.Current.Ft4.InputDeviceDisplayName);
            _deviceSampleRate = _audio.CaptureSampleRate;
            Status = _l.Get("Ft4.Status.Listening");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FT4 capture restart failed");
            Status = ex.Message;
            Changed?.Invoke();
        }
    }

    /// <summary>Build a passband magnitude row for the waterfall (does not consume decode audio).</summary>
    public bool TryBuildSpectrum(Span<float> bins)
    {
        if (!IsRunning || bins.Length == 0)
            return false;

        Span<float> scratch = stackalloc float[4096];
        var n = _audio.CopyMonitorSamples(scratch);
        if (n < 512)
            return false;

        return Ft4SpectrumAnalyzer.TryComputePassband(
            scratch[..n],
            _audio.CaptureSampleRate,
            bins);
    }

    public void SetManualPromptHandler(Action<string>? handler) =>
        _ptt.SetManualPromptHandler(text =>
        {
            ManualPrompt = text;
            handler?.Invoke(text);
            Changed?.Invoke();
        });

    public void Start()
    {
        if (IsRunning)
            return;

        if (!Ft8Native.IsAvailable)
        {
            Status = _l.Get("Ft4.NativeUnavailable");
            Changed?.Invoke();
            return;
        }

        var call = Ft4MessageCodec.NormalizeCall(_settings.Current.GroundStation.Callsign ?? "");
        var grid = _settings.Current.GroundStation.GridSquare?.Trim() ?? "";
        if (call.Length == 0 || grid.Length < 4)
        {
            Status = _l.Get("Ft4.Status.NeedStation");
            Changed?.Invoke();
            return;
        }

        _sequencer = new Ft4QsoSequencer(
            () => Ft4MessageCodec.NormalizeCall(_settings.Current.GroundStation.Callsign ?? ""),
            () =>
            {
                var g = _settings.Current.GroundStation.GridSquare ?? "";
                return g.Length >= 4 ? g[..4] : g;
            },
            () => _settings.Current.Ft4.SkipRrr,
            () => _settings.Current.Ft4.HoldTxFrequency);
        _sequencer.TxAudioHz = Math.Clamp(_settings.Current.Ft4.TxAudioHz, 200, 3000);
        RxAudioHz = _sequencer.TxAudioHz;
        _lastLoggedKey = null;

        Ft8Native.ClearCallsigns();
        Ft8Native.RememberCallsign(call);

        // Pass recording shares the downlink capture card with FT4 RX. Stop it before we open
        // PortAudio so the modem loop is not starved waiting on a contended input stream.
        StopPassRecordingForModem();

        _audio.StartCapture(
            _settings.Current.Ft4.InputDeviceId,
            _settings.Current.Ft4.InputDeviceDisplayName);
        _audio.StartOutput(
            _settings.Current.Ft4.OutputDeviceId,
            _settings.Current.Ft4.OutputDeviceDisplayName);
        _deviceSampleRate = _audio.CaptureSampleRate;
        _slotBuffer.Clear();
        _currentSlotStart = DateTime.MinValue;
        _txThisSlot = false;
        Interlocked.Exchange(ref _txKickedSlotTicks, 0);
        _decodeQueuedThisSlot = false;
        lock (_decodePostGate)
        {
            _postedDecodeKeys.Clear();
            _lastEchoCalibrationSlot = DateTime.MinValue;
        }
        _lastRelevantDecodeUtc = DateTime.UtcNow;
        RefreshClockFromGps();

        // OrbitDeck: hold CAT dial within each slot; audio-domain corrects within-slot drift.
        _rig.SetFt4SlotGatedDoppler(true);
        _rig.ForceFt4DopplerStep();

        _loopCts = new CancellationTokenSource();
        // Long-running: capture/TX timing must not share the thread-pool with native decode.
        _loopTask = Task.Factory.StartNew(
            () => LoopAsync(_loopCts.Token).GetAwaiter().GetResult(),
            _loopCts.Token,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        IsRunning = true;
        Status = _l.Get("Ft4.Status.Listening");
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
            return;

        StopTune();
        CancelTxSchedule();
        _txCts?.Cancel();
        _loopCts?.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }

        await _ptt.UnkeyAsync().ConfigureAwait(false);
        _audio.StopOutput();
        _audio.StopCapture();
        _rig.SetFt4SlotGatedDoppler(false);
        IsRunning = false;
        Status = _l.Get("Ft4.Status.Stopped");
        Changed?.Invoke();
    }

    public void StartCq(bool evenSlot)
    {
        StopTune();
        if (!EnsureTransmitAllowed())
            return;

        _sequencer?.StartCq(evenSlot);
        _lastLoggedKey = null;
        _lastRelevantDecodeUtc = DateTime.UtcNow;
        Status = _l.Get("Ft4.Status.CallingCq");
        Changed?.Invoke();
    }

    public bool QueueReport(float? snrDb)
    {
        if (_sequencer is null || string.IsNullOrWhiteSpace(_sequencer.TheirCall))
            return false;
        StopTune();
        if (!EnsureTransmitAllowed())
            return false;
        if (!_sequencer.ForceReport(snrDb))
            return false;

        _lastRelevantDecodeUtc = DateTime.UtcNow;
        Status = _l.Get("Ft4.Status.SendingReport", _sequencer.TheirCall);
        Changed?.Invoke();
        return true;
    }

    public bool Queue73()
    {
        if (_sequencer is null || string.IsNullOrWhiteSpace(_sequencer.TheirCall))
            return false;
        StopTune();
        if (!EnsureTransmitAllowed())
            return false;
        if (!_sequencer.Force73())
            return false;

        _lastRelevantDecodeUtc = DateTime.UtcNow;
        Status = _l.Get("Ft4.Status.Sending73", _sequencer.TheirCall);
        Changed?.Invoke();
        return true;
    }

    public void EnableTx()
    {
        StopTune();
        if (!EnsureTransmitAllowed())
            return;

        _sequencer?.EnableTx();
        // Reset idle timeout so re-arming after a watchdog halt does not trip again immediately.
        _lastRelevantDecodeUtc = DateTime.UtcNow;
        Status = _l.Get("Ft4.Status.TxEnabled");
        Changed?.Invoke();
    }

    public void HaltTx()
    {
        StopTune();
        _sequencer?.HaltTx();
        CancelTxSchedule();
        _txCts?.Cancel();
        ClearPrepared();
        Interlocked.Exchange(ref _preparedPlayed, 0);
        _audio.StopPlayback();
        _ = _ptt.UnkeyAsync();
        _txThisSlot = false;
        Status = _l.Get("Ft4.Status.TxHalted");
        Changed?.Invoke();
    }

    /// <summary>
    /// WSJT-X-style Tune: continuous tone on the TX audio frequency with PTT.
    /// Call again (or Halt Tx) to stop. While on, the downlink tone can trim the uplink.
    /// </summary>
    public bool StartTune()
    {
        if (!IsRunning)
            return false;
        if (!EnsureTransmitAllowed())
            return false;
        if (IsTuning)
            return true;

        // Stop sequenced FT4 bursts; Tune owns the transmitter until cancelled.
        _sequencer?.HaltTx();
        CancelTxSchedule();
        _txCts?.Cancel();
        ClearPrepared();
        Interlocked.Exchange(ref _preparedPlayed, 0);
        Interlocked.Exchange(ref _txRunning, 0);
        _audio.StopPlayback();

        Volatile.Write(ref _tuning, 1);
        _lastTuneCalUtc = DateTime.UtcNow;
        _tuneCalibrator.Reset();
        Status = _l.Get("Ft4.Status.Tuning", FormatTuneHz());
        Changed?.Invoke();

        _ = Task.Run(async () =>
        {
            try
            {
                await _ptt.KeyAsync().ConfigureAwait(false);
                if (!IsTuning)
                {
                    await _ptt.UnkeyAsync().ConfigureAwait(false);
                    return;
                }

                StartTuneTone();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "FT4 Tune failed to start");
                Volatile.Write(ref _tuning, 0);
                _audio.StopPlayback();
                _ = _ptt.UnkeyAsync();
                Status = _l.Get(
                    "Ft4.Status.TxError",
                    ComPortConflictLocalizer.Localize(ex.Message, _l));
                Changed?.Invoke();
            }
        });

        return true;
    }

    public void StopTune()
    {
        if (Interlocked.Exchange(ref _tuning, 0) == 0)
            return;

        _audio.StopPlayback();
        _ = _ptt.UnkeyAsync();
        Status = _l.Get("Ft4.Status.TuneStopped");
        Changed?.Invoke();
    }

    /// <summary>Retarget the Tune tone when the operator moves the TX Hz spinner.</summary>
    public void UpdateTuneFrequency()
    {
        if (!IsTuning)
            return;

        StartTuneTone();
        Status = _l.Get("Ft4.Status.Tuning", FormatTuneHz());
        Changed?.Invoke();
    }

    private void StartTuneTone()
    {
        var ft4 = _settings.Current.Ft4;
        var hz = _sequencer?.TxAudioHz ?? ft4.TxAudioHz;
        _audio.StartContinuousTone(
            hz,
            ft4.TxLevel,
            ft4.OutputDeviceId,
            ft4.OutputDeviceDisplayName);
    }

    private string FormatTuneHz()
    {
        var hz = _sequencer?.TxAudioHz ?? _settings.Current.Ft4.TxAudioHz;
        return Math.Clamp(hz, 200, 3000).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Answer(Ft4DecodedMessage decode)
    {
        if (_sequencer is null)
            return;
        StopTune();
        if (!EnsureTransmitAllowed())
            return;

        var even = Ft4SlotClock.IsEvenSlot(decode.SlotUtc, Ft4SlotClock.Ft4SlotSeconds);
        _sequencer.StartAnswer(decode, oppositeEvenSlot: !even);
        _lastLoggedKey = null;
        _lastRelevantDecodeUtc = DateTime.UtcNow;
        Status = _l.Get("Ft4.Status.Answering", decode.Text);
        Changed?.Invoke();
    }

    /// <summary>
    /// Re-check satellite eligibility while listening (e.g. operator switched to FM, FO-29, or AO-7).
    /// Halts TX when the focused satellite is not allowed for FT4.
    /// </summary>
    public void RefreshSatelliteEligibility()
    {
        var reason = EvaluateTransmitBlock();
        if (reason == Ft4SatelliteEligibility.BlockReason.None)
            return;

        if (_sequencer?.TransmitEnabled == true)
            HaltTx();

        var msg = _l.Get(Ft4SatelliteEligibility.StatusKey(reason));
        if (string.Equals(Status, msg, StringComparison.Ordinal))
            return;

        Status = msg;
        Changed?.Invoke();
    }

    private bool EnsureTransmitAllowed()
    {
        var reason = EvaluateTransmitBlock();
        if (reason != Ft4SatelliteEligibility.BlockReason.None)
        {
            if (_sequencer?.TransmitEnabled == true)
                HaltTx();

            Status = _l.Get(Ft4SatelliteEligibility.StatusKey(reason));
            Changed?.Invoke();
            return false;
        }

        if (_rig.TryGetUplinkRfPowerWatts(out var watts) && Ft4RfPowerLimit.ExceedsLimit(watts))
        {
            if (_sequencer?.TransmitEnabled == true)
                HaltTx();

            Status = _l.Get(Ft4RfPowerLimit.StatusKey, (int)Ft4RfPowerLimit.MaxWatts);
            Changed?.Invoke();
            return false;
        }

        return true;
    }

    private Ft4SatelliteEligibility.BlockReason EvaluateTransmitBlock()
    {
        var snap = _snapshot.GetCurrent();
        var name = !string.IsNullOrWhiteSpace(snap.SatelliteName)
            ? snap.SatelliteName
            : _frequencies.SatelliteName;
        var norad = !string.IsNullOrWhiteSpace(_snapshot.FocusedNoradId)
            ? _snapshot.FocusedNoradId
            : _tracking.FocusedNoradId;
        return Ft4SatelliteEligibility.Evaluate(name, norad, _frequencies.SelectedMode);
    }

    /// <summary>Clear the on-screen decode / activity list (does not stop the modem).</summary>
    public void ClearDecodes()
    {
        void Clear()
        {
            Decodes.Clear();
            lock (_decodePostGate)
                _postedDecodeKeys.Clear();
            Status = _l.Get("Ft4.Status.DecodesCleared");
            Changed?.Invoke();
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            Clear();
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(Clear);
    }

    /// <summary>Chronological text dump of the current decode list for Save Activity.</summary>
    public string BuildActivityText() => Ft4ActivityLog.FormatAll(Decodes);

    /// <summary>
    /// Save the current QSO to the OscarWatch Logbook.
    /// Manual log needs their callsign; auto-complete still requires both reports.
    /// </summary>
    public Task LogQsoAsync(bool manual = false) => TryLogAsync(manual);

    private async Task LoopAsync(CancellationToken ct)
    {
        var scratch = new float[4096];
        var resampled = new float[4096];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = Ft4Clock.UtcNow;
                var slotStart = Ft4SlotClock.SlotStartUtc(now, Ft4SlotClock.Ft4SlotSeconds);
                // Forward only: a GPS clock correction stepping back must not reopen the previous slot.
                if (slotStart > _currentSlotStart)
                {
                    // Snapshot the previous slot, clear, and start TX first so decode CPU
                    // never delays the next transmit or capture alignment.
                    float[]? previousSamples = null;
                    var previousSlot = _currentSlotStart;
                    var kickedTicks = Interlocked.Read(ref _txKickedSlotTicks);
                    var alreadyTx = kickedTicks == slotStart.Ticks;
                    var previousWasTx = previousSlot != DateTime.MinValue && kickedTicks == previousSlot.Ticks;
                    var needEndDecode = previousSlot != DateTime.MinValue
                        && (!_decodeQueuedThisSlot || previousWasTx);
                    lock (_gate)
                    {
                        if (needEndDecode && _slotBuffer.Count >= (int)(12000 * Ft4SlotClock.Ft4SlotSeconds / 2))
                            previousSamples = _slotBuffer.ToArray();
                        _slotBuffer.Clear();
                    }

                    _currentSlotStart = slotStart;
                    // Soft timer may already have kicked TX; keep the TX-slot flag.
                    _txThisSlot = alreadyTx;
                    _decodeQueuedThisSlot = false;
                    // Keep the finished slot's keys: its end-of-slot decode runs after this point
                    // and must not republish (or re-calibrate from) lines the early decode posted.
                    var keepPrefix = previousSlot.Ticks + "|";
                    lock (_decodePostGate)
                        _postedDecodeKeys.RemoveWhere(k => !k.StartsWith(keepPrefix, StringComparison.Ordinal));

                    // Audio first when the soft timer missed; Doppler can follow.
                    if (!alreadyTx && !IsTuning)
                        KickTransmit(slotStart, ct);
                    _rig.ForceFt4DopplerStep();
                    RefreshClockFromGps();

                    if (previousSamples is not null)
                        QueueDecode(previousSlot, previousSamples, previousWasTx);
                }

                // Build the next TX burst before its slot, so the boundary only starts playback.
                // Soft-schedule KickTransmit so a slow capture loop cannot hold the tone.
                if (!IsTuning)
                {
                    MaybePrepareTransmit(now);
                    MaybeScheduleTransmit(now, ct);
                }

                // Early RX decode once the FT4 burst should be in the buffer (~6 s).
                MaybeQueueEarlyDecode(now);

                if (IsTuning)
                    MaybeCalibrateTune(now);

                // Watchdog: stop TX if nothing relevant for 3 minutes.
                if (!IsTuning
                    && _sequencer is { TransmitEnabled: true }
                    && DateTime.UtcNow - _lastRelevantDecodeUtc > TimeSpan.FromMinutes(3))
                {
                    HaltTx();
                    Status = _l.Get("Ft4.Status.WatchdogStopped");
                    Changed?.Invoke();
                }

                var n = _audio.ReadCaptureSamples(scratch);
                if (n <= 0)
                {
                    await Task.Delay(15, ct).ConfigureAwait(false);
                    continue;
                }

                var needed = (int)(n * (12000.0 / _deviceSampleRate)) + 8;
                if (resampled.Length < needed)
                    resampled = new float[needed];

                var outCount = ResampleTo12k(scratch.AsSpan(0, n), _deviceSampleRate, resampled);
                lock (_gate)
                {
                    for (var i = 0; i < outCount; i++)
                        _slotBuffer.Add(resampled[i]);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "FT4 modem loop error");
                Status = _l.Get("Ft4.Status.ModemError", ex.Message);
                Changed?.Invoke();
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
    }

    private void RefreshClockFromGps()
    {
        var before = Ft4Clock.UsingGps;
        Ft4Clock.Update(_gps.GetFt4ClockOffset());
        if (Ft4Clock.UsingGps == before)
            return;

        var pcErrorMs = -(Ft4Clock.MeasuredOffset ?? TimeSpan.Zero).TotalMilliseconds;
        if (Ft4Clock.UsingGps)
            Log.Information("FT4 slot timing now follows GPS (PC clock {PcErrorMs:0} ms out)", pcErrorMs);
        else
            Log.Information("FT4 slot timing back on the PC clock (measured {PcErrorMs:0} ms)", pcErrorMs);
    }

    private void KickTransmit(DateTime slotStart, CancellationToken loopCt)
    {
        if (IsTuning)
            return;

        var seq = _sequencer;
        if (seq is null || !seq.TransmitEnabled || string.IsNullOrWhiteSpace(seq.CurrentTxMessage))
            return;

        if (!EnsureTransmitAllowed())
            return;

        if (Ft4SlotClock.IsEvenSlot(slotStart, Ft4SlotClock.Ft4SlotSeconds) != seq.PreferEvenSlot)
            return;

        if (Interlocked.CompareExchange(ref _txRunning, 1, 0) != 0)
            return;

        Interlocked.Exchange(ref _txKickedSlotTicks, slotStart.Ticks);
        _txThisSlot = true;
        _txCts?.Cancel();
        _txCts?.Dispose();
        _txCts = CancellationTokenSource.CreateLinkedTokenSource(loopCt);
        var txCt = _txCts.Token;

        // Start a ready buffer on this thread for every PTT method. Waiting for the TX task
        // (CAT lead, encode) was holding the tone; VOX also needs the tone as soon as the slot opens.
        if (TryTakePrepared(slotStart, seq.CurrentTxMessage, out var ready, out var readyRate)
            && _audio.TryPlayPrepared(ready, readyRate))
        {
            Interlocked.Exchange(ref _preparedPlayed, 1);
            var intoMs = (Ft4Clock.UtcNow - slotStart).TotalMilliseconds;
            Log.Information("FT4 TX audio started {IntoMs:0} ms into the slot from a prepared buffer", intoMs);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunTransmitAsync(slotStart, txCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Halt Tx / stop.
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "FT4 transmit task failed");
                Status = _l.Get(
                    "Ft4.Status.TxError",
                    ComPortConflictLocalizer.Localize(ex.Message, _l));
                Changed?.Invoke();
            }
            finally
            {
                Interlocked.Exchange(ref _txRunning, 0);
            }
        }, CancellationToken.None);
    }

    private async Task RunTransmitAsync(DateTime slotStart, CancellationToken ct)
    {
        var playedEarly = Interlocked.Exchange(ref _preparedPlayed, 0) == 1;
        var seq = _sequencer;
        if (seq is null || !seq.TransmitEnabled || string.IsNullOrWhiteSpace(seq.CurrentTxMessage))
        {
            if (playedEarly)
                _audio.StopPlayback();
            return;
        }

        var audioHz = (float)Math.Clamp(seq.TxAudioHz, 200, 3000);

        // Native encode pads to a full 7.5 s slot (silence tail). Key only for lead-in + burst
        // so VOX/CAT unkey before the opposite RX slot.
        const double leadInSeconds = 0.5;
        var keySeconds = leadInSeconds + Ft4SlotClock.Ft4SymbolBurstSeconds + 0.15;
        var keyedUntil = slotStart.AddSeconds(keySeconds);

        await _ptt.KeyAsync(ct).ConfigureAwait(false);
        try
        {
            if (!playedEarly)
            {
                var ft4 = _settings.Current.Ft4;
                if (TryTakePrepared(slotStart, seq.CurrentTxMessage, out var ready, out var readyRate)
                    && _audio.TryPlayPrepared(ready, readyRate))
                {
                    var intoMs = (Ft4Clock.UtcNow - slotStart).TotalMilliseconds;
                    Log.Information("FT4 TX audio started {IntoMs:0} ms into the slot from a prepared buffer", intoMs);
                }
                else if (!TryBuildTransmitPcm(slotStart, seq.CurrentTxMessage, audioHz, out var pcm, out var encodeError)
                    || pcm is null)
                {
                    _txThisSlot = false;
                    Status = string.IsNullOrWhiteSpace(encodeError)
                        ? _l.Get("Ft4.Status.EncodeFailed")
                        : encodeError;
                    Log.Warning("FT4 encode failed for '{Message}': {Error}", seq.CurrentTxMessage, encodeError);
                    Changed?.Invoke();
                    return;
                }
                else
                {
                    _audio.PlayPcm(
                        pcm,
                        ft4.TxLevel,
                        ft4.OutputDeviceId,
                        ft4.OutputDeviceDisplayName);
                    var intoMs = (Ft4Clock.UtcNow - slotStart).TotalMilliseconds;
                    Log.Information("FT4 TX audio started {IntoMs:0} ms into the slot after building on the slot", intoMs);
                }
            }

            while (!ct.IsCancellationRequested)
            {
                var remaining = keyedUntil - Ft4Clock.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    break;
                if (!_audio.IsPlaying)
                    break;
                var slice = remaining > TimeSpan.FromMilliseconds(50)
                    ? TimeSpan.FromMilliseconds(50)
                    : remaining;
                await Task.Delay(slice, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await _ptt.UnkeyAsync(ct).ConfigureAwait(false);
            _audio.StopPlayback();
        }

        if (ct.IsCancellationRequested)
            return;

        AppendTransmittedMessage(slotStart, seq.CurrentTxMessage, audioHz);

        if (seq.OnTxCompleted())
            await TryLogAsync(manual: false).ConfigureAwait(false);

        Status = _l.Get("Ft4.Status.TxDone", seq.CurrentTxMessage);
        Changed?.Invoke();
    }

    private void StopPassRecordingForModem()
    {
        if (!_recording.IsRecording || AudioRecordingSessions.IsManualTest(_recording))
            return;

        try
        {
            _recording.StopAsync().GetAwaiter().GetResult();
            Log.Information("FT4 stopped pass recording so the downlink capture card is free for the modem");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FT4 could not stop pass recording before opening capture");
        }
    }

    private void CancelTxSchedule()
    {
        try
        {
            _txScheduleCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        _txScheduleCts?.Dispose();
        _txScheduleCts = null;
        _scheduledTxSlot = DateTime.MinValue;
    }

    /// <summary>
    /// Soft-schedule KickTransmit onto a dedicated waiter so a slow capture loop cannot hold TX.
    /// </summary>
    private void MaybeScheduleTransmit(DateTime utcNow, CancellationToken loopCt)
    {
        if (IsTuning)
        {
            CancelTxSchedule();
            return;
        }

        var seq = _sequencer;
        if (seq is not { TransmitEnabled: true } || string.IsNullOrWhiteSpace(seq.CurrentTxMessage))
        {
            CancelTxSchedule();
            return;
        }

        var next = Ft4SlotClock.NextTransmitSlotStart(utcNow, Ft4SlotClock.Ft4SlotSeconds, seq.PreferEvenSlot);
        if (next == _currentSlotStart && Volatile.Read(ref _txRunning) == 1)
            return;

        if (_scheduledTxSlot == next && _txScheduleCts is { IsCancellationRequested: false })
            return;

        CancelTxSchedule();
        _scheduledTxSlot = next;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(loopCt);
        _txScheduleCts = linked;
        var ct = linked.Token;
        var slot = next;

        _ = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    Ft4SlotWait.UntilUtc(slot, ct);
                    if (ct.IsCancellationRequested)
                        return;
                    KickTransmit(slot, loopCt);
                    _rig.ForceFt4DopplerStep();
                }
                catch (OperationCanceledException)
                {
                    // Halt / stop / retarget.
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "FT4 TX schedule failed for slot {Slot}", slot);
                }
            },
            ct,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private void MaybePrepareTransmit(DateTime utcNow)
    {
        if (IsTuning)
            return;

        var seq = _sequencer;
        if (seq is not { TransmitEnabled: true } || string.IsNullOrWhiteSpace(seq.CurrentTxMessage))
        {
            ClearPrepared();
            return;
        }

        var next = Ft4SlotClock.NextTransmitSlotStart(utcNow, Ft4SlotClock.Ft4SlotSeconds, seq.PreferEvenSlot);
        if (_txThisSlot && next == _currentSlotStart)
            return;

        var audioHz = (float)Math.Clamp(seq.TxAudioHz, 200, 3000);
        var level = _settings.Current.Ft4.TxLevel;
        var message = seq.CurrentTxMessage;
        var doppler = _settings.Current.Ft4.AudioDopplerTx;
        var key = PrepareKey(next, message, audioHz, level, doppler);

        lock (_prepareGate)
        {
            if (_preparedKey == key && _preparedDevicePcm is not null)
                return;
        }

        if (Interlocked.CompareExchange(ref _prepareRunning, 1, 0) != 0)
            return;

        var deviceId = _settings.Current.Ft4.OutputDeviceId;
        var deviceName = _settings.Current.Ft4.OutputDeviceDisplayName;
        _ = Task.Run(() =>
        {
            try
            {
                if (!TryBuildTransmitPcm(next, message, audioHz, out var pcm, out _) || pcm is null)
                    return;
                if (!_audio.TryPreparePlayback(pcm, level, deviceId, deviceName, out var devicePcm, out var rate))
                    return;

                lock (_prepareGate)
                {
                    var nowSeq = _sequencer;
                    if (nowSeq is not { TransmitEnabled: true }
                        || !string.Equals(nowSeq.CurrentTxMessage, message, StringComparison.Ordinal))
                        return;

                    var keyNow = PrepareKey(
                        next,
                        nowSeq.CurrentTxMessage,
                        Math.Clamp(nowSeq.TxAudioHz, 200, 3000),
                        _settings.Current.Ft4.TxLevel,
                        _settings.Current.Ft4.AudioDopplerTx);
                    if (!string.Equals(keyNow, key, StringComparison.Ordinal))
                        return;

                    _preparedKey = key;
                    _preparedDevicePcm = devicePcm;
                    _preparedSampleRate = rate;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "FT4 transmit prepare failed");
            }
            finally
            {
                Interlocked.Exchange(ref _prepareRunning, 0);
            }
        });
    }

    private bool TryTakePrepared(DateTime slotStart, string message, out float[] devicePcm, out int sampleRate)
    {
        devicePcm = [];
        sampleRate = 0;
        var seq = _sequencer;
        if (seq is null)
            return false;

        var key = PrepareKey(
            slotStart,
            message,
            Math.Clamp(seq.TxAudioHz, 200, 3000),
            _settings.Current.Ft4.TxLevel,
            _settings.Current.Ft4.AudioDopplerTx);

        lock (_prepareGate)
        {
            if (_preparedDevicePcm is null || !string.Equals(_preparedKey, key, StringComparison.Ordinal))
                return false;

            devicePcm = _preparedDevicePcm;
            sampleRate = _preparedSampleRate;
            _preparedDevicePcm = null;
            _preparedKey = null;
            return true;
        }
    }

    private void ClearPrepared()
    {
        lock (_prepareGate)
        {
            _preparedKey = null;
            _preparedDevicePcm = null;
        }
    }

    private static string PrepareKey(DateTime slotStart, string message, double audioHz, double level, bool doppler) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{slotStart.Ticks}|{message}|{audioHz:0}|{level:0.000}|{(doppler ? 1 : 0)}");

    private bool TryBuildTransmitPcm(
        DateTime slotStart,
        string message,
        float audioHz,
        out float[]? pcm,
        out string encodeError)
    {
        pcm = null;
        encodeError = "";
        lock (_encodeGate)
        {
            if (!Ft8Native.TryEncodeFt4(message, audioHz, 12000, out pcm, out encodeError) || pcm is null)
                return false;

            if (_settings.Current.Ft4.AudioDopplerTx
                && TryGetDopplerSlopeHzPerSec(slotStart, Ft4SlotClock.Ft4SymbolBurstSeconds, out _, out var ulSlope))
            {
                var uplinkMode = _frequencies.SelectedMode is null
                    ? null
                    : Core.Radio.TransponderOperatingModes.GetEffectiveUplinkMode(
                        _frequencies.SelectedMode,
                        _frequencies.IsCwUplink);
                pcm = Ft4AudioDoppler.ApplyTxPrecompensation(pcm, 12000, ulSlope, uplinkMode);
            }

            return true;
        }
    }

    private void AppendTransmittedMessage(DateTime slotStart, string text, float freqHz)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        Ft4MessageCodec.TryParse(text, out var callTo, out var callDe, out var extra);
        var msg = new Ft4DecodedMessage(
            slotStart,
            text.Trim(),
            freqHz,
            0f,
            0f,
            callTo,
            callDe,
            extra,
            IsOwnEcho: false,
            IsTransmitted: true);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Decodes.Insert(0, msg);
            while (Decodes.Count > 200)
                Decodes.RemoveAt(Decodes.Count - 1);
        });
        Changed?.Invoke();
    }

    /// <summary>
    /// While Tune is on, measure the downlink tone and nudge uplink trim toward the red bracket.
    /// A correction is only applied when two slots agree, and it is undone if the next slot
    /// does not show the tone on the marker (the peak was not our tone, or the trim did not land).
    /// </summary>
    private void MaybeCalibrateTune(DateTime utcNow)
    {
        if (_tuneCalibrator.GaveUp || utcNow - _lastTuneCalUtc < TimeSpan.FromSeconds(1.5))
            return;

        // The uplink trim only reaches the rig at the next slot-boundary CAT step, so measure
        // once per slot, after this slot's step has landed.
        var slot = _currentSlotStart;
        var intoSlot = (utcNow - slot).TotalSeconds;
        if (slot == DateTime.MinValue || intoSlot < 2.0 || !TryClaimEchoCalibration(slot))
            return;

        _lastTuneCalUtc = utcNow;
        var txHz = _sequencer?.TxAudioHz ?? _settings.Current.Ft4.TxAudioHz;
        Span<float> scratch = stackalloc float[8192];
        var n = _audio.CopyMonitorSamples(scratch);
        var rate = _audio.CaptureSampleRate;
        if (rate < 8000)
            return;

        double? errorHz = Ft4EchoAligner.TryMeasureTonePeakHz(scratch[..n], rate, txHz, out var peakHz)
            ? peakHz - txHz
            : null;
        Log.Debug(
            "FT4 Tune measurement {IntoSlot:0.0} s into the slot: peak {Peak} Hz, TX marker {Tx:0} Hz, candidate {Candidate}, awaiting confirm {Applied}",
            intoSlot,
            errorHz is null ? "none" : peakHz.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
            txHz,
            _tuneCalibrator.CandidateErrorHz,
            _tuneCalibrator.AwaitingConfirmHz);

        var decision = _tuneCalibrator.Next(errorHz);
        switch (decision.Kind)
        {
            case Ft4TuneCalibrator.ActionKind.Confirmed:
                Log.Information("FT4 Tune calibration confirmed: tone now {Error:0} Hz from the TX marker", decision.CorrectionHz);
                break;

            case Ft4TuneCalibrator.ActionKind.Undo:
                Log.Information(
                    "FT4 Tune calibration undone: after a {Applied:0} Hz correction the tone was {After} Hz from the marker",
                    -decision.CorrectionHz,
                    errorHz is null ? "not found" : errorHz.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                ApplyEchoCalibrationHz(decision.CorrectionHz);
                Status = _l.Get("Ft4.Status.TuneCalibrationFailed");
                Changed?.Invoke();
                break;

            case Ft4TuneCalibrator.ActionKind.Apply:
                Log.Information(
                    "FT4 Tune tone on the waterfall is {Peak:0} Hz, TX marker {Tx:0} Hz, error {Error:0} Hz (two slots agree)",
                    peakHz,
                    txHz,
                    decision.CorrectionHz);
                ApplyEchoCalibrationHz(decision.CorrectionHz);
                // Keep the Tune status visible after a calibration note.
                Status = _l.Get("Ft4.Status.Tuning", FormatTuneHz());
                Changed?.Invoke();
                break;
        }
    }

    private void MaybeQueueEarlyDecode(DateTime utcNow)
    {
        if (_decodeQueuedThisSlot || _currentSlotStart == DateTime.MinValue)
            return;

        var into = (utcNow - _currentSlotStart).TotalSeconds;
        if (into < Ft4SlotClock.Ft4EarlyDecodeSeconds)
            return;

        float[] snapshot;
        lock (_gate)
        {
            var minSamples = (int)(12000 * Ft4SlotClock.Ft4EarlyDecodeSeconds * 0.92);
            if (_slotBuffer.Count < minSamples)
                return;
            snapshot = _slotBuffer.ToArray();
        }

        _decodeQueuedThisSlot = true;
        // TX slots still decode: that is when the full-duplex own echo is in the buffer.
        QueueDecode(_currentSlotStart, snapshot, txSlot: _txThisSlot);
    }

    private void QueueDecode(DateTime slotStart, float[] samples, bool txSlot)
    {
        // Long-running: native decode must not occupy a thread-pool worker the modem loop needs.
        _ = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    DecodeSamples(slotStart, samples, txSlot);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "FT4 decode failed for slot {Slot}", slotStart);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private void DecodeSamples(DateTime slotStart, float[] samples, bool txSlot)
    {
        if (samples.Length < (int)(12000 * Ft4SlotClock.Ft4SlotSeconds / 2))
            return;

        var raw = samples;
        var corrected = raw;
        if (_settings.Current.Ft4.AudioDopplerRx
            && TryGetDopplerSlopeHzPerSec(slotStart, Ft4SlotClock.Ft4SlotSeconds, out var dlSlope, out _)
            && Math.Abs(dlSlope) >= 0.05)
        {
            corrected = Ft4AudioDoppler.RemoveLinearDrift(raw, 12000, dlSlope);
        }

        var txHz = _sequencer?.TxAudioHz ?? _settings.Current.Ft4.TxAudioHz;
        var rxHz = RxAudioHz > 0 ? RxAudioHz : txHz;
        if (txSlot && _settings.Current.Ft4.ParallelTxEchoDecode)
        {
            DecodeTxSlotParallel(slotStart, raw, corrected, rxHz, txHz);
            return;
        }

        var foundOwn = PublishDecoded(slotStart, corrected, txSlot, timeShiftSec: 0, ownOnly: false, rxHz, txHz);
        if (!txSlot || foundOwn)
            return;

        RecoverOwnEchoSequential(slotStart, raw, corrected, rxHz, txHz);
    }

    /// <summary>
    /// Normal decode and late-echo pass together so wall-clock is about one decode, not two in a row.
    /// </summary>
    private void DecodeTxSlotParallel(
        DateTime slotStart,
        float[] raw,
        float[] corrected,
        double rxHz,
        double txHz)
    {
        var primary = Task.Factory.StartNew(
            () =>
            {
                var own = PublishDecoded(slotStart, corrected, txSlot: true, timeShiftSec: 0, ownOnly: false, rxHz, txHz);
                if (!own && !ReferenceEquals(corrected, raw))
                    own = PublishDecoded(slotStart, raw, txSlot: true, timeShiftSec: 0, ownOnly: true, rxHz, txHz);
                return own;
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

        var late = Task.Factory.StartNew(
            () =>
            {
                foreach (var (aligned, shiftSec) in Ft4EchoAligner.EnumerateEchoAlignments(raw, 12000, txHz))
                {
                    if (!PublishDecoded(slotStart, aligned, txSlot: true, shiftSec, ownOnly: true, rxHz, txHz))
                        continue;

                    Log.Information(
                        "FT4 own echo recovered after shifting the slot by {Shift:0.00} s (parallel pass)",
                        shiftSec);
                    return true;
                }

                return false;
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

        Task.WaitAll(primary, late);
        if (primary.Result || late.Result)
            return;

        TryCalibrateEchoFromSpectrum(slotStart, raw, txHz);
    }

    private void RecoverOwnEchoSequential(
        DateTime slotStart,
        float[] raw,
        float[] corrected,
        double rxHz,
        double txHz)
    {
        // The waterfall is the raw capture. Doppler removal and the decoder's early
        // time window both hide a full-duplex copy that is obvious on screen.
        var foundOwn = false;
        if (!ReferenceEquals(corrected, raw))
            foundOwn = PublishDecoded(slotStart, raw, txSlot: true, timeShiftSec: 0, ownOnly: true, rxHz, txHz);
        if (foundOwn)
            return;

        foreach (var (aligned, shiftSec) in Ft4EchoAligner.EnumerateEchoAlignments(raw, 12000, txHz))
        {
            foundOwn = PublishDecoded(slotStart, aligned, txSlot: true, shiftSec, ownOnly: true, rxHz, txHz);
            if (foundOwn)
            {
                Log.Information(
                    "FT4 own echo recovered after shifting the slot by {Shift:0.00} s",
                    shiftSec);
                return;
            }
        }

        TryCalibrateEchoFromSpectrum(slotStart, raw, txHz);
    }

    /// <summary>Post decoder output. Returns true when our own callsign was published.</summary>
    private bool PublishDecoded(
        DateTime slotStart,
        float[] samples,
        bool txSlot,
        double timeShiftSec,
        bool ownOnly,
        double rxHz,
        double txHz)
    {
        Ft8Native.ResolveSearchBand(ownOnly ? txHz : rxHz, txHz, out var fMin, out var fMax);
        var decoded = Ft8Native.DecodeFt4(samples, 12000, fMin, fMax);
        var my = Ft4MessageCodec.NormalizeCall(_settings.Current.GroundStation.Callsign ?? "");
        var any = false;
        var foundOwn = false;

        foreach (var d in decoded)
        {
            Ft4MessageCodec.TryParse(d.text, out var callTo, out var callDe, out var extra);
            var isOwn = my.Length > 0
                && callDe is not null
                && callDe.Equals(my, StringComparison.OrdinalIgnoreCase);

            if (ownOnly && !isOwn)
                continue;

            if (isOwn)
                _lastRelevantDecodeUtc = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(callDe))
                Ft8Native.RememberCallsign(callDe);

            var timeSec = d.time_sec + (float)timeShiftSec;

            if (isOwn && txSlot)
            {
                var echo = new Ft4DecodedMessage(
                    slotStart,
                    d.text,
                    d.freq_hz,
                    timeSec,
                    d.snr,
                    callTo,
                    callDe,
                    extra,
                    IsOwnEcho: true);

                var echoKey = slotStart.Ticks + "|echo|" + d.text + "|" + ((int)Math.Round(d.freq_hz / 5.0) * 5);
                lock (_decodePostGate)
                {
                    if (!_postedDecodeKeys.Add(echoKey))
                        continue;
                }

                ApplyEchoCalibration(slotStart, echo);

                foundOwn = true;
                any = true;
                Log.Information(
                    "FT4 own echo: {Text} at {Hz:0} Hz, DT {Dt:0.00} s, SNR {Snr:0} dB",
                    d.text,
                    d.freq_hz,
                    timeSec,
                    d.snr);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    Decodes.Insert(0, echo);
                    while (Decodes.Count > 200)
                        Decodes.RemoveAt(Decodes.Count - 1);
                });
                continue;
            }

            var dedupeKey = slotStart.Ticks + "|" + d.text + "|" + ((int)Math.Round(d.freq_hz / 5.0) * 5);
            lock (_decodePostGate)
            {
                if (!_postedDecodeKeys.Add(dedupeKey))
                    continue;
            }

            var msg = new Ft4DecodedMessage(
                slotStart,
                d.text,
                d.freq_hz,
                timeSec,
                d.snr,
                callTo,
                callDe,
                extra,
                isOwn);

            any = true;
            if (isOwn)
                foundOwn = true;
            ReportToPskReporter(msg);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Decodes.Insert(0, msg);
                while (Decodes.Count > 200)
                    Decodes.RemoveAt(Decodes.Count - 1);
            });

            if (_sequencer is not null && !isOwn)
            {
                var finished = _sequencer.OnDecoded(msg);
                _lastRelevantDecodeUtc = DateTime.UtcNow;
                if (finished)
                    _ = TryLogAsync(manual: false);
            }
        }

        if (any)
            Changed?.Invoke();
        return foundOwn;
    }

    private void ApplyEchoCalibration(DateTime slotStart, Ft4DecodedMessage own)
    {
        var seq = _sequencer;
        if (seq is null)
            return;

        if (TryClaimEchoCalibration(slotStart))
            ApplyEchoCalibrationHz(own.FreqHz - seq.TxAudioHz);
    }

    /// <summary>
    /// The trace is on the waterfall but the decoder missed the message.
    /// Measure that tone and nudge the uplink so the next slot sits on the red bracket.
    /// </summary>
    private void TryCalibrateEchoFromSpectrum(DateTime slotStart, float[] raw, double txHz)
    {
        if (!Ft4EchoAligner.TryMeasurePeakHz(raw, 12000, txHz, out var peakHz))
            return;

        var errorHz = peakHz - txHz;
        if (Math.Abs(errorHz) < 15)
            return;

        // Undecoded peaks can be another station near the bracket. Only trim small drift here;
        // large corrections come from a decoded echo (or Tune).
        if (Math.Abs(errorHz) > MaxWaterfallEchoCorrectionHz)
        {
            Log.Debug(
                "FT4 waterfall peak {Peak:0} Hz is {Error:0} Hz from the TX marker; left for a decoded echo",
                peakHz,
                errorHz);
            return;
        }

        if (!TryClaimEchoCalibration(slotStart))
            return;

        Log.Information(
            "FT4 echo on the waterfall is {Peak:0} Hz, TX marker {Tx:0} Hz, error {Error:0} Hz",
            peakHz,
            txHz,
            errorHz);
        ApplyEchoCalibrationHz(errorHz);
    }

    /// <summary>
    /// A TX slot is decoded more than once (early and end-of-slot passes), and each trim only
    /// takes effect on the next transmit. Correct at most once per slot or the trim overshoots.
    /// </summary>
    private bool TryClaimEchoCalibration(DateTime slotStart)
    {
        lock (_decodePostGate)
        {
            if (_lastEchoCalibrationSlot == slotStart)
                return false;
            _lastEchoCalibrationSlot = slotStart;
            return true;
        }
    }

    private void ApplyEchoCalibrationHz(double errorHz)
    {
        if (Math.Abs(errorHz) < 5 || !double.IsFinite(errorHz))
            return;

        var sat = ResolveCalibrationSatellite();
        if (sat is null)
            return;

        // Positive audio error (echo above the marker) needs a higher uplink dial on a
        // reversing LSB-up / USB-down satellite, which brings the echo back down.
        var deltaKHz = errorHz / 1000.0;
        var current = _settings.Current.Ft4.GetUplinkCalibrationKHz(sat);
        _settings.Current.Ft4.SetUplinkCalibrationKHz(sat, current + deltaKHz);
        _settings.RequestSave();
        Status = _l.Get("Ft4.Status.EchoCalibration", deltaKHz * 1000.0, sat);
        Changed?.Invoke();
    }

    /// <summary>Satellite the uplink trim belongs to. Ignores the overlay placeholder.</summary>
    private string? ResolveCalibrationSatellite()
    {
        var snap = _snapshot.GetCurrent();
        if (IsRealSatelliteName(snap.SatelliteName))
            return snap.SatelliteName.Trim();
        if (IsRealSatelliteName(_frequencies.SatelliteName))
            return _frequencies.SatelliteName.Trim();
        return null;
    }

    private static bool IsRealSatelliteName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var trimmed = name.Trim();
        return trimmed is not "-" and not "—" and not "–";
    }

    private async Task TryLogAsync(bool manual)
    {
        var seq = _sequencer;
        if (seq?.TheirCall is null)
        {
            if (manual)
            {
                Status = _l.Get("Ft4.Status.NothingToLog");
                Changed?.Invoke();
            }
            return;
        }

        if (!manual && !seq.CanLog())
            return;

        var key = $"{seq.TheirCall}|{seq.ReportSent}|{seq.ReportReceived}|{seq.TheirGrid}";
        if (!manual && string.Equals(key, _lastLoggedKey, StringComparison.Ordinal))
            return;

        try
        {
            var books = await _logbook.ListLogbooksAsync().ConfigureAwait(false);
            var book = books.FirstOrDefault();
            if (book is null)
            {
                Status = _l.Get("Ft4.Status.NeedLogbook");
                Changed?.Invoke();
                return;
            }

            var snap = _snapshot.GetCurrent();
            var cloudlogUpload = book.CloudlogAutoUpload && book.CloudlogStationProfileId.HasValue
                ? CloudlogUploadStatus.Pending
                : CloudlogUploadStatus.None;
            var record = await _logbook.AddQsoAsync(new QsoRecordCreateRequest
            {
                LogbookId = book.Id,
                QsoUtc = Ft4Clock.UtcNow,
                Call = seq.TheirCall,
                RstSent = Ft4MessageCodec.NormalizeSnrReport(seq.ReportSent),
                RstRcvd = Ft4MessageCodec.NormalizeSnrReport(seq.ReportReceived),
                GridSquare = seq.TheirGrid ?? "",
                SatName = snap.IsAvailable ? snap.SatelliteName : _frequencies.SatelliteName,
                Mode = "FT4",
                ModeRx = "FT4",
                FreqHz = snap.IsAvailable ? snap.UplinkHz : 0,
                FreqRxHz = snap.IsAvailable ? snap.DownlinkHz : 0,
                Band = snap.IsAvailable ? snap.Band : "",
                BandRx = snap.IsAvailable ? snap.BandRx : "",
                PropMode = "SAT",
                Comment = manual ? "FT4 manual log" : "",
                CloudlogUploadStatus = cloudlogUpload
            }).ConfigureAwait(false);

            if (cloudlogUpload == CloudlogUploadStatus.Pending)
                await _cloudlogUpload.QueueUploadIfEnabledAsync(record.Id).ConfigureAwait(false);

            _lastLoggedKey = key;
            Status = manual
                ? _l.Get("Ft4.Status.LoggedManual", record.Call)
                : _l.Get("Ft4.Logged", record.Call);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FT4 logbook save failed");
            Status = _l.Get("Ft4.Status.LogbookSaveFailed");
            Changed?.Invoke();
        }
    }

    private bool TryGetDopplerSlopeHzPerSec(
        DateTime anchorUtc,
        double intervalSec,
        out double downlinkSlopeHzPerSec,
        out double uplinkSlopeHzPerSec)
    {
        downlinkSlopeHzPerSec = 0;
        uplinkSlopeHzPerSec = 0;

        var mode = _frequencies.SelectedMode;
        if (mode is null)
            return false;

        var norad = _tracking.FocusedNoradId;
        if (string.IsNullOrWhiteSpace(norad))
            return false;

        try
        {
            var site = _settings.Current.GroundStation;
            var t0 = anchorUtc.Kind == DateTimeKind.Utc ? anchorUtc : anchorUtc.ToUniversalTime();
            var t1 = t0.AddSeconds(intervalSec);
            var rr0 = _propagator.GetLookAngles(norad, site, t0).RangeRateKmPerSec;
            var rr1 = _propagator.GetLookAngles(norad, site, t1).RangeRateKmPerSec;

            var rxOffset = _frequencies.ReceiveOffsetKHz;
            var txOffset = _frequencies.TransmitOffsetKHz
                + _settings.Current.Ft4.GetUplinkCalibrationKHz(ResolveCalibrationSatellite());

            var s0 = Ft4DopplerShift.ComputeShiftsHz(mode, rr0, rxOffset, txOffset);
            var s1 = Ft4DopplerShift.ComputeShiftsHz(mode, rr1, rxOffset, txOffset);
            downlinkSlopeHzPerSec = Ft4DopplerShift.SlopeHzPerSec(s0.DownlinkHz, s1.DownlinkHz, intervalSec);
            uplinkSlopeHzPerSec = Ft4DopplerShift.SlopeHzPerSec(s0.UplinkHz, s1.UplinkHz, intervalSec);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "FT4 Doppler slope unavailable");
            return false;
        }
    }

    private static int ResampleTo12k(ReadOnlySpan<float> input, int inRate, Span<float> output)
    {
        if (inRate == 12000)
        {
            input.CopyTo(output);
            return input.Length;
        }

        var outLen = (int)((long)input.Length * 12000 / inRate);
        outLen = Math.Min(outLen, output.Length);
        for (var i = 0; i < outLen; i++)
        {
            var srcPos = i * (double)inRate / 12000.0;
            var i0 = (int)srcPos;
            var i1 = Math.Min(i0 + 1, input.Length - 1);
            var frac = srcPos - i0;
            output[i] = (float)(input[i0] * (1 - frac) + input[i1] * frac);
        }

        return outLen;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _pskReporter.Dispose();
        _ptt.Dispose();
        _audio.Dispose();
    }
}
