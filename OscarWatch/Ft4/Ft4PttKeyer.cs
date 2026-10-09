using System.IO.Ports;
using OscarWatch.Core.Ft4;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Services;
using OscarWatch.Rig;
using Serilog;

namespace OscarWatch.Ft4;

/// <summary>Keys the uplink for FT4 using CAT, handshake lines, VOX, or a manual prompt.</summary>
public sealed class Ft4PttKeyer : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<Ft4PttKeyer>();

    private readonly IRigController _rig;
    private readonly ISettingsService _settings;
    private readonly object _portGate = new();
    private SerialPort? _separatePort;
    private bool _keyed;
    private Action<string>? _manualPrompt;

    public Ft4PttKeyer(IRigController rig, ISettingsService settings)
    {
        _rig = rig;
        _settings = settings;
    }

    public void SetManualPromptHandler(Action<string>? handler) => _manualPrompt = handler;

    public Ft4PttMethod Method => _settings.Current.Ft4.PttMethod;

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
        var lead = Math.Clamp(_settings.Current.Ft4.PttLeadMs, 0, 2000);
        if (lead > 0)
            await Task.Delay(lead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns false when PTT was already asserted, or when VOX needs no lead wait.</summary>
    private bool TryAssert()
    {
        if (_keyed)
            return false;

        var ft4 = _settings.Current.Ft4;
        switch (ft4.PttMethod)
        {
            case Ft4PttMethod.Vox:
                _keyed = true;
                return false;

            case Ft4PttMethod.Cat:
                _rig.SetPtt(true);
                break;

            case Ft4PttMethod.CatPortHandshake:
                _rig.SetHandshakePtt(ft4.PttLine == Ft4PttLine.Rts, assert: !ft4.PttInvert);
                break;

            case Ft4PttMethod.SeparateComPort:
                lock (_portGate)
                {
                    EnsureSeparatePort(ft4);
                    SetLine(_separatePort!, ft4.PttLine, assert: !ft4.PttInvert);
                }
                break;

            case Ft4PttMethod.Manual:
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

        var ft4 = _settings.Current.Ft4;
        var tail = Math.Clamp(ft4.PttTailMs, 0, 2000);
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

    private void DropPtt()
    {
        if (!_keyed)
            return;

        var ft4 = _settings.Current.Ft4;
        try
        {
            switch (ft4.PttMethod)
            {
                case Ft4PttMethod.Vox:
                    break;
                case Ft4PttMethod.Cat:
                    _rig.SetPtt(false);
                    break;
                case Ft4PttMethod.CatPortHandshake:
                    _rig.SetHandshakePtt(ft4.PttLine == Ft4PttLine.Rts, assert: ft4.PttInvert);
                    break;
                case Ft4PttMethod.SeparateComPort:
                    lock (_portGate)
                    {
                        if (_separatePort is { IsOpen: true })
                            SetLine(_separatePort, ft4.PttLine, assert: ft4.PttInvert);
                    }
                    break;
                case Ft4PttMethod.Manual:
                    _manualPrompt?.Invoke("unkey");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FT4 PTT unkey failed");
        }
        finally
        {
            _keyed = false;
        }
    }

    private void EnsureSeparatePort(Ft4Settings ft4)
    {
        var portName = ft4.SeparatePttPort?.Trim() ?? "";
        if (portName.Length == 0)
            throw new InvalidOperationException("Separate PTT COM port is not configured.");

        if (SerialPortConflictHelper.TryDescribeFt4SeparatePttConflict(
                portName,
                _settings.Current.Rig,
                _settings.Current.Rotator,
                _settings.Current.Gps,
                out var conflict))
        {
            throw new InvalidOperationException(conflict);
        }

        if (_separatePort is { IsOpen: true }
            && string.Equals(_separatePort.PortName, portName, StringComparison.OrdinalIgnoreCase))
            return;

        _separatePort?.Dispose();
        _separatePort = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false
        };
        _separatePort.Open();
    }

    private static void SetLine(SerialPort port, Ft4PttLine line, bool assert)
    {
        if (line == Ft4PttLine.Rts)
            port.RtsEnable = assert;
        else
            port.DtrEnable = assert;
    }

    /// <summary>
    /// Drop PTT and close the separate PTT COM port so another application (MSHV, WSJT-X)
    /// can open it. The next transmission opens it again.
    /// </summary>
    public void ReleasePort()
    {
        DropPtt();
        lock (_portGate)
            ClosePort();
    }

    /// <summary>
    /// Close the separate PTT port when the settings no longer use it (another method,
    /// or a different port), so the old port is free at once. Closing it also drops its lines.
    /// </summary>
    public void ReleaseUnusedPort()
    {
        var ft4 = _settings.Current.Ft4;
        lock (_portGate)
        {
            if (_separatePort is null)
                return;
            if (ft4.PttMethod == Ft4PttMethod.SeparateComPort
                && string.Equals(_separatePort.PortName, ft4.SeparatePttPort?.Trim(), StringComparison.OrdinalIgnoreCase))
                return;

            ClosePort();
        }
    }

    private void ClosePort()
    {
        var port = _separatePort;
        _separatePort = null;
        if (port is null)
            return;

        try
        {
            port.Dispose();
            Log.Information("FT4 released PTT port {Port}", port.PortName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FT4 PTT port close failed");
        }
    }

    public void Dispose() => ReleasePort();
}
