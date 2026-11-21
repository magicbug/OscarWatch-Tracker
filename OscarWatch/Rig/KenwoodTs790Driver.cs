using System.IO.Ports;
using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using Serilog;

namespace OscarWatch.Rig;

/// <summary>
/// Kenwood TS-790A/E CAT driver for cross-band full-duplex Doppler.
/// OscarWatch Main (downlink) maps to radio SUB; Sub (uplink) maps to radio MAIN.
/// </summary>
public sealed class KenwoodTs790Driver : IRigDriver
{
    private static readonly ILogger Log = Serilog.Log.ForContext<KenwoodTs790Driver>();

    private readonly IKenwoodCatTransport _transport;
    private readonly int _catDelayMs;
    private bool _satelliteTracking;
    private bool _destinationIsSub;
    private bool _destinationKnown;
    private bool _ensuredVfoA;
    private RigVfo _currentVfo = RigVfo.Main;
    private long _lastMainHz;
    private long _lastSubHz;

    public KenwoodTs790Driver(
        string port,
        int baudRate,
        int catDelayMs = 50,
        bool hardwareRtsEnabled = true)
        : this(
            new KenwoodCatTransport(port, baudRate, hardwareRtsEnabled, StopBits.Two),
            catDelayMs)
    {
    }

    internal KenwoodTs790Driver(IKenwoodCatTransport transport, int catDelayMs = 50)
    {
        _transport = transport;
        _catDelayMs = catDelayMs;
    }

    public RigType RigType => RigType.KenwoodTs790;
    public bool IsConnected => _transport.IsOpen;
    public bool SupportsTracking => true;
    public bool IsSatelliteModeActive => _satelliteTracking;
    public bool SupportsVfoExchange => false;

    public void Open() => _transport.Open();

    public bool TryConfirmIdentity()
    {
        if (!_transport.IsOpen)
            return false;

        var reply = _transport.Transact(KenwoodTs790CatCodec.BuildIdentityQuery(), _catDelayMs);
        if (reply is not null && KenwoodTs790CatCodec.IsTs790Identity(reply))
            return true;

        // Some interfaces answer slowly; a MAIN frequency read still proves a Kenwood link.
        EnsureDestination(sub: false, force: true);
        EnsureVfoA();
        reply = _transport.Transact(KenwoodTs790CatCodec.BuildReadFrequencyCommand(), _catDelayMs);
        return KenwoodCatCodec.TryParseFrequencyHz(reply ?? "", out var hz)
            && hz is >= 100_000 and <= 1_300_000_000;
    }

    public long? ReadFrequencyHz(RigVfo vfo)
    {
        var cached = CachedFrequencyHz(vfo);
        if (!_transport.IsOpen)
            return cached > 0 ? cached : null;

        EnsureDestination(IsSubSide(vfo), force: true);
        EnsureVfoA();
        var reply = _transport.Transact(KenwoodTs790CatCodec.BuildReadFrequencyCommand(), _catDelayMs);
        if (reply is null || !KenwoodCatCodec.TryParseFrequencyHz(reply, out var hz) || hz <= 0)
            return cached > 0 ? cached : null;

        StoreFrequencyHz(vfo, hz);
        return hz;
    }

    public bool SetFrequencyHz(long hz)
    {
        if (hz < 0)
            return false;

        StoreFrequencyHz(_currentVfo, hz);
        if (!_transport.IsOpen)
            return true;

        EnsureDestination(IsSubSide(_currentVfo));
        EnsureVfoA();
        return _transport.SendFireAndForget(KenwoodTs790CatCodec.BuildSetFrequencyCommand(hz), _catDelayMs);
    }

