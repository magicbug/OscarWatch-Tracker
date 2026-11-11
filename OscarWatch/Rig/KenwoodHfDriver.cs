using OscarWatch.Core.Cw;
using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using Serilog;

namespace OscarWatch.Rig;

/// <summary>
/// Kenwood TS-990S, TS-890S, TS-590S and TS-590SG as one dual-radio leg.
/// Tunes main / VFO A only. With transverter mode on, the radio's own <c>XO</c> offset is applied so
/// OscarWatch works in dial (sky) frequencies while <c>FA</c> stays on the HF IF.
/// </summary>
public sealed class KenwoodHfDriver : IRigDriver
{
    private static readonly ILogger Log = Serilog.Log.ForContext<KenwoodHfDriver>();

    private readonly KenwoodHfProfile _profile;
    private readonly IKenwoodCatTransport _transport;
    private readonly int _catDelayMs;
    private bool _sessionReady;
    private bool _transverterOn;
    private bool _transverterMinus;
    private long _transverterOffsetHz;
    private long _lastSkyHz;

    public KenwoodHfDriver(RigType rigType, string port, int baudRate, int catDelayMs = 50, bool hardwareRtsEnabled = true)
        : this(rigType, new KenwoodCatTransport(port, baudRate, hardwareRtsEnabled), catDelayMs)
    {
    }

    internal KenwoodHfDriver(RigType rigType, IKenwoodCatTransport transport, int catDelayMs = 50)
    {
        _profile = KenwoodHfCatCodec.GetProfile(rigType);
        _transport = transport;
        _catDelayMs = catDelayMs;
    }

    public bool IsConnected => _transport.IsOpen;
    public RigType RigType => _profile.Type;
    public bool SupportsTracking => true;
    public bool SupportsVfoExchange => false;

    /// <summary>True when the last transverter read reported transverter mode on.</summary>
    internal bool TransverterOn => _transverterOn;

    internal long TransverterOffsetHz => _transverterOffsetHz;

    public void Open()
    {
        _transport.Open();
        _sessionReady = false;
        _transport.SendCommand(KenwoodHfCatCodec.BuildAutoInfoOffCommand(), Math.Max(20, _catDelayMs));
    }

    public bool TryConfirmIdentity()
    {
        var reply = _transport.Transact(KenwoodHfCatCodec.BuildIdQuery(), Math.Max(100, _catDelayMs));
        if (reply is null || !KenwoodHfCatCodec.TryParseId(reply, out var id))
        {
            Log.Warning("{RigType} did not answer ID;", _profile.Type);
            return false;
        }

        if (id == _profile.ExpectedId)
            return true;

        Log.Warning("{RigType} expected ID{Expected} but the radio answered ID{Actual}", _profile.Type, _profile.ExpectedId, id);
        return false;
    }

    public long? ReadFrequencyHz(RigVfo vfo)
    {
        if (!_transport.IsOpen)
            return _lastSkyHz > 0 ? _lastSkyHz : null;

        EnsureSession();
        var reply = _transport.Transact(KenwoodHfCatCodec.BuildReadFrequencyCommand(), _catDelayMs);
        if (reply is not null && KenwoodHfCatCodec.TryParseFrequencyHz(reply, out var vfoHz) && vfoHz > 0)
        {
            _lastSkyHz = KenwoodHfCatCodec.VfoToSkyHz(vfoHz, _transverterOn, _transverterMinus, _transverterOffsetHz);
            return _lastSkyHz;
        }

        return _lastSkyHz > 0 ? _lastSkyHz : null;
    }

    public bool SetFrequencyHz(long hz)
    {
        if (hz <= 0)
            return false;

        if (!_transport.IsOpen)
        {
            _lastSkyHz = hz;
            return true;
        }

        if (!EnsureSession())
            return false;

        if (TryWriteSkyFrequency(hz))
            return true;

        // The operator may have changed transverter mode or the offset since pass start.
        RefreshTransverter();
        return TryWriteSkyFrequency(hz);
    }

    public void SelectVfo(RigVfo vfo, bool force = false)
    {
        // One leg, always main / VFO A. EnsureSession sends the simplex commands once per session.
    }

