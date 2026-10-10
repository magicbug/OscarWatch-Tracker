using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using Serilog;

namespace OscarWatch.Rig;

/// <summary>
/// Kenwood TS-2000 CAT driver for cross-band satellite (SATL) doppler tracking.
/// FA/FB for RX/TX frequencies; SA for main/sub CTRL (no DC/FR in SATL).
/// </summary>
public sealed class KenwoodTs2000Driver : IRigDriver
{
    private static readonly ILogger Log = Serilog.Log.ForContext<KenwoodTs2000Driver>();

    private readonly IKenwoodCatTransport _transport;
    private readonly int _catDelayMs;
    private readonly int _satModeSettlingDelayMs;
    private readonly int _satModeRetryCount;
    private readonly int _satModeRetryDelayMs;
    private readonly int _linkHoldPollIntervalMs;
    private readonly bool _traceEnabled;
    private DateTime _lastLinkHoldPollUtc = DateTime.MinValue;
    private bool _satelliteMode;
    private bool _satelliteLayoutConfirmed;
    /// <summary>True after <see cref="SetSatelliteMode"/>(true); FA/FB doppler works even when SA; did not confirm SATL.</summary>
    private bool _faFbSatelliteTracking;
    private RigVfo _currentVfo = RigVfo.Main;
    private long _lastMainHz;
    private long _lastSubHz;
    private long _lastVfoAHz;
    private long _lastVfoBHz;
    private char? _savedMainVfoSelect;
    private char? _savedSubVfoSelect;

    public KenwoodTs2000Driver(
        string port,
        int baudRate,
        int catDelayMs = 50,
        int satModeSettlingDelayMs = 250,
        int satModeRetryCount = 3,
        int satModeRetryDelayMs = 200,
        bool hardwareRtsEnabled = true,
        bool traceEnabled = true)
        : this(
            new KenwoodCatTransport(port, baudRate, hardwareRtsEnabled),
            catDelayMs,
            satModeSettlingDelayMs,
            satModeRetryCount,
            satModeRetryDelayMs,
            linkHoldPollIntervalMs: null,
            traceEnabled: traceEnabled)
    {
    }

    internal KenwoodTs2000Driver(
        IKenwoodCatTransport transport,
        int catDelayMs = 50,
        int satModeSettlingDelayMs = 250,
        int satModeRetryCount = 3,
        int satModeRetryDelayMs = 200,
        int? linkHoldPollIntervalMs = null,
        bool traceEnabled = true)
    {
        _transport = transport;
        _catDelayMs = catDelayMs;
        _satModeSettlingDelayMs = satModeSettlingDelayMs;
        _satModeRetryCount = satModeRetryCount;
        _satModeRetryDelayMs = satModeRetryDelayMs;
        _linkHoldPollIntervalMs = linkHoldPollIntervalMs ?? KenwoodCatCodec.SatelliteLinkHoldPollIntervalMs;
        _traceEnabled = traceEnabled;
    }

    public RigType RigType => RigType.KenwoodTs2000;
    public bool IsConnected => _transport.IsOpen;
    public bool SupportsTracking => true;
    public bool IsSatelliteModeActive => _satelliteMode;
    /// <summary>True when cross-band FA/FB satellite tracking was requested (SATL confirmed or fallback).</summary>
    public bool UsesFaFbSatelliteTracking => _faFbSatelliteTracking;
    public bool SupportsVfoExchange => true;
    public bool SupportsRfPowerRead => true;

    public void Open()
    {
        _transport.Open();
    }

    public bool TryConfirmIdentity()
    {
        if (!_transport.IsOpen)
            return false;

        var reply = _transport.Transact(KenwoodCatCodec.BuildReadFrequencyCommand('A'), _catDelayMs);
        return KenwoodCatCodec.TryParseFrequencyHz(reply ?? "", out var hz) && hz is >= 100_000 and <= 1_300_000_000;
    }

    public bool TryReadRfPowerWatts(out double watts)
    {
        watts = 0;
        if (!_transport.IsOpen)
            return false;

        var reply = _transport.Transact(KenwoodCatCodec.BuildReadPowerCommand(), _catDelayMs);
        if (reply is null || !KenwoodCatCodec.TryParsePowerWatts(reply, out var value))
            return false;

        watts = value;
        return true;
    }

    public bool SupportsRfPowerWrite => true;

