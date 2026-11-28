using OscarWatch.Core.Hardware;
using OscarWatch.Core.Services;
using Serilog;

namespace OscarWatch.Hardware;

/// <summary>Why the Terminal Settings Test PTT button cannot start.</summary>
public enum PttTestBlockReason
{
    None = 0,
    NoRig,
    RigDisconnected,
    TerminalBusy,
    Ft4Busy,
    CwBusy,
    TestToneBusy,
    SeparatePortConflict,
    AlreadyRunning,
}

/// <summary>How a <see cref="PttTestSession"/> finished.</summary>
public enum PttTestOutcome
{
    Completed,
    Cancelled,
    RigDisconnected,
}

/// <summary>
/// Keys the radio for a short fixed time with a snapshot of on-screen PTT settings, then unkeys.
/// Used by the Terminal Settings Test PTT button.
/// </summary>
public sealed class PttTestSession : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PttTestSession>();

    public static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DisconnectPoll = TimeSpan.FromMilliseconds(100);

    private readonly IRigController _rig;
    private readonly PttOptions _options;
    private readonly PttKeyer _keyer;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public PttTestSession(
        IRigController rig,
        PttOptions options,
        Func<string, string?>? describePortConflict = null,
        PttSessionMarker? marker = null,
        HandshakePortPool? portPool = null)
    {
        _rig = rig;
        _options = options;
        _keyer = new PttKeyer(
            rig,
            () => _options,
            "Terminal PTT test",
            describePortConflict,
            marker,
            portPool);
    }

    public PttOptions Options => _options;

    public bool IsKeyed => _keyer.IsKeyed;

    /// <summary>True when CAT or the CAT-port handshake needs a connected radio.</summary>
    public static bool NeedsRigConnection(PttMethod method) =>
        method is PttMethod.Cat or PttMethod.CatPortHandshake;

    /// <summary>
    /// Returns why a test cannot start, or <see cref="PttTestBlockReason.None"/> when it can.
    /// VOX is never blocked for connection; the caller shows a message instead of keying.
    /// </summary>
    public static PttTestBlockReason GetBlockReason(
        PttMethod method,
        bool hasRig,
        bool rigConnected,
        bool terminalTransmitting,
        bool ft4Active,
        bool cwSending,
        bool testToneBusy,
        bool separatePortConflict,
        bool sessionActive)
    {
        if (sessionActive)
            return PttTestBlockReason.AlreadyRunning;
        if (testToneBusy)
            return PttTestBlockReason.TestToneBusy;
        if (terminalTransmitting)
            return PttTestBlockReason.TerminalBusy;
        if (ft4Active)
            return PttTestBlockReason.Ft4Busy;
        if (cwSending)
            return PttTestBlockReason.CwBusy;
        if (separatePortConflict)
            return PttTestBlockReason.SeparatePortConflict;
        if (NeedsRigConnection(method))
        {
            if (!hasRig)
                return PttTestBlockReason.NoRig;
            if (!rigConnected)
                return PttTestBlockReason.RigDisconnected;
        }

        return PttTestBlockReason.None;
    }

    /// <summary>
    /// Asserts PTT (with lead), holds for <see cref="HoldDuration"/>, then unkeys without a tail.
    /// Cancel or dispose unkeys at once. For CAT methods, a disconnect ends the test early.
    /// </summary>
    public async Task<PttTestOutcome> RunAsync(CancellationToken external = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, external);
        var ct = linked.Token;

        Log.Information(
            "Terminal PTT test key method={Method} line={Line} invert={Invert} port={Port}",
            _options.Method,
            _options.Line,
            _options.Invert,
            string.IsNullOrWhiteSpace(_options.SeparatePort) ? "-" : _options.SeparatePort.Trim());

        try
        {
            await _keyer.KeyAsync(ct).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + HoldDuration;
            while (DateTime.UtcNow < deadline)
            {
                if (NeedsRigConnection(_options.Method) && !_rig.GetStatus().IsConnected)
                {
                    Log.Information("Terminal PTT test unkey (rig disconnected) method={Method}", _options.Method);
                    _keyer.ForceOff();
                    return PttTestOutcome.RigDisconnected;
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    break;
                var wait = remaining < DisconnectPoll ? remaining : DisconnectPoll;
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            Log.Information("Terminal PTT test unkey method={Method} line={Line}", _options.Method, _options.Line);
            _keyer.UnkeyNow();
            return PttTestOutcome.Completed;
        }
        catch (OperationCanceledException)
        {
            Log.Information("Terminal PTT test unkey (cancelled) method={Method}", _options.Method);
            _keyer.UnkeyNow();
            return PttTestOutcome.Cancelled;
        }
        catch
        {
            _keyer.UnkeyNow();
            throw;
        }
    }

    /// <summary>Ends the hold early so the radio unkeys at once.</summary>
    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _keyer.Dispose();
        _cts.Dispose();
    }
}