    public void SetMode(string mode)
    {
        if (!_transport.IsOpen)
            return;

        // SetMode runs at pass start and on mode change, so refresh the session and transverter state here.
        _sessionReady = false;
        if (!EnsureSession())
            return;

        if (!_transport.SendCommand(KenwoodHfCatCodec.BuildSetModeCommand(_profile, mode), _catDelayMs))
            Log.Warning("{RigType} rejected CAT mode {Mode}", _profile.Type, mode);
    }

    public void SetSplitOn(bool on) { }
    public void SetSatelliteMode(bool on) { }
    public void ExchangeVfos() { }

    // Tone stays under operator control; these radios are used for the HF or transverter leg.
    public void SetToneOn(bool on) { }
    public void SetToneSquelchOn(bool on) { }
    public void SetToneHz(double hz, bool squelchTone) { }

    public bool SupportsCwText => true;

    public bool TrySendCwText(CwMessage piece) =>
        _transport.IsOpen && !piece.IsEmpty
        && _transport.SendCommand(KenwoodCwCodec.BuildSendCommand(piece), _catDelayMs);

    public bool TryStopCwText() =>
        _transport.IsOpen && _transport.SendCommand(KenwoodCwCodec.BuildStopCommand(ts2000: false), _catDelayMs);

    public bool TrySetCwSpeed(int wpm) =>
        _transport.IsOpen && _transport.SendCommand(KenwoodCwCodec.BuildSpeedCommand(wpm, ts2000: false), _catDelayMs);

    public void Dispose() => _transport.Dispose();

    private bool TryWriteSkyFrequency(long skyHz)
    {
        if (!KenwoodHfCatCodec.TrySkyToVfoHz(skyHz, _transverterOn, _transverterMinus, _transverterOffsetHz, out var vfoHz))
        {
            Log.Warning(
                "{RigType} cannot tune {SkyHz} Hz with transverter {State} and offset {OffsetHz} Hz",
                _profile.Type,
                skyHz,
                _transverterOn ? "on" : "off",
                _transverterOffsetHz);
            return false;
        }

        if (!_transport.SendCommand(KenwoodHfCatCodec.BuildSetFrequencyCommand(vfoHz), _catDelayMs))
            return false;

        _lastSkyHz = skyHz;
        return true;
    }

    private bool EnsureSession()
    {
        if (_sessionReady)
            return true;

        foreach (var command in KenwoodHfCatCodec.BuildMainSimplexCommands(_profile))
        {
            if (!_transport.SendCommand(command, Math.Max(20, _catDelayMs)))
                Log.Warning("{RigType} rejected {Command}", _profile.Type, command);
        }

        RefreshTransverter();
        _sessionReady = true;
        return true;
    }

    private void RefreshTransverter()
    {
        var stateReply = _transport.Transact(KenwoodHfCatCodec.BuildTransverterStateQuery(_profile), _catDelayMs);
        if (stateReply is null || !KenwoodHfCatCodec.TryParseTransverterOn(_profile, stateReply, out var on))
        {
            Log.Debug("{RigType} transverter state unreadable; keeping {State}", _profile.Type, _transverterOn);
            return;
        }

        if (!on)
        {
            _transverterOn = false;
            return;
        }

        var offsetReply = _transport.Transact(KenwoodHfCatCodec.BuildTransverterOffsetQuery(), _catDelayMs);
        if (offsetReply is null
            || !KenwoodHfCatCodec.TryParseTransverterOffset(offsetReply, out var minus, out var offsetHz))
        {
            Log.Warning("{RigType} transverter is on but XO; was unreadable; tuning without an offset", _profile.Type);
            _transverterOn = false;
            return;
        }

        var changed = !_transverterOn || minus != _transverterMinus || offsetHz != _transverterOffsetHz;
        _transverterOn = true;
        _transverterMinus = minus;
        _transverterOffsetHz = offsetHz;
        if (changed)
        {
            Log.Information(
                "{RigType} transverter on, offset {Direction}{OffsetHz} Hz",
                _profile.Type,
                minus ? "-" : "+",
                offsetHz);
        }
    }
}