    public bool TrySetRfPowerWatts(double watts)
    {
        if (!_transport.IsOpen || !KenwoodCatCodec.TryBuildSetPowerCommand(watts, out var command))
            return false;

        var reply = _transport.Transact(command, _catDelayMs);
        return !string.IsNullOrEmpty(reply)
            && !reply.Contains('?', StringComparison.Ordinal);
    }

    public long? ReadFrequencyHz(RigVfo vfo)
    {
        var cached = CachedFrequencyHz(vfo);
        if (!_transport.IsOpen)
            return cached > 0 ? cached : null;

        var letter = VfoLetterFor(vfo);
        var reply = _transport.Transact(KenwoodCatCodec.BuildReadFrequencyCommand(letter), _catDelayMs);
        if (reply is null)
            return cached > 0 ? cached : null;

        if (!KenwoodCatCodec.TryParseFrequencyHz(reply, out var hz) || hz <= 0)
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

        if (_faFbSatelliteTracking)
            return false;

        var letter = VfoLetterFor(_currentVfo);
        return _transport.SendFireAndForget(KenwoodCatCodec.BuildSetFrequencyCommand(letter, hz), _catDelayMs);
    }

    /// <summary>
    /// SATL doppler update: FA/FB/SM cluster. Link-hold <c>FA;</c> polls run on a timer (~1/s) from <see cref="RigController"/>.
    /// Returns false if FA/FB fail to write or the radio rejects them with <c>?;</c>/<c>E;</c>.
    /// <c>SM</c> band-select is best-effort — some radios reject it while still accepting FA/FB.
    /// (Sets still do not require a success ACK — silence is OK.)
    /// </summary>
    public bool ApplySatelliteDopplerStep(long downlinkHz, long uplinkHz)
    {
        if (!_faFbSatelliteTracking || !_transport.IsOpen || downlinkHz <= 0 || uplinkHz <= 0)
            return false;

        _lastMainHz = downlinkHz;
        _lastSubHz = uplinkHz;
        _lastVfoAHz = downlinkHz;
        _lastVfoBHz = uplinkHz;

        var vhfSm = KenwoodCatCodec.BuildSatelliteBandSelectSubCommand(downlinkHz);

        // FA/FB are required; SM must not abort the rest of the cluster when rejected.
        var ok =
            SendSet(KenwoodCatCodec.BuildSetFrequencyCommand('A', downlinkHz))
            && SendSet(KenwoodCatCodec.BuildSetFrequencyCommand('B', uplinkHz));
        SendBestEffort(KenwoodCatCodec.BuildSatelliteBandSelectMainCommand());
        ok = SendSet(KenwoodCatCodec.BuildSetFrequencyCommand('A', downlinkHz)) && ok;
        SendBestEffort(vhfSm);
        ok = SendSet(KenwoodCatCodec.BuildSetFrequencyCommand('B', uplinkHz)) && ok;
        SendBestEffort(vhfSm);
        SendBestEffort(KenwoodCatCodec.BuildSatelliteBandSelectMainCommand());

        if (!ok)
            Log.Warning("TS-2000 doppler FA/FB cluster send failed");

        return ok;
    }

    /// <summary>
    /// Beacon / receive-only SATL doppler: update Main (<c>FA</c>) only; leave Sub (<c>FB</c>) parked.
    /// </summary>
    public bool ApplySatelliteBeaconDopplerStep(long downlinkHz)
    {
        if (!_faFbSatelliteTracking || !_transport.IsOpen || downlinkHz <= 0)
            return false;

        _lastMainHz = downlinkHz;
        _lastVfoAHz = downlinkHz;

        var ok = SendSet(KenwoodCatCodec.BuildSetFrequencyCommand('A', downlinkHz));
        if (!ok)
            Log.Warning("TS-2000 beacon FA doppler send failed");

        return ok;
    }

