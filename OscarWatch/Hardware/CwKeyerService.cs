using System.Diagnostics;
using System.IO.Ports;
using System.Runtime.InteropServices;
using OscarWatch.Core.Cw;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using Serilog;

namespace OscarWatch.Hardware;

/// <summary>Why <see cref="CwKeyerService.Send"/> did not start.</summary>
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

public sealed record CwKeyerFinishedEventArgs(int Slot, CwKeyerFinish Finish);

/// <summary>
/// Sends logbook CW macros on the uplink: as text the radio keys itself (Kenwood <c>KY</c>, Icom <c>0x17</c>),
/// or by timing DTR or RTS on the CAT port or a COM port of its own. One message at a time; a new
/// message aborts the one in progress.
/// </summary>
public sealed class CwKeyerService : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<CwKeyerService>();

    /// <summary>How long a CAT text piece may wait behind a Doppler write.</summary>
    private static readonly TimeSpan TextCommandTimeout = TimeSpan.FromSeconds(3);

    private readonly IRigController _rig;
    private readonly ISettingsService _settings;
    private readonly object _gate = new();
    private readonly object _portGate = new();
    private SerialPort? _port;
    private Thread? _worker;
    private CancellationTokenSource? _cts;
    private volatile int _wpm;
    private int _activeSlot = -1;

    public CwKeyerService(IRigController rig, ISettingsService settings)
    {
        _rig = rig;
        _settings = settings;
        _wpm = CwKeyerSettings.ClampWpm(settings.Current.QsoLogbook.CwKeyer.Wpm);
    }

    /// <summary>Raised on the keyer thread when a message that started ends.</summary>
    public event EventHandler<CwKeyerFinishedEventArgs>? Finished;

    public bool IsSending => Volatile.Read(ref _activeSlot) >= 0;

    /// <summary>Message slot (0 to 11) being sent, or -1.</summary>
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

    public CwKeyerStartResult Send(int slot, CwMessage message)
    {
        if (message.IsEmpty)
            return CwKeyerStartResult.NothingToSend;

        Stop();

        var settings = _settings.Current;
        var cw = settings.QsoLogbook.CwKeyer;
        var method = CwKeyingRoute.Resolve(cw, settings.Rig);
        var useRts = cw.KeyLine == PttLine.Rts;

        switch (method)
        {
            case CwKeyingMethod.KenwoodText:
            case CwKeyingMethod.IcomText:
                if (!settings.Rig.Enabled)
                    return CwKeyerStartResult.RigNotConnected;
                break;

            case CwKeyingMethod.CatPortLine:
                if (!settings.Rig.Enabled)
                    return CwKeyerStartResult.RigNotConnected;
                if (CwKeyingRoute.CatPortRtsBlocked(cw, settings.Rig))
                    return CwKeyerStartResult.RtsBlocked;
                if (!_rig.TrySetCwKeyLine(useRts, assert: false))
                    return CwKeyerStartResult.KeyLineUnavailable;
                break;

            case CwKeyingMethod.SeparatePortLine:
            {
                var portResult = EnsurePort(cw.SeparatePort, settings);
                if (portResult != CwKeyerStartResult.Started)
                    return portResult;
                break;
            }
        }

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _cts = cts;
            Volatile.Write(ref _activeSlot, slot);
            _worker = new Thread(() => Run(slot, message, method, useRts, cts.Token))
            {
                IsBackground = true,
                Name = "OscarWatch.CwKeyer",
                Priority = ThreadPriority.AboveNormal
            };
            _worker.Start();
        }

        return CwKeyerStartResult.Started;
    }

    /// <summary>Abort the message in progress and leave the key up.</summary>
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

        if (cts is null)
            return;

        cts.Cancel();
        if (worker is not null && worker != Thread.CurrentThread)
            worker.Join(TimeSpan.FromSeconds(4));
        cts.Dispose();
    }

    /// <summary>Close the keyer's own COM port so another application can use it.</summary>
    public void ReleasePort()
    {
        Stop();
        lock (_portGate)
            ClosePort();
    }

    public void Dispose() => ReleasePort();

    private void Run(int slot, CwMessage message, CwKeyingMethod method, bool useRts, CancellationToken token)
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
            Interlocked.CompareExchange(ref _activeSlot, -1, slot);
        }

        Finished?.Invoke(this, new CwKeyerFinishedEventArgs(slot, finish));
    }

    private CwKeyerFinish RunText(CwMessage message, CwKeyingMethod method, CancellationToken token)
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
            if (_port is not { IsOpen: true } port)
                return false;
            try
            {
                if (useRts)
                    port.RtsEnable = assert;
                else
                    port.DtrEnable = assert;
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "CW key line change on {Port} failed", port.PortName);
                return false;
            }
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
            var port = new SerialPort(name, 9600, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                DtrEnable = false,
                RtsEnable = false
            };
            try
            {
                port.Open();
                _port = port;
                Log.Information("CW keyer opened {Port}", name);
                return CwKeyerStartResult.Started;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
            {
                try { port.Dispose(); } catch { /* the open already failed */ }
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
        if (port is null)
            return;

        try
        {
            port.Dispose();
            Log.Information("CW keyer released {Port}", port.PortName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CW keyer port close failed");
        }
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
