using System.Globalization;
using Avalonia.Threading;
using OscarWatch.Core.Cw;
using OscarWatch.Core.CwDecode;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Hardware;
using OscarWatch.Localization;
using OscarWatch.ViewModels;
using Serilog;

namespace OscarWatch.Cw;

/// <summary>Lifecycle state of an operator-triggered CW uplink alignment probe.</summary>
public enum CwAlignState
{
    Idle,
    Sending,
    Listening
}

/// <summary>Specific prerequisite that prevents starting CW uplink alignment.</summary>
public enum CwAlignRefusalReason
{
    None,
    NotLinearTransponder,
    NotCwMode,
    RadioNotConnected,
    InStandby,
    NoCallsign,
    DecoderNotRunning,
    NoDownlinkAudio,
    KeyerUnavailable
}

/// <summary>Undo state for the most recently applied automatic CW uplink alignment.</summary>
public sealed record CwAlignUndoInfo(
    CwTxTrimSnapshot Snapshot,
    double AppliedDeltaHz,
    double NewTransmitOffsetKHz,
    double MeasuredEchoHz,
    double TargetPitchHz);

/// <summary>
/// Orchestrates operator-triggered automatic CW uplink alignment on linear transponders:
/// verifies prerequisites, sends <c>{MYCALL}</c> once under a hard transmit time cap, captures and
/// verifies the satellite echo through DeepCW, applies and persists the TX offset correction, and
/// supports immediate one-click Undo.
/// </summary>
public sealed class CwUplinkAlignService : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<CwUplinkAlignService>();
    private static readonly TimeSpan DefaultEarlyCheckDelay = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan DefaultFinalCheckDelay = TimeSpan.FromMilliseconds(2500);

    private readonly CwKeyerService _keyer;
    private readonly ICwProbeReceiver _receiver;
    private readonly IRigController _rig;
    private readonly ISettingsService _settings;
    private readonly FrequencyOverlayViewModel _frequencyOverlay;
    private readonly ILiveTrackerSnapshotProvider _tracker;
    private readonly ILocalizationService _l;
    private readonly Action<Action> _dispatchToUi;
    private readonly TimeSpan _earlyCheckDelay;
    private readonly TimeSpan _finalCheckDelay;
    private readonly object _gate = new();

    private int _state = (int)CwAlignState.Idle;
    private int _generation;
    private ActiveProbeSession? _activeSession;
    private CwAlignUndoInfo? _lastUndo;
    private bool _disposed;

    public CwUplinkAlignService(
        CwKeyerService keyer,
        ICwProbeReceiver receiver,
        IRigController rig,
        ISettingsService settings,
        FrequencyOverlayViewModel frequencyOverlay,
        ILiveTrackerSnapshotProvider tracker,
        ILocalizationService localization,
        Action<Action>? dispatchToUi = null,
        TimeSpan? earlyCheckDelay = null,
        TimeSpan? finalCheckDelay = null)
    {
        _keyer = keyer;
        _receiver = receiver;
        _rig = rig;
        _settings = settings;
        _frequencyOverlay = frequencyOverlay;
        _tracker = tracker;
        _l = localization;
        _dispatchToUi = dispatchToUi ?? (action => Dispatcher.UIThread.Post(action));
        _earlyCheckDelay = earlyCheckDelay ?? DefaultEarlyCheckDelay;
        _finalCheckDelay = finalCheckDelay ?? DefaultFinalCheckDelay;

        _keyer.Finished += OnKeyerFinished;
    }

    public CwAlignState State => (CwAlignState)Volatile.Read(ref _state);

    public bool IsActive => State != CwAlignState.Idle;

    public bool CanUndo => _lastUndo is not null;

    public CwAlignUndoInfo? LastUndo => _lastUndo;

    public string LastStatusMessage { get; private set; } = "";

    /// <summary>Raised on the UI dispatcher when state, undo availability, or status changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Evaluate all safety and hardware prerequisites without transmitting.</summary>
    public CwAlignRefusalReason CheckPrerequisites(LiveTrackerSnapshot? snapshot = null)
    {
        snapshot ??= _tracker.GetCurrent();

        if (!CwUplinkAlignCalculator.IsEligibleTransponder(_frequencyOverlay.SelectedMode))
            return CwAlignRefusalReason.NotLinearTransponder;

        if (!CwUplinkAlignCalculator.IsCwMode(snapshot.Mode))
            return CwAlignRefusalReason.NotCwMode;

        if (!_rig.GetStatus().IsConnected)
            return CwAlignRefusalReason.RadioNotConnected;

        if (snapshot.IsStandby)
            return CwAlignRefusalReason.InStandby;

        var call = _settings.Current.GroundStation.Callsign?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(call) || CwMessage.FromText(call).IsEmpty)
            return CwAlignRefusalReason.NoCallsign;

        if (!_receiver.IsRunning || _receiver.ActiveModelRunner is null)
            return CwAlignRefusalReason.DecoderNotRunning;

        if (!_receiver.CaptureHealth.IsReceivingAudio || _receiver.IsInputSilent)
            return CwAlignRefusalReason.NoDownlinkAudio;

        if (_keyer.DryCheckKeying() != CwKeyerStartResult.Started)
            return CwAlignRefusalReason.KeyerUnavailable;

        return CwAlignRefusalReason.None;
    }

    /// <summary>Human-readable localized explanation for a refusal reason.</summary>
    public string DescribeRefusal(CwAlignRefusalReason reason) => reason switch
    {
        CwAlignRefusalReason.NotLinearTransponder => _l.Get("Logbook.Cw.Align.Refusal.NotLinearTransponder"),
        CwAlignRefusalReason.NotCwMode => _l.Get("Logbook.Cw.Align.Refusal.NotCwMode"),
        CwAlignRefusalReason.RadioNotConnected => _l.Get("Logbook.Cw.Align.Refusal.RadioNotConnected"),
        CwAlignRefusalReason.InStandby => _l.Get("Logbook.Cw.Align.Refusal.InStandby"),
        CwAlignRefusalReason.NoCallsign => _l.Get("Logbook.Cw.Align.Refusal.NoCallsign"),
        CwAlignRefusalReason.DecoderNotRunning => _l.Get("Logbook.Cw.Align.Refusal.DecoderNotRunning"),
        CwAlignRefusalReason.NoDownlinkAudio => _l.Get("Logbook.Cw.Align.Refusal.NoDownlinkAudio"),
        CwAlignRefusalReason.KeyerUnavailable => DescribeKeyerStartFailure(_keyer.DryCheckKeying()),
        _ => ""
    };

    /// <summary>Start an operator-triggered uplink alignment probe. Returns false without transmitting if any check fails.</summary>
    public bool Start()
    {
        if (IsActive)
            return false;

        var snapshot = _tracker.GetCurrent();
        var refusal = CheckPrerequisites(snapshot);
        if (refusal != CwAlignRefusalReason.None)
        {
            LastStatusMessage = DescribeRefusal(refusal);
            RaiseStateChanged();
            return false;
        }

        var mode = _frequencyOverlay.SelectedMode!;
        var trimSnapshot = _frequencyOverlay.CaptureTxTrimSnapshot();
        if (trimSnapshot is null)
        {
            LastStatusMessage = DescribeRefusal(CwAlignRefusalReason.NotLinearTransponder);
            RaiseStateChanged();
            return false;
        }

        var myCall = (_settings.Current.GroundStation.Callsign?.Trim() ?? "").ToUpperInvariant();
        var message = CwMessage.FromText(myCall);
        if (message.IsEmpty)
        {
            LastStatusMessage = DescribeRefusal(CwAlignRefusalReason.NoCallsign);
            RaiseStateChanged();
            return false;
        }

        var cw = _settings.Current.QsoLogbook.CwKeyer;
        cw.Normalise();
        var maxDurationSec = CwKeyerSettings.ClampAlignDurationSeconds(cw.AlignMaxDurationSeconds);
        var targetPitchHz = CwKeyerSettings.ClampAlignTargetPitchHz(cw.AlignTargetPitchHz);
        var maxCorrectionHz = CwKeyerSettings.ClampAlignMaxCorrectionHz(cw.AlignMaxCorrectionHz);
        var receiveMode = ResolveReceiveMode(snapshot, mode);

        var maxCaptureSec = maxDurationSec + _finalCheckDelay.TotalSeconds + 2.0;
        var captureHandle = _receiver.BeginProbeCapture(maxCaptureSec);
        if (captureHandle is null)
        {
            LastStatusMessage = DescribeRefusal(CwAlignRefusalReason.DecoderNotRunning);
            RaiseStateChanged();
            return false;
        }

        var generation = Interlocked.Increment(ref _generation);
        var session = new ActiveProbeSession(
            Generation: generation,
            Callsign: myCall,
            SatelliteName: snapshot.SatelliteName,
            Mode: mode,
            ReceiveMode: receiveMode,
            TargetPitchHz: targetPitchHz,
            MaxCorrectionHz: maxCorrectionHz,
            TrimSnapshot: trimSnapshot,
            CaptureHandle: captureHandle,
            ListenCts: new CancellationTokenSource());

        lock (_gate)
        {
            _activeSession = session;
            Volatile.Write(ref _state, (int)CwAlignState.Sending);
        }

        var startResult = _keyer.SendProbe(message, TimeSpan.FromSeconds(maxDurationSec));
        if (startResult != CwKeyerStartResult.Started)
        {
            CleanupSession(session, incrementGeneration: true);
            LastStatusMessage = DescribeKeyerStartFailure(startResult);
            RaiseStateChanged();
            return false;
        }

        Log.Information(
            "CW uplink alignment started: call={Call} sat={Sat} mode={Mode} rxMode={RxMode} targetPitch={Pitch}Hz cap={Cap}s",
            myCall, snapshot.SatelliteName, mode.Type, receiveMode, targetPitchHz, maxDurationSec);

        LastStatusMessage = _l.Get("Logbook.Cw.Align.Status.Sending", myCall);
        RaiseStateChanged();
        return true;
    }

    /// <summary>Abort any active probe (sending or listening), unkey immediately, and leave TX offset unchanged.</summary>
    public void Abort(string? statusMessage = null)
    {
        Interlocked.Increment(ref _generation);
        ActiveProbeSession? session;
        lock (_gate)
        {
            session = _activeSession;
            _activeSession = null;
            Volatile.Write(ref _state, (int)CwAlignState.Idle);
        }

        if (session is not null)
        {
            try { session.ListenCts.Cancel(); } catch (ObjectDisposedException) { }
            _receiver.EndProbeCapture(session.CaptureHandle);
            session.ListenCts.Dispose();
        }

        if (_keyer.IsProbeSession)
            _keyer.Stop();

        if (statusMessage is not null)
            LastStatusMessage = statusMessage;

        if (session is not null || statusMessage is not null)
            RaiseStateChanged();
    }

    /// <summary>Immediate abort when entering Standby or shutting down.</summary>
    public void AbortForStandby()
    {
        if (IsActive)
            Abort(_l.Get("Logbook.Cw.Align.Status.AbortedStandby"));

        if (_keyer.IsSending)
            _keyer.Stop();
    }

    /// <summary>
    /// Periodic safety guard called on the UI refresh tick (~100 ms): aborts an active probe if
    /// station/radio/decoder/transponder state changes, and clears stale Undo state on satellite switch.
    /// </summary>
    public void CheckRuntimeGuards(LiveTrackerSnapshot snapshot)
    {
        if (_lastUndo is { } undo)
        {
            var modeType = _frequencyOverlay.SelectedMode?.Type?.Trim() ?? "";
            var satName = _frequencyOverlay.CurrentSatelliteStorageName;
            if (!string.Equals(satName, undo.Snapshot.SatelliteName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(modeType, undo.Snapshot.ModeType, StringComparison.OrdinalIgnoreCase))
            {
                _lastUndo = null;
                RaiseStateChanged();
            }
        }

        ActiveProbeSession? session;
        lock (_gate)
            session = _activeSession;

        if (session is null)
            return;

        if (snapshot.IsStandby)
        {
            Abort(_l.Get("Logbook.Cw.Align.Status.AbortedStandby"));
            return;
        }

        if (!_rig.GetStatus().IsConnected)
        {
            Abort(_l.Get("Logbook.Cw.Align.Status.AbortedDisconnected"));
            return;
        }

        if (!CwUplinkAlignCalculator.IsCwMode(snapshot.Mode))
        {
            Abort(_l.Get("Logbook.Cw.Align.Status.AbortedModeChanged"));
            return;
        }

        var currentModeType = _frequencyOverlay.SelectedMode?.Type?.Trim() ?? "";
        var currentSat = _frequencyOverlay.CurrentSatelliteStorageName;
        if (!string.Equals(currentModeType, session.TrimSnapshot.ModeType, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(currentSat, session.TrimSnapshot.SatelliteName, StringComparison.OrdinalIgnoreCase))
        {
            Abort(_l.Get("Logbook.Cw.Align.Status.AbortedTransponderChanged"));
            return;
        }

        if (!_receiver.IsRunning)
        {
            Abort(_l.Get("Logbook.Cw.Align.Status.AbortedDecoderStopped"));
            return;
        }

        if (_keyer.IsSending && !_keyer.IsProbeSession)
            Abort();
    }

    /// <summary>Restore both the live and persisted TX offset from before the last alignment.</summary>
    public bool Undo()
    {
        var undo = _lastUndo;
        if (undo is null)
            return false;

        _lastUndo = null;
        var restored = _frequencyOverlay.TryRestoreTxTrimSnapshot(undo.Snapshot);
        LastStatusMessage = restored
            ? _l.Get("Logbook.Cw.Align.Status.Undone", FormatSignedHz(-undo.AppliedDeltaHz))
            : _l.Get("Logbook.Cw.Align.Status.UndoUnavailable");
        RaiseStateChanged();
        return restored;
    }

    public static string FormatSignedHz(double hz)
    {
        var rounded = (int)Math.Round(hz);
        if (rounded > 0)
            return "+" + rounded.ToString(CultureInfo.CurrentCulture);
        if (rounded < 0)
            return "−" + Math.Abs(rounded).ToString(CultureInfo.CurrentCulture);
        return "0";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _keyer.Finished -= OnKeyerFinished;
        Abort();
    }

    private void OnKeyerFinished(object? sender, CwKeyerFinishedEventArgs e)
    {
        if (e.Slot != CwKeyerService.ProbeSlot)
            return;

        ActiveProbeSession? session;
        lock (_gate)
        {
            session = _activeSession;
            if (session is null || Volatile.Read(ref _state) != (int)CwAlignState.Sending)
                return;

            if (e.Finish == CwKeyerFinish.Completed)
                Volatile.Write(ref _state, (int)CwAlignState.Listening);
        }

        if (e.Finish == CwKeyerFinish.Completed)
        {
            _dispatchToUi(() =>
            {
                if (session.Generation != Volatile.Read(ref _generation))
                    return;
                LastStatusMessage = _l.Get("Logbook.Cw.Align.Status.Listening");
                RaiseStateChanged();
            });

            _ = Task.Run(() => ListenAndAnalyzeAsync(session));
            return;
        }

        var message = e.TimedOut
            ? _l.Get("Logbook.Cw.Align.Status.TimedOut")
            : e.Finish == CwKeyerFinish.RigRejected
                ? _l.Get("Logbook.Cw.Status.Rejected")
                : null;

        CleanupSession(session, incrementGeneration: true);
        _dispatchToUi(() =>
        {
            if (message is not null)
                LastStatusMessage = message;
            RaiseStateChanged();
        });
    }

    private async Task ListenAndAnalyzeAsync(ActiveProbeSession session)
    {
        try
        {
            var token = session.ListenCts.Token;
            if (_earlyCheckDelay > TimeSpan.Zero)
                await Task.Delay(_earlyCheckDelay, token).ConfigureAwait(false);

            if (token.IsCancellationRequested || session.Generation != Volatile.Read(ref _generation))
                return;

            var early = TryAnalyzeSnapshot(session);
            if (early is null)
            {
                FinalizeProbe(session, result: null, decoderStopped: true);
                return;
            }

            if (early.Outcome is CwEchoProbeOutcome.Matched or CwEchoProbeOutcome.Ambiguous)
            {
                FinalizeProbe(session, early, decoderStopped: false);
                return;
            }

            var remaining = _finalCheckDelay - _earlyCheckDelay;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, token).ConfigureAwait(false);

            if (token.IsCancellationRequested || session.Generation != Volatile.Read(ref _generation))
                return;

            var finalResult = TryAnalyzeSnapshot(session);
            FinalizeProbe(session, finalResult, decoderStopped: finalResult is null);
        }
        catch (OperationCanceledException)
        {
            // Aborted cleanly; state already reset by Abort().
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CW uplink alignment analysis failed");
            FinalizeProbe(session, result: null, decoderStopped: false);
        }
    }

    private CwEchoProbeResult? TryAnalyzeSnapshot(ActiveProbeSession session)
    {
        var runner = _receiver.ActiveModelRunner;
        if (!_receiver.IsRunning || runner is null)
            return null;

        var capture = _receiver.SnapshotProbeCapture(session.CaptureHandle);
        if (capture is null || capture.WasAborted)
            return null;

        if (capture.SamplesAt8k.Length == 0 || capture.ProbeRows.Count == 0)
            return new CwEchoProbeResult(CwEchoProbeOutcome.NoEcho);

        return CwEchoProbeAnalyzer.Analyze(
            capture.SamplesAt8k,
            capture.ProbeRows,
            capture.BinHz,
            session.Callsign,
            _receiver.ActiveModelMetadata,
            runner,
            capture.BaselineRows,
            capture.TunedBandCentreHz);
    }

    private void FinalizeProbe(ActiveProbeSession session, CwEchoProbeResult? result, bool decoderStopped)
    {
        _dispatchToUi(() =>
        {
            if (session.Generation != Volatile.Read(ref _generation))
                return;

            CleanupSession(session, incrementGeneration: false);

            if (decoderStopped)
            {
                LastStatusMessage = _l.Get("Logbook.Cw.Align.Status.AbortedDecoderStopped");
                RaiseStateChanged();
                return;
            }

            // Re-verify station & radio safety right before applying any TX offset change.
            var snapshot = _tracker.GetCurrent();
            if (snapshot.IsStandby)
            {
                LastStatusMessage = _l.Get("Logbook.Cw.Align.Status.AbortedStandby");
                RaiseStateChanged();
                return;
            }

            if (!_rig.GetStatus().IsConnected)
            {
                LastStatusMessage = _l.Get("Logbook.Cw.Align.Status.AbortedDisconnected");
                RaiseStateChanged();
                return;
            }

            if (!CwUplinkAlignCalculator.IsCwMode(snapshot.Mode))
            {
                LastStatusMessage = _l.Get("Logbook.Cw.Align.Status.AbortedModeChanged");
                RaiseStateChanged();
                return;
            }

            var currentModeType = _frequencyOverlay.SelectedMode?.Type?.Trim() ?? "";
            var currentSat = _frequencyOverlay.CurrentSatelliteStorageName;
            if (!string.Equals(currentModeType, session.TrimSnapshot.ModeType, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(currentSat, session.TrimSnapshot.SatelliteName, StringComparison.OrdinalIgnoreCase))
            {
                LastStatusMessage = _l.Get("Logbook.Cw.Align.Status.AbortedTransponderChanged");
                RaiseStateChanged();
                return;
            }

            if (result is null || result.Outcome == CwEchoProbeOutcome.NoEcho)
            {
                LastStatusMessage = _l.Get("Logbook.Cw.Align.Status.NoEcho", session.Callsign);
                RaiseStateChanged();
                return;
            }

            if (result.Outcome == CwEchoProbeOutcome.Ambiguous)
            {
                var freqs = result.AmbiguousFrequenciesHz is { Count: > 0 } list
                    ? string.Join(", ", list.Select(f => $"{Math.Round(f):0} Hz"))
                    : "";
                LastStatusMessage = _l.Get("Logbook.Cw.Align.Status.Ambiguous", freqs);
                RaiseStateChanged();
                return;
            }

            var echoHz = result.EchoHz!.Value;
            var eval = CwUplinkAlignCalculator.Evaluate(
                session.Mode,
                session.ReceiveMode,
                echoHz,
                session.TargetPitchHz,
                _frequencyOverlay.TransmitOffsetKHz,
                session.MaxCorrectionHz,
                FrequencyOverlayViewModel.OffsetMinKHz,
                FrequencyOverlayViewModel.OffsetMaxKHz);

            switch (eval.Decision)
            {
                case CwUplinkAlignDecision.AlreadyAligned:
                    LastStatusMessage = _l.Get(
                        "Logbook.Cw.Align.Status.AlreadyAligned",
                        Math.Round(eval.EchoAudioHz),
                        Math.Round(eval.TargetPitchHz));
                    break;

                case CwUplinkAlignDecision.ExceedsSafetyCap:
                    LastStatusMessage = _l.Get(
                        "Logbook.Cw.Align.Status.ExceedsCap",
                        FormatSignedHz(eval.TxDeltaHz),
                        session.MaxCorrectionHz);
                    break;

                case CwUplinkAlignDecision.OutOfPassbandBounds:
                    LastStatusMessage = _l.Get(
                        "Logbook.Cw.Align.Status.OutOfBounds",
                        FormatSignedHz(eval.TxDeltaHz));
                    break;

                case CwUplinkAlignDecision.Apply:
                    _frequencyOverlay.ApplyAndPersistTxTrimKHz(eval.NewTransmitOffsetKHz);
                    _lastUndo = new CwAlignUndoInfo(
                        session.TrimSnapshot,
                        eval.TxDeltaHz,
                        eval.NewTransmitOffsetKHz,
                        eval.EchoAudioHz,
                        eval.TargetPitchHz);

                    Log.Information(
                        "CW uplink aligned: echo={EchoHz:0.0}Hz target={TargetHz:0}Hz txDelta={DeltaHz:+0.0;-0.0;0}Hz newTxOffset={NewOffset:+0.000;-0.000;0.000}kHz",
                        eval.EchoAudioHz, eval.TargetPitchHz, eval.TxDeltaHz, eval.NewTransmitOffsetKHz);

                    LastStatusMessage = _l.Get(
                        "Logbook.Cw.Align.Status.Applied",
                        FormatSignedHz(eval.EchoAudioHz - eval.TargetPitchHz),
                        FormatSignedHz(eval.TxDeltaHz));
                    break;

                default:
                    LastStatusMessage = DescribeRefusal(CwAlignRefusalReason.NotLinearTransponder);
                    break;
            }

            RaiseStateChanged();
        });
    }

    private void CleanupSession(ActiveProbeSession session, bool incrementGeneration)
    {
        if (incrementGeneration)
            Interlocked.Increment(ref _generation);

        lock (_gate)
        {
            if (ReferenceEquals(_activeSession, session))
            {
                _activeSession = null;
                Volatile.Write(ref _state, (int)CwAlignState.Idle);
            }
        }

        try { session.ListenCts.Cancel(); } catch (ObjectDisposedException) { }
        _receiver.EndProbeCapture(session.CaptureHandle);
        session.ListenCts.Dispose();
    }

    private string? ResolveReceiveMode(LiveTrackerSnapshot snapshot, SatelliteTransponderMode mode)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ModeRx))
            return snapshot.ModeRx;

        return _frequencyOverlay.IsCwOperatingStyleSelected && _settings.Current.Rig?.CwKeepSidebandDownlink != true
            ? "CW"
            : mode.DownlinkMode;
    }

    private string DescribeKeyerStartFailure(CwKeyerStartResult result)
    {
        var port = _settings.Current.QsoLogbook.CwKeyer.SeparatePort?.Trim() ?? "";
        return result switch
        {
            CwKeyerStartResult.NothingToSend => _l.Get("Logbook.Cw.Align.Refusal.NoCallsign"),
            CwKeyerStartResult.RigNotConnected => _l.Get("Logbook.Cw.Status.RigNotConnected"),
            CwKeyerStartResult.KeyLineUnavailable => _l.Get("Logbook.Cw.Status.KeyLineUnavailable"),
            CwKeyerStartResult.RtsBlocked => _l.Get("Logbook.Cw.Status.RtsBlocked"),
            CwKeyerStartResult.NoPortConfigured => _l.Get("Logbook.Cw.Status.NoPort"),
            CwKeyerStartResult.PortConflict => _l.Get("Logbook.Cw.Status.PortConflict", port),
            CwKeyerStartResult.PortBusy => _l.Get("Logbook.Cw.Status.PortBusy", port),
            CwKeyerStartResult.PortNotFound => _l.Get("Logbook.Cw.Status.PortNotFound", port),
            _ => _l.Get("Logbook.Cw.Status.PortFailed", port)
        };
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private sealed record ActiveProbeSession(
        int Generation,
        string Callsign,
        string SatelliteName,
        SatelliteTransponderMode Mode,
        string? ReceiveMode,
        int TargetPitchHz,
        int MaxCorrectionHz,
        CwTxTrimSnapshot TrimSnapshot,
        CwProbeCaptureHandle CaptureHandle,
        CancellationTokenSource ListenCts);
}
