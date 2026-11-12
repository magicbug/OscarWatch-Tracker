using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;
using OscarWatch.Core.Net;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Services;
using OscarWatch.Core.Terminal;
using OscarWatch.Core.Terminal.Uhm;
using OscarWatch.Hardware;
using OscarWatch.Services;
using Serilog;

namespace OscarWatch.Terminal;

public sealed record TerminalLogEntry(
    long Id,
    DateTime Utc,
    TerminalMessageKind Kind,
    string From,
    string To,
    string Text,
    TimeSpan? Delay,
    string Path,
    string Monitor,
    AprsMicEStatus? MicEStatus = null,
    int? Quality = null);

public sealed record TerminalStationSnapshot(
    string Call,
    string? Grid,
    DateTime FirstHeardUtc,
    DateTime LastHeardUtc,
    DateTime LastActivityUtc,
    int CqCount,
    int TxCount,
    TerminalStationState State,
    bool InQso,
    bool RogerSent,
    bool RogerReceived,
    bool Logged,
    UhmMatchState Uhm,
    bool IsUhmUser,
    AprsPosition? Position = null,
    DateTime? PositionUtc = null);

public sealed record TerminalStatsSnapshot(
    int UniqueCallsigns,
    int MyTx,
    int MyRx,
    double? MyDigipeatSuccessPercent,
    int Digipeated,
    double? MedianDelaySeconds,
    int UhmUsers);

/// <summary>Next AOS (<paramref name="IsAos"/> true) or LOS for the focused satellite.</summary>
public sealed record TerminalHorizonCrossing(bool IsAos, DateTime Utc);

public enum TerminalUhmStatus
{
    Off,
    NoToken,
    NoSatellite,
    BelowHorizon,
    Active,
    RateLimited,
    NotEnabled,
    Unauthorised,
    Forbidden,
    Error
}

public enum TerminalSendResult
{
    Sent,
    NotConnected,
    NoCallsign,
    InvalidDestination,
    EmptyMessage,
    Failed,
    NotAprs,
    NoPosition,

    /// <summary>Built-in modem: the focused satellite is below the horizon.</summary>
    BelowHorizon,

    /// <summary>Built-in modem: the satellite needs a mode the built-in modem does not implement (for example BPSK or GMSK).</summary>
    UnsupportedModem,

    /// <summary>Built-in modem, half duplex: the downlink stayed busy for the maximum channel wait.</summary>
    ChannelBusy,

    /// <summary>Built-in modem: the transmit watchdog dropped PTT.</summary>
    Watchdog,

    /// <summary>Built-in modem: the radio did not move to the uplink before keying.</summary>
    UplinkHoldFailed
}