    /// <summary>
    /// Pass setup with band-collision ordering: MAIN (uplink) first on a full swap, then SUB (downlink).
    /// Retries the SUB write once when readback stays on the old band.
    /// </summary>
    public bool ApplySatellitePassFrequencies(long downlinkHz, long uplinkHz)
    {
        if (!_transport.IsOpen || downlinkHz <= 0 || uplinkHz <= 0)
            return false;

        if (!RigSatModeHelper.AreDifferentMainSubBands(downlinkHz / 1000.0, uplinkHz / 1000.0))
        {
            Log.Warning(
                "TS-790 cannot track same-band passes over CAT (MAIN and SUB must be different bands)");
            return false;
        }

        _lastMainHz = downlinkHz;
        _lastSubHz = uplinkHz;
        _satelliteTracking = true;

        var mainHz = ReadSideHz(sub: false);
        var subHz = ReadSideHz(sub: true);

        var uplinkCollidesWithSub = subHz is > 0
            && RigSatModeHelper.AreDifferentMainSubBands(uplinkHz / 1000.0, subHz.Value / 1000.0) == false
            && RigSatModeHelper.AreDifferentMainSubBands(uplinkHz / 1000.0, downlinkHz / 1000.0);
        var downlinkCollidesWithMain = mainHz is > 0
            && RigSatModeHelper.AreDifferentMainSubBands(downlinkHz / 1000.0, mainHz.Value / 1000.0) == false
            && RigSatModeHelper.AreDifferentMainSubBands(downlinkHz / 1000.0, uplinkHz / 1000.0);
        var fullSwap = uplinkCollidesWithSub && downlinkCollidesWithMain;

        bool uplinkOk;
        bool downlinkOk;
        if (fullSwap || uplinkCollidesWithSub)
        {
            // Free SUB by moving MAIN (TX) first, then write downlink to SUB.
            uplinkOk = WriteSide(sub: false, uplinkHz);
            downlinkOk = WriteSide(sub: true, downlinkHz);
            if (!downlinkOk || SideStillOnWrongBand(sub: true, downlinkHz))
                downlinkOk = WriteSide(sub: true, downlinkHz);
        }
        else if (downlinkCollidesWithMain)
        {
            downlinkOk = WriteSide(sub: true, downlinkHz);
            uplinkOk = WriteSide(sub: false, uplinkHz);
        }
        else
        {
            // No collision: either order works; match OscarWatch Main-then-Sub (SUB then MAIN on radio).
            downlinkOk = WriteSide(sub: true, downlinkHz);
            uplinkOk = WriteSide(sub: false, uplinkHz);
        }

        _currentVfo = RigVfo.Main;
        EnsureDestination(sub: true);
        return downlinkOk && uplinkOk;
    }

    /// <summary>Beacon / receive-only: downlink on SUB only.</summary>
    public bool ApplySatelliteBeaconPassFrequencies(long downlinkHz)
    {
        if (!_transport.IsOpen || downlinkHz <= 0)
            return false;

        _lastMainHz = downlinkHz;
        _satelliteTracking = true;
        var ok = WriteSide(sub: true, downlinkHz);
        _currentVfo = RigVfo.Main;
        EnsureDestination(sub: true);
        return ok;
    }

    public void SelectVfo(RigVfo vfo, bool force = false)
    {
        if (_currentVfo == vfo && !force)
            return;

        _currentVfo = vfo;
        if (!_transport.IsOpen)
            return;

        EnsureDestination(IsSubSide(vfo), force);
        EnsureVfoA();
    }

    public void SetMode(string mode)
    {
        if (!_transport.IsOpen)
            return;

        if (!KenwoodCatCodec.TryGetModeCode(mode, out var modeCode))
        {
            Log.Warning("TS-790 unsupported mode {Mode}", mode);
            return;
        }

        EnsureDestination(IsSubSide(_currentVfo));
        EnsureVfoA();
        _transport.SendFireAndForget(KenwoodTs790CatCodec.BuildSetModeCommand(modeCode), _catDelayMs);
    }

    public void SetSplitOn(bool on)
    {
        if (!on)
            return;

        // Same-band split cannot be turned on over CAT for full-duplex satellite work.
        Log.Warning("TS-790 refuses CAT split for same-band passes; use a cross-band mode");
    }

    public void SetSatelliteMode(bool on)
    {
        // No SA command: independent MAIN/SUB is the satellite layout.
        _satelliteTracking = on;
        if (!on)
            return;

        if (_transport.IsOpen)
        {
            EnsureDestination(sub: true, force: true);
            EnsureVfoA();
        }
    }

