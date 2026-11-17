using OscarWatch.Core.Hardware;
using OscarWatch.Core.Services;
using Serilog;

namespace OscarWatch.Hardware;

/// <summary>
/// Keys the uplink using CAT, handshake lines, VOX or a manual prompt. FT4 and the Terminal built-in
/// modem each own one, with their own <see cref="PttOptions"/>.
/// </summary>
public sealed class PttKeyer : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PttKeyer>();

    private readonly IRigController _rig;
    private readonly Func<PttOptions> _options;
    private readonly string _label;
    private readonly Func<string, string?>? _describePortConflict;
    private readonly PttSessionMarker? _marker;
    private readonly HandshakePortPool _portPool;
    private readonly object _portGate = new();
    private HandshakePortLease? _separatePort;
    private (PttLine Line, bool IdleAssert)? _keyedSeparateLine;
    private volatile bool _keyed;
    private Action<string>? _manualPrompt;

    /// <param name="label">Feature name for logs, for example <c>FT4</c> or <c>Terminal</c>.</param>
    /// <param name="describePortConflict">Returns a message when the separate PTT port is in use elsewhere.</param>
    /// <param name="marker">Records that PTT is up, so a crash while keyed is noticed at the next start.</param>
    /// <param name="portPool">Shared handshake COM port pool, or null for the process-wide pool.</param>
    public PttKeyer(
        IRigController rig,
        Func<PttOptions> options,
        string label,
        Func<string, string?>? describePortConflict = null,
        PttSessionMarker? marker = null,
        HandshakePortPool? portPool = null)
    {
        _rig = rig;
        _options = options;
        _label = label;
        _describePortConflict = describePortConflict;
        _marker = marker;
        _portPool = portPool ?? HandshakePortPool.Shared;
    }

    public void SetManualPromptHandler(Action<string>? handler) => _manualPrompt = handler;

    public PttMethod Method => _options().Method;

    public bool IsKeyed => _keyed;

    /// <summary>
    /// Assert PTT without the lead-in wait. CAT and the CAT-port handshake line share the
    /// rig thread with Doppler, so the command has to be queued at the slot boundary,
    /// before that write, or the radio stays in receive for the start of the burst.
    /// </summary>
    public void KeyNow() => TryAssert();

    public async Task KeyAsync(CancellationToken cancellationToken = default)
    {
        if (!TryAssert())
            return;

        // VOX keys from the audio itself. The lead wait is for CAT and hardware lines,
        // and TryAssert already returned false for VOX on a fresh key (see below).
        var lead = _options().ClampedLeadMs;
        if (lead > 0)
            await Task.Delay(lead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns false when PTT was already asserted, or when VOX needs no lead wait.</summary>
    private bool TryAssert()
    {
        if (_keyed)
            return false;

        var options = _options();
        if (options.Method != PttMethod.Vox)
            _marker?.MarkKeyed(_label, options.Method);

        switch (options.Method)
        {
            case PttMethod.Vox:
                _keyed = true;
                return false;

            case PttMethod.Cat:
                _rig.SetPtt(true);
                break;

            case PttMethod.CatPortHandshake:
                _rig.SetHandshakePtt(options.Line == PttLine.Rts, assert: !options.Invert);
                break;

            case PttMethod.SeparateComPort:
                lock (_portGate)
                {
                    EnsureSeparatePort(options);
                    _separatePort!.SetLine(options.Line, assert: !options.Invert);
                    _keyedSeparateLine = (options.Line, IdleAssert: options.Invert);
                }
                break;

            case PttMethod.Manual:
                _manualPrompt?.Invoke("key");
                break;
        }

        _keyed = true;
        return true;
    }

    public async Task UnkeyAsync(CancellationToken cancellationToken = default)
    {
        if (!_keyed)
            return;

        var tail = _options().ClampedTailMs;
        if (tail > 0)
        {
            try
            {
                await Task.Delay(tail, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Halt still has to drop PTT. The tail must not leave the radio keyed.
            }
        }

        DropPtt();
    }

    /// <summary>Drop PTT at once, without the tail delay. Used when the operator stops a transmission.</summary>
    public void UnkeyNow() => DropPtt();

    /// <summary>
    /// Send an unkey whatever this keyer thinks the state is. Covers a radio left transmitting by a
    /// crash: called when the Terminal modem starts and whenever the rig reconnects. VOX and Manual
    /// have nothing to send.
    /// </summary>
    public void ForceOff()
    {
        var options = _options();
        try
        {
            switch (options.Method)
            {
                case PttMethod.Cat:
                    _rig.SetPtt(false);
                    break;
                case PttMethod.CatPortHandshake:
                    _rig.SetHandshakePtt(options.Line == PttLine.Rts, assert: options.Invert);
                    break;
                case PttMethod.SeparateComPort:
                    lock (_portGate)
                    {
                        _keyedSeparateLine = null;
                        if (_separatePort is { IsOpen: true })
                            _separatePort.SetLine(options.Line, assert: options.Invert);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{Label} PTT force off failed", _label);
        }
        finally
        {
            _keyed = false;
            _marker?.Clear();
        }
    }

    private void DropPtt()
    {
        if (!_keyed)
            return;

        var options = _options();
        try
        {
            var droppedTrackedLine = false;
            lock (_portGate)
            {
                if (_keyedSeparateLine is { } keyed)
                {
                    _keyedSeparateLine = null;
                    droppedTrackedLine = true;
                    if (_separatePort is { IsOpen: true })
                        _separatePort.SetLine(keyed.Line, keyed.IdleAssert);
                }
            }

            if (!droppedTrackedLine)
            {
                switch (options.Method)
                {
                    case PttMethod.Vox:
                        break;
                    case PttMethod.Cat:
                        _rig.SetPtt(false);
                        break;
                    case PttMethod.CatPortHandshake:
                        _rig.SetHandshakePtt(options.Line == PttLine.Rts, assert: options.Invert);
                        break;
                    case PttMethod.SeparateComPort:
                        lock (_portGate)
                        {
                            if (_separatePort is { IsOpen: true })
                                _separatePort.SetLine(options.Line, assert: options.Invert);
                        }
                        break;
                    case PttMethod.Manual:
                        _manualPrompt?.Invoke("unkey");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{Label} PTT unkey failed", _label);
        }
        finally
        {
            _keyed = false;
            _marker?.Clear();
        }
    }

    private void EnsureSeparatePort(PttOptions options)
    {
        var portName = options.SeparatePort?.Trim() ?? "";
        if (portName.Length == 0)
            throw new InvalidOperationException("Separate PTT COM port is not configured.");

        if (_describePortConflict?.Invoke(portName) is { Length: > 0 } conflict)
            throw new InvalidOperationException(conflict);

        if (_separatePort is { IsOpen: true }
            && string.Equals(_separatePort.PortName, portName, StringComparison.OrdinalIgnoreCase))
            return;

        _separatePort?.Dispose();
        _separatePort = null;
        try
        {
            _separatePort = _portPool.Acquire(portName, _label);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            throw new InvalidOperationException(DescribeOpenFailure(portName, ex), ex);
        }
    }

    private static string DescribeOpenFailure(string portName, Exception ex) =>
        SerialPortConnectErrorHelper.Classify(ex) switch
        {
            SerialPortConnectErrorKind.PortBusy =>
                $"Serial port in use ({portName}). Close the other program using it, or choose a different port.",
            SerialPortConnectErrorKind.PortNotFound =>
                $"Serial port not found ({portName}). Check the USB cable and refresh the port list.",
            _ => ex.Message
        };

    /// <summary>
    /// Drop PTT and release the separate PTT COM port lease. Once no OscarWatch feature holds a lease,
    /// the OS handle closes so another application (MSHV, WSJT-X) can open it.
    /// </summary>
    public void ReleasePort()
    {
        DropPtt();
        lock (_portGate)
            ClosePort();
    }

    /// <summary>
    /// Release the separate PTT port lease when the settings no longer use it (another method,
    /// or a different port), dropping the keyed line first in case another feature keeps the port open.
    /// </summary>
    public void ReleaseUnusedPort()
    {
        var options = _options();
        lock (_portGate)
        {
            if (_separatePort is null)
                return;
            if (options.Method == PttMethod.SeparateComPort
                && string.Equals(_separatePort.PortName, options.SeparatePort?.Trim(), StringComparison.OrdinalIgnoreCase))
                return;

            if (_keyedSeparateLine is { } keyed)
            {
                _keyedSeparateLine = null;
                _separatePort.TrySetLine(keyed.Line, keyed.IdleAssert);
                _keyed = false;
                _marker?.Clear();
            }

            ClosePort();
        }
    }

    private void ClosePort()
    {
        var port = _separatePort;
        _separatePort = null;
        _keyedSeparateLine = null;
        port?.Dispose();
    }

    public void Dispose() => ReleasePort();
}
