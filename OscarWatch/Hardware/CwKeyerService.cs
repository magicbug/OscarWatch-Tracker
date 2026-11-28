using System.Diagnostics;
using System.Runtime.InteropServices;
using OscarWatch.Core.Cw;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using Serilog;

namespace OscarWatch.Hardware;

/// <summary>Why <see cref="CwKeyerService.Send"/> or <see cref="CwKeyerService.BeginKeyboard"/> did not start.</summary>
public enum CwKeyerStartResult
{
    Started,
    NothingToSend,
    RigNotConnected,
    KeyLineUnavailable,
    RtsBlocked,
    NoPortConfigured,
    PortConflict,
    PortBusy,
    PortNotFound,
    PortFailed
}

/// <summary>How a message that did start ended.</summary>
public enum CwKeyerFinish
{
    Completed,
    Aborted,
    RigRejected
}

public sealed record CwKeyerFinishedEventArgs(int Slot, CwKeyerFinish Finish, bool SaveQso = false, bool TimedOut = false);

/// <summary>A symbol the keyboard session has started keying.</summary>
public sealed record CwKeyboardProgressEventArgs(CwSymbol Symbol);

/// <summary>
/// Sends logbook CW macros on the uplink: as text the radio keys itself (Kenwood <c>KY</c>, Icom <c>0x17</c>),
/// or by timing DTR or RTS on the CAT port or a COM port of its own. One message at a time; a new
/// message aborts the one in progress. A keyboard session queues symbols for type-ahead sending.
/// </summary>
public sealed class CwKeyerService : IDisposable
{
    /// <summary>Active slot while the uplink alignment probe is sending.</summary>
    public const int ProbeSlot = -3;

    /// <summary>Active slot while the live keyboard is sending (not one of the twelve macros).</summary>
    public const int KeyboardSlot = -2;

    private const int IdleSlot = -1;

    private static readonly ILogger Log = Serilog.Log.ForContext<CwKeyerService>();

    /// <summary>How long a CAT text piece may wait behind a Doppler write.</summary>
    private static readonly TimeSpan TextCommandTimeout = TimeSpan.FromSeconds(3);

    private readonly IRigController _rig;
    private readonly ISettingsService _settings;
    private readonly HandshakePortPool _portPool;
    private readonly object _gate = new();
    private readonly object _portGate = new();
    private readonly object _keyboardGate = new();
    private readonly Queue<KeyboardItem> _keyboardQueue = new();
    private readonly ManualResetEventSlim _keyboardPulse = new(false);
    private HandshakePortLease? _port;
    private Thread? _worker;
    private CancellationTokenSource? _cts;
    private volatile int _wpm;
    private int _activeSlot = IdleSlot;
    private bool _keyboardLastIsGap = true;

    public CwKeyerService(IRigController rig, ISettingsService settings, HandshakePortPool? portPool = null)
    {
        _rig = rig;
        _settings = settings;
        _portPool = portPool ?? HandshakePortPool.Shared;
        _wpm = CwKeyerSettings.ClampWpm(settings.Current.QsoLogbook.CwKeyer.Wpm);
    }

    /// <summary>Raised on the keyer thread when a message that started ends.</summary>
    public event EventHandler<CwKeyerFinishedEventArgs>? Finished;

    /// <summary>Raised on the keyer thread when the keyboard session starts keying a symbol.</summary>
    public event EventHandler<CwKeyboardProgressEventArgs>? KeyboardProgress;

    /// <summary>Raised on the keyer thread when a queued <c>{SAVEQSO}</c> marker is reached.</summary>
    public event EventHandler? KeyboardSaveQsoRequested;

    public bool IsSending => Volatile.Read(ref _activeSlot) != IdleSlot;

    /// <summary>True while the live keyboard session owns the keyer.</summary>
    public bool IsKeyboardSession => Volatile.Read(ref _activeSlot) == KeyboardSlot;

