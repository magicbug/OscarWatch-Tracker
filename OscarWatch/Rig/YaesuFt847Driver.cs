using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using Serilog;

namespace OscarWatch.Rig;

/// <summary>
/// Yaesu FT-847 CAT driver for satellite doppler (SAT RX / SAT TX VFOs).
/// Two-way CAT firmware required; VFO A/B swap is not available over CAT.
/// </summary>
public sealed class YaesuFt847Driver : IRigDriver
{
    private static readonly ILogger Log = Serilog.Log.ForContext<YaesuFt847Driver>();

    private readonly IYaesuCatTransport _transport;
    private readonly int _catDelayMs;
    private bool _satelliteMode;
    private RigVfo _currentVfo = RigVfo.Main;
    private long _lastMainHz;
    private long _lastSubHz;
    private long _lastVfoAHz;
    private long _lastVfoBHz;

    public YaesuFt847Driver(string port, int baudRate, int catDelayMs = 50)
        : this(new YaesuCatTransport(port, baudRate), catDelayMs)
    {
    }

    internal YaesuFt847Driver(IYaesuCatTransport transport, int catDelayMs = 50)
    {
        _transport = transport;
        _catDelayMs = catDelayMs;
    }

    public RigType RigType => RigType.YaesuFt847;
    public bool IsConnected => _transport.IsOpen;

    public bool TryConfirmIdentity()
    {
        if (!_transport.IsOpen)
            return false;

        // CAT ON is sent by Open. This poll is the proof something answered as an FT-847.
        var response = _transport.QueryFrame(YaesuFt847CatCodec.PollMainFreqMode, _catDelayMs);
        if (response is null || response.Length < 5)
            return false;

        var hz = YaesuFt847CatCodec.DecodeFrequency10Hz(response);
        return hz is >= 100_000 and <= 470_000_000;
    }
    public bool SupportsTracking => true;
    public bool SupportsVfoExchange => false;

    public void Open()
    {
        _transport.Open();
        _transport.SendFrame(YaesuFt847CatCodec.CatOn, _catDelayMs);
    }

    public long? ReadFrequencyHz(RigVfo vfo)
    {
        var target = ResolveTarget(vfo, uplink: vfo is RigVfo.Sub or RigVfo.VfoB);
        var cached = CachedFrequencyHz(vfo);
        if (!_transport.IsOpen)
            return cached > 0 ? cached : null;

        var poll = YaesuFt847CatCodec.BuildPollCommand(target, _satelliteMode);
        var response = _transport.QueryFrame(poll, _catDelayMs);
        if (response is null)
            return cached > 0 ? cached : null;

        var hz = YaesuFt847CatCodec.DecodeFrequency10Hz(response);
        if (hz > 0)
            StoreFrequencyHz(vfo, hz);
        return hz > 0 ? hz : cached > 0 ? cached : null;
    }

    public bool SetFrequencyHz(long hz)
    {
        var rounded = RoundToCatHz(hz);
        var uplink = _currentVfo is RigVfo.Sub or RigVfo.VfoB;
        StoreFrequencyHz(_currentVfo, rounded);

        if (!_transport.IsOpen)
            return true;

        var target = ResolveTarget(_currentVfo, uplink);
        var cmd = YaesuFt847CatCodec.BuildSetFrequencyCommand(rounded, target, _satelliteMode);
        return _transport.SendFrame(cmd, _catDelayMs);
    }

    public void SelectVfo(RigVfo vfo, bool force = false) => _currentVfo = vfo;

    public void SetMode(string mode)
    {
        if (!_transport.IsOpen)
            return;

        var uplink = _currentVfo is RigVfo.Sub or RigVfo.VfoB;
        var target = ResolveTarget(_currentVfo, uplink);
        var cmd = YaesuFt847CatCodec.BuildSetModeCommand(mode, target, _satelliteMode);
        _transport.SendFrame(cmd, _catDelayMs);
    }

    public void SetSplitOn(bool on)
    {
        // Satellite layout uses SAT mode instead of split.
    }

    public void SetSatelliteMode(bool on)
    {
        _satelliteMode = on;
        if (!_transport.IsOpen)
            return;

        _transport.SendFrame(
            on ? YaesuFt847CatCodec.SatelliteModeOn : YaesuFt847CatCodec.SatelliteModeOff,
            _catDelayMs);
    }

    public void ExchangeVfos()
    {
        // FT-847 cannot swap VFOs via CAT — use the front-panel A/B control.
    }

    public void SetToneOn(bool on)
    {
        SetCtcssEnable(on, encoderOnly: true);
    }

    public void SetToneSquelchOn(bool on)
    {
        SetCtcssEnable(on, encoderOnly: false);
    }

    public void SetToneHz(double hz, bool squelchTone)
    {
        if (!_transport.IsOpen)
            return;

        if (!YaesuFt847CatCodec.TryGetCtcssCatCode(hz, out _))
        {
            Log.Warning("FT-847 does not support CTCSS {Hz} Hz", hz);
            return;
        }

        var target = YaesuFt847VfoTarget.SatTx;
        var cmd = YaesuFt847CatCodec.BuildCtcssFrequencyCommand(hz, target, _satelliteMode);
        _transport.SendFrame(cmd, _catDelayMs);
    }

    public void Dispose()
    {
        try
        {
            if (_transport.IsOpen)
                _transport.SendFrame(YaesuFt847CatCodec.CatOff, _catDelayMs);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "FT-847 CAT off failed");
        }

        _transport.Dispose();
    }

    private void SetCtcssEnable(bool on, bool encoderOnly)
    {
        if (!_transport.IsOpen)
            return;

        var target = YaesuFt847VfoTarget.SatTx;
        var cmd = on
            ? YaesuFt847CatCodec.BuildCtcssOnCommand(encoderOnly, target, _satelliteMode)
            : YaesuFt847CatCodec.BuildCtcssOffCommand(target, _satelliteMode);
        _transport.SendFrame(cmd, _catDelayMs);
    }

    private YaesuFt847VfoTarget ResolveTarget(RigVfo vfo, bool uplink)
    {
        if (_satelliteMode)
            return uplink ? YaesuFt847VfoTarget.SatTx : YaesuFt847VfoTarget.SatRx;

        return vfo switch
        {
            RigVfo.Sub or RigVfo.VfoB => YaesuFt847VfoTarget.SatTx,
            _ => YaesuFt847VfoTarget.Main
        };
    }

    private long CachedFrequencyHz(RigVfo vfo) => vfo switch
    {
        RigVfo.Main => _lastMainHz,
        RigVfo.Sub => _lastSubHz,
        RigVfo.VfoA => _lastVfoAHz,
        RigVfo.VfoB => _lastVfoBHz,
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
                _lastVfoAHz = hz;
                break;
            case RigVfo.VfoB:
                _lastVfoBHz = hz;
                break;
        }
    }

    private static long RoundToCatHz(long hz) => (long)(Math.Round(hz / 10.0) * 10);
}