    public void ExchangeVfos()
    {
        // TX cannot move to SUB on this radio.
    }

    public void SetToneOn(bool on)
    {
        if (!_transport.IsOpen)
            return;

        EnsureDestination(IsSubSide(_currentVfo));
        _transport.SendFireAndForget(KenwoodTs790CatCodec.BuildToneEnableCommand(on), _catDelayMs);
    }

    public void SetToneSquelchOn(bool on)
    {
        if (!_transport.IsOpen)
            return;

        EnsureDestination(IsSubSide(_currentVfo));
        _transport.SendFireAndForget(KenwoodTs790CatCodec.BuildCtcssEnableCommand(on), _catDelayMs);
    }

    public void SetToneHz(double hz, bool squelchTone)
    {
        if (!_transport.IsOpen || !KenwoodCatCodec.TryGetCtcssIndex(hz, out var index))
            return;

        EnsureDestination(IsSubSide(_currentVfo));
        var cmd = squelchTone
            ? KenwoodTs790CatCodec.BuildCtcssFrequencyCommand(index)
            : KenwoodTs790CatCodec.BuildToneFrequencyCommand(index);
        _transport.SendFireAndForget(cmd, _catDelayMs);
    }

    public void Dispose() => _transport.Dispose();

    private bool WriteSide(bool sub, long hz)
    {
        EnsureDestination(sub, force: true);
        EnsureVfoA();
        var ok = _transport.SendFireAndForget(KenwoodTs790CatCodec.BuildSetFrequencyCommand(hz), _catDelayMs);
        if (ok)
        {
            if (sub)
                _lastMainHz = hz;
            else
                _lastSubHz = hz;
        }

        return ok;
    }

    private long? ReadSideHz(bool sub)
    {
        EnsureDestination(sub, force: true);
        EnsureVfoA();
        var reply = _transport.Transact(KenwoodTs790CatCodec.BuildReadFrequencyCommand(), _catDelayMs);
        if (reply is null || !KenwoodCatCodec.TryParseFrequencyHz(reply, out var hz) || hz <= 0)
            return null;

        if (sub)
            _lastMainHz = hz;
        else
            _lastSubHz = hz;

        return hz;
    }

    private bool SideStillOnWrongBand(bool sub, long targetHz)
    {
        var read = ReadSideHz(sub);
        return read is > 0
            && RigSatModeHelper.AreDifferentMainSubBands(read.Value / 1000.0, targetHz / 1000.0);
    }

    private void EnsureDestination(bool sub, bool force = false)
    {
        if (_destinationKnown && _destinationIsSub == sub && !force)
            return;

        var cmd = sub
            ? KenwoodTs790CatCodec.BuildSelectSubCommand()
            : KenwoodTs790CatCodec.BuildSelectMainCommand();
        if (_transport.SendFireAndForget(cmd, _catDelayMs))
        {
            _destinationIsSub = sub;
            _destinationKnown = true;
        }
    }

    private void EnsureVfoA()
    {
        if (_ensuredVfoA || !_transport.IsOpen)
            return;

        if (_transport.SendFireAndForget(KenwoodTs790CatCodec.BuildSelectVfoACommand(), _catDelayMs))
            _ensuredVfoA = true;
    }

    /// <summary>OscarWatch Main (RX) is radio SUB; Sub (TX) is radio MAIN.</summary>
    private static bool IsSubSide(RigVfo vfo) => vfo is RigVfo.Main or RigVfo.VfoA;

    private long CachedFrequencyHz(RigVfo vfo) => vfo switch
    {
        RigVfo.Main or RigVfo.VfoA => _lastMainHz,
        RigVfo.Sub or RigVfo.VfoB => _lastSubHz,
        _ => 0
    };

    private void StoreFrequencyHz(RigVfo vfo, long hz)
    {
        switch (vfo)
        {
            case RigVfo.Main:
            case RigVfo.VfoA:
                _lastMainHz = hz;
                break;
            case RigVfo.Sub:
            case RigVfo.VfoB:
                _lastSubHz = hz;
                break;
        }
    }
}
