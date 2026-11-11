using System.Collections.Concurrent;
using OscarWatch.Diagnostics;
using OscarWatch.Core.Cw;
using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using Serilog;

namespace OscarWatch.Rig;

public abstract class IcomCivDriverBase : IRigDriver
{
    private static readonly ILogger Log = Serilog.Log.ForContext<IcomCivDriverBase>();
    private static readonly ConcurrentDictionary<string, ThrottledLogger> OpenFailureLogs = new(StringComparer.OrdinalIgnoreCase);
    private IIcomCivTransport? _transport;
    private readonly IIcomCivTransport? _injectedTransport;
    private readonly Func<IIcomCivTransport>? _transportFactory;
    private readonly int _catDelayMs;
    private RigVfo _currentVfo = RigVfo.VfoA;
    private long _lastMainHz;
    private long _lastSubHz;
    private long _lastVfoAFreq;
    private long _lastVfoBFreq;

    protected IcomCivDriverBase(RigType rigType, string port, int baudRate, string civAddressHex, int catDelayMs = 50)
        : this(rigType, port, baudRate, civAddressHex, catDelayMs, injectedTransport: null)
    {
    }

    internal IcomCivDriverBase(RigType rigType, IIcomCivTransport injectedTransport)
    {
        RigType = rigType;
        Port = "";
        BaudRate = 0;
        CivAddress = 0x60;
        _catDelayMs = 50;
        _injectedTransport = injectedTransport;
        _transport = injectedTransport;
    }

    /// <summary>
    /// Driver that creates its own transport on each <see cref="Open"/> (network control).
    /// Connection errors propagate so the caller can report them, and <see cref="Dispose"/> closes the session.
    /// </summary>
    internal IcomCivDriverBase(
        RigType rigType,
        Func<IIcomCivTransport> transportFactory,
        string endpointLabel,
        string civAddressHex,
        int catDelayMs = 50)
    {
        RigType = rigType;
        Port = endpointLabel;
        BaudRate = 0;
        CivAddress = IcomCivCodec.ParseCivAddressHex(civAddressHex);
        _catDelayMs = Math.Max(catDelayMs, 15);
        _transportFactory = transportFactory;
    }

    private IcomCivDriverBase(
        RigType rigType,
        string port,
        int baudRate,
        string civAddressHex,
        int catDelayMs,
        IIcomCivTransport? injectedTransport)
    {
        RigType = rigType;
        Port = port;
        BaudRate = baudRate;
        CivAddress = IcomCivCodec.ParseCivAddressHex(civAddressHex);
        _catDelayMs = Math.Max(catDelayMs, 15);
        _injectedTransport = injectedTransport;
    }

    public RigType RigType { get; }
    protected string Port { get; }
    protected int BaudRate { get; }
    protected int CivAddress { get; }

    public bool IsConnected => _transport?.IsOpen == true;
    public abstract bool SupportsTracking { get; }

    public bool TryConfirmIdentity()
    {
        if (_transport is null || !IsConnected)
            return false;

        // Operating-frequency read (0x03). Do not send 0x19 (transceiver ID): on an IC-910
        // that reply is addressed to CI-V 0x00, and the radio can treat it as a command.
        var response = _transport.WriteCommand([0x03], _catDelayMs);
        var hz = IcomCivCodec.DecodeFrequencyFromResponse(response);
        if (hz is >= 100_000 and <= 10_500_000_000)
            return true;

        Log.Warning("CI-V frequency read on {Port} did not return a frequency", Port);
        return false;
    }

