using System.Collections.ObjectModel;
using OscarWatch.Core.Ft4;
using OscarWatch.Core.Logbook;
using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
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
    private readonly ILiveTrackerSnapshotProvider _snapshot;
    private readonly IOrbitPropagator _propagator;
    private readonly IRigController _rig;
    private readonly ILocalizationService _l;
    private readonly Ft4AudioService _audio = new();
    private readonly Ft4PttKeyer _ptt;
    private readonly object _gate = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private CancellationTokenSource? _txCts;
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
    private bool _decodeQueuedThisSlot;
    private readonly HashSet<string> _postedDecodeKeys = new(StringComparer.Ordinal);
    private readonly object _decodePostGate = new();
    private DateTime _lastRelevantDecodeUtc = DateTime.UtcNow;
    private Ft4QsoSequencer? _sequencer;

    public Ft4ModemService(
        ISettingsService settings,
        ILiveTrackingService tracking,
        FrequencyOverlayViewModel frequencies,
        IRigController rig,
        IQsoLogbookRepository logbook,
        ILiveTrackerSnapshotProvider snapshot,
        IOrbitPropagator propagator,
        ILocalizationService localization)
    {
        _settings = settings;
        _tracking = tracking;
        _frequencies = frequencies;
        _logbook = logbook;
        _snapshot = snapshot;
        _propagator = propagator;
        _rig = rig;
        _l = localization;
        _ptt = new Ft4PttKeyer(rig, settings);
    }

    public ObservableCollection<Ft4DecodedMessage> Decodes { get; } = new();
    public string Status { get; private set; } = "";
    public string ManualPrompt { get; private set; } = "";
    public bool IsRunning { get; private set; }
    public bool NativeAvailable => Ft8Native.IsAvailable;
    public Ft4QsoSequencer? Sequencer => _sequencer;
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
        _lastLoggedKey = null;

        Ft8Native.ow_ft8_clear_callsigns();
        Ft8Native.ow_ft8_remember_callsign(call);

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
        _decodeQueuedThisSlot = false;
        lock (_decodePostGate)
            _postedDecodeKeys.Clear();
        _lastRelevantDecodeUtc = DateTime.UtcNow;

        // OrbitDeck: hold CAT dial within each slot; audio-domain corrects within-slot drift.
        _rig.SetFt4SlotGatedDoppler(true);
        _rig.ForceFt4DopplerStep();

        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_loopCts.Token));
        IsRunning = true;
        Status = _l.Get("Ft4.Status.Listening");
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
            return;

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
        if (!EnsureTransmitAllowed())
            return;

        _sequencer?.StartCq(evenSlot);
        _lastLoggedKey = null;
        _lastRelevantDecodeUtc = DateTime.UtcNow;
        Status = _l.Get("Ft4.Status.CallingCq");
        Changed?.Invoke();
    }

    public void EnableTx()
    {
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
        _sequencer?.HaltTx();
        _txCts?.Cancel();
        ClearPrepared();
        Interlocked.Exchange(ref _preparedPlayed, 0);
        _audio.StopPlayback();
        _ = _ptt.UnkeyAsync();
        _txThisSlot = false;
        Status = _l.Get("Ft4.Status.TxHalted");
        Changed?.Invoke();
    }

    public void Answer(Ft4DecodedMessage decode)
    {
        if (_sequencer is null)
            return;
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
    /// Re-check satellite eligibility while listening (e.g. operator switched to FM or FO-29).
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
                var now = DateTime.UtcNow;
                var slotStart = Ft4SlotClock.SlotStartUtc(now, Ft4SlotClock.Ft4SlotSeconds);
                if (_currentSlotStart != slotStart)
                {
                    // Snapshot the previous slot, clear, and start TX first so decode CPU
                    // never delays the next transmit or capture alignment.
                    float[]? previousSamples = null;
                    var previousSlot = _currentSlotStart;
                    var previousWasTx = _txThisSlot;
                    var needEndDecode = previousSlot != DateTime.MinValue
                        && (!_decodeQueuedThisSlot || previousWasTx);
                    lock (_gate)
                    {
                        if (needEndDecode && _slotBuffer.Count >= (int)(12000 * Ft4SlotClock.Ft4SlotSeconds / 2))
                            previousSamples = _slotBuffer.ToArray();
                        _slotBuffer.Clear();
                    }

                    _currentSlotStart = slotStart;
                    _txThisSlot = false;
                    _decodeQueuedThisSlot = false;
                    lock (_decodePostGate)
                        _postedDecodeKeys.Clear();

                    _rig.ForceFt4DopplerStep();
                    KickTransmit(slotStart, ct);

                    if (previousSamples is not null)
                        QueueDecode(previousSlot, previousSamples, previousWasTx);
                }

                // Build the next TX burst before its slot, so the boundary only starts playback.
                MaybePrepareTransmit(now);

                // Early RX decode once the FT4 burst should be in the buffer (~6 s).
                MaybeQueueEarlyDecode(now);

                // Watchdog: stop TX if nothing relevant for 3 minutes.
                if (_sequencer is { TransmitEnabled: true }
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

    private void KickTransmit(DateTime slotStart, CancellationToken loopCt)
    {
        var seq = _sequencer;
        if (seq is null || !seq.TransmitEnabled || string.IsNullOrWhiteSpace(seq.CurrentTxMessage))
            return;

        if (!EnsureTransmitAllowed())
            return;

        if (Ft4SlotClock.IsEvenSlot(slotStart, Ft4SlotClock.Ft4SlotSeconds) != seq.PreferEvenSlot)
            return;

        if (Interlocked.CompareExchange(ref _txRunning, 1, 0) != 0)
            return;

        _txThisSlot = true;
        _txCts?.Cancel();
        _txCts?.Dispose();
        _txCts = CancellationTokenSource.CreateLinkedTokenSource(loopCt);
        var txCt = _txCts.Token;

        // VOX keys from the audio itself, so start a ready buffer on this thread.
        // Waiting for the TX task to encode and resample was holding the tone until about +1 s.
        if (_ptt.Method == Ft4PttMethod.Vox
            && TryTakePrepared(slotStart, seq.CurrentTxMessage, out var ready, out var readyRate)
            && _audio.TryPlayPrepared(ready, readyRate))
        {
            Interlocked.Exchange(ref _preparedPlayed, 1);
            var intoMs = (DateTime.UtcNow - slotStart).TotalMilliseconds;
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
                    var intoMs = (DateTime.UtcNow - slotStart).TotalMilliseconds;
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
                    var intoMs = (DateTime.UtcNow - slotStart).TotalMilliseconds;
                    Log.Information("FT4 TX audio started {IntoMs:0} ms into the slot after building on the slot", intoMs);
                }
            }

            while (!ct.IsCancellationRequested)
            {
                var remaining = keyedUntil - DateTime.UtcNow;
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

    private void MaybePrepareTransmit(DateTime utcNow)
    {
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

    private void MaybeQueueEarlyDecode(DateTime utcNow)
    {
        if (_decodeQueuedThisSlot || _txThisSlot || _currentSlotStart == DateTime.MinValue)
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
        QueueDecode(_currentSlotStart, snapshot, txSlot: false);
    }

    private void QueueDecode(DateTime slotStart, float[] samples, bool txSlot)
    {
        _ = Task.Run(() =>
        {
            try
            {
                DecodeSamples(slotStart, samples, txSlot);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "FT4 decode failed for slot {Slot}", slotStart);
            }
        });
    }

    private void DecodeSamples(DateTime slotStart, float[] samples, bool txSlot)
    {
        if (samples.Length < (int)(12000 * Ft4SlotClock.Ft4SlotSeconds / 2))
            return;

        if (_settings.Current.Ft4.AudioDopplerRx
            && TryGetDopplerSlopeHzPerSec(slotStart, Ft4SlotClock.Ft4SlotSeconds, out var dlSlope, out _)
            && Math.Abs(dlSlope) >= 0.05)
        {
            samples = Ft4AudioDoppler.RemoveLinearDrift(samples, 12000, dlSlope);
        }

        var decoded = Ft8Native.DecodeFt4(samples);
        var my = Ft4MessageCodec.NormalizeCall(_settings.Current.GroundStation.Callsign ?? "");
        var any = false;

        foreach (var d in decoded)
        {
            Ft4MessageCodec.TryParse(d.text, out var callTo, out var callDe, out var extra);
            var isOwn = my.Length > 0
                && callDe is not null
                && callDe.Equals(my, StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(callDe))
                Ft8Native.ow_ft8_remember_callsign(callDe);

            if (isOwn && txSlot)
            {
                var echo = new Ft4DecodedMessage(
                    slotStart,
                    d.text,
                    d.freq_hz,
                    d.time_sec,
                    d.snr,
                    callTo,
                    callDe,
                    extra,
                    IsOwnEcho: true);
                ApplyEchoCalibration(echo);

                var echoKey = slotStart.Ticks + "|echo|" + d.text + "|" + ((int)Math.Round(d.freq_hz / 5.0) * 5);
                lock (_decodePostGate)
                {
                    if (!_postedDecodeKeys.Add(echoKey))
                        continue;
                }

                any = true;
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
                d.time_sec,
                d.snr,
                callTo,
                callDe,
                extra,
                isOwn);

            any = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Decodes.Insert(0, msg);
                while (Decodes.Count > 200)
                    Decodes.RemoveAt(Decodes.Count - 1);
            });

            if (_sequencer is not null)
            {
                var finished = _sequencer.OnDecoded(msg);
                _lastRelevantDecodeUtc = DateTime.UtcNow;
                if (finished)
                    _ = TryLogAsync(manual: false);
            }
        }

        if (any)
            Changed?.Invoke();
    }

    private void ApplyEchoCalibration(Ft4DecodedMessage own)
    {
        var seq = _sequencer;
        if (seq is null)
            return;

        var errorHz = own.FreqHz - seq.TxAudioHz;
        if (Math.Abs(errorHz) < 5)
            return;

        var sat = _frequencies.SatelliteName;
        if (string.IsNullOrWhiteSpace(sat))
            return;

        // Positive audio error → we need a touch more uplink (reverse path depends on doppler sense).
        // Store as kHz trim applied to transmit offset path.
        var deltaKHz = errorHz / 1000.0;
        var current = _settings.Current.Ft4.GetUplinkCalibrationKHz(sat);
        _settings.Current.Ft4.SetUplinkCalibrationKHz(sat, current + deltaKHz);
        _settings.RequestSave();
        Status = _l.Get("Ft4.Status.EchoCalibration", deltaKHz * 1000.0, sat);
        Changed?.Invoke();
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
            var record = await _logbook.AddQsoAsync(new QsoRecordCreateRequest
            {
                LogbookId = book.Id,
                QsoUtc = DateTime.UtcNow,
                Call = seq.TheirCall,
                RstSent = seq.ReportSent ?? "",
                RstRcvd = seq.ReportReceived ?? "",
                GridSquare = seq.TheirGrid ?? "",
                SatName = snap.IsAvailable ? snap.SatelliteName : _frequencies.SatelliteName,
                Mode = "FT4",
                ModeRx = "FT4",
                FreqHz = snap.IsAvailable ? snap.UplinkHz : 0,
                FreqRxHz = snap.IsAvailable ? snap.DownlinkHz : 0,
                Band = snap.IsAvailable ? snap.Band : "",
                BandRx = snap.IsAvailable ? snap.BandRx : "",
                PropMode = "SAT",
                Comment = manual ? "FT4 manual log" : ""
            }).ConfigureAwait(false);

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
                + _settings.Current.Ft4.GetUplinkCalibrationKHz(_frequencies.SatelliteName);

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
        _ptt.Dispose();
        _audio.Dispose();
    }
}