    /// <summary>
    /// Programs pass frequencies after SAT entry: double FA/FB, SM, main/sub finalize (modes, tones).
    /// Mode finalize always runs even when <c>SM</c>/tone clears are rejected — entry handshake leaves
    /// USB/LSB placeholders that must be overwritten with the pass modes (e.g. FM for AO-91).
    /// Does not set RF power — the operator controls power on the radio.
    /// </summary>
    public bool ApplySatellitePassFrequencies(
        long downlinkHz,
        long uplinkHz,
        char downlinkModeCode,
        char uplinkModeCode)
    {
        if (!_faFbSatelliteTracking || !_transport.IsOpen)
            return false;

        _lastMainHz = downlinkHz;
        _lastSubHz = uplinkHz;
        _lastVfoAHz = downlinkHz;
        _lastVfoBHz = uplinkHz;

        // Do not short-circuit on SM/tone failures — modes must still be applied.
        var freqOk = ProgramSatelliteFrequencies(downlinkHz, uplinkHz);
        var mainOk = FinalizeSatelliteMainPath(downlinkModeCode, downlinkHz);
        var subOk = FinalizeSatelliteSubPath(uplinkModeCode, downlinkHz, uplinkHz);
        var wrapOk =
            SendSet(SatelliteModeOnCommand())
            && SendSet(KenwoodCatCodec.BuildAutoinfoOffCommand());

        ForceUplinkPttOnSub();
        var ok = freqOk && mainOk && subOk && wrapOk;
        if (!ok)
            Log.Warning("TS-2000 pass frequency programming send failed");

        return ok;
    }

    /// <summary>
    /// Programs pass frequencies; on FA/FB failure, recover inverted Main/Sub band layout via VFO exchange and SAT re-entry.
    /// </summary>
    public bool ApplySatellitePassFrequenciesWithBandRecovery(
        long downlinkHz,
        long uplinkHz,
        double downlinkKHz,
        char downlinkModeCode,
        char uplinkModeCode)
    {
        var ok = ApplySatellitePassFrequencies(downlinkHz, uplinkHz, downlinkModeCode, uplinkModeCode);
        if (ok)
            return true;

        return TryRecoverSatelliteBandLayout(
            downlinkKHz,
            () => ApplySatellitePassFrequencies(downlinkHz, uplinkHz, downlinkModeCode, uplinkModeCode));
    }

    /// <summary>
    /// Beacon / receive-only pass setup in SATL: Main mode + <c>FA</c> only (Sub / <c>FB</c> untouched).
    /// </summary>
    public bool ApplySatelliteBeaconPassFrequencies(long downlinkHz, char downlinkModeCode)
    {
        if (!_faFbSatelliteTracking || !_transport.IsOpen || downlinkHz <= 0)
            return false;

        _lastMainHz = downlinkHz;
        _lastVfoAHz = downlinkHz;

        var ok =
            SendSet(KenwoodCatCodec.BuildSetFrequencyCommand('A', downlinkHz))
            && SendSet(SatelliteModeOnCommand())
            && SendSet(KenwoodCatCodec.BuildSetModeCommand(downlinkModeCode))
            && SendSet(KenwoodCatCodec.BuildSetFrequencyCommand('A', downlinkHz))
            && SendSet(SatelliteModeOnCommand())
            && SendSet(KenwoodCatCodec.BuildAutoinfoOffCommand());

        if (!ok)
            Log.Warning("TS-2000 beacon pass frequency programming send failed");

        return ok;
    }

    /// <summary>
    /// Beacon pass setup with band-layout recovery when <c>FA</c> programming fails.
    /// </summary>
    public bool ApplySatelliteBeaconPassFrequenciesWithBandRecovery(
        long downlinkHz,
        double downlinkKHz,
        char downlinkModeCode)
    {
        var ok = ApplySatelliteBeaconPassFrequencies(downlinkHz, downlinkModeCode);
        if (ok)
            return true;

        return TryRecoverSatelliteBandLayout(
            downlinkKHz,
            () => ApplySatelliteBeaconPassFrequencies(downlinkHz, downlinkModeCode));
    }

    public void SelectVfo(RigVfo vfo, bool force = false)
    {
        if (_currentVfo == vfo && !force)
            return;

        _currentVfo = vfo;
        // Never send FR while FA/FB satellite tracking is active — including SA;-unconfirmed fallback.
        if (_faFbSatelliteTracking || !_transport.IsOpen)
            return;

        var vfoB = vfo is RigVfo.Sub or RigVfo.VfoB;
        SendSet(KenwoodCatCodec.BuildSelectVfoCommand(vfoB));
    }