    public void Open()
    {
        if (_injectedTransport is not null)
        {
            try
            {
                _injectedTransport.Open();
            }
            catch (Exception ex)
            {
                OpenFailureLogs.GetOrAdd("injected", _ => new ThrottledLogger(Log))
                    .Warning(ex, "Failed to open injected CI-V transport");
            }

            return;
        }

        if (_transportFactory is not null)
        {
            _transport?.Dispose();
            _transport = null;
            var owned = _transportFactory();
            try
            {
                owned.Open();
            }
            catch
            {
                owned.Dispose();
                throw;
            }

            _transport = owned;
            return;
        }

        _transport?.Dispose();
        _transport = new IcomSerialTransport(Port, BaudRate, CivAddress, _catDelayMs);
        try
        {
            _transport.Open();
            if (_transport.IsOpen)
                OpenFailureLogs.GetOrAdd(Port, _ => new ThrottledLogger(Log)).Reset();
        }
        catch (Exception ex)
        {
            OpenFailureLogs.GetOrAdd(Port, _ => new ThrottledLogger(Log))
                .Warning(ex, "Failed to open CI-V port {Port} at {BaudRate}", Port, BaudRate);
            _transport.Dispose();
            _transport = null;
        }
    }

    public long? ReadFrequencyHz(RigVfo vfo)
    {
        SelectVfoInternal(vfo, force: false);
        var cached = CachedFrequencyHz(vfo);

        if (_transport is null || !IsConnected)
            return cached > 0 ? cached : null;

        Thread.Sleep(_catDelayMs);
        var response = _transport.WriteCommand([0x03], _catDelayMs);
        var hz = IcomCivCodec.DecodeFrequencyFromResponse(response);
        if (hz is { } value && IsFrequencyAllowedHz(value))
        {
            StoreFrequencyHz(vfo, value);
            return value;
        }

        // The radio did not answer. The last commanded frequency is not the dial: reporting
        // it made a busy IC-9700 look untouched, and the next Doppler write snapped the VFO
        // back while the operator was still tuning.
        return null;
    }

    public bool SetFrequencyHz(long hz)
    {
        if (!IsFrequencyAllowedHz(hz))
            return false;

        if (_transport is null || !IsConnected)
        {
            StoreFrequencyHz(_currentVfo, hz);
            return true;
        }

        var body = IcomCivCodec.EncodeSetFrequencyHz(hz);
        if (!SendWithAckRetry(body, $"set frequency on {_currentVfo}"))
            return false;

        StoreFrequencyHz(_currentVfo, hz);
        return true;
    }

    public void SelectVfo(RigVfo vfo, bool force = false) => SelectVfoInternal(vfo, force);

    /// <summary>Map RigController VFO names to CI-V selectors (e.g. IC-705 uses VFO A instead of Main).</summary>
    protected virtual RigVfo MapOperationalVfo(RigVfo vfo) => vfo;

    /// <summary>Per-model band gate before CI-V frequency writes/reads (default: shared satellite bands).</summary>
    protected virtual bool IsFrequencyAllowedHz(long hz) =>
        IcomCivCodec.IsValidSatelliteFrequencyHz(hz);

    private void SelectVfoInternal(RigVfo vfo, bool force)
    {
        if (!force && _currentVfo == vfo && _transport is { IsOpen: true })
            return;

        if (_transport is null || !IsConnected)
        {
            _currentVfo = vfo;
            return;
        }

        var operational = MapOperationalVfo(vfo);
        var cmd = operational switch
        {
            RigVfo.VfoA => new byte[] { 0x07, 0x00 },
            RigVfo.VfoB => new byte[] { 0x07, 0x01 },
            RigVfo.Main => new byte[] { 0x07, 0xD0 },
            RigVfo.Sub => new byte[] { 0x07, 0xD1 },
            _ => new byte[] { 0x07, 0x00 }
        };

        var previousVfo = _currentVfo;
        if (TrySelectVfo(cmd, vfo, $"select VFO {vfo}"))
        {
            if (previousVfo != vfo)
                Thread.Sleep(_catDelayMs);
            return;
        }

        Log.Warning("CI-V select VFO {Vfo} failed after retry", vfo);
    }

