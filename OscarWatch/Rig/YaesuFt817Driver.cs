using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using Serilog;

namespace OscarWatch.Rig;

/// <summary>
/// Yaesu FT-817 / FT-818 CAT driver (dual-radio endpoints only — one VFO per physical radio).
/// </summary>
public class YaesuFt817Driver : IRigDriver
{
    private static readonly ILogger Log = Serilog.Log.ForContext<YaesuFt817Driver>();

    private readonly IYaesuCatTransport _transport;
    private readonly RigType _rigType;
    private readonly RigRegion _region;
    private readonly int _catDelayMs;
    private bool _splitOn;
    private bool _onVfoB;
    private RigVfo _currentVfo = RigVfo.Main;
    private long _lastMainHz;
    private long _lastSubHz;
    private long _lastVfoAHz;
    private long _lastVfoBHz;

    public YaesuFt817Driver(
        RigType rigType,
        string port,
        int baudRate,
        RigRegion region = RigRegion.EU,
        int catDelayMs = 50)
        : this(rigType, new YaesuCatTransport(port, baudRate), region, catDelayMs)
    {
    }

    internal YaesuFt817Driver(
        RigType rigType,
        IYaesuCatTransport transport,
        RigRegion region = RigRegion.EU,
        int catDelayMs = 50)
    {
        if (rigType is not (RigType.YaesuFt817 or RigType.YaesuFt818 or RigType.YaesuFt857))
            throw new ArgumentOutOfRangeException(nameof(rigType));

        _rigType = rigType;
        _transport = transport;
        _region = region;
        _catDelayMs = catDelayMs;
    }

    public RigType RigType => _rigType;
    public bool IsConnected => _transport.IsOpen;

    public bool TryConfirmIdentity()
    {
        if (!_transport.IsOpen)
            return false;

        var response = _transport.QueryFrame(YaesuFt817CatCodec.PollFreqMode, _catDelayMs);
        if (response is null || response.Length < 5)
            return false;

        var hz = YaesuFt817CatCodec.DecodeFrequency10Hz(response);
        return hz is >= 100_000 and <= 470_000_000;
    }
    public bool SupportsTracking => true;
    public bool SupportsVfoExchange => false;

    public void Open()
    {
        _transport.Open();
        SetDialLock(false);
        _onVfoB = false;
    }

    public long? ReadFrequencyHz(RigVfo vfo)
    {
        var wantB = IsVfoB(vfo);
        var cached = CachedFrequencyHz(vfo);
        if (!_transport.IsOpen)
            return cached > 0 ? cached : null;

        EnsureRadioVfo(wantB);
        var response = _transport.QueryFrame(YaesuFt817CatCodec.PollFreqMode, _catDelayMs);
        if (response is null)
            return cached > 0 ? cached : null;

        var hz = YaesuFt817CatCodec.DecodeFrequency10Hz(response);
        if (hz > 0)
            StoreFrequencyHz(vfo, hz);
        return hz > 0 ? hz : cached > 0 ? cached : null;
    }

    public bool SetFrequencyHz(long hz)
    {
        var rounded = RoundToCatHz(hz);
        StoreFrequencyHz(_currentVfo, rounded);

        if (!_transport.IsOpen)
            return true;

        EnsureRadioVfo(IsVfoB(_currentVfo));
        var cmd = YaesuFt817CatCodec.BuildSetFrequencyCommand(rounded);
        return _transport.SendFrame(cmd, _catDelayMs);
    }

    public void SelectVfo(RigVfo vfo, bool force = false)
    {
        _currentVfo = vfo;
        if (!_transport.IsOpen)
            return;

        EnsureRadioVfo(IsVfoB(vfo));
    }

    public void SetMode(string mode)
    {
        if (!_transport.IsOpen)
            return;

        EnsureRadioVfo(IsVfoB(_currentVfo));
        var cmd = YaesuFt817CatCodec.BuildSetModeCommand(mode);
        _transport.SendFrame(cmd, _catDelayMs);
        SetDialLock(YaesuFt817CatCodec.IsFmMode(mode));
    }

    /// <summary>Front-panel/dial lock (CAT 0x00 on / 0x80 off). FM tracking may lock; linear needs the knob free.</summary>
    public void SetDialLock(bool on)
    {
        if (!_transport.IsOpen)
            return;

        _transport.SendFrame(
            on ? YaesuFt817CatCodec.DialLockOn : YaesuFt817CatCodec.DialLockOff,
            _catDelayMs);
    }

    public void SetSplitOn(bool on)
    {
        _splitOn = on;
        if (!_transport.IsOpen)
            return;

        _transport.SendFrame(on ? YaesuFt817CatCodec.SplitOn : YaesuFt817CatCodec.SplitOff, _catDelayMs);
    }

    public void SetSatelliteMode(bool on)
    {
        // FT-817/818 have no satellite CAT mode.
    }

    public void ExchangeVfos()
    {
        // Use front-panel A/B or split; not available over CAT.
    }

    public void SetToneOn(bool on) => SetCtcssEnable(on, encoderOnly: true);

    public void SetToneSquelchOn(bool on) => SetCtcssEnable(on, encoderOnly: false);

    public void SetToneHz(double hz, bool squelchTone)
    {
        if (!_transport.IsOpen)
            return;

        if (!YaesuFt817CatCodec.IsSupportedCtcssTone(hz))
        {
            Log.Warning("{RigType} does not support CTCSS {Hz} Hz", _rigType, hz);
            return;
        }

        EnsureRadioVfo(IsVfoB(_currentVfo));
        var cmd = YaesuFt817CatCodec.BuildCtcssFrequencyCommand(hz);
        _transport.SendFrame(cmd, _catDelayMs);
    }

    public void Dispose()
    {
        try
        {
            if (_transport.IsOpen)
                SetDialLock(false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "{RigType} CAT off failed", _rigType);
        }

        _transport.Dispose();
    }

    private void SetCtcssEnable(bool on, bool encoderOnly)
    {
        if (!_transport.IsOpen)
            return;

        EnsureRadioVfo(IsVfoB(_currentVfo));
        var cmd = on
            ? YaesuFt817CatCodec.BuildCtcssOnCommand(encoderOnly)
            : YaesuFt817CatCodec.BuildCtcssOffCommand();
        _transport.SendFrame(cmd, _catDelayMs);
    }

    private void EnsureRadioVfo(bool wantB)
    {
        if (_onVfoB == wantB)
            return;

        _transport.SendFrame(YaesuFt817CatCodec.ToggleVfo, _catDelayMs);
        _onVfoB = wantB;
    }

    private static bool IsVfoB(RigVfo vfo) =>
        vfo is RigVfo.Sub or RigVfo.VfoB;

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
            case RigVfo.VfoA:
                _lastMainHz = hz;
                _lastVfoAHz = hz;
                break;
            case RigVfo.Sub:
            case RigVfo.VfoB:
                _lastSubHz = hz;
                _lastVfoBHz = hz;
                break;
        }
    }

    private static long RoundToCatHz(long hz) => (long)(Math.Round(hz / 10.0) * 10);
}