    /// <summary>True while an uplink alignment probe owns the keyer.</summary>
    public bool IsProbeSession => Volatile.Read(ref _activeSlot) == ProbeSlot;

    /// <summary>Message slot (0 to 11) being sent, <see cref="KeyboardSlot"/> for the keyboard, <see cref="ProbeSlot"/> for a probe, or -1.</summary>
    public int ActiveSlot => Volatile.Read(ref _activeSlot);

    public int Wpm => _wpm;

    public CwKeyingMethod CurrentMethod =>
        CwKeyingRoute.Resolve(_settings.Current.QsoLogbook.CwKeyer, _settings.Current.Rig);

    /// <summary>
    /// Change speed now. Line keying uses it from the next element; text radios get a speed command.
    /// </summary>
    public void SetWpm(int wpm)
    {
        _wpm = CwKeyerSettings.ClampWpm(wpm);
        if (!CwKeyingRoute.IsTextMethod(CurrentMethod))
            return;

        var target = _wpm;
        _ = Task.Run(() => _rig.TrySetCwSpeed(target));
    }

    public CwKeyerStartResult Send(int slot, CwMessage message, bool saveQso = false)
    {
        if (message.IsEmpty)
            return CwKeyerStartResult.NothingToSend;

        Stop();

        var prepare = PrepareKeying(out var method, out var useRts);
        if (prepare != CwKeyerStartResult.Started)
            return prepare;

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _cts = cts;
            Volatile.Write(ref _activeSlot, slot);
            _worker = new Thread(() => Run(slot, message, method, useRts, saveQso, cts.Token))
            {
                IsBackground = true,
                Name = "OscarWatch.CwKeyer",
                Priority = ThreadPriority.AboveNormal
            };
            _worker.Start();
        }