    /// <summary>
    /// VFO select (0x07). NAK means rejected; empty/no reply often means already selected — ICOM omits FB in SAT mode.
    /// </summary>
    private bool TrySelectVfo(ReadOnlySpan<byte> body, RigVfo vfo, string description)
    {
        if (_transport is null || !IsConnected)
        {
            _currentVfo = vfo;
            return true;
        }

        var changingVfo = _currentVfo != vfo;
        var maxAttempts = changingVfo ? 3 : 2;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(_catDelayMs);

            var response = _transport.WriteCommand(body, _catDelayMs);
            if (IsCivAck(response))
            {
                _currentVfo = vfo;
                return true;
            }

            if (IsCivNak(response))
            {
                Log.Debug("CI-V {Description} NAK (0xFA), attempt {Attempt}", description, attempt + 1);
                continue;
            }

            if (changingVfo && attempt < maxAttempts - 1)
            {
                Log.Debug("CI-V {Description} no ACK while changing VFO; retrying", description);
                continue;
            }

            Log.Debug("CI-V {Description} no ACK; assuming VFO selected", description);
            _currentVfo = vfo;
            return true;
        }

        return false;
    }

    public virtual void SetMode(string mode)
    {
        if (_transport is null || !IsConnected)
            return;

        foreach (var cmd in GetSetModeCommands(mode))
            SendWithAckRetry(cmd, $"set mode {mode}");
    }

    /// <summary>
    /// True when the radio has DATA sidebands (USB-D / LSB-D / FM-D) selectable with CI-V 0x1A 0x06.
    /// Catalogue DATA-* modes then turn data on, and plain USB/LSB turn it off.
    /// </summary>
    protected virtual bool SupportsDataMode => false;

    protected virtual IEnumerable<byte[]> GetSetModeCommands(string mode)
    {
        if (SupportsDataMode)
            return IcomCivCodec.EncodeSetModeWithDataCommands(mode);

        return IcomCivCodec.EncodeSetModeCommand(mode) is { } cmd ? [cmd] : [];
    }

    public virtual void SetSplitOn(bool on) =>
        SendWithAckRetry(on ? [0x0F, 0x01] : [0x0F, 0x00], on ? "split on" : "split off");

    public abstract void SetSatelliteMode(bool on);

    public void ExchangeVfos()
    {
        if (_transport is null || !IsConnected)
            return;

        // Exchange is a toggle — send once only; a retry would swap twice and undo it.
        var response = _transport.WriteCommand([0x07, 0xB0], _catDelayMs);
        if (!IsCivAck(response))
        {
            Log.Warning("CI-V VFO exchange failed");
            return;
        }

        _currentVfo = _currentVfo switch
        {
            RigVfo.Main => RigVfo.Sub,
            RigVfo.Sub => RigVfo.Main,
            RigVfo.VfoA => RigVfo.VfoB,
            _ => RigVfo.VfoA
        };
        (_lastMainHz, _lastSubHz) = (_lastSubHz, _lastMainHz);
        (_lastVfoAFreq, _lastVfoBFreq) = (_lastVfoBFreq, _lastVfoAFreq);
        Thread.Sleep(150);
    }

    private long CachedFrequencyHz(RigVfo vfo) => vfo switch
    {
        RigVfo.Main => _lastMainHz,
        RigVfo.Sub => _lastSubHz,
        RigVfo.VfoA => _lastVfoAFreq,
        RigVfo.VfoB => _lastVfoBFreq,
        _ => 0
    };

    private void StoreFrequencyHz(RigVfo vfo, long hz)
    {
        switch (vfo)
        {
            case RigVfo.Main:
                _lastMainHz = hz;
                break;
            case RigVfo.Sub:
                _lastSubHz = hz;
                break;
            case RigVfo.VfoA:
                _lastVfoAFreq = hz;
                break;
            case RigVfo.VfoB:
                _lastVfoBFreq = hz;
                break;
        }
    }

    public virtual void SetToneOn(bool on) =>
        SendWithAckRetry(on ? [0x16, 0x42, 0x01] : [0x16, 0x42, 0x00], on ? "tone on" : "tone off");

    public virtual void SetToneSquelchOn(bool on) =>
        SendWithAckRetry(on ? [0x16, 0x43, 0x01] : [0x16, 0x43, 0x00], on ? "tone squelch on" : "tone squelch off");

    public virtual void SetToneHz(double hz, bool squelchTone)
    {
        if (_transport is null || !IsConnected)
            return;

        SendWithAckRetry(IcomCivCodec.EncodeToneHz(hz, squelchTone), "set tone");
    }

    public bool SupportsCatPtt => true;

    public bool SupportsRfPowerRead => true;

    public bool TryReadRfPowerLevel(out int level0To255)
    {
        level0To255 = 0;
        if (_transport is null || !IsConnected)
            return false;

        Thread.Sleep(_catDelayMs);
        var response = _transport.WriteCommand(IcomCivCodec.EncodeReadRfPowerCommand(), _catDelayMs);
        var level = IcomCivCodec.DecodeLevel255FromResponse(response);
        if (level is null)
            return false;

        level0To255 = level.Value;
        return true;
    }

    public bool SupportsRfPowerWrite => true;

    public bool TrySetRfPowerLevel(int level0To255)
    {
        if (_transport is null || !IsConnected)
            return false;

        return SendWithAckRetry(IcomCivCodec.EncodeWriteRfPowerCommand(level0To255), "set RF power");
    }

    public void SetPtt(bool transmit) =>
        SendWithAckRetry(
            transmit ? [0x1C, 0x00, 0x01] : [0x1C, 0x00, 0x00],
            transmit ? "PTT on" : "PTT off");

    public bool TrySetHandshakeLine(bool useRts, bool assert)
    {
        if (_transport is null || !IsConnected || _transportFactory is not null)
            return false;
        _transport.SetHandshakeLine(useRts, assert);
        return true;
    }

    public bool SupportsCwText => IcomCwCodec.SupportsTextKeying(RigType);

    public bool TrySendCwText(CwMessage piece)
    {
        if (!SupportsCwText || _transport is null || !IsConnected || piece.IsEmpty)
            return false;

        // Sent once: a retry after a lost ACK would key the text twice.
        var response = _transport.WriteCommand(IcomCwCodec.BuildSendCommand(piece), _catDelayMs);
        if (IsCivNak(response))
        {
            Log.Warning("CI-V CW text rejected (0xFA); is the radio in CW mode?");
            return false;
        }

        return true;
    }

    public bool TryStopCwText()
    {
        if (!SupportsCwText || _transport is null || !IsConnected)
            return false;

        var response = _transport.WriteCommand(IcomCwCodec.BuildStopCommand(), _catDelayMs);
        return !IsCivNak(response);
    }

    public bool TrySetCwSpeed(int wpm)
    {
        if (!SupportsCwText || _transport is null || !IsConnected)
            return false;

        return SendWithAckRetry(IcomCwCodec.BuildSpeedCommand(wpm), "set key speed");
    }

    protected void WriteWithRetry(ReadOnlySpan<byte> body) =>
        SendWithAckRetry(body, "CI-V command");

    private bool SendWithAckRetry(ReadOnlySpan<byte> body, string description)
    {
        if (_transport is null || !IsConnected)
            return false;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(_catDelayMs);

            var response = _transport.WriteCommand(body, _catDelayMs);
            if (IsCivAck(response))
                return true;

            if (IsCivNak(response))
                Log.Debug("CI-V {Description} NAK (0xFA), attempt {Attempt}", description, attempt + 1);
            else
                Log.Debug("CI-V {Description} no ACK, attempt {Attempt}", description, attempt + 1);
        }

        Log.Warning("CI-V {Description} failed after retry", description);
        return false;
    }

    private static bool IsCivAck(ReadOnlySpan<byte> response) =>
        response.Length > 0 && response.Contains((byte)0xFB);

    private static bool IsCivNak(ReadOnlySpan<byte> response) =>
        response.Length > 0 && response.Contains((byte)0xFA);

    public void Dispose()
    {
        if (_injectedTransport is null)
        {
            _transport?.Dispose();
            _transport = null;
        }
    }
}