    public void SetMode(string mode)
    {
        if (!_transport.IsOpen)
            return;

        if (!KenwoodCatCodec.TryGetModeCode(mode, out var modeCode))
        {
            Log.Warning("TS-2000 unsupported mode {Mode}", mode);
            return;
        }

        if (_faFbSatelliteTracking)
        {
            var sub = _currentVfo is RigVfo.Sub or RigVfo.VfoB;
            if (sub)
            {
                SendSet(SatelliteModeOnSubControlCommand());
                SendSet(KenwoodCatCodec.BuildSetModeCommand(modeCode));
                SendSet(SatelliteModeOnCommand());
            }
            else
            {
                SendSet(SatelliteModeOnCommand());
                SendSet(KenwoodCatCodec.BuildSetModeCommand(modeCode));
            }

            return;
        }

        SendSet(KenwoodCatCodec.BuildSetModeCommand(modeCode));
    }

    public void SetSplitOn(bool on)
    {
        if (!_transport.IsOpen || _faFbSatelliteTracking)
            return;

        if (on)
            _transport.SendFireAndForget("FR0;FT1;", _catDelayMs);
        else
            _transport.SendFireAndForget("FR0;FT0;", _catDelayMs);
    }

    /// <summary>Send one <c>FA;</c> link-hold poll when the SatPC32-style interval has elapsed.</summary>
    public void SendSatelliteLinkHoldPollIfDue()
    {
        if (!_faFbSatelliteTracking || !_transport.IsOpen)
            return;

        if (_linkHoldPollIntervalMs > 0
            && _lastLinkHoldPollUtc != DateTime.MinValue
            && DateTime.UtcNow - _lastLinkHoldPollUtc < TimeSpan.FromMilliseconds(_linkHoldPollIntervalMs))
        {
            return;
        }

        SendSatelliteLinkHoldPollNow();
    }

    /// <summary>Send one <c>FA;</c> link-hold poll immediately (tests and first poll after SAT entry).</summary>
    public void SendSatelliteLinkHoldPollNow()
    {
        if (!_faFbSatelliteTracking || !_transport.IsOpen)
            return;

        _transport.Transact(KenwoodCatCodec.BuildReadFrequencyCommand('A'), _catDelayMs);
        _lastLinkHoldPollUtc = DateTime.UtcNow;
    }

    public void SetSatelliteMode(bool on)
    {
        if (!_transport.IsOpen)
        {
            _satelliteMode = on;
            _satelliteLayoutConfirmed = false;
            _faFbSatelliteTracking = on;
            return;
        }

        if (on)
        {
            var result = TryEnableSatelliteMode();
            _satelliteMode = result;
            _satelliteLayoutConfirmed = result;
            _faFbSatelliteTracking = true;
            if (!result)
            {
                Log.Warning(
                    "TS-2000 SATL not confirmed after {RetryCount} SA; verification attempts; continuing FA/FB tracking.",
                    _satModeRetryCount);
            }

            return;
        }

        _satelliteMode = false;
        _satelliteLayoutConfirmed = false;
        _faFbSatelliteTracking = false;
        _lastLinkHoldPollUtc = DateTime.MinValue;
        SendSatelliteModeExitSequence();
        if (_satModeSettlingDelayMs > 0)
            Thread.Sleep(_satModeSettlingDelayMs);
        RestoreMemoryVfoIfNeeded();
    }