/// <summary>
/// OscarWatch Terminal engine: modem connection with reconnect, decode, classification, QSO tracking,
/// statistics, logging, UHM hearing reports and match polling, and the optional APRS-IS gateway.
/// Events fire on background threads; view models marshal to the UI thread.
/// </summary>
public sealed class TerminalService : IAsyncDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<TerminalService>();

    public static readonly TimeSpan UhmMatchInterval = TimeSpan.FromSeconds(7);
    public static readonly TimeSpan UhmActiveInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan UhmStatusInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ActivePartnerWindow = TimeSpan.FromMinutes(2);
    public const int MaxLogEntries = 2000;

    private readonly ISettingsService _settings;
    private readonly LiveQsoLogger _qsoLogger;
    private readonly IOrbitPropagator _propagator;
    private readonly ILiveTrackingService _tracking;
    private readonly UhmApiClient _uhm;
    private readonly IGpsService? _gps;
    private readonly AprsIsClient _aprsIs = new();
    private readonly object _gate = new();
    private readonly TerminalQsoTracker _tracker = new();
    private readonly TerminalStatistics _stats = new();
    private readonly UhmReportPolicy _uhmPolicy = new();
    private readonly UhmMatchTracker _uhmMatches = new();
    private readonly Dictionary<string, string> _lastTextSentTo = new(StringComparer.Ordinal);
    private readonly string _clientId = $"OscarWatch-Tracker/{OscarWatchHttpClients.GetProductVersion()}";

    private readonly IRigController? _rig;
    private readonly TerminalTransmitWindow _transmitWindow = new();

    private CancellationTokenSource? _runCts;
    private Task? _connectionTask;
    private Task? _uhmTask;
    private ITerminalModemTransport? _transport;
    private TerminalSatelliteProfile? _profile;
    private long _nextId;
    private DateTime _uhmBlockedUntilUtc = DateTime.MinValue;
    private string? _losCacheNorad;
    private DateTime _losCacheComputedUtc = DateTime.MinValue;
    private TerminalHorizonCrossing? _losCache;
    private string? _passStartCacheNorad;
    private DateTime _passStartComputedUtc = DateTime.MinValue;
    private DateTime? _passStartCache;

    private readonly Func<IPacketAudio> _createPacketAudio;

    public TerminalService(
        ISettingsService settings,
        LiveQsoLogger qsoLogger,
        IOrbitPropagator propagator,
        ILiveTrackingService tracking,
        UhmApiClient? uhm = null,
        IGpsService? gps = null,
        IRigController? rig = null,
        Func<IPacketAudio>? createPacketAudio = null)
    {
        _settings = settings;
        _qsoLogger = qsoLogger;
        _propagator = propagator;
        _tracking = tracking;
        _uhm = uhm ?? new UhmApiClient();
        _gps = gps;
        _rig = rig;
        _createPacketAudio = createPacketAudio ?? (() => new PacketAudioService());
        _aprsIs.StateChanged += _ => Changed?.Invoke();
    }

    /// <summary>The built-in modem while it is the active transport, for level and decode counts.</summary>
    public BuiltInModemTransport? BuiltInModem => _transport as BuiltInModemTransport;

    /// <summary>The built-in modem is selected and the focused satellite needs an unsupported mode.</summary>
    public bool ProfileUnsupportedByBuiltIn =>
        Settings.Transport == TerminalTransportKind.BuiltIn
        && Profile is { } profile
        && !TerminalSatelliteProfiles.IsSupportedByBuiltIn(profile.ModemHint);

    /// <summary>The built-in modem's TX block for a satellite below the horizon applies now.</summary>
    public bool BlockedBelowHorizon =>
        Settings.Transport == TerminalTransportKind.BuiltIn
        && Settings.BuiltIn.BlockTxBelowHorizon
        && Profile is not null
        && CurrentLookAngles() is { ElevationDeg: < 0 };

    private TerminalSendResult? CheckModemAllowsTransmit()
    {
        if (ProfileUnsupportedByBuiltIn)
            return TerminalSendResult.UnsupportedModem;
        if (BlockedBelowHorizon)
            return TerminalSendResult.BelowHorizon;
        return null;
    }

    private static TerminalSendResult? MapTransmitFailure(Exception ex) =>
        ex is TerminalTransmitException t
            ? t.Failure switch
            {
                TerminalTransmitFailure.ChannelBusy => TerminalSendResult.ChannelBusy,
                TerminalTransmitFailure.Watchdog => TerminalSendResult.Watchdog,
                TerminalTransmitFailure.UplinkHoldFailed => TerminalSendResult.UplinkHoldFailed,
                _ => null
            }
            : null;

    private BuiltInModemTransport CreateBuiltInModem()
    {
        if (_rig is null)
            throw new InvalidOperationException("The built-in modem needs the rig controller for PTT.");

        var marker = PttSessionMarker.ForTerminal(_settings.SettingsPath);
        var ptt = new PttKeyer(
            _rig,
            () => Settings.BuiltIn.ToPttOptions(),
            "Terminal",
            port => SerialPortConflictHelper.TryDescribeTerminalSeparatePttConflict(_settings.Current, port, out var message)
                ? message
                : null,
            marker);
        var modem = new BuiltInModemTransport(_createPacketAudio(), ptt, () => Settings, _rig)
        {
            LinkType = Profile?.LinkType ?? TerminalLinkType.Unknown,
            ModemHint = Profile?.ModemHint,
            BeforeKey = HoldUplinkAtKeyAsync
        };
        return modem;
    }

    /// <summary>
    /// Built-in modem: start the FT-847 same-band hold as PTT goes up. Only a radio that needs the hold
    /// can fail it; other radios decline, which is fine.
    /// </summary>
    private async Task<bool> HoldUplinkAtKeyAsync(TimeSpan airtime, CancellationToken cancellationToken)
    {
        if (_rig is not { } rig)
            return true;

        var rigSettings = _settings.Current.Rig;
        var needsHold = rigSettings is { Enabled: true, DualRadioEnabled: false, Type: RigType.YaesuFt847 }
                        && Profile?.LinkType is TerminalLinkType.Simplex or TerminalLinkType.SameBand
                        && !rigSettings.CatUpdatesPaused
                        && rig.GetStatus().IsConnected;
        try
        {
            var held = await Task.Run(() => rig.TryHoldSameBandTransmit(airtime, TimeSpan.FromSeconds(1)), cancellationToken)
                .ConfigureAwait(false);
            return held || !needsHold;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "Same-band uplink hold failed");
            return !needsHold;
        }
    }

    /// <summary>A message row was added (received, digipeated own frame, or sent).</summary>
    public event Action<TerminalLogEntry>? MessageAdded;

    /// <summary>Connection, statistics, stations or UHM state changed.</summary>
    public event Action? Changed;

    /// <summary>A contact was saved to the logbook.</summary>
    public event Action<QsoRecord, bool>? Logged;

    public bool IsRunning => _runCts is not null;
    public TerminalTransportState TransportState => _transport?.State ?? TerminalTransportState.Disconnected;
    public string? ModemDescription => _transport?.ModemDescription;
    public string? LastConnectionError { get; private set; }

    /// <summary>Why the last send did not go out, or null after a send that did.</summary>
    public string? LastSendFailure { get; private set; }
    public AprsIsState AprsIsState => _aprsIs.State;
    public TerminalUhmStatus UhmStatus { get; private set; } = TerminalUhmStatus.Off;
    public string? UhmStatusDetail { get; private set; }
    public string? UhmServerMessage { get; private set; }

    /// <summary>Call in the To box; polled for UHM matches alongside active QSO partners.</summary>
    public string? CurrentToCall { get; set; }

    public TerminalSatelliteProfile? Profile
    {
        get
        {
            lock (_gate)
                return _profile;
        }
    }

    public TerminalSettings Settings => _settings.Current.Terminal;

    public string MyCall => Settings.EffectiveCallsign(_settings.Current.GroundStation.Callsign);

    public string MyGrid => _settings.Current.GroundStation.GridSquare ?? "";

    /// <summary>Set by the view model from the focused satellite; null when it has no packet digipeater.</summary>
    public void SetSatelliteContext(TerminalSatelliteProfile? profile)
    {
        lock (_gate)
        {
            if (Equals(_profile, profile))
                return;
            _profile = profile;
        }

        if (_transport is BuiltInModemTransport modem)
        {
            modem.LinkType = profile?.LinkType ?? TerminalLinkType.Unknown;
            modem.ModemHint = profile?.ModemHint;
        }

        Changed?.Invoke();
    }

    public void Start()
    {
        if (_runCts is not null)
            return;

        var cts = new CancellationTokenSource();
        _runCts = cts;
        if (TerminalTransportFactory.IsSupported(Settings.Transport))
            _connectionTask = Task.Run(() => ConnectionLoopAsync(cts.Token));
        _uhmTask = Task.Run(() => UhmLoopAsync(cts.Token));
        _aprsIs.Start(Settings.AprsIs, MyCall);
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        var cts = _runCts;
        if (cts is null)
            return;
        _runCts = null;
        cts.Cancel();
        _aprsIs.Stop();

        var transport = _transport;
        if (transport is not null)
            await transport.DisposeAsync().ConfigureAwait(false);

        foreach (var task in new[] { _connectionTask, _uhmTask })
        {
            if (task is null)
                continue;
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        cts.Dispose();
        _connectionTask = null;
        _uhmTask = null;
        UhmStatus = TerminalUhmStatus.Off;
        Changed?.Invoke();
    }

    /// <summary>Reconnects with the current settings (after the settings window is saved).</summary>
    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Start();
    }

    public async Task<IReadOnlyList<TerminalRadioPort>> GetRadioPortsAsync(CancellationToken cancellationToken)
    {
        var transport = _transport;
        if (transport is null || transport.State != TerminalTransportState.Connected)
            return [];
        return await transport.GetRadioPortsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TerminalSendResult> SendAsync(string? to, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TerminalSendResult.EmptyMessage;
        var myCall = MyCall;
        if (!TerminalText.LooksLikeCallsign(myCall))
            return TerminalSendResult.NoCallsign;
        var transport = _transport;
        if (transport is null || transport.State != TerminalTransportState.Connected)
            return TerminalSendResult.NotConnected;
        if (CheckModemAllowsTransmit() is { } blocked)
            return blocked;

        var profile = Profile;
        var format = profile?.Format ?? TerminalMessageFormat.Aprs;
        var path = Ax25Frame.ParsePath(profile?.Path);
        var source = new Ax25Address(myCall, Settings.Ssid);

        TerminalOutgoingFrame outgoing;
        try
        {
            outgoing = TerminalMessageCodec.BuildOutgoing(format, source, to, text, path);
        }
        catch (FormatException)
        {
            return TerminalSendResult.InvalidDestination;
        }

        var delay = Settings.TxDelaySeconds;
        if (delay > 0)
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);

        try
        {
            var bytes = outgoing.Frame.Encode();
            await HoldUplinkForFrameAsync(transport, bytes.Length).ConfigureAwait(false);
            await transport.SendAsync(bytes, Settings.ActiveRadioPort, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException
                                       or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            Log.Warning(ex, "Terminal send failed");
            LastSendFailure = ex.Message;
            return MapTransmitFailure(ex) ?? TerminalSendResult.Failed;
        }

        LastSendFailure = null;
        var utc = DateTime.UtcNow;
        TerminalLogEntry entry;
        TerminalStation? station;
        QsoListTrace? qsoTrace = null;
        lock (_gate)
        {
            _stats.RecordSent(outgoing.Frame.InfoText, utc);
            var wasListed = WasInQsoList(outgoing.To);
            station = _tracker.OnSent(outgoing.To, outgoing.Text, utc);
            if (station is not null)
            {
                _lastTextSentTo[station.Call] = outgoing.Text;
                qsoTrace = QsoListTrace.Capture("send", station, wasListed, outgoing.To, outgoing.Text, utc);
            }

            entry = new TerminalLogEntry(
                Interlocked.Increment(ref _nextId),
                utc,
                TerminalMessageKind.MyTx,
                source.Text,
                outgoing.To,
                outgoing.Text,
                null,
                outgoing.Frame.PathText(),
                outgoing.Frame.ToMonitorString());
        }

        MessageAdded?.Invoke(entry);
        Changed?.Invoke();
        qsoTrace?.Write(Log);
        if (station is not null)
            await TryAutoLogAsync(station).ConfigureAwait(false);
        return TerminalSendResult.Sent;
    }

    /// <summary>Our location for Send position: a recent GPS fix, the station latitude and longitude, or the grid centre.</summary>
    public TerminalPositionFix? ResolvePosition()
    {
        var gpsEnabled = _settings.Current.Gps?.Enabled == true;
        return TerminalPositionSource.Resolve(gpsEnabled, ReadGpsStatus(gpsEnabled), _settings.Current.GroundStation, DateTime.UtcNow);
    }

    /// <summary>Whether <see cref="MyGrid"/> follows GPS; null when GPS is not enabled.</summary>
    public TerminalGridSource? GridSource()
    {
        var gps = _settings.Current.Gps;
        return TerminalPositionSource.GridSource(gps, ReadGpsStatus(gps?.Enabled == true), DateTime.UtcNow);
    }

    private GpsConnectionStatus? ReadGpsStatus(bool gpsEnabled)
    {
        if (!gpsEnabled || _gps is null)
            return null;
        try
        {
            return _gps.GetStatus();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GPS status read failed");
            return null;
        }
    }

    /// <summary>
    /// Sends our APRS position as a broadcast UI frame (APRS profiles only). <paramref name="describe"/> gives the
    /// text shown in the message log for the fix that was sent.
    /// </summary>
    public async Task<(TerminalSendResult Result, TerminalPositionFix? Fix)> SendPositionAsync(
        Func<TerminalPositionFix, string> describe,
        CancellationToken cancellationToken = default)
    {
        var myCall = MyCall;
        if (!TerminalText.LooksLikeCallsign(myCall))
            return (TerminalSendResult.NoCallsign, null);
        var transport = _transport;
        if (transport is null || transport.State != TerminalTransportState.Connected)
            return (TerminalSendResult.NotConnected, null);
        if (CheckModemAllowsTransmit() is { } blocked)
            return (blocked, null);
        var profile = Profile;
        if ((profile?.Format ?? TerminalMessageFormat.Aprs) != TerminalMessageFormat.Aprs)
            return (TerminalSendResult.NotAprs, null);
        if (ResolvePosition() is not { } fix)
            return (TerminalSendResult.NoPosition, null);

        var source = new Ax25Address(myCall, Settings.Ssid);
        var frame = Ax25Frame.CreateUi(
            Ax25Address.Parse(AprsPayloadCodec.Tocall),
            source,
            Ax25Frame.ParsePath(profile?.Path),
            TerminalPositionSource.BuildPayload(fix, Settings, myCall));

        var delay = Settings.TxDelaySeconds;
        if (delay > 0)
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);

        try
        {
            var bytes = frame.Encode();
            await HoldUplinkForFrameAsync(transport, bytes.Length).ConfigureAwait(false);
            await transport.SendAsync(bytes, Settings.ActiveRadioPort, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException
                                       or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            Log.Warning(ex, "Terminal position send failed");
            LastSendFailure = ex.Message;
            return (MapTransmitFailure(ex) ?? TerminalSendResult.Failed, fix);
        }

        LastSendFailure = null;
        var utc = DateTime.UtcNow;
        TerminalLogEntry entry;
        lock (_gate)
        {
            _stats.RecordSent(frame.InfoText, utc);
            entry = new TerminalLogEntry(
                Interlocked.Increment(ref _nextId),
                utc,
                TerminalMessageKind.MyTx,
                source.Text,
                TerminalMessageCodec.AllTo,
                describe(fix),
                null,
                frame.PathText(),
                frame.ToMonitorString());
        }

        MessageAdded?.Invoke(entry);
        Changed?.Invoke();
        return (TerminalSendResult.Sent, fix);
    }

    /// <summary>Logs <paramref name="call"/> now (F12), using whatever grid and reports Terminal has seen.</summary>
    public Task<QsoRecord?> LogCallAsync(string call) => LogAsync(TerminalText.BaseCallsign(call), manual: true);

    public IReadOnlyList<TerminalStationSnapshot> GetStations()
    {
        lock (_gate)
        {
            return _tracker.Stations
                .Select(s => new TerminalStationSnapshot(
                    s.Call,
                    s.Grid,
                    s.FirstHeardUtc,
                    s.LastHeardUtc,
                    s.LastActivityUtc,
                    s.CqCount,
                    s.TxCount,
                    s.State,
                    s.IsInQso,
                    s.RogerSent,
                    s.RogerReceived,
                    s.Logged,
                    _uhmMatches.GetState(s.Call),
                    _uhmMatches.IsActiveUser(s.Call),
                    s.Position,
                    s.PositionUtc))
                .ToList();
        }
    }

    public TerminalStatsSnapshot GetStatistics()
    {
        lock (_gate)
        {
            return new TerminalStatsSnapshot(
                _stats.UniqueCallsigns,
                _stats.MyTx,
                _stats.MyRx,
                _stats.MyDigipeatSuccessPercent,
                _stats.Digipeated,
                _stats.MedianDelaySeconds,
                _uhmMatches.ActiveUserCount);
        }
    }

    public void ResetStatistics()
    {
        lock (_gate)
        {
            _stats.Reset();
            _tracker.Clear();
            _lastTextSentTo.Clear();
        }

        Changed?.Invoke();
    }

    /// <summary>Clears QSO progress. Heard-only stations stay, and former QSO stations remain as heard.</summary>
    public void ClearQsoStations()
    {
        lock (_gate)
            _tracker.ClearQsoProgress();

        Changed?.Invoke();
    }

    /// <summary>Removes every heard station, which also empties the QSO list. Statistics are left as they are.</summary>
    public void ClearHeardStations()
    {
        lock (_gate)
        {
            _tracker.Clear();
            _lastTextSentTo.Clear();
        }

        Changed?.Invoke();
    }

    /// <summary>Satellite elevation seen from the centre of <paramref name="grid"/>, or null.</summary>
    public double? ElevationFromGrid(string? grid)
    {
        var norad = _tracking.FocusedNoradId;
        if (string.IsNullOrWhiteSpace(norad) || string.IsNullOrWhiteSpace(grid) || grid.Length < 4
            || !_propagator.HasSatellite(norad))
            return null;
        try
        {
            var (lat, lon) = Core.Geo.MaidenheadGrid.ToLatLonCenter(grid);
            var site = new GroundStation { LatitudeDeg = lat, LongitudeDeg = lon, AltitudeMetersAsl = 0 };
            return _propagator.GetLookAngles(norad, site, DateTime.UtcNow).ElevationDeg;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Focused satellite look angles from the ground station, or null.</summary>
    public LookAngles? CurrentLookAngles()
    {
        var norad = _tracking.FocusedNoradId;
        if (string.IsNullOrWhiteSpace(norad))
            return null;
        return _tracking.GetLiveNowSnapshot()
            .FirstOrDefault(s => string.Equals(s.NoradId, norad, StringComparison.Ordinal))?.LookAngles;
    }

    /// <summary>True when the focused satellite has look angles at or above the status-report horizon (−1°).</summary>
    internal bool UhmSatelliteIsUp()
    {
        var angles = CurrentLookAngles();
        return angles is not null && SatelliteStatusReportFormatting.IsElevationReportable(angles.ElevationDeg);
    }

    /// <summary>
    /// Next horizon crossing for the focused satellite: LOS while it is up, otherwise the next AOS within
    /// 24 hours. Cached for 30 s; null when there is no focused satellite or no crossing was found.
    /// </summary>
    public TerminalHorizonCrossing? EstimateHorizonCrossing()
    {
        var norad = _tracking.FocusedNoradId;
        var now = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(norad) || !_propagator.HasSatellite(norad))
            return null;
        if (norad == _losCacheNorad && now - _losCacheComputedUtc < TimeSpan.FromSeconds(30)
            && (_losCache is null || _losCache.Utc > now))
            return _losCache;

        TerminalHorizonCrossing? crossing = null;
        try
        {
            var site = _settings.Current.GroundStation;
            var up = _propagator.GetLookAngles(norad, site, now).ElevationDeg > 0;
            var limit = now.AddHours(up ? 1 : 24);
            var step = TimeSpan.FromSeconds(up ? 10 : 30);
            for (var t = now + step; t < limit; t += step)
            {
                if (_propagator.GetLookAngles(norad, site, t).ElevationDeg > 0 != up)
                {
                    crossing = new TerminalHorizonCrossing(!up, t);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Terminal horizon estimate failed");
        }

        _losCacheNorad = norad;
        _losCacheComputedUtc = now;
        _losCache = crossing;
        return crossing;
    }

    /// <summary>
    /// AOS of the current pass of the focused satellite, or of the most recent pass within 3 hours when it is
    /// below the horizon. Cached for 30 s; null when there is no focused satellite or no recent pass.
    /// </summary>
    public DateTime? LastPassStartUtc()
    {
        var norad = _tracking.FocusedNoradId;
        var now = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(norad) || !_propagator.HasSatellite(norad))
            return null;
        if (norad == _passStartCacheNorad && now - _passStartComputedUtc < TimeSpan.FromSeconds(30))
            return _passStartCache;

        DateTime? start = null;
        try
        {
            var site = _settings.Current.GroundStation;
            var limit = now.AddHours(-3);
            var step = TimeSpan.FromSeconds(10);
            var t = now;
            while (t > limit && _propagator.GetLookAngles(norad, site, t).ElevationDeg <= 0)
                t -= step;
            if (t > limit)
            {
                while (t > limit && _propagator.GetLookAngles(norad, site, t - step).ElevationDeg > 0)
                    t -= step;
                start = t;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Terminal pass start estimate failed");
        }

        _passStartCacheNorad = norad;
        _passStartComputedUtc = now;
        _passStartCache = start;
        return start;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _aprsIs.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            ITerminalModemTransport transport;
            try
            {
                transport = TerminalTransportFactory.Create(
                    Settings,
                    MyCall,
                    CreateBuiltInModem,
                    port => SerialPortConflictHelper.TryDescribeKissSerialConflict(_settings.Current, port, out var message)
                        ? message
                        : null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                LastConnectionError = ex.Message;
                Changed?.Invoke();
                return;
            }
            transport.FrameReceived += OnFrameReceived;
            transport.StateChanged += OnTransportStateChanged;
            _transport = transport;
            try
            {
                await transport.ConnectAsync(ct).ConfigureAwait(false);
                LastConnectionError = null;
                backoff = TimeSpan.FromSeconds(2);
                Changed?.Invoke();
                await transport.WaitForDisconnectAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                LastConnectionError = ex.Message;
                if (PacketAudioOpenFailure.IsStreamOpenFailure(ex))
                {
                    var output = Settings.BuiltIn.OutputDeviceDisplayName;
                    Log.Warning(
                        "Terminal modem could not open the sound card '{Device}'. It may already be in use, for example by FT4",
                        string.IsNullOrWhiteSpace(output) ? "(default)" : output);
                }
                else
                {
                    Log.Information("Terminal modem connection failed: {Message}", ex.Message);
                }
                Changed?.Invoke();
            }

            transport.FrameReceived -= OnFrameReceived;
            transport.StateChanged -= OnTransportStateChanged;
            if (ct.IsCancellationRequested)
                break;
            await transport.DisposeAsync().ConfigureAwait(false);

            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(30).Ticks, backoff.Ticks * 2));
        }
    }

    private void OnTransportStateChanged(TerminalTransportState state) => Changed?.Invoke();

    private void OnFrameReceived(TerminalReceivedFrame received)
    {
        try
        {
            HandleFrame(received);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Terminal frame handling failed");
        }
    }

    private void HandleFrame(TerminalReceivedFrame received)
    {
        if (received.RadioPort != Settings.ActiveRadioPort)
            return;
        if (!Ax25Frame.TryParse(received.Ax25, out var frame) || frame is null || !frame.IsUi)
            return;

        var myCall = MyCall;
        var profile = Profile;
        var format = profile?.Format ?? TerminalMessageFormat.Aprs;
        var message = TerminalMessageCodec.Decode(frame, format, received.Utc);
        var own = TerminalText.SameStation(message.From, myCall);

        TerminalLogEntry entry;
        TerminalStation? station = null;
        QsoListTrace? qsoTrace = null;
        lock (_gate)
        {
            TimeSpan? delay = null;
            TerminalMessageKind kind;
            if (own)
            {
                if (_stats.EvaluateOwnFrame(frame.InfoText, frame.HasBeenDigipeated, received.Utc, out delay)
                    == TerminalOwnFrameResult.LocalEcho)
                    return;
                kind = TerminalMessageKind.MyDigipeated;
            }
            else
            {
                kind = TerminalMessageClassifier.Classify(message, myCall, locallySent: false);
                _stats.RecordReceived(message, kind);
                var wasListed = WasInQsoList(message.From);
                station = _tracker.OnReceived(message, myCall);
                if (station is not null)
                {
                    var listed = TerminalQsoTracker.ShowsInQsoList(station);
                    var addressed = TerminalText.SameStation(message.To, myCall);
                    if (addressed || wasListed != listed)
                        qsoTrace = QsoListTrace.Capture("receive", station, wasListed, message.To, message.Text, message.Utc);
                }
            }

            entry = new TerminalLogEntry(
                Interlocked.Increment(ref _nextId),
                received.Utc,
                kind,
                message.From,
                message.To,
                message.Text,
                delay,
                message.Path,
                message.Monitor,
                message.Aprs?.Position?.MicEStatus,
                received.Quality);
        }

        MessageAdded?.Invoke(entry);
        Changed?.Invoke();
        qsoTrace?.Write(Log);

        _aprsIs.Gate(frame, received.Utc);

        if (own)
            return;

        if (profile is not null)
            PostUhmHeard(message, myCall, profile);

        if (format == TerminalMessageFormat.Aprs && Settings.AprsAutoAck
            && message.Aprs is { Type: AprsPacketType.Message, MessageNumber: { Length: > 0 } number }
            && TerminalText.SameStation(message.To, myCall))
            _ = SendAckAsync(message.From, number);

        if (station is not null)
            _ = TryAutoLogAsync(station);
    }

    /// <summary>
    /// A single-VFO radio on a same-band satellite (FT-847) must be on the uplink before the
    /// modem keys it. Other radios have a real split and the rig controller declines.
    /// The built-in modem takes the hold itself when PTT goes up, so this is for external modems.
    /// </summary>
    private async Task HoldUplinkForFrameAsync(ITerminalModemTransport transport, int frameBytes)
    {
        if (_rig is not { } rig || transport is BuiltInModemTransport)
            return;

        var settings = Settings;
        var hold = _transmitWindow.Add(
            frameBytes,
            TerminalTransmitWindow.BaudForModemHint(Profile?.ModemHint),
            settings.ModemTxDelayMs,
            settings.ModemTxTailMs,
            DateTime.UtcNow);
        try
        {
            await Task.Run(() => rig.TryHoldSameBandTransmit(hold, TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Same-band uplink hold failed");
        }
    }

    private async Task SendAckAsync(string to, string number)
    {
        var transport = _transport;
        if (transport is null || transport.State != TerminalTransportState.Connected
            || CheckModemAllowsTransmit() is not null)
            return;
        try
        {
            var source = new Ax25Address(MyCall, Settings.Ssid);
            var frame = Ax25Frame.CreateUi(
                Ax25Address.Parse(AprsPayloadCodec.Tocall),
                source,
                Ax25Frame.ParsePath(Profile?.Path),
                AprsPayloadCodec.BuildAck(to, number));
            var bytes = frame.Encode();
            await HoldUplinkForFrameAsync(transport, bytes.Length).ConfigureAwait(false);
            await transport.SendAsync(bytes, Settings.ActiveRadioPort, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
                _stats.RecordSent(frame.InfoText, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Terminal APRS ack send failed");
        }
    }

    private async Task TryAutoLogAsync(TerminalStation station)
    {
        if (!Settings.AutoLog)
            return;
        bool ready;
        lock (_gate)
            ready = TerminalQsoTracker.IsReadyToLog(station);
        if (ready)
            await LogAsync(station.Call, manual: false).ConfigureAwait(false);
    }

    private async Task<QsoRecord?> LogAsync(string call, bool manual)
    {
        if (!TerminalText.LooksLikeCallsign(call))
            return null;

        string grid;
        string rstSent;
        string rstRcvd;
        lock (_gate)
        {
            var station = _tracker.Get(call);
            if (!manual && (station is null || station.Logged))
                return null;
            _tracker.MarkLogged(call);
            grid = station?.Grid ?? "";
            rstSent = station?.RstSent ?? "59";
            rstRcvd = station?.RstReceived ?? "59";
        }

        try
        {
            var record = await _qsoLogger.LogAsync(new LiveQsoEntry(
                DateTime.UtcNow,
                call,
                rstSent,
                rstRcvd,
                grid,
                "PKT",
                "PKT",
                manual ? "OscarWatch Terminal manual log" : "OscarWatch Terminal")).ConfigureAwait(false);
            Logged?.Invoke(record, manual);
            Changed?.Invoke();
            return record;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Terminal logbook save failed for {Call}", call);
            lock (_gate)
                _tracker.SetLogged(call, false);

            return null;
        }
    }

    private void PostUhmHeard(TerminalDecodedMessage message, string myCall, TerminalSatelliteProfile profile)
    {
        if (!Settings.Uhm.Enabled || !HasUhmToken() || !UhmSatelliteIsUp() || DateTime.UtcNow < _uhmBlockedUntilUtc)
            return;

        UhmHeardReport? report;
        lock (_gate)
        {
            if (!_uhmPolicy.TryCreateReport(message, myCall, profile.UhmName, _clientId, out report) || report is null)
                return;
        }

        _ = Task.Run(async () =>
        {
            var result = await _uhm.PostHeardAsync(_settings.Current.SatelliteStatus, report).ConfigureAwait(false);
            ApplyUhmResult(result.Result);
        });
    }

    private async Task UhmLoopAsync(CancellationToken ct)
    {
        var nextActive = DateTime.MinValue;
        var nextStatus = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await UhmTickAsync(ref nextActive, ref nextStatus, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "UHM tick failed");
            }

            try
            {
                await Task.Delay(UhmMatchInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private Task UhmTickAsync(ref DateTime nextActive, ref DateTime nextStatus, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var uhm = Settings.Uhm;
        var profile = Profile;
        TerminalUhmStatus? idle = !uhm.Enabled ? TerminalUhmStatus.Off
            : !HasUhmToken() ? TerminalUhmStatus.NoToken
            : profile is null ? TerminalUhmStatus.NoSatellite
            : !UhmSatelliteIsUp() ? TerminalUhmStatus.BelowHorizon
            : null;
        if (idle is { } status)
        {
            SetUhmStatus(status, null);
            return Task.CompletedTask;
        }

        if (now < _uhmBlockedUntilUtc)
            return Task.CompletedTask;

        if (UhmStatus is TerminalUhmStatus.Off or TerminalUhmStatus.NoToken or TerminalUhmStatus.NoSatellite
            or TerminalUhmStatus.BelowHorizon)
            SetUhmStatus(TerminalUhmStatus.Active, null);

        var doActive = now >= nextActive;
        var doStatus = now >= nextStatus;
        if (doActive)
            nextActive = now + UhmActiveInterval;
        if (doStatus)
            nextStatus = now + UhmStatusInterval;
        return UhmPollAsync(profile!, doActive, doStatus, ct);
    }

    private async Task UhmPollAsync(TerminalSatelliteProfile profile, bool doActive, bool doStatus, CancellationToken ct)
    {
        var settings = _settings.Current.SatelliteStatus;
        var myCall = MyCall;
        var now = DateTime.UtcNow;

        if (doStatus)
        {
            var status = await _uhm.GetStatusAsync(settings, ct).ConfigureAwait(false);
            if (status.Result.Success)
            {
                UhmServerMessage = status.Message;
                Changed?.Invoke();
            }
        }

        if (doActive)
        {
            var active = await _uhm.GetActiveAsync(settings, ct).ConfigureAwait(false);
            if (!ApplyUhmResult(active.Result))
                return;
            lock (_gate)
                _uhmMatches.SetActiveUsers(active.Callsigns);
            Changed?.Invoke();
        }

        List<(string Call, string? Text)> partners;
        lock (_gate)
        {
            var calls = _tracker.ActivePartners(now, ActivePartnerWindow).Select(s => s.Call).ToList();
            var to = TerminalText.BaseCallsign(CurrentToCall);
            if (TerminalText.LooksLikeCallsign(to) && !calls.Contains(to, StringComparer.Ordinal))
                calls.Add(to);
            partners = calls
                .Where(c => !TerminalText.SameStation(c, myCall) && _tracker.Get(c)?.Logged != true)
                .Select(c => (c, Settings.Uhm.FrameLevelMatch && _lastTextSentTo.TryGetValue(c, out var t) ? t : null))
                .ToList();
            foreach (var p in partners)
                _uhmMatches.BeginPolling(p.Call, now);
        }

        foreach (var (call, text) in partners)
        {
            var match = await _uhm.GetMatchAsync(settings, profile.UhmName, myCall, call, text, ct).ConfigureAwait(false);
            if (!ApplyUhmResult(match.Result))
                return;

            lock (_gate)
                _uhmMatches.ApplyMatch(call, match.Matched, match.Mutual, DateTime.UtcNow);

            Changed?.Invoke();
        }
    }

    /// <summary>Updates UHM status from a result; returns false when polling should stop for now.</summary>
    private bool ApplyUhmResult(UhmResult result)
    {
        if (result.Success)
        {
            if (UhmStatus != TerminalUhmStatus.Active)
                SetUhmStatus(TerminalUhmStatus.Active, null);
            return true;
        }

        switch (result.Error)
        {
            case UhmErrorKind.RateLimited:
                _uhmBlockedUntilUtc = DateTime.UtcNow + (result.RetryAfter ?? TimeSpan.FromSeconds(30));
                SetUhmStatus(TerminalUhmStatus.RateLimited, result.Message);
                return false;
            case UhmErrorKind.NotEnabled:
                _uhmBlockedUntilUtc = DateTime.UtcNow + TimeSpan.FromMinutes(5);
                SetUhmStatus(TerminalUhmStatus.NotEnabled, result.Message);
                return false;
            case UhmErrorKind.Unauthorised:
            case UhmErrorKind.MissingToken:
                _uhmBlockedUntilUtc = DateTime.UtcNow + TimeSpan.FromMinutes(5);
                SetUhmStatus(TerminalUhmStatus.Unauthorised, result.Message);
                return false;
            case UhmErrorKind.Forbidden:
                _uhmBlockedUntilUtc = DateTime.UtcNow + TimeSpan.FromMinutes(5);
                SetUhmStatus(TerminalUhmStatus.Forbidden, result.Message);
                return false;
            case UhmErrorKind.Validation:
                SetUhmStatus(TerminalUhmStatus.Error, result.Message);
                return true;
            default:
                SetUhmStatus(TerminalUhmStatus.Error, result.Message);
                return false;
        }
    }

    private void SetUhmStatus(TerminalUhmStatus status, string? detail)
    {
        if (UhmStatus == status && UhmStatusDetail == detail)
            return;
        UhmStatus = status;
        UhmStatusDetail = detail;
        Changed?.Invoke();
    }

    private bool HasUhmToken() => !string.IsNullOrWhiteSpace(_settings.Current.SatelliteStatus.ApiToken);

    private bool WasInQsoList(string? call)
    {
        var station = _tracker.Get(call);
        return station is not null && TerminalQsoTracker.ShowsInQsoList(station);
    }

    /// <summary>One directed frame's effect on QSO-list membership, logged after the tracker lock is released.</summary>
    private readonly record struct QsoListTrace(
        string Via,
        string Change,
        string Call,
        string To,
        string Text,
        DateTime FrameUtc,
        TerminalStationState State,
        bool WeCalled,
        bool TheyCalledUs)
    {
        public static QsoListTrace Capture(
            string via,
            TerminalStation station,
            bool wasListed,
            string? to,
            string? text,
            DateTime frameUtc)
        {
            var listed = TerminalQsoTracker.ShowsInQsoList(station);
            var change = !wasListed && listed ? "added"
                : wasListed && !listed ? "removed"
                : listed ? "updated"
                : "not listed";
            var body = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (body.Length > 80)
                body = body[..80];
            return new QsoListTrace(
                via,
                change,
                station.Call,
                to ?? "",
                body,
                frameUtc,
                station.State,
                station.WeCalled,
                station.TheyCalledUs);
        }

        public void Write(ILogger log) =>
            log.Debug(
                "Terminal QSO list {Change} on {Via} {Call} to {To} at {FrameUtc:HH:mm:ss.fff}: state {State}, we called {WeCalled}, they called {TheyCalled}, text {Text}",
                Change,
                Via,
                Call,
                To,
                FrameUtc,
                State,
                WeCalled,
                TheyCalledUs,
                Text);
    }
}
