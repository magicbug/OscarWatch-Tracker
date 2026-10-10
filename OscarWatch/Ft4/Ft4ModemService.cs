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
    private readonly ISatelliteLinkBroadcastService _satelliteLink;
    private readonly ILiveTrackerSnapshotProvider _snapshot;
    private readonly IOrbitPropagator _propagator;
    private readonly IRigController _rig;
    private readonly ILocalizationService _l;
    private readonly IAudioRecordingService _recording;
    private readonly IGpsService _gps;
    private readonly Ft4AudioService _audio = new();
    private readonly Ft4PttKeyer _ptt;
    private readonly Ft4SlotRecorder _slotRecorder;
    private readonly PskReporterClient _pskReporter = new();
    private readonly OscarWatchSpotReporter _spotReporter;
    private readonly string _spotClientId;
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
    private long _txKickedSlotTicks; // slot start of the last KickTransmit, set from the TX timer thread
    private long _dopplerPreparedSlotTicks; // TX slot whose CAT Doppler write already finished
    private double _txKickedAudioHz; // TX audio Hz sent in that slot; set before _txKickedSlotTicks
    private DateTime _lastEchoCalibrationSlot = DateTime.MinValue;
    private bool _decodeQueuedThisSlot;
    private bool? _deepDecodeActive;
    private readonly Ft4RxHealth _rxHealth = new(DateTime.UtcNow);
    private readonly HashSet<string> _postedDecodeKeys = new(StringComparer.Ordinal);
    private readonly Ft4CallDt _callDt = new();
    private readonly Dictionary<string, Ft4DecodedMessage> _postedEchoes = new(StringComparer.Ordinal);
    private readonly object _decodePostGate = new();
    private DateTime _txWatchdogResetUtc = DateTime.UtcNow;
    private int _tuning; // 0 off, 1 on
    private Ft4QsoSequencer? _sequencer;

    public Ft4ModemService(
        ISettingsService settings,
        ILiveTrackingService tracking,
        FrequencyOverlayViewModel frequencies,
        IRigController rig,
        IQsoLogbookRepository logbook,
        ICloudlogQsoUploadService cloudlogUpload,
        ISatelliteLinkBroadcastService satelliteLink,
        ILiveTrackerSnapshotProvider snapshot,
        IOrbitPropagator propagator,
        ILocalizationService localization,
        IAudioRecordingService recording,
        IGpsService gps,
        ISatelliteSpotService spots)
    {
        _settings = settings;
        _tracking = tracking;
        _frequencies = frequencies;
        _logbook = logbook;
        _cloudlogUpload = cloudlogUpload;
        _satelliteLink = satelliteLink;
        _snapshot = snapshot;
        _propagator = propagator;
        _rig = rig;
        _l = localization;
        _recording = recording;
        _gps = gps;
        _ptt = new Ft4PttKeyer(rig, settings);
        _slotRecorder = new Ft4SlotRecorder(() => _settings.Current.Ft4.SaveSlotAudio);
        _pskReporter.Diagnostic += (message, ex) =>
        {
            if (ex is null)
                Log.Information("{Message}", message);
            else
                Log.Warning(ex, "{Message}", message);
        };
        ApplyPskReporterSettings();
        _spotClientId = $"OscarWatch-Tracker/{OscarWatchHttpClients.GetProductVersion()}";
        _spotReporter = new OscarWatchSpotReporter(spots, OscarWatchSpotsActive, () => _settings.Current.SatelliteStatus);
        _spotReporter.Diagnostic += (message, ex) =>
        {
            if (ex is null)
                Log.Information("{Message}", message);
            else
                Log.Warning(ex, "{Message}", message);
        };
        _frequencies.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FrequencyOverlayViewModel.SelectedMode))
                SyncSlotGate(IsRunning);
        };
    }

    private bool OscarWatchSpotsActive() =>
        _settings.Current.Ft4.OscarWatchSpotsEnabled
        && Ft4OscarWatchSpots.HasApiToken(_settings.Current.SatelliteStatus.ApiToken);

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

    private void ReportToOscarWatch(Ft4DecodedMessage msg)
    {
        if (!OscarWatchSpotsActive())
            return;

        try
        {
            var snap = _snapshot.GetCurrent();
            var station = _settings.Current.GroundStation;
            if (!Ft4OscarWatchSpots.TryCreate(msg, snap, station.Callsign, _spotClientId, out var spot))
                return;

            _spotReporter.TryEnqueue(spot);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "OscarWatch spot skipped");
        }
    }

    public ObservableCollection<Ft4DecodedMessage> Decodes { get; } = new();
    public string Status { get; private set; } = "";
    public string ManualPrompt { get; private set; } = "";
    public bool IsRunning { get; private set; }
    public bool IsTuning => Volatile.Read(ref _tuning) == 1;
    public bool NativeAvailable => Ft8Native.IsAvailable;
    public Ft4QsoSequencer? Sequencer => _sequencer;

    /// <summary>Green-bracket RX audio Hz. Hold Tx can keep this different from the TX bracket.</summary>
    public double RxAudioHz { get; set; } = 1500;
    public double TxPlaybackPeak => _audio.PlaybackPeak;
    public event Action? Changed;

    private string? _lastLoggedKey;
    private string? _eligibilityBlockStatus;
    private Ft4PounceTarget? _pounceTarget;
    private Ft4DecodedMessage? _pouncedDecode;
    private readonly object _slotGateLock = new();
    private volatile bool _slotGateHeld;
    private volatile bool _windowOpen;

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
            Status = _l.Get("Ft4.Status.InputUnavailable");
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
            Status = _l.Get(Ft8Native.UnavailableMessageKey);
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
            () => _settings.Current.Ft4.HoldTxFrequency,
            () => _settings.Current.Ft4.AutoReply);
        _sequencer.TxAudioHz = Math.Clamp(_settings.Current.Ft4.TxAudioHz, 200, 3000);
        RxAudioHz = _sequencer.TxAudioHz;
        _lastLoggedKey = null;

        Ft8Native.ClearCallsigns();
        Ft8Native.RememberCallsign(call);

        // Pass recording shares the downlink capture card with FT4 RX. Stop it before we open
        // PortAudio so the modem loop is not starved waiting on a contended input stream.
        StopPassRecordingForModem();

        // A missing input (radio USB audio powered off) must not escape onto the UI thread.
        // That unhandled exception closes OscarWatch.
        try
        {
            _audio.StartCapture(
                _settings.Current.Ft4.InputDeviceId,
                _settings.Current.Ft4.InputDeviceDisplayName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FT4 capture open failed");
            try
            {
                _audio.StopCapture();
            }
            catch (Exception stopEx)
            {
                Log.Debug(stopEx, "FT4 capture cleanup after a failed open");
            }

            _sequencer = null;
            Status = _l.Get("Ft4.Status.InputUnavailable");
            Changed?.Invoke();
            return;
        }

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
            _postedEchoes.Clear();
            _callDt.Clear();
            _lastEchoCalibrationSlot = DateTime.MinValue;
        }
        _txWatchdogResetUtc = DateTime.UtcNow;
        _rxHealth.Reset(DateTime.UtcNow);
        RefreshClockFromGps();

        SyncSlotGate(sessionRunning: true);

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

    /// <summary>The FT4 window is on screen. The session keeps running after it closes.</summary>
    public void SetWindowOpen(bool open)
    {
        _windowOpen = open;
        SyncSlotGate(IsRunning);
    }

    /// <summary>
    /// OrbitDeck: hold the CAT dial within each slot while audio corrects within-slot drift.
    /// Released when FT4 is not in use, so other transponders get continuous Doppler.
    /// </summary>
    private void SyncSlotGate(bool sessionRunning)
    {
        var hold = Ft4SlotGate.ShouldHold(sessionRunning, _windowOpen, _frequencies.SelectedMode);
        lock (_slotGateLock)
        {
            if (hold == _slotGateHeld)
                return;
            _slotGateHeld = hold;
            _rig.SetFt4SlotGatedDoppler(hold);
            if (hold)
                _rig.ForceFt4DopplerStep();
        }

        Log.Information("FT4 slot gate {State}", hold ? "held" : "released");
    }

    // Audio-domain Doppler assumes the dial is held for the slot. With continuous CAT
    // Doppler it would correct the drift twice.
    private bool UseAudioDopplerTx => _settings.Current.Ft4.AudioDopplerTx && _slotGateHeld;

    private bool UseAudioDopplerRx => _settings.Current.Ft4.AudioDopplerRx && _slotGateHeld;

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
        _ptt.ReleasePort();
        _slotRecorder.FlushAll();
        _audio.StopOutput();
        _audio.StopCapture();
        SyncSlotGate(sessionRunning: false);
        IsRunning = false;
        Status = _l.Get("Ft4.Status.Stopped");
        Changed?.Invoke();
    }

    public void StartCq(bool evenSlot)
    {
        StopTune();
        if (!EnsureSatelliteAllowed())
            return;

        _sequencer?.StartCq(evenSlot);
        _lastLoggedKey = null;
        _txWatchdogResetUtc = DateTime.UtcNow;
        if (!TryAcceptRecentCaller(out var answered))
            Status = _l.Get("Ft4.Status.CallingCq");
        else
            Status = _l.Get("Ft4.Status.Answering", answered);
        Changed?.Invoke();
    }

    /// <summary>
    /// Apply the Auto reply checkbox at once. While a CQ is going out, a station
    /// that already called in the last few seconds is answered on the next slot.
    /// </summary>
    public void SetAutoReply(bool enabled)
    {
        var seq = _sequencer;
        if (seq is null)
            return;

        seq.SetAutoReply(enabled);
        if (!enabled || !TryAcceptRecentCaller(out var answered))
            return;

        _txWatchdogResetUtc = DateTime.UtcNow;
        Status = _l.Get("Ft4.Status.Answering", answered);
        Changed?.Invoke();
    }

    /// <summary>
    /// A caller decoded while Auto reply was off is not seen again. Look back
    /// about three FT4 slots so ticking the box still answers them.
    /// </summary>
    private bool TryAcceptRecentCaller(out string answeredText)
    {
        answeredText = "";
        var seq = _sequencer;
        if (seq is null || !seq.PrepareAutoReply())
            return false;

        var cutoff = DateTime.UtcNow.AddSeconds(-22);
        foreach (var msg in Decodes)
        {
            if (msg.SlotUtc < cutoff)
                break;
            // Newest first: once we reach the contact that just finished, everything
            // older is that same contact. A station who calls after 73 is still answered.
            if (seq.IsHistoricDecode(msg.SlotUtc))
                break;
            if (!msg.IsReceiveActivity)
                continue;

            var before = seq.Phase;
            var finished = seq.OnDecoded(msg);
            if (seq.Phase != Ft4QsoPhase.InQso || before == Ft4QsoPhase.InQso)
                continue;

            answeredText = msg.Text;
            Log.Information("FT4 auto reply to a recent call: {Text}", msg.Text);
            if (finished)
                _ = TryLogAsync(manual: false);
            return true;
        }

        return false;
    }

    public bool QueueReport(float? snrDb)
    {
        if (_sequencer is null || string.IsNullOrWhiteSpace(_sequencer.TheirCall))
            return false;
        StopTune();
        if (!EnsureSatelliteAllowed())
            return false;
        if (!_sequencer.ForceReport(snrDb))
            return false;

        _txWatchdogResetUtc = DateTime.UtcNow;
        Status = _l.Get("Ft4.Status.SendingReport", _sequencer.TheirCall);
        Changed?.Invoke();
        return true;
    }

    public bool Queue73()
    {
        if (_sequencer is null || string.IsNullOrWhiteSpace(_sequencer.TheirCall))
            return false;
        StopTune();
        if (!EnsureSatelliteAllowed())
            return false;
        if (!_sequencer.Force73())
            return false;

        _txWatchdogResetUtc = DateTime.UtcNow;
        Status = _l.Get("Ft4.Status.Sending73", _sequencer.TheirCall);
        Changed?.Invoke();
        return true;
    }

    public void EnableTx()
    {
        StopTune();
        if (!EnsureSatelliteAllowed())
            return;

        _sequencer?.EnableTx();
        // Reset idle timeout so re-arming after a watchdog halt does not trip again immediately.
        _txWatchdogResetUtc = DateTime.UtcNow;
        if (TryAcceptRecentCaller(out var answered))
            Status = _l.Get("Ft4.Status.Answering", answered);
        else
            Status = _l.Get("Ft4.Status.TxEnabled");
        Changed?.Invoke();
        CheckRfPowerInBackground();
    }

    public void HaltTx()
    {
        Volatile.Write(ref _pounceTarget, null);
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

    /// <summary>True while CQ/QSO transmit is armed, Tune is on, or audio is still playing.</summary>
    public bool IsTransmissionActive =>
        IsTuning
        || _sequencer is { TransmitEnabled: true }
        || _audio.IsPlaying;

    /// <summary>Stop the current transmission at once: audio off and PTT dropped, without waiting for the slot.</summary>
    public void StopTransmissionNow()
    {
        HaltTx();
        _ptt.UnkeyNow();
    }

    /// <summary>Folder for saved receive slots (Settings, Save slot audio).</summary>
    public string SlotRecordingDirectory => _slotRecorder.Directory;

    /// <summary>Free the separate PTT COM port when the PTT method or port setting no longer uses it.</summary>
    public void OnPttSettingsChanged() => _ptt.ReleaseUnusedPort();

    /// <summary>
    /// WSJT-X-style Tune: continuous tone on the TX audio frequency with PTT.
    /// Call again (or Halt Tx) to stop. Tune does not change the stored uplink trim.
    /// </summary>
    public bool StartTune()
    {
        if (!IsRunning)
            return false;
        if (!EnsureSatelliteAllowed())
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
        Status = _l.Get("Ft4.Status.Tuning", FormatTuneHz());
        Changed?.Invoke();

        _ = Task.Run(async () =>
        {
            try
            {
                // Tune keys at once, so the RF power read must answer before PTT (off the UI thread).
                if (!CheckRfPowerAllowed())
                {
                    Volatile.Write(ref _tuning, 0);
                    Changed?.Invoke();
                    return;
                }

                if (!IsTuning)
                    return;

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

    public void Answer(Ft4DecodedMessage decode) => TryAnswer(decode);

    private bool TryAnswer(Ft4DecodedMessage decode)
    {
        if (_sequencer is null)
            return false;
        StopTune();
        if (!EnsureSatelliteAllowed())
            return false;

        var even = Ft4SlotClock.IsEvenSlot(decode.SlotUtc, Ft4SlotClock.Ft4SlotSeconds);
        _sequencer.StartAnswer(decode, oppositeEvenSlot: !even);
        _lastLoggedKey = null;
        _txWatchdogResetUtc = DateTime.UtcNow;
        Status = _l.Get("Ft4.Status.Answering", decode.Text);
        Changed?.Invoke();
        return true;
    }

    /// <summary>The armed wait-and-pounce target, or null.</summary>
    public Ft4PounceTarget? PounceTarget => Volatile.Read(ref _pounceTarget);

    public bool IsPounceArmed => PounceTarget is not null;

    public void ArmPounce(Ft4PounceTarget target)
    {
        Volatile.Write(ref _pounceTarget, target);
        Status = _l.Get("Ft4.Status.PounceWaiting", target.Value);
        Changed?.Invoke();
    }

    public void DisarmPounce()
    {
        if (Interlocked.Exchange(ref _pounceTarget, null) is null)
            return;
        Status = _l.Get("Ft4.Status.PounceCancelled");
        Changed?.Invoke();
    }

    /// <summary>The decode pounce last answered, handed to the UI once so it can move the RX / TX brackets.</summary>
    public Ft4DecodedMessage? TakePouncedDecode() => Interlocked.Exchange(ref _pouncedDecode, null);

    private void TryPounce(Ft4DecodedMessage msg, string myCall)
    {
        var target = Volatile.Read(ref _pounceTarget);
        var seq = _sequencer;
        if (target is null || seq is null || !target.Matches(msg, myCall))
            return;

        if (seq.Phase == Ft4QsoPhase.InQso)
        {
            // Auto reply may already have taken the target from a CQ: nothing left to wait for.
            var partner = Ft4PounceTarget.BaseCall(seq.TheirCall ?? "");
            var from = Ft4PounceTarget.BaseCall(msg.CallDe ?? "");
            if (partner.Length > 0
                && partner.Equals(from, StringComparison.OrdinalIgnoreCase)
                && Interlocked.CompareExchange(ref _pounceTarget, null, target) == target)
            {
                Status = _l.Get("Ft4.Status.PounceFired", msg.CallDe ?? target.Value, msg.Text);
                Changed?.Invoke();
            }

            return;
        }

        // One shot: only the thread that clears the target answers.
        if (Interlocked.CompareExchange(ref _pounceTarget, null, target) != target)
            return;

        Log.Information("FT4 pounce on {Target}: {Message}", target.Value, msg.Text);
        if (!TryAnswer(msg))
        {
            Changed?.Invoke();
            return;
        }

        Volatile.Write(ref _pouncedDecode, msg);
        Status = _l.Get("Ft4.Status.PounceFired", msg.CallDe ?? target.Value, msg.Text);
        Changed?.Invoke();
    }

    /// <summary>
    /// Re-check satellite eligibility while listening (e.g. operator switched to FM, FO-29, or AO-7).
    /// Halts TX when the focused satellite is not allowed for FT4, and clears the block
    /// message once the operator moves to an allowed satellite.
    /// </summary>
    public void RefreshSatelliteEligibility()
    {
        var reason = EvaluateTransmitBlock();
        if (reason == Ft4SatelliteEligibility.BlockReason.None)
        {
            if (_eligibilityBlockStatus is null)
                return;

            var showingBlock = string.Equals(Status, _eligibilityBlockStatus, StringComparison.Ordinal);
            _eligibilityBlockStatus = null;
            if (!showingBlock)
                return;

            Status = _l.Get("Ft4.Status.Listening");
            Changed?.Invoke();
            return;
        }

        if (_sequencer?.TransmitEnabled == true)
            HaltTx();

        var msg = _l.Get(Ft4SatelliteEligibility.StatusKey(reason));
        _eligibilityBlockStatus = msg;
        if (string.Equals(Status, msg, StringComparison.Ordinal))
            return;

        Status = msg;
        Changed?.Invoke();
    }

    /// <summary>Satellite and mode eligibility only (no CAT I/O), halting TX when it fails.</summary>
    private bool EnsureSatelliteAllowed()
    {
        var reason = EvaluateTransmitBlock();
        if (reason == Ft4SatelliteEligibility.BlockReason.None)
            return true;

        if (_sequencer?.TransmitEnabled == true)
            HaltTx();

        Status = _l.Get(Ft4SatelliteEligibility.StatusKey(reason));
        _eligibilityBlockStatus = Status;
        Changed?.Invoke();
        return false;
    }

    /// <summary>
    /// When the operator presses Enable Tx: read RF power off the UI thread so the window does not wait.
    /// A reading over the limit halts TX or lowers the power once the radio answers. Later slots
    /// do not ask again.
    /// </summary>
    private void CheckRfPowerInBackground()
    {
        _ = Task.Run(() =>
        {
            try
            {
                CheckRfPowerAllowed();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "FT4 RF power check failed");
            }
        });
    }

    /// <summary>CAT RF power read, lowering power or halting TX when it is over the FT4 limit.</summary>
    private bool CheckRfPowerAllowed()
    {
        if (_rig.TryGetUplinkRfPowerWatts(out var watts) && Ft4RfPowerLimit.ExceedsLimit(watts))
        {
            var lowered = _settings.Current.Ft4.AutoLowerRfPower
                && _rig.TrySetUplinkRfPowerWatts(Ft4RfPowerLimit.MaxWatts)
                && _rig.TryGetUplinkRfPowerWatts(out watts)
                && !Ft4RfPowerLimit.ExceedsLimit(watts);
            if (!lowered)
            {
                if (_sequencer?.TransmitEnabled == true)
                    HaltTx();

                Status = _l.Get(Ft4RfPowerLimit.StatusKey, (int)Ft4RfPowerLimit.MaxWatts);
                Changed?.Invoke();
                return false;
            }

            Log.Information("FT4 lowered uplink RF power to {Watts:0} W", watts);
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
            {
                _postedDecodeKeys.Clear();
                _postedEchoes.Clear();
                _callDt.Clear();
            }
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

    /// <summary>True when the current period is being transmitted, or will still be used.</summary>
    public bool IsLiveTransmitSlot(DateTime utc)
    {
        var seq = _sequencer;
        if (seq is not { TransmitEnabled: true })
            return false;

        var ticks = Volatile.Read(ref _txKickedSlotTicks);
        DateTime? kicked = ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        return Ft4SlotClock.IsLiveTransmitSlot(
            utc,
            Ft4SlotClock.Ft4SlotSeconds,
            transmitEnabled: true,
            seq.PreferEvenSlot,
            kicked);
    }

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
                    var fullSlotPass = previousSlot != DateTime.MinValue
                        && !previousWasTx
                        && _decodeQueuedThisSlot
                        && Ft4DecodeDepth.UseFullSlotDecode(_snapshot.GetCurrent().ElevationDeg);
                    var needEndDecode = previousSlot != DateTime.MinValue
                        && (!_decodeQueuedThisSlot || previousWasTx || fullSlotPass);
                    var recordSlot = previousSlot != DateTime.MinValue
                        && !previousWasTx
                        && _slotRecorder.Enabled
                        && Ft4SlotRecordingFiles.ShouldRecord(_snapshot.GetCurrent().ElevationDeg);
                    lock (_gate)
                    {
                        if ((needEndDecode || recordSlot) && _slotBuffer.Count >= (int)(12000 * Ft4SlotClock.Ft4SlotSeconds / 2))
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
                    {
                        _postedDecodeKeys.RemoveWhere(k => !k.StartsWith(keepPrefix, StringComparison.Ordinal));
                        List<string>? staleEchoes = null;
                        foreach (var key in _postedEchoes.Keys)
                        {
                            if (key.StartsWith(keepPrefix, StringComparison.Ordinal))
                                continue;
                            staleEchoes ??= new List<string>();
                            staleEchoes.Add(key);
                        }

                        if (staleEchoes is not null)
                        {
                            foreach (var key in staleEchoes)
                                _postedEchoes.Remove(key);
                        }
                    }

                    // Audio first when the soft timer missed. CAT PTT is queued inside KickTransmit
                    // so it is not stuck behind this slot's Doppler write.
                    if (!alreadyTx && !IsTuning)
                        KickTransmit(slotStart, ct);
                    SyncSlotGate(sessionRunning: true);
                    if (!CatTransmitOwnsTheRig(slotStart))
                        _rig.ForceFt4DopplerStep();
                    RefreshClockFromGps();

                    if (previousSamples is not null && recordSlot)
                    {
                        var snap = _snapshot.GetCurrent();
                        _slotRecorder.SaveAudio(previousSlot, previousSamples, 12000, snap.SatelliteName, snap.ElevationDeg);
                    }

                    if (previousSamples is not null && needEndDecode)
                        QueueDecode(previousSlot, previousSamples, previousWasTx, fullSlotPass);

                    // The slot before last has had its end-of-slot passes by now.
                    _slotRecorder.Flush(previousSlot);
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

                // Watchdog: stop TX if nobody has replied to this station.
                var watchdogMinutes = Ft4TxWatchdog.ClampMinutes(_settings.Current.Ft4.TxWatchdogMinutes);
                if (Ft4TxWatchdog.ShouldHalt(
                        _sequencer is { TransmitEnabled: true },
                        IsTuning,
                        watchdogMinutes,
                        DateTime.UtcNow,
                        _txWatchdogResetUtc))
                {
                    HaltTx();
                    Status = _l.Get("Ft4.Status.WatchdogStopped", watchdogMinutes);
                    Changed?.Invoke();
                }

                var n = _audio.ReadCaptureSamples(scratch);
                if (_rxHealth.OnCapture(DateTime.UtcNow, n > 0) is { } captureEvent)
                    LogRxHealth(captureEvent);
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

    private void LogRxHealth(Ft4RxHealthEvent e)
    {
        switch (e.Kind)
        {
            case Ft4RxHealthKind.NoAudio:
                Log.Warning(
                    "FT4 capture has delivered no audio for {Seconds:0.0} s from '{Device}'",
                    e.Gap.TotalSeconds,
                    _settings.Current.Ft4.InputDeviceDisplayName);
                break;
            case Ft4RxHealthKind.AudioResumed:
                Log.Information("FT4 capture audio resumed after {Seconds:0.0} s without any", e.Gap.TotalSeconds);
                break;
            case Ft4RxHealthKind.Silent:
                Log.Warning(
                    "FT4 capture is silent ({Level:0} dBFS) for {Slots} receive slots in a row from '{Device}'",
                    e.LevelDbfs,
                    e.Slots,
                    _settings.Current.Ft4.InputDeviceDisplayName);
                break;
            case Ft4RxHealthKind.SoundResumed:
                Log.Information(
                    "FT4 capture has sound again ({Level:0} dBFS) after {Slots} silent receive slots",
                    e.LevelDbfs,
                    e.Slots);
                break;
            case Ft4RxHealthKind.NoDecodes:
                Log.Warning(
                    "FT4 has decoded nothing for {Slots} receive slots with the satellite at {Elevation:0}° (capture level {Level:0} dBFS)",
                    e.Slots,
                    e.ElevationDeg,
                    e.LevelDbfs);
                break;
            case Ft4RxHealthKind.DecodesResumed:
                Log.Information(
                    "FT4 decodes resumed after {Slots} empty receive slots (elevation {Elevation:0}°)",
                    e.Slots,
                    e.ElevationDeg);
                break;
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

        if (!EnsureSatelliteAllowed())
            return;

        if (Ft4SlotClock.IsEvenSlot(slotStart, Ft4SlotClock.Ft4SlotSeconds) != seq.PreferEvenSlot)
            return;

        if (Interlocked.CompareExchange(ref _txRunning, 1, 0) != 0)
            return;

        Interlocked.Exchange(ref _txKickedAudioHz, Math.Clamp(seq.TxAudioHz, 200, 3000));
        Interlocked.Exchange(ref _txKickedSlotTicks, slotStart.Ticks);
        _txThisSlot = true;

        // Queue CAT PTT before playback and before the caller can enqueue Doppler.
        // The 0.5 s silence at the start of the waveform is the lead-in.
        if (CatPttSharesRig())
            _ptt.KeyNow();
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
                _ptt.UnkeyNow();
            }
            catch (Exception ex)
            {
                _ptt.UnkeyNow();
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
            await _ptt.UnkeyAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var audioHz = (float)Math.Clamp(seq.TxAudioHz, 200, 3000);

        // Native encode pads to a full 7.5 s slot (silence tail). Key only for lead-in + burst
        // so VOX/CAT unkey before the opposite RX slot.
        const double leadInSeconds = 0.5;
        var keySeconds = leadInSeconds + Ft4SlotClock.Ft4SymbolBurstSeconds + 0.15;
        var keyedUntil = slotStart.AddSeconds(keySeconds);

        // Without a prepared buffer, build before the lead-in wait. CAT PTT is already queued
        // at the slot boundary when that method shares the rig port.
        var ft4 = _settings.Current.Ft4;
        float[]? ready = null;
        var readyRate = 0;
        var fromPrepared = false;
        float[]? pcm12k = null;
        if (!playedEarly)
        {
            fromPrepared = TryTakePrepared(slotStart, seq.CurrentTxMessage, out var prepared, out readyRate);
            if (fromPrepared)
            {
                ready = prepared;
            }
            else
            {
                Log.Information(
                    "FT4 TX building on the slot: {Reason}",
                    DescribePreparedMiss(slotStart, seq.CurrentTxMessage));
                var built = TryBuildDevicePcm(
                    slotStart,
                    seq.CurrentTxMessage,
                    audioHz,
                    ft4.TxLevel,
                    ft4.OutputDeviceId,
                    ft4.OutputDeviceDisplayName,
                    out ready,
                    out readyRate,
                    out var encodeError);
                // No error text means the output would not open: build at 12 kHz, and PlayPcm
                // reports the soundcard problem after keying, as it always has.
                if (!built && string.IsNullOrEmpty(encodeError))
                    built = TryBuildTransmitPcm(slotStart, seq.CurrentTxMessage, audioHz, 12000, 1f, out pcm12k, out encodeError);
                if (!built)
                {
                    ReportEncodeFailure(seq.CurrentTxMessage, encodeError);
                    await _ptt.UnkeyAsync(ct).ConfigureAwait(false);
                    return;
                }
            }
        }

        try
        {
            await _ptt.KeyAsync(ct).ConfigureAwait(false);
            if (!playedEarly)
            {
                if (ready is not null && _audio.TryPlayPrepared(ready, readyRate))
                {
                    var intoMs = (Ft4Clock.UtcNow - slotStart).TotalMilliseconds;
                    if (fromPrepared)
                        Log.Information("FT4 TX audio started {IntoMs:0} ms into the slot from a prepared buffer", intoMs);
                    else
                        Log.Information("FT4 TX audio started {IntoMs:0} ms into the slot after building on the slot", intoMs);
                }
                else
                {
                    // Output missing or its rate changed: the 12 kHz path reopens it, or reports why it cannot.
                    if (pcm12k is null
                        && !TryBuildTransmitPcm(slotStart, seq.CurrentTxMessage, audioHz, 12000, 1f, out pcm12k, out var encodeError))
                    {
                        ReportEncodeFailure(seq.CurrentTxMessage, encodeError);
                        return;
                    }

                    _audio.PlayPcm(
                        pcm12k!,
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

        var sent = seq.CurrentTxMessage;
        AppendTransmittedMessage(slotStart, sent, audioHz);

        if (seq.OnTxCompleted())
            await TryLogAsync(manual: false).ConfigureAwait(false);

        Status = _l.Get("Ft4.Status.TxDone", sent);
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

    /// <summary>CAT and the CAT-port handshake line share the rig thread with Doppler writes.</summary>
    private bool CatPttSharesRig()
    {
        var method = _settings.Current.Ft4.PttMethod;
        return method is Ft4PttMethod.Cat or Ft4PttMethod.CatPortHandshake;
    }

    /// <summary>
    /// True when this boundary is a CAT transmit slot, so a Doppler write must not take the rig port.
    /// The write for that slot is started <see cref="Ft4SlotClock.PreTransmitCatLead"/> beforehand.
    /// </summary>
    private bool CatTransmitOwnsTheRig(DateTime slotStart)
    {
        if (!CatPttSharesRig())
            return false;
        if (Interlocked.Read(ref _dopplerPreparedSlotTicks) == slotStart.Ticks)
            return true;
        return Interlocked.Read(ref _txKickedSlotTicks) == slotStart.Ticks;
    }

    private void WaitAndKickTransmit(DateTime slot, CancellationToken ct, CancellationToken loopCt)
    {
        if (CatPttSharesRig())
        {
            var dopplerAt = slot - Ft4SlotClock.PreTransmitCatLead;
            Ft4SlotWait.UntilUtc(dopplerAt, ct);
            if (ct.IsCancellationRequested)
                return;

            var budget = slot - Ft4Clock.UtcNow - TimeSpan.FromMilliseconds(40);
            if (budget > TimeSpan.FromMilliseconds(80))
            {
                var stepped = _rig.TryForceFt4DopplerStep(slot, budget);
                if (ct.IsCancellationRequested)
                    return;
                if (stepped)
                {
                    Interlocked.Exchange(ref _dopplerPreparedSlotTicks, slot.Ticks);
                    Log.Information(
                        "FT4 CAT Doppler stepped {EarlyMs:0} ms before the TX slot",
                        (slot - Ft4Clock.UtcNow).TotalMilliseconds);
                }
                else
                {
                    Log.Information(
                        "FT4 CAT Doppler step was still running at the TX slot, so PTT goes first");
                }
            }
        }

        Ft4SlotWait.UntilUtc(slot, ct);
        if (ct.IsCancellationRequested)
            return;

        KickTransmit(slot, loopCt);
        if (!CatPttSharesRig())
            _rig.ForceFt4DopplerStep();
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
                    WaitAndKickTransmit(slot, ct, loopCt);
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
        var doppler = UseAudioDopplerTx;
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
                if (!TryBuildDevicePcm(
                        next, message, audioHz, level, deviceId, deviceName, out var devicePcm, out var rate, out _)
                    || devicePcm is null)
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
                        UseAudioDopplerTx);
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
            UseAudioDopplerTx);

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

    private void ReportEncodeFailure(string message, string encodeError)
    {
        _txThisSlot = false;
        Status = string.IsNullOrWhiteSpace(encodeError)
            ? _l.Get("Ft4.Status.EncodeFailed")
            : encodeError;
        Log.Warning("FT4 encode failed for '{Message}': {Error}", message, encodeError);
        Changed?.Invoke();
    }

    private string DescribePreparedMiss(DateTime slotStart, string message)
    {
        lock (_prepareGate)
        {
            if (_preparedDevicePcm is null)
                return Volatile.Read(ref _prepareRunning) == 1 ? "still preparing" : "nothing prepared";
            if (_preparedKey is { } key && !key.StartsWith(slotStart.Ticks + "|", StringComparison.Ordinal))
                return "prepared for another slot";
            return _preparedKey is { } k && !k.Contains("|" + message + "|", StringComparison.Ordinal)
                ? "message changed"
                : "audio frequency, level or Doppler setting changed";
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

    /// <summary>
    /// Synthesise the slot's burst at <paramref name="sampleRate"/>, scaled by <paramref name="gain"/>,
    /// with TX Doppler pre-compensation built into the tones.
    /// </summary>
    private bool TryBuildTransmitPcm(
        DateTime slotStart,
        string message,
        float audioHz,
        int sampleRate,
        float gain,
        out float[]? pcm,
        out string encodeError)
    {
        var slope = 0.0;
        if (UseAudioDopplerTx
            && TryGetDopplerSlopeHzPerSec(slotStart, Ft4SlotClock.Ft4SymbolBurstSeconds, out _, out var ulSlope))
        {
            var uplinkMode = _frequencies.SelectedMode is null
                ? null
                : Core.Radio.TransponderOperatingModes.GetEffectiveUplinkMode(
                    _frequencies.SelectedMode,
                    _frequencies.IsCwUplink);
            slope = Ft4AudioDoppler.TxPrecompAudioSlope(ulSlope, uplinkMode);
        }

        lock (_encodeGate)
        {
            return Ft8Native.TryEncodeFt4(message, audioHz, sampleRate, (float)slope, gain, out pcm, out encodeError)
                && pcm is not null;
        }
    }

    /// <summary>Build the slot's burst at the output device rate, ready for <see cref="Ft4AudioService.TryPlayPrepared"/>.</summary>
    private bool TryBuildDevicePcm(
        DateTime slotStart,
        string message,
        float audioHz,
        double level,
        string? deviceId,
        string? deviceName,
        out float[]? devicePcm,
        out int sampleRate,
        out string encodeError)
    {
        devicePcm = null;
        encodeError = "";
        if (!_audio.TryGetOutputSampleRate(deviceId, deviceName, out sampleRate))
            return false;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ok = TryBuildTransmitPcm(
            slotStart, message, audioHz, sampleRate, Ft4AudioService.OutputGain(level), out devicePcm, out encodeError);
        Log.Debug(
            "FT4 TX burst built at {Rate} Hz in {Ms:0.0} ms",
            sampleRate,
            sw.Elapsed.TotalMilliseconds);
        return ok;
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

    private void QueueDecode(DateTime slotStart, float[] samples, bool txSlot, bool fullSlotPass = false)
    {
        // Long-running: native decode must not occupy a thread-pool worker the modem loop needs.
        _ = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    DecodeSamples(slotStart, samples, txSlot, fullSlotPass);
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

    /// <summary>
    /// Deep budget only while the focused satellite is above the horizon and still low.
    /// Below the horizon, and through the middle of the pass, stay on the fast decode.
    /// </summary>
    private bool UseDeepDecode()
    {
        var elevation = _snapshot.GetCurrent().ElevationDeg;
        var deep = Ft4DecodeDepth.UseDeep(elevation);
        if (_deepDecodeActive != deep)
        {
            _deepDecodeActive = deep;
            if (deep)
            {
                Log.Information(
                    "FT4 deep decode on (elevation {Elevation:0}°, below {Limit:0}°)",
                    elevation,
                    Ft4DecodeDepth.HorizonElevationDeg);
            }
            else if (elevation is null)
            {
                Log.Information("FT4 deep decode off (elevation unknown)");
            }
            else
            {
                Log.Information("FT4 deep decode off (elevation {Elevation:0}°)", elevation);
            }
        }

        return deep;
    }

    /// <param name="fullSlotPass">
    /// Second receive decode of the whole slot near the horizon. Also tries the raw capture,
    /// because a slightly wrong Doppler slope can smear a weak sync in the corrected copy.
    /// </param>
    private void DecodeSamples(DateTime slotStart, float[] samples, bool txSlot, bool fullSlotPass = false)
    {
        if (samples.Length < (int)(12000 * Ft4SlotClock.Ft4SlotSeconds / 2))
            return;

        var raw = samples;
        var corrected = raw;
        var correctedSlope = 0.0;
        var drift = default(DriftRange);
        // Slope over the on-air burst: the symbols the decoder uses, not the quiet end of the slot.
        if (UseAudioDopplerRx
            && TryGetDopplerSlopeHzPerSec(slotStart, Ft4SlotClock.Ft4SymbolBurstSeconds, out var dl, out var ul))
        {
            // Stations whose uplink slides while they transmit: one native pass searches the
            // leftover slope on both sides instead of a decode per slope.
            if (!txSlot)
                drift = new DriftRange(Ft4DriftSearch.MaxResidualHzPerSec(ul), Ft4DriftSearch.Steps(ul));

            if (Math.Abs(dl) >= 0.05)
            {
                corrected = Ft4AudioDoppler.RemoveLinearDrift(raw, 12000, dl);
                correctedSlope = dl;
            }
        }

        var txHz = _sequencer?.TxAudioHz ?? _settings.Current.Ft4.TxAudioHz;
        var deep = UseDeepDecode();
        if (txSlot && _settings.Current.Ft4.ParallelTxEchoDecode)
        {
            DecodeTxSlotParallel(slotStart, raw, corrected, txHz, deep);
            return;
        }

        var foundOwn = PublishDecoded(slotStart, corrected, txSlot, timeShiftSec: 0, ownOnly: false, txHz, deep, out var decodedCount, drift, correctedSlope);
        if (fullSlotPass && !txSlot && !ReferenceEquals(corrected, raw))
        {
            PublishDecoded(slotStart, raw, txSlot: false, timeShiftSec: 0, ownOnly: false, txHz, deep, out var rawCount);
            decodedCount += rawCount;
        }

        if (!txSlot)
        {
            foreach (var e in _rxHealth.OnReceiveSlot(
                         slotStart,
                         Ft4RxHealth.LevelDbfs(raw),
                         decodedCount,
                         _snapshot.GetCurrent().ElevationDeg))
                LogRxHealth(e);
        }

        if (!txSlot || foundOwn)
            return;

        RecoverOwnEchoSequential(slotStart, raw, corrected, txHz, deep);
    }

    /// <summary>Leftover slope range the native decoder searches; default is steady only.</summary>
    private readonly record struct DriftRange(double MaxResidualHzPerSec, int Steps)
    {
        public bool IsActive => Steps > 0 && MaxResidualHzPerSec > 0;
    }

    /// <summary>
    /// Normal decode and late-echo pass together so wall-clock is about one decode, not two in a row.
    /// </summary>
    private void DecodeTxSlotParallel(
        DateTime slotStart,
        float[] raw,
        float[] corrected,
        double txHz,
        bool deep)
    {
        var primary = Task.Factory.StartNew(
            () =>
            {
                var own = PublishDecoded(slotStart, corrected, txSlot: true, timeShiftSec: 0, ownOnly: false, txHz, deep, out _);
                if (!own && !ReferenceEquals(corrected, raw))
                    own = PublishDecoded(slotStart, raw, txSlot: true, timeShiftSec: 0, ownOnly: true, txHz, deep, out _);
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
                    if (!PublishDecoded(slotStart, aligned, txSlot: true, shiftSec, ownOnly: true, txHz, deep, out _))
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
    }

    private void RecoverOwnEchoSequential(
        DateTime slotStart,
        float[] raw,
        float[] corrected,
        double txHz,
        bool deep)
    {
        // The waterfall is the raw capture. Doppler removal and the decoder's early
        // time window both hide a full-duplex copy that is obvious on screen.
        var foundOwn = false;
        if (!ReferenceEquals(corrected, raw))
            foundOwn = PublishDecoded(slotStart, raw, txSlot: true, timeShiftSec: 0, ownOnly: true, txHz, deep, out _);
        if (foundOwn)
            return;

        foreach (var (aligned, shiftSec) in Ft4EchoAligner.EnumerateEchoAlignments(raw, 12000, txHz))
        {
            foundOwn = PublishDecoded(slotStart, aligned, txSlot: true, shiftSec, ownOnly: true, txHz, deep, out _);
            if (foundOwn)
            {
                Log.Information(
                    "FT4 own echo recovered after shifting the slot by {Shift:0.00} s",
                    shiftSec);
                return;
            }
        }
    }

    /// <summary>Post decoder output. Returns true when our own callsign was published.</summary>
    /// <param name="drift">
    /// Leftover slope range to search beyond the downlink Doppler. Hinted replies are only
    /// tried on steady candidates, since guessing across a grid of slopes would turn noise
    /// into reports.
    /// </param>
    private bool PublishDecoded(
        DateTime slotStart,
        float[] samples,
        bool txSlot,
        double timeShiftSec,
        bool ownOnly,
        double txHz,
        bool deep,
        out int decodedCount,
        DriftRange drift = default,
        double dopplerSlopeHzPerSec = 0)
    {
        float fMin, fMax;
        if (ownOnly)
            Ft8Native.ResolveSearchBand(txHz, txHz, out fMin, out fMax);
        else
            Ft8Native.ResolveWaterfallSearchBand(out fMin, out fMax);
        string? apHints = null;
        var apHz = 0f;
        // A transmit slot is our own signal. Guessing the other station's reply
        // there is how a report appears in the same period as our 73.
        if (!ownOnly
            && !txSlot
            && _settings.Current.Ft4.ApEnabled
            && Ft4DecodeDepth.UseApriori(_snapshot.GetCurrent().ElevationDeg)
            && _sequencer is not null
            && _sequencer.TryGetApHints(out var hintText, out var hintHz))
        {
            apHints = hintText;
            apHz = (float)hintHz;
        }

        var decoded = drift.IsActive && !ownOnly
            ? Ft8Native.DecodeFt4Drift(samples, 12000, fMin, fMax, deep, apHints, apHz, (float)drift.MaxResidualHzPerSec, drift.Steps)
            : Ft8Native.DecodeFt4(samples, 12000, fMin, fMax, deep, apHints, apHz);
        decodedCount = decoded.Length;
        if (!ownOnly && !txSlot && timeShiftSec == 0)
            RecordPass(slotStart, samples.Length, dopplerSlopeHzPerSec, deep, fMin, fMax, apHints, apHz, drift, decoded);
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

                // One echo per transmission. A second pass often reports the same
                // message about 10 Hz away; that copy only clutters the list.
                var echoIdentity = slotStart.Ticks + "|echo|" + d.text;
                var publishEcho = false;
                var replaced = false;
                float replacedHz = 0;
                lock (_decodePostGate)
                {
                    if (_postedEchoes.TryGetValue(echoIdentity, out var shown))
                    {
                        foundOwn = true;
                        if (!Ft4EchoChoice.IsClearerCopy(shown.FreqHz, shown.SnrDb, echo.FreqHz, echo.SnrDb, txHz))
                        {
                            Log.Debug(
                                "FT4 duplicate echo suppressed: {Text} at {Hz:0} Hz, kept {KeptHz:0} Hz",
                                d.text,
                                d.freq_hz,
                                shown.FreqHz);
                            continue;
                        }

                        replacedHz = shown.FreqHz;
                        replaced = true;
                        _postedEchoes[echoIdentity] = echo;
                        publishEcho = true;
                    }
                    else
                    {
                        _postedEchoes[echoIdentity] = echo;
                        publishEcho = true;
                    }
                }

                if (!publishEcho)
                    continue;

                ApplyEchoCalibration(slotStart, echo);

                foundOwn = true;
                any = true;
                if (replaced)
                {
                    Log.Information(
                        "FT4 own echo kept the clearer copy of {Text} at {Hz:0} Hz (dropped {OldHz:0} Hz)",
                        d.text,
                        d.freq_hz,
                        replacedHz);
                }
                else
                {
                    Log.Information(
                        "FT4 own echo: {Text} at {Hz:0} Hz, DT {Dt:0.00} s, SNR {Snr:0} dB",
                        d.text,
                        d.freq_hz,
                        timeSec,
                        d.snr);
                }

                Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowChosenEcho(echo));
                continue;
            }

            // Own echoes can sit 1–2 s into the slot (full duplex). Another station
            // on a satellite cannot: +2 s is a moonbounce delay.
            if (!Ft4DecodeDepth.IsPlausibleSatelliteDt(timeSec))
            {
                Log.Debug(
                    "FT4 decode ignored, DT {Dt:0.00} s is outside a satellite path: {Text}",
                    timeSec,
                    d.text);
                continue;
            }

            // A hinted reply on the SNR floor has no measurable signal. R+35 was −21 dB.
            // A CRC-checked hint carried its own report through the CRC, so only a
            // guessed one needs the SNR and DT guards.
            var hinted = d.ap != 0;
            var guessed = d.ap == Ft8Native.ApGuessed;
            if (guessed && !Ft4DecodeDepth.IsPublishableHint(timeSec, d.snr))
            {
                Log.Debug(
                    "FT4 hinted reply ignored, SNR {Snr:0} dB at DT {Dt:0.00} s: {Text}",
                    d.snr,
                    timeSec,
                    d.text);
                continue;
            }

            // Same text in one slot is the same transmission. A later pass often
            // reports it a few hertz away, which a 5 Hz bucket let through as a second line.
            var dedupeKey = slotStart.Ticks + "|" + d.text;
            // A station sends one message per slot. Once a CRC decode from it is posted,
            // a hinted line from the same call in that slot is its burst read as a reply to us.
            // The native burst guard only sees one pass; the early and end-of-slot decodes are separate.
            var senderKey = string.IsNullOrWhiteSpace(callDe)
                ? null
                : slotStart.Ticks + "|sender|" + callDe.ToUpperInvariant();
            var hintedKey = senderKey is null ? null : senderKey + "|ap";
            var contradictsHint = false;
            lock (_decodePostGate)
            {
                if (hinted && senderKey is not null && _postedDecodeKeys.Contains(senderKey))
                {
                    Log.Information(
                        "FT4 hinted reply ignored, {Call} already decoded this slot with another message: {Text} at {Hz:0} Hz",
                        callDe,
                        d.text,
                        d.freq_hz);
                    continue;
                }

                // A second guess of this station in the slot is the same try. A CRC-checked
                // hint is not dropped: the guess may have been the wrong message.
                if (guessed && hintedKey is not null && _postedDecodeKeys.Contains(hintedKey))
                    continue;

                if (guessed && !_callDt.AllowsHint(callDe, timeSec))
                {
                    Log.Debug(
                        "FT4 hinted reply ignored, DT {Dt:0.00} s does not match {Call}: {Text}",
                        timeSec,
                        callDe,
                        d.text);
                    continue;
                }

                // A guess does not take the text key. A later pass that confirms the same
                // message, or reads a different one, still has to be posted.
                if (!guessed && !_postedDecodeKeys.Add(dedupeKey))
                    continue;

                if (!hinted)
                {
                    _callDt.NoteReliable(callDe, timeSec, d.snr);
                    if (senderKey is not null)
                        _postedDecodeKeys.Add(senderKey);
                    // The same text was dropped above as a duplicate, so this is a different message.
                    contradictsHint = hintedKey is not null && _postedDecodeKeys.Remove(hintedKey);
                }
                else if (hintedKey is not null)
                {
                    _postedDecodeKeys.Add(hintedKey);
                }
            }

            if (contradictsHint)
                RejectHintedReply(slotStart, callDe!, d.text);

            if (d.drift_hz_s != 0)
            {
                Log.Information(
                    "FT4 decode needed {Slope:+0;-0} Hz/s beyond the downlink Doppler: {Text} at {Hz:0} Hz, SNR {Snr:0} dB",
                    d.drift_hz_s,
                    d.text,
                    d.freq_hz,
                    d.snr);
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
                isOwn,
                IsApriori: hinted,
                IsRejected: guessed);

            any = true;
            if (isOwn)
                foundOwn = true;
            if (guessed)
            {
                Log.Information(
                    "FT4 hinted reply not used, the CRC did not confirm it: {Text} at {Hz:0} Hz, SNR {Snr:0} dB",
                    d.text,
                    d.freq_hz,
                    d.snr);
            }
            else
            {
                ReportToPskReporter(msg);
                ReportToOscarWatch(msg);
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Decodes.Insert(0, msg);
                while (Decodes.Count > 200)
                    Decodes.RemoveAt(Decodes.Count - 1);
            });

            if (_sequencer is not null && !isOwn && !guessed)
            {
                var wasCallingCq = _sequencer.Phase == Ft4QsoPhase.CallingCq;
                var finished = _sequencer.OnDecoded(msg);
                if (Ft4TxWatchdog.IsReply(callTo, callDe, my))
                    _txWatchdogResetUtc = DateTime.UtcNow;
                if (wasCallingCq && _sequencer.Phase == Ft4QsoPhase.InQso)
                {
                    Log.Information(
                        "FT4 auto reply to {Call}: {Message}",
                        _sequencer.TheirCall,
                        _sequencer.CurrentTxMessage);
                }

                if (finished)
                    _ = TryLogAsync(manual: false);

                TryPounce(msg, my);
            }
        }

        if (any)
            Changed?.Invoke();
        return foundOwn;
    }

    private void RecordPass(
        DateTime slotStart,
        int sampleCount,
        double dopplerSlopeHzPerSec,
        bool deep,
        float fMin,
        float fMax,
        string? apHints,
        float apHz,
        DriftRange drift,
        Ft8Native.Decode[] decoded)
    {
        if (!_slotRecorder.Enabled)
            return;

        _slotRecorder.NotePass(slotStart, new Ft4RecordedPass
        {
            SampleCount = sampleCount,
            DopplerSlopeHzPerSec = dopplerSlopeHzPerSec,
            Deep = deep,
            FMinHz = fMin,
            FMaxHz = fMax,
            ApHints = apHints,
            ApHz = apHz,
            DriftMaxHzPerSec = drift.IsActive ? drift.MaxResidualHzPerSec : 0,
            DriftSteps = drift.IsActive ? drift.Steps : 0,
            Decodes = decoded.Select(d => new Ft4RecordedDecode
            {
                Text = d.text,
                FreqHz = d.freq_hz,
                TimeSec = d.time_sec,
                SnrDb = d.snr,
                Ap = d.ap,
                DriftHzPerSec = d.drift_hz_s,
            }).ToList(),
        });
    }

    /// <summary>
    /// A later pass decoded <paramref name="callDe"/> sending <paramref name="actualText"/> in a slot
    /// where a hinted reply from that station was already posted. Undo what the hint did to the
    /// contact and mark its line rejected. A transmission already built from it still goes out.
    /// </summary>
    private void RejectHintedReply(DateTime slotStart, string callDe, string actualText)
    {
        var reverted = _sequencer?.RevertApriori(slotStart, callDe) == true;
        Log.Information(
            "FT4 hinted reply from {Call} rejected, a later pass decoded {Text}; contact {Outcome}",
            callDe,
            actualText,
            reverted ? "restored" : "unchanged (it had moved on)");

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            for (var i = 0; i < Decodes.Count; i++)
            {
                var row = Decodes[i];
                if (row.SlotUtc == slotStart
                    && row is { IsApriori: true, IsRejected: false }
                    && string.Equals(row.CallDe, callDe, StringComparison.OrdinalIgnoreCase))
                {
                    Decodes[i] = row with { IsRejected = true };
                }
            }
        });
        Changed?.Invoke();
    }

    /// <summary>
    /// Show this echo and drop any other copy of the same transmission.
    /// A later post for a copy we no longer prefer does nothing.
    /// </summary>
    private void ShowChosenEcho(Ft4DecodedMessage echo)
    {
        var identity = echo.SlotUtc.Ticks + "|echo|" + echo.Text;
        lock (_decodePostGate)
        {
            if (!_postedEchoes.TryGetValue(identity, out var chosen) || !ReferenceEquals(chosen, echo))
                return;
        }

        for (var i = Decodes.Count - 1; i >= 0; i--)
        {
            var row = Decodes[i];
            if (!row.IsOwnEcho || row.SlotUtc != echo.SlotUtc)
                continue;
            if (!string.Equals(row.Text, echo.Text, StringComparison.Ordinal))
                continue;
            if (ReferenceEquals(row, echo))
                return;
            Decodes.RemoveAt(i);
        }

        Decodes.Insert(0, echo);
        while (Decodes.Count > 200)
            Decodes.RemoveAt(Decodes.Count - 1);
    }

    /// <summary>
    /// Any decoded own echo is our signal, so it can set the uplink trim. Measure against the
    /// TX audio actually sent in that slot: answering a station can move TX audio before the
    /// decode finishes.
    /// </summary>
    private void ApplyEchoCalibration(DateTime slotStart, Ft4DecodedMessage own)
    {
        if (Interlocked.Read(ref _txKickedSlotTicks) != slotStart.Ticks)
            return;

        var sentHz = Interlocked.CompareExchange(ref _txKickedAudioHz, 0, 0);
        if (TryClaimEchoCalibration(slotStart))
            ApplyEchoCalibrationHz(own.FreqHz - sentHz);
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
            var station = _settings.Current.GroundStation;
            var book = await _logbook.GetOrCreateLogbookAsync(new QsoLogbookCreateRequest
            {
                Name = _l.Get("Logbook.DefaultName"),
                MyGridSquare = station.GridSquare
            }).ConfigureAwait(false);

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

            _satelliteLink.PublishQso(record, book, SatelliteLinkQsoEventKind.Logged, _tracking.FocusedNoradId);

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
        _spotReporter.Dispose();
        _ptt.Dispose();
        _audio.Dispose();
    }
}