    public void Dispose()
    {
        try
        {
            if (_transport.IsOpen && (_satelliteMode || _faFbSatelliteTracking))
            {
                Log.Information("TS-2000 disposing; exiting satellite tracking before closing CAT");
                SetSatelliteMode(false);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TS-2000 satellite mode exit failed during dispose");
        }
        finally
        {
            _transport.Dispose();
        }
    }

    private void SendSatelliteModeExitSequence()
    {
        foreach (var cmd in KenwoodCatCodec.SatelliteModeExitSequence)
        {
            if (KenwoodCatCodec.IsSatelliteModeExitReadCommand(cmd))
                _transport.Transact(cmd, _catDelayMs);
            else
                _transport.SendFireAndForget(cmd, _catDelayMs);
        }
    }

    private void RestoreMemoryVfoIfNeeded()
    {
        var restored = false;

        // Sub first, main last — on the TS-2000, restoring sub CTRL after main can clear main memory mode.
        if (_savedSubVfoSelect is { } subSelect)
        {
            _transport.SendFireAndForget(KenwoodCatCodec.BuildControlSubReceiverCommand(), _catDelayMs);
            _transport.SendFireAndForget(KenwoodCatCodec.BuildSetVfoSelectCommand(subSelect), _catDelayMs);
            _savedSubVfoSelect = null;
            restored = true;
        }

        if (_savedMainVfoSelect is { } mainSelect)
        {
            _transport.SendFireAndForget(KenwoodCatCodec.BuildControlMainCommand(), _catDelayMs);
            _transport.SendFireAndForget(KenwoodCatCodec.BuildSetVfoSelectCommand(mainSelect), _catDelayMs);
            _savedMainVfoSelect = null;
            restored = true;
        }

        if (restored)
            _transport.SendFireAndForget(KenwoodCatCodec.BuildControlMainCommand(), _catDelayMs);
    }

    private void ExitMemoryModeIfNeeded()
    {
        ExitMemoryModeForReceiver(subReceiver: false);
        ExitMemoryModeForReceiver(subReceiver: true);
    }

    private void ExitMemoryModeForReceiver(bool subReceiver)
    {
        _transport.SendFireAndForget(
            subReceiver
                ? KenwoodCatCodec.BuildControlSubReceiverCommand()
                : KenwoodCatCodec.BuildControlMainCommand(),
            _catDelayMs);

        var reply = _transport.Transact(KenwoodCatCodec.BuildReadVfoSelectCommand(), _catDelayMs);
        if (reply is null
            || !KenwoodCatCodec.TryParseVfoSelect(reply, out var selectCode)
            || selectCode != KenwoodCatCodec.VfoSelectMemoryCode)
        {
            return;
        }

        if (subReceiver)
            _savedSubVfoSelect = selectCode;
        else
            _savedMainVfoSelect = selectCode;

        _transport.SendFireAndForget(KenwoodCatCodec.BuildSetVfoSelectCommand('0'), _catDelayMs);
    }

    /// <summary>Re-assert SATL layout (P3 main=downlink) without the full entry handshake.</summary>
    public void ReaffirmSatelliteLayout()
    {
        if (!_faFbSatelliteTracking || !_transport.IsOpen)
            return;

        _transport.SendFireAndForget(SatelliteModeOnCommand(), _catDelayMs);
        ForceUplinkPttOnSub();
    }

    /// <summary>
    /// Best-effort: pin TX/PTT to SUB while leaving CTRL on MAIN (matches SATL uplink = FB).
    /// May be ignored by some radios while in SATL.
    /// </summary>
    private void ForceUplinkPttOnSub()
    {
        if (!_transport.IsOpen)
            return;

        _transport.SendFireAndForget(KenwoodCatCodec.BuildTxSubControlMainCommand(), _catDelayMs);
    }

    public void ExchangeVfos()
    {
        if (!_faFbSatelliteTracking || !_transport.IsOpen)
            return;

        var downlinkHz = ReadFrequencyHz(RigVfo.Main);
        var uplinkHz = ReadFrequencyHz(RigVfo.Sub);
        if (downlinkHz is null or <= 0 || uplinkHz is null or <= 0)
            return;

        _transport.SendFireAndForget(KenwoodCatCodec.BuildSetFrequencyCommand('A', uplinkHz.Value), _catDelayMs);
        _transport.SendFireAndForget(KenwoodCatCodec.BuildSetFrequencyCommand('B', downlinkHz.Value), _catDelayMs);
        (_lastMainHz, _lastSubHz) = (_lastSubHz, _lastMainHz);
        (_lastVfoAHz, _lastVfoBHz) = (_lastVfoBHz, _lastVfoAHz);
    }

    public void SetToneOn(bool on) => SetCtcssPath(on, squelchTone: false);

    public void SetToneSquelchOn(bool on) => SetCtcssPath(on, squelchTone: true);

    public void SetToneHz(double hz, bool squelchTone)
    {
        if (!_transport.IsOpen)
            return;

        if (!KenwoodCatCodec.TryGetCtcssIndex(hz, out var index))
        {
            Log.Warning("TS-2000 does not support CTCSS {Hz} Hz", hz);
            return;
        }

        var cmd = squelchTone
            ? KenwoodCatCodec.BuildCtcssFrequencyCommand(index)
            : KenwoodCatCodec.BuildToneFrequencyCommand(index);

        if (_faFbSatelliteTracking)
        {
            var sub = _currentVfo is RigVfo.Sub or RigVfo.VfoB;
            if (sub)
            {
                SendSet(SatelliteModeOnSubControlCommand());
                SendSet(cmd);
                SendSet(SatelliteModeOnCommand());
            }
            else
            {
                SendSet(SatelliteModeOnCommand());
                SendSet(cmd);
            }

            return;
        }

        SendSet(cmd);
    }

    private void SetCtcssPath(bool on, bool squelchTone)
    {
        if (!_transport.IsOpen)
            return;

        var cmd = squelchTone
            ? KenwoodCatCodec.BuildCtcssEnableCommand(on)
            : KenwoodCatCodec.BuildToneEnableCommand(on);

        if (_faFbSatelliteTracking)
        {
            var sub = _currentVfo is RigVfo.Sub or RigVfo.VfoB;
            if (sub)
            {
                SendSet(SatelliteModeOnSubControlCommand());
                SendSet(cmd);
                SendSet(SatelliteModeOnCommand());
            }
            else
            {
                SendSet(SatelliteModeOnCommand());
                SendSet(cmd);
            }

            return;
        }

        SendSet(cmd);
    }

    private bool SendSet(string command) =>
        _transport.SendFireAndForget(command, _catDelayMs);

    /// <summary>Send a CAT set and ignore rejection (SM / tone clears must not block FA/FB or MD).</summary>
    private void SendBestEffort(string command) => SendSet(command);

    private long CachedFrequencyHz(RigVfo vfo) => vfo switch
    {
        RigVfo.Main => _lastMainHz,
        RigVfo.Sub => _lastSubHz,
        RigVfo.VfoA => _lastVfoAHz,
        RigVfo.VfoB => _lastVfoBHz,
        _ => 0
    };

    private static char VfoLetterFor(RigVfo vfo) => vfo switch
    {
        RigVfo.Sub or RigVfo.VfoB => 'B',
        _ => 'A'
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

    private bool TryEnableSatelliteMode()
    {
        _lastLinkHoldPollUtc = DateTime.MinValue;
        ExitMemoryModeIfNeeded();
        _transport.SendFireAndForget(SatelliteModeOnCommand(), _catDelayMs);

        // Settling delay: give the radio time to transition its internal state to SATL
        // before proceeding with the entry handshake and verification query.
        if (_satModeSettlingDelayMs > 0)
            Thread.Sleep(_satModeSettlingDelayMs);

        foreach (var toneOff in KenwoodCatCodec.SatelliteModeEntryToneOffSequence)
            _transport.SendFireAndForget(toneOff, _catDelayMs);

        SendSatelliteEntryHandshake();

        // Retry loop: attempt SA; verification up to _satModeRetryCount times
        // with inter-attempt delays, accounting for variable radio processing time.
        for (var attempt = 1; attempt <= _satModeRetryCount; attempt++)
        {
            var reply = _transport.Transact(KenwoodCatCodec.BuildSatelliteStatusQuery(), _catDelayMs);
            if (reply is not null && KenwoodCatCodec.TryParseSatelliteOn(reply))
            {
                SendSatelliteToneAndSquelchOff();
                ForceUplinkPttOnSub();
                return true;
            }

            if (attempt < _satModeRetryCount && _satModeRetryDelayMs > 0)
                Thread.Sleep(_satModeRetryDelayMs);
        }

        ForceUplinkPttOnSub();
        return false;
    }

    private void SendSatelliteEntryHandshake()
    {
        _transport.Transact(KenwoodCatCodec.BuildReadFrequencyCommand('A'), _catDelayMs);
        _transport.SendFireAndForget(KenwoodCatCodec.BuildSatelliteEntryTsCommand(), _catDelayMs);
        _transport.SendFireAndForget(KenwoodCatCodec.BuildAutoinfoExtendedCommand(), _catDelayMs);
        _transport.SendFireAndForget(SatelliteModeOnCommand(), _catDelayMs);

        _transport.SendFireAndForget(SatelliteModeOnCommand(), _catDelayMs);
        _transport.SendFireAndForget(KenwoodCatCodec.BuildSetModeCommand('2'), _catDelayMs);
        _transport.SendFireAndForget(SatelliteModeOnSubControlCommand(), _catDelayMs);
        _transport.SendFireAndForget(KenwoodCatCodec.BuildSetModeCommand('1'), _catDelayMs);
        _transport.SendFireAndForget(SatelliteModeOnCommand(), _catDelayMs);
        _transport.SendFireAndForget(KenwoodCatCodec.BuildToneEnableCommand(false), _catDelayMs);
    }

    private bool ProgramSatelliteFrequencies(long downlinkHz, long uplinkHz)
    {
        var ok =
            SendSet(KenwoodCatCodec.BuildSetFrequencyCommand('A', downlinkHz))
            && SendSet(KenwoodCatCodec.BuildSetFrequencyCommand('B', uplinkHz))
            && SendSet(KenwoodCatCodec.BuildSetFrequencyCommand('A', downlinkHz))
            && SendSet(KenwoodCatCodec.BuildSetFrequencyCommand('B', uplinkHz));
        SendBestEffort(KenwoodCatCodec.BuildSatelliteBandSelectMainCommand());
        SendBestEffort(KenwoodCatCodec.BuildSatelliteBandSelectSubCommand(downlinkHz));
        SendBestEffort(SatelliteModeOnCommand());
        SendBestEffort(KenwoodCatCodec.BuildToneEnableCommand(false));
        return ok;
    }

    private bool FinalizeSatelliteMainPath(char downlinkModeCode, long downlinkHz)
    {
        // SatPC32 order: tone clear then MD. Tone/SM rejects must not skip the mode set.
        SendBestEffort(SatelliteModeOnCommand());
        SendBestEffort(KenwoodCatCodec.BuildToneEnableCommand(false));
        SendBestEffort(KenwoodCatCodec.BuildCtcssEnableCommand(false));
        SendBestEffort("DQ0;");
        var ok =
            SendSet(SatelliteModeOnCommand())
            && SendSet(KenwoodCatCodec.BuildSetModeCommand(downlinkModeCode));
        SendBestEffort(KenwoodCatCodec.BuildSatelliteBandSelectSubCommand(downlinkHz));
        SendBestEffort(KenwoodCatCodec.BuildSatelliteBandSelectMainCommand());
        return ok;
    }

    private bool FinalizeSatelliteSubPath(char uplinkModeCode, long downlinkHz, long uplinkHz)
    {
        var ok =
            SendSet(SatelliteModeOnSubControlCommand())
            && SendSet(KenwoodCatCodec.BuildSetModeCommand(uplinkModeCode));
        SendBestEffort(KenwoodCatCodec.BuildSatelliteBandSelectSubCommand(downlinkHz));
        SendBestEffort(KenwoodCatCodec.BuildToneEnableCommand(false));
        SendBestEffort(KenwoodCatCodec.BuildCtcssEnableCommand(false));
        SendBestEffort("DQ0;");
        SendBestEffort(KenwoodCatCodec.BuildSatelliteBandSelectMainCommand());
        SendBestEffort(KenwoodCatCodec.BuildSatelliteBandSelectSubCommand(uplinkHz));
        ok = SendSet(SatelliteModeOnCommand()) && ok;
        return ok;
    }

    private void SendSatelliteToneAndSquelchOff()
    {
        ClearSatelliteTonePath(subControl: false);
        ClearSatelliteTonePath(subControl: true);
        _transport.SendFireAndForget(SatelliteModeOnCommand(), _catDelayMs);
    }

    private void ClearSatelliteTonePath(bool subControl)
    {
        if (subControl)
            _transport.SendFireAndForget(SatelliteModeOnSubControlCommand(), _catDelayMs);
        else
            _transport.SendFireAndForget(SatelliteModeOnCommand(), _catDelayMs);

        _transport.SendFireAndForget(KenwoodCatCodec.BuildToneEnableCommand(false), _catDelayMs);
        _transport.SendFireAndForget("DQ0;", _catDelayMs);
        _transport.SendFireAndForget(KenwoodCatCodec.BuildCtcssEnableCommand(false), _catDelayMs);
    }

    private bool TryRecoverSatelliteBandLayout(double downlinkKHz, Func<bool> reprogram)
    {
        Log.Warning("TS-2000 pass frequency programming failed — attempting SAT band layout recovery");

        ExchangeVfos();
        var ok = reprogram();
        var mainHz = ReadFrequencyHz(RigVfo.Main);
        var bandOk = mainHz is null or <= 0
            || !RigSatModeHelper.NeedsMainSubBandSwap(mainHz.Value, downlinkKHz);

        if (ok && bandOk)
            return true;

        Log.Warning("TS-2000 band recovery via VFO exchange insufficient — re-entering SATL");
        SetSatelliteMode(false);
        SetSatelliteMode(true);
        return reprogram();
    }

    private string SatelliteModeOnCommand() =>
        KenwoodCatCodec.BuildSetSatelliteModeOnCommand(_traceEnabled);

    private string SatelliteModeOnSubControlCommand() =>
        KenwoodCatCodec.BuildSetSatelliteModeOnSubControlCommand(_traceEnabled);
}