        return CwKeyerStartResult.Started;
    }

    /// <summary>
    /// Side-effect-free prerequisite check for CW keying availability (does not open ports or toggle key lines).
    /// </summary>
    public CwKeyerStartResult DryCheckKeying()
    {
        var settings = _settings.Current;
        var cw = settings.QsoLogbook.CwKeyer;
        var method = CwKeyingRoute.Resolve(cw, settings.Rig);

        return method switch
        {
            CwKeyingMethod.KenwoodText or CwKeyingMethod.IcomText =>
                settings.Rig.Enabled ? CwKeyerStartResult.Started : CwKeyerStartResult.RigNotConnected,
            CwKeyingMethod.CatPortLine when !settings.Rig.Enabled => CwKeyerStartResult.RigNotConnected,
            CwKeyingMethod.CatPortLine when CwKeyingRoute.CatPortRtsBlocked(cw, settings.Rig) => CwKeyerStartResult.RtsBlocked,
            CwKeyingMethod.CatPortLine => CwKeyerStartResult.Started,
            CwKeyingMethod.SeparatePortLine when string.IsNullOrWhiteSpace(cw.SeparatePort) => CwKeyerStartResult.NoPortConfigured,
            CwKeyingMethod.SeparatePortLine when SerialPortConflictHelper.IsCwKeyPortOccupied(settings, cw.SeparatePort.Trim()) => CwKeyerStartResult.PortConflict,
            CwKeyingMethod.SeparatePortLine => CwKeyerStartResult.Started,
            _ => CwKeyerStartResult.KeyLineUnavailable
        };
    }

    /// <summary>
    /// Send a single CW alignment probe message bounded by a hard transmit time cap.
    /// Aborts any running macro or keyboard session first.
    /// </summary>
    public CwKeyerStartResult SendProbe(CwMessage message, TimeSpan maxDuration)
    {
        if (message.IsEmpty)
            return CwKeyerStartResult.NothingToSend;
        if (maxDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxDuration), "Probe time cap must be positive.");

        Stop();

        var prepare = PrepareKeying(out var method, out var useRts);
        if (prepare != CwKeyerStartResult.Started)
            return prepare;

        var manualCts = new CancellationTokenSource();
        var timeoutCts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(manualCts.Token, timeoutCts.Token);
        timeoutCts.CancelAfter(maxDuration);

        lock (_gate)
        {
            _cts = manualCts;
            Volatile.Write(ref _activeSlot, ProbeSlot);
            _worker = new Thread(() =>
            {
                try
                {
                    Run(ProbeSlot, message, method, useRts, saveQso: false, linkedCts.Token, manualCts.Token, timeoutCts.Token);
                }
                finally
                {
                    linkedCts.Dispose();
                    timeoutCts.Dispose();
                }
            })
            {
                IsBackground = true,
                Name = "OscarWatch.CwProbeKeyer",
                Priority = ThreadPriority.AboveNormal
            };
            _worker.Start();
        }

        return CwKeyerStartResult.Started;
    }

    /// <summary>
    /// Start a type-ahead keyboard session. Aborts any message in progress. Symbols are added with
    /// <see cref="Enqueue(CwSymbol)"/> until <see cref="Stop"/> ends the session.
    /// </summary>
    public CwKeyerStartResult BeginKeyboard()
    {
        Stop();

        var prepare = PrepareKeying(out var method, out var useRts);
        if (prepare != CwKeyerStartResult.Started)
            return prepare;

        lock (_keyboardGate)
        {
            _keyboardQueue.Clear();
            _keyboardLastIsGap = true;
            _keyboardPulse.Reset();
        }

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _cts = cts;
            Volatile.Write(ref _activeSlot, KeyboardSlot);
            _worker = new Thread(() => RunKeyboard(method, useRts, cts.Token))
            {
                IsBackground = true,
                Name = "OscarWatch.CwKeyboard",
                Priority = ThreadPriority.AboveNormal
            };
            _worker.Start();
        }

        return CwKeyerStartResult.Started;
    }

    /// <summary>Queue one symbol for the open keyboard session. Leading and doubled word gaps are dropped.</summary>
    public bool Enqueue(CwSymbol symbol)
    {
        if (!IsKeyboardSession)
            return false;

        lock (_keyboardGate)
        {
            if (symbol.IsWordGap)
            {
                if (_keyboardLastIsGap)
                    return false;
                _keyboardLastIsGap = true;
            }
            else
            {
                _keyboardLastIsGap = false;
            }

            _keyboardQueue.Enqueue(KeyboardItem.ForSymbol(symbol));
            _keyboardPulse.Set();
        }

        return true;
    }

    /// <summary>
    /// Queue every symbol in <paramref name="message"/>, then an optional <c>{SAVEQSO}</c> marker.
    /// </summary>
    public bool Enqueue(CwMessage message, bool saveQso = false)
    {
        if (!IsKeyboardSession)
            return false;

        var added = false;
        foreach (var symbol in message.Symbols)
        {
            if (Enqueue(symbol))
                added = true;
        }

        if (saveQso)
        {
            lock (_keyboardGate)
            {
                _keyboardQueue.Enqueue(KeyboardItem.SaveQso);
                _keyboardPulse.Set();
            }

            added = true;
        }

        return added;
    }

    /// <summary>Remove the last symbol the worker has not started. Returns false when the queue is empty.</summary>
    public bool TryDropLastUnsent()
    {
        if (!IsKeyboardSession)
            return false;

        lock (_keyboardGate)
        {
            if (_keyboardQueue.Count == 0)
                return false;

            var items = _keyboardQueue.ToArray();
            _keyboardQueue.Clear();
            for (var i = 0; i < items.Length - 1; i++)
                _keyboardQueue.Enqueue(items[i]);

            _keyboardLastIsGap = true;
            foreach (var item in _keyboardQueue)
            {
                if (item.IsSaveQso)
                    continue;
                _keyboardLastIsGap = item.Symbol!.Value.IsWordGap;
            }

            return true;
        }
    }

    /// <summary>Abort the message or keyboard session in progress and leave the key up.</summary>
    public void Stop()
    {
        Thread? worker;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            worker = _worker;
            cts = _cts;
            _worker = null;
            _cts = null;
        }

        lock (_keyboardGate)
        {
            _keyboardQueue.Clear();
            _keyboardLastIsGap = true;
            _keyboardPulse.Set();
        }

        if (cts is null)
            return;

        cts.Cancel();
        if (worker is not null && worker != Thread.CurrentThread)
            worker.Join(TimeSpan.FromSeconds(4));
        cts.Dispose();

        lock (_keyboardGate)
            _keyboardPulse.Reset();
    }

    /// <summary>Release the keyer's separate COM port lease.</summary>
    public void ReleasePort()
    {
        Stop();
        lock (_portGate)
            ClosePort();
    }

    /// <summary>Release the separate COM port lease when settings no longer key CW on it.</summary>
    public void ReleaseUnusedPort()
    {
        var settings = _settings.Current;
        var cw = settings.QsoLogbook.CwKeyer;
        var method = CwKeyingRoute.Resolve(cw, settings.Rig);
        lock (_portGate)
        {
            if (_port is null)
                return;
            if (cw.Enabled
                && method == CwKeyingMethod.SeparatePortLine
                && string.Equals(_port.PortName, cw.SeparatePort?.Trim(), StringComparison.OrdinalIgnoreCase))
                return;
        }

        ReleasePort();
    }

    public void Dispose()
    {
        ReleasePort();
        _keyboardPulse.Dispose();
    }

    private CwKeyerStartResult PrepareKeying(out CwKeyingMethod method, out bool useRts)
    {
        var settings = _settings.Current;
        var cw = settings.QsoLogbook.CwKeyer;
        method = CwKeyingRoute.Resolve(cw, settings.Rig);
        useRts = cw.KeyLine == PttLine.Rts;

        if (method != CwKeyingMethod.SeparatePortLine)
        {
            lock (_portGate)
                ClosePort();
        }

        switch (method)
        {
            case CwKeyingMethod.KenwoodText:
            case CwKeyingMethod.IcomText:
                if (!settings.Rig.Enabled)
                    return CwKeyerStartResult.RigNotConnected;
                return CwKeyerStartResult.Started;

            case CwKeyingMethod.CatPortLine:
                if (!settings.Rig.Enabled)
                    return CwKeyerStartResult.RigNotConnected;
                if (CwKeyingRoute.CatPortRtsBlocked(cw, settings.Rig))
                    return CwKeyerStartResult.RtsBlocked;
                if (!_rig.TrySetCwKeyLine(useRts, assert: false))
                    return CwKeyerStartResult.KeyLineUnavailable;
                return CwKeyerStartResult.Started;

            case CwKeyingMethod.SeparatePortLine:
                return EnsurePort(cw.SeparatePort, settings);

            default:
                return CwKeyerStartResult.KeyLineUnavailable;
        }
    }

    private void Run(
        int slot,
        CwMessage message,
        CwKeyingMethod method,
        bool useRts,
        bool saveQso,
        CancellationToken token,
        CancellationToken manualToken = default,
        CancellationToken timeoutToken = default)
    {
        var finish = CwKeyerFinish.Completed;
        try
        {
            finish = CwKeyingRoute.IsTextMethod(method)
                ? RunText(message, method, token)
                : RunLine(message, method, useRts, token);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CW keyer failed");
            finish = CwKeyerFinish.RigRejected;
        }
        finally
        {
            if (!CwKeyingRoute.IsTextMethod(method))
                SetLine(method, useRts, assert: false);
            Interlocked.CompareExchange(ref _activeSlot, IdleSlot, slot);
        }

        var timedOut = finish == CwKeyerFinish.Aborted
            && timeoutToken.IsCancellationRequested
            && !manualToken.IsCancellationRequested;

        Finished?.Invoke(this, new CwKeyerFinishedEventArgs(slot, finish, saveQso, timedOut));
    }

    private void RunKeyboard(CwKeyingMethod method, bool useRts, CancellationToken token)
    {
        var finish = CwKeyerFinish.Aborted;
        try
        {
            finish = CwKeyingRoute.IsTextMethod(method)
                ? RunKeyboardText(method, token)
                : RunKeyboardLine(method, useRts, token);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CW keyboard failed");
            finish = CwKeyerFinish.RigRejected;
        }
        finally
        {
            if (!CwKeyingRoute.IsTextMethod(method))
            {
                SetLine(method, useRts, assert: false);
            }
            else
            {
                if (token.IsCancellationRequested || finish != CwKeyerFinish.Completed)
                    _rig.TryStopCwText();
                // Release after stop so a Doppler write cannot land between them.
                _rig.SetCwTextDopplerHold(false);
            }

            Interlocked.CompareExchange(ref _activeSlot, IdleSlot, KeyboardSlot);
            lock (_keyboardGate)
            {
                _keyboardQueue.Clear();
                _keyboardLastIsGap = true;
            }
        }

        Finished?.Invoke(this, new CwKeyerFinishedEventArgs(KeyboardSlot, finish));
    }

    private CwKeyerFinish RunText(CwMessage message, CwKeyingMethod method, CancellationToken token)
    {
        _rig.SetCwTextDopplerHold(true);
        try
        {
            _rig.TrySetCwSpeed(_wpm);

            var pieces = method == CwKeyingMethod.KenwoodText
                ? KenwoodCwCodec.BuildSendCommands(message).Select(p => p.Piece).ToList()
                : IcomCwCodec.BuildSendCommands(message).Select(p => p.Piece).ToList();

            foreach (var piece in pieces)
            {
                if (token.IsCancellationRequested)
                    break;

                if (!_rig.TrySendCwText(piece, TextCommandTimeout))
                {
                    _rig.TryStopCwText();
                    return CwKeyerFinish.RigRejected;
                }

                // The radios report no buffer space, so wait until this piece has gone out.
                if (token.WaitHandle.WaitOne(MorseCode.EstimateDuration(piece, _wpm)))
                    break;
            }

            if (!token.IsCancellationRequested)
                return CwKeyerFinish.Completed;

            _rig.TryStopCwText();
            return CwKeyerFinish.Aborted;
        }
        finally
        {
            _rig.SetCwTextDopplerHold(false);
        }
    }

    private CwKeyerFinish RunKeyboardText(CwKeyingMethod method, CancellationToken token)
    {
        _rig.SetCwTextDopplerHold(true);
        _rig.TrySetCwSpeed(_wpm);
        // Idle until the first keystroke so Doppler can keep up while the operator thinks.
        _rig.SetCwTextDopplerHold(false);

        var clock = Stopwatch.StartNew();
        var readyAtMs = 0.0;
        var sentAny = false;

        while (!token.IsCancellationRequested)
        {
            if (!TryTakeKeyboardItem(out var item, token))
                break;

            if (item.IsSaveQso)
            {
                KeyboardSaveQsoRequested?.Invoke(this, EventArgs.Empty);
                continue;
            }

            var symbol = item.Symbol!.Value;
            KeyboardProgress?.Invoke(this, new CwKeyboardProgressEventArgs(symbol));

            if (symbol.IsWordGap)
            {
                if (!sentAny)
                    continue;

                readyAtMs = Math.Max(readyAtMs, clock.Elapsed.TotalMilliseconds)
                    + MorseCode.WordGapUnits * MorseCode.DotMs(_wpm);
                if (!WaitUntil(clock, readyAtMs, token))
                    return CwKeyerFinish.Aborted;
                if (IsKeyboardQueueEmpty())
                    _rig.SetCwTextDopplerHold(false);
                continue;
            }

            var piece = new CwMessage([symbol]);
            var gapUnits = sentAny ? MorseCode.CharacterGapUnits : 0;
            var startAt = Math.Max(readyAtMs, clock.Elapsed.TotalMilliseconds);
            if (gapUnits > 0)
            {
                var gapTarget = readyAtMs + gapUnits * MorseCode.DotMs(_wpm);
                startAt = Math.Max(gapTarget, clock.Elapsed.TotalMilliseconds);
                if (!WaitUntil(clock, startAt, token))
                    return CwKeyerFinish.Aborted;
            }

            _rig.SetCwTextDopplerHold(true);
            if (!_rig.TrySendCwText(piece, TextCommandTimeout))
            {
                _rig.TryStopCwText();
                return CwKeyerFinish.RigRejected;
            }

            readyAtMs = startAt + MorseCode.CountUnits(piece) * MorseCode.DotMs(_wpm);
            if (!WaitUntil(clock, readyAtMs, token))
                return CwKeyerFinish.Aborted;

            sentAny = true;
            if (IsKeyboardQueueEmpty())
                _rig.SetCwTextDopplerHold(false);
        }

        return token.IsCancellationRequested ? CwKeyerFinish.Aborted : CwKeyerFinish.Completed;
    }

    private bool IsKeyboardQueueEmpty()
    {
        lock (_keyboardGate)
            return _keyboardQueue.Count == 0;
    }

    private CwKeyerFinish RunLine(CwMessage message, CwKeyingMethod method, bool useRts, CancellationToken token)
    {
        var events = MorseCode.BuildKeyEvents(message);
        using var timer = HighResolutionTimer.Begin();
        var clock = Stopwatch.StartNew();
        var dueMs = 0.0;

        foreach (var keyEvent in events)
        {
            if (token.IsCancellationRequested)
                return CwKeyerFinish.Aborted;

            if (!SetLine(method, useRts, keyEvent.KeyDown) && keyEvent.KeyDown)
                return CwKeyerFinish.RigRejected;

            dueMs += keyEvent.Units * MorseCode.DotMs(_wpm);
            if (!WaitUntil(clock, dueMs, token))
                return CwKeyerFinish.Aborted;
        }

        return CwKeyerFinish.Completed;
    }

    private CwKeyerFinish RunKeyboardLine(CwKeyingMethod method, bool useRts, CancellationToken token)
    {
        using var timer = HighResolutionTimer.Begin();
        var clock = Stopwatch.StartNew();
        var dueMs = 0.0;
        var sentAny = false;
        var pendingGapUnits = 0;

        while (!token.IsCancellationRequested)
        {
            if (!TryTakeKeyboardItem(out var item, token))
                break;

            if (item.IsSaveQso)
            {
                KeyboardSaveQsoRequested?.Invoke(this, EventArgs.Empty);
                continue;
            }

            var symbol = item.Symbol!.Value;
            KeyboardProgress?.Invoke(this, new CwKeyboardProgressEventArgs(symbol));

            if (symbol.IsWordGap)
            {
                if (sentAny)
                    pendingGapUnits = MorseCode.WordGapUnits;
                continue;
            }

            if (sentAny)
            {
                var gapUnits = Math.Max(pendingGapUnits, MorseCode.CharacterGapUnits);
                pendingGapUnits = 0;
                SetLine(method, useRts, assert: false);
                var gapTarget = dueMs + gapUnits * MorseCode.DotMs(_wpm);
                var startAt = Math.Max(gapTarget, clock.Elapsed.TotalMilliseconds);
                if (!WaitUntil(clock, startAt, token))
                    return CwKeyerFinish.Aborted;
                dueMs = startAt;
            }

            var events = MorseCode.BuildKeyEvents(new CwMessage([symbol]));
            foreach (var keyEvent in events)
            {
                if (token.IsCancellationRequested)
                    return CwKeyerFinish.Aborted;

                if (!SetLine(method, useRts, keyEvent.KeyDown) && keyEvent.KeyDown)
                    return CwKeyerFinish.RigRejected;

                dueMs += keyEvent.Units * MorseCode.DotMs(_wpm);
                if (!WaitUntil(clock, dueMs, token))
                    return CwKeyerFinish.Aborted;
            }

            SetLine(method, useRts, assert: false);
            sentAny = true;
        }

        return token.IsCancellationRequested ? CwKeyerFinish.Aborted : CwKeyerFinish.Completed;
    }

    private bool TryTakeKeyboardItem(out KeyboardItem item, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            lock (_keyboardGate)
            {
                if (_keyboardQueue.Count > 0)
                {
                    item = _keyboardQueue.Dequeue();
                    if (_keyboardQueue.Count == 0)
                        _keyboardPulse.Reset();
                    return true;
                }

                _keyboardPulse.Reset();
            }

            try
            {
                _keyboardPulse.Wait(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        item = default;
        return false;
    }

    /// <summary>
    /// Waits on an absolute schedule so element lengths do not drift: sleep while more than
    /// 2 ms remain, then spin. Returns false when cancelled.
    /// </summary>
    private static bool WaitUntil(Stopwatch clock, double dueMs, CancellationToken token)
    {
        while (true)
        {
            var remaining = dueMs - clock.Elapsed.TotalMilliseconds;
            if (remaining <= 0)
                return !token.IsCancellationRequested;
            if (remaining > 2)
            {
                if (token.WaitHandle.WaitOne((int)(remaining - 1.5)))
                    return false;
            }
            else
            {
                if (token.IsCancellationRequested)
                    return false;
                Thread.SpinWait(50);
            }
        }
    }

    private bool SetLine(CwKeyingMethod method, bool useRts, bool assert)
    {
        if (method == CwKeyingMethod.CatPortLine)
            return _rig.TrySetCwKeyLine(useRts, assert);

        lock (_portGate)
        {
            return _port is { IsOpen: true } port
                && port.TrySetLine(useRts ? PttLine.Rts : PttLine.Dtr, assert);
        }
    }

    private CwKeyerStartResult EnsurePort(string? portName, AppSettings settings)
    {
        var name = portName?.Trim() ?? "";
        if (name.Length == 0)
            return CwKeyerStartResult.NoPortConfigured;
        if (SerialPortConflictHelper.IsCwKeyPortOccupied(settings, name))
            return CwKeyerStartResult.PortConflict;

        lock (_portGate)
        {
            if (_port is { IsOpen: true }
                && string.Equals(_port.PortName, name, StringComparison.OrdinalIgnoreCase))
                return CwKeyerStartResult.Started;

            ClosePort();
            try
            {
                _port = _portPool.Acquire(name, "CW keyer");
                return CwKeyerStartResult.Started;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
            {
                Log.Warning(ex, "CW keyer could not open {Port}", name);
                return SerialPortConnectErrorHelper.Classify(ex) switch
                {
                    SerialPortConnectErrorKind.PortBusy => CwKeyerStartResult.PortBusy,
                    SerialPortConnectErrorKind.PortNotFound => CwKeyerStartResult.PortNotFound,
                    _ => CwKeyerStartResult.PortFailed
                };
            }
        }
    }

    private void ClosePort()
    {
        var port = _port;
        _port = null;
        port?.Dispose();
    }

    private readonly record struct KeyboardItem(CwSymbol? Symbol, bool IsSaveQso)
    {
        public static KeyboardItem ForSymbol(CwSymbol symbol) => new(symbol, false);

        public static KeyboardItem SaveQso { get; } = new(null, true);
    }

    /// <summary>Raises the Windows timer resolution to 1 ms while a message is keyed.</summary>
    private readonly struct HighResolutionTimer : IDisposable
    {
        private readonly bool _active;

        private HighResolutionTimer(bool active) => _active = active;

        public static HighResolutionTimer Begin()
        {
            if (!OperatingSystem.IsWindows())
                return default;
            try
            {
                return new HighResolutionTimer(TimeBeginPeriod(1) == 0);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return default;
            }
        }

        public void Dispose()
        {
            if (_active)
                TimeEndPeriod(1);
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint period);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint period);
    }
}
