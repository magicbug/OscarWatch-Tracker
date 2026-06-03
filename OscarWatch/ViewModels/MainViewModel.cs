using System.Collections.ObjectModel;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using OscarWatch.Core.Display;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using OscarWatch.Core.Services;
using OscarWatch.Theme;
using OscarWatch.Diagnostics;
using OscarWatch.Help;
using OscarWatch.Views;
using Serilog;

namespace OscarWatch.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MainViewModel>();
    private readonly ISettingsService _settings;
    private readonly ITleService _tleService;
    private readonly TrackingOrchestrator _tracking;
    private readonly ILiveTrackingService _liveTracking;
    private readonly ISpeechService _speech;
    private readonly RisingPassAnnouncer _passAnnouncer;
    private readonly PassRecordingCoordinator _passRecordingCoordinator;
    private readonly IAudioRecordingService _recording;
    private readonly IRecordingTaskScheduler _recordingTasks;
    private readonly IRotatorController _rotator;
    private readonly IRigController _rig;
    private readonly ICloudlogRadioSyncService _cloudlog;
    private readonly ISatelliteDatabaseSyncService _transponderDatabaseSync;
    private readonly DispatcherTimer _timer;
    private string? _lastCloudlogErrorShown;
    private string? _recordingPassNoradId;
    private DateTime? _recordingPassAosUtc;

    public FrequencyOverlayViewModel Frequencies { get; }
    public DxStationOverlayViewModel DxStation { get; }
    private DispatcherTimer? _tleRefreshTimer;
    private DispatcherTimer? _passListRefreshTimer;
    private DispatcherTimer? _liveDisplayTimer;
    private static readonly TimeSpan PassListRefreshInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LiveDisplayInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ImminentPassWindow = TimeSpan.FromMinutes(15);
    /// <summary>Coalesce spinner clicks into one CAT write after the user pauses.</summary>

    [ObservableProperty]
    private string _statusText = "Starting…";

    [ObservableProperty]
    private string _utcClock = "";

    [ObservableProperty]
    private string _selectedSatelliteName = "—";

    [ObservableProperty]
    private string _azimuthText = "—";

    [ObservableProperty]
    private string _elevationText = "—";

    [ObservableProperty]
    private string _rangeText = "—";

    [ObservableProperty]
    private string _altitudeText = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSatelliteInEclipse))]
    private bool _showSunlightStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSatelliteInEclipse))]
    private bool _isSatelliteSunlit;

    public bool IsSatelliteInEclipse => ShowSunlightStatus && !IsSatelliteSunlit;

    [ObservableProperty]
    private string _nextPassText = "—";

    [ObservableProperty]
    private bool _showRotatorStatus;

    [ObservableProperty]
    private string _rotatorAzimuthText = "—";

    [ObservableProperty]
    private string _rotatorElevationText = "—";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ParkRotatorCommand))]
    [NotifyPropertyChangedFor(nameof(RotatorParkButtonText))]
    private bool _isRotatorParked;

    public string RotatorParkButtonText => IsRotatorParked ? "Parked" : "Park";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ParkRotatorCommand))]
    private bool _canParkRotator;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ParkRotatorCommand))]
    [NotifyPropertyChangedFor(nameof(StandbyButtonText))]
    [NotifyPropertyChangedFor(nameof(ShowRotatorMenuItem))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRigCatPauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenRotatorManualCommand))]
    private bool _isStandby;

    public string StandbyButtonText => IsStandby ? "Resume" : "Standby";

    public bool ShowRotatorMenuItem => IsStandby && _settings.Current.Rotator.Enabled;

    private bool? _rigCatPausedBeforeStandby;
    private bool _suppressCatPausePersist;

    [ObservableProperty]
    private bool _showRigStatus;

    [ObservableProperty]
    private string _rigStatusText = "—";

    [ObservableProperty]
    private string _rigReceiveText = "—";

    [ObservableProperty]
    private string _rigTransmitText = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RigCatPauseButtonText))]
    private bool _rigCatPaused;

    public string RigCatPauseButtonText => RigCatPaused ? "Resume CAT" : "Pause CAT";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRigCatPauseCommand))]
    private bool _showComPortConflict;

    [ObservableProperty]
    private string _comPortConflictText = "";

    [ObservableProperty]
    private bool _soloFocusedSatellite;

    [ObservableProperty]
    private string? _focusedNoradId;

    public ObservableCollection<IPassListItem> Passes { get; } = [];

    [ObservableProperty]
    private IPassListItem? _selectedListItem;
    private readonly ObservableCollection<SatelliteTrackState> _liveStates = [];

    public ObservableCollection<SatelliteTrackState> LiveStates => _liveStates;

    [ObservableProperty]
    private GroundStation _groundStation = new();

    [ObservableProperty]
    private double _minimumElevationDeg = 5;

    [ObservableProperty]
    private bool _showFootprintMotionArrows = true;

    [ObservableProperty]
    private bool _showGrayline = true;

    [ObservableProperty]
    private bool _isSkyPlotExpanded = true;

    public MainViewModel(
        ISettingsService settings,
        ITleService tleService,
        TrackingOrchestrator tracking,
        ILiveTrackingService liveTracking,
        ISpeechService speech,
        RisingPassAnnouncer passAnnouncer,
        PassRecordingCoordinator passRecordingCoordinator,
        IAudioRecordingService recording,
        IRecordingTaskScheduler recordingTasks,
        IRotatorController rotator,
        IRigController rig,
        ICloudlogRadioSyncService cloudlog,
        ISatelliteDatabaseSyncService transponderDatabaseSync,
        FrequencyOverlayViewModel frequencies,
        DxStationOverlayViewModel dxStation)
    {
        _settings = settings;
        _tleService = tleService;
        _tracking = tracking;
        _liveTracking = liveTracking;
        _speech = speech;
        _passAnnouncer = passAnnouncer;
        _passRecordingCoordinator = passRecordingCoordinator;
        _recording = recording;
        _recordingTasks = recordingTasks;
        _rotator = rotator;
        _rig = rig;
        _cloudlog = cloudlog;
        _transponderDatabaseSync = transponderDatabaseSync;
        Frequencies = frequencies;
        DxStation = dxStation;
        Frequencies.OffsetsChanged += (_, reinitializePass) => RefreshRigFromOverlay(reinitializePass);
        Frequencies.CtcssChanged += (_, _) => OnCtcssSelectorChanged();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();

        _liveDisplayTimer = new DispatcherTimer { Interval = LiveDisplayInterval };
        _liveDisplayTimer.Tick += (_, _) => OnLiveDisplayTick();
    }

    private void OnCtcssSelectorChanged()
    {
        if (ShowComPortConflict)
            return;

        var focused = GetFocusedTrackState(_liveTracking.GetSnapshot(), FocusedNoradId);
        var context = Frequencies.TryBuildRigTrackingContext(focused);
        var rigSettings = GetRigSettingsForController();
        _rig.ApplySelectedCtcss(rigSettings, context);
        _rig.PublishContext(rigSettings, context);
        RefreshRigUi(focused);
    }

    private void RefreshRigFromOverlay(bool reinitializePass = true)
    {
        if (ShowComPortConflict)
            return;

        var focused = GetFocusedTrackState(_liveTracking.GetSnapshot(), FocusedNoradId);
        var context = Frequencies.TryBuildRigTrackingContext(focused);
        _rig.PublishContext(GetRigSettingsForController(), context, reinitializePass);
        RefreshRigUi(focused);
    }

    partial void OnIsSkyPlotExpandedChanged(bool value)
    {
        _settings.Current.SkyPlotExpanded = value;
        _settings.RequestSave();
    }

    private void RefreshRigUi(SatelliteTrackState? focused)
    {
        var rigStatus = _rig.GetStatus();
        SyncOverlayPassbandFromRig();
        UpdateRigDisplay(rigStatus);
        PushCloudlogRadio(focused);
    }

    public async Task InitializeAsync()
    {
        StatusText = "Loading settings…";
        await _settings.LoadAsync().ConfigureAwait(true);
        AppThemeManager.Apply(_settings.Current.Theme);
        RefreshGroundStationFromSettings();
        ShowFootprintMotionArrows = _settings.Current.ShowFootprintMotionArrows;
        ShowGrayline = _settings.Current.ShowGrayline;
        IsSkyPlotExpanded = _settings.Current.SkyPlotExpanded;
        RigCatPaused = _settings.Current.Rig.CatUpdatesPaused;

        StatusText = "Loading TLE catalog…";
        await _tleService.EnsureLoadedAsync().ConfigureAwait(true);

        if (TleSourceResolver.UsesNetwork(_settings.Current.TleSource)
            && _tleService.IsStale(_settings.Current.TleStaleHours))
        {
            try
            {
                StatusText = "Refreshing TLEs…";
                await _tleService.RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "TLE refresh failed during startup");
                StatusText = $"TLE refresh failed: {ex.Message}";
            }
        }

        _liveTracking.Start();
        _liveTracking.RequestReload();
        Tick();
        _timer.Start();
        ConfigurePassListRefreshTimer();
        _liveDisplayTimer?.Start();

        StatusText = "Computing passes…";
        await RefreshPassesAsync().ConfigureAwait(true);
        UpdateStatus();
        Tick();

        if (_settings.Current.TransponderDatabaseCheckOnStartup)
            await CheckTransponderDatabaseUpdatesAsync(showWhenUpToDate: false).ConfigureAwait(true);
    }

    private void Tick()
    {
        UtcClock = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
        MinimumElevationDeg = _settings.Current.MinimumElevationDeg;
        var states = _liveTracking.GetSnapshot();
        SyncLiveStates(states);

        UpdateNextPassCountdown();
        PruneExpiredPasses();
        ProcessPassRecording(states);
        UpdatePassHighlightState();
        var focused = GetFocusedTrackState(states, FocusedNoradId);
        UpdateComPortConflictState();
        _rotator.Update(_settings.Current.Rotator, EnrichRotatorTarget(focused));
        UpdateRotatorDisplay();
        Frequencies.Update(focused);
        DxStation.Update(focused);

        if (ShowComPortConflict)
            _rig.Disconnect();

        RefreshRigUi(focused);
    }

    /// <summary>4 Hz: az/el/range readout and rig doppler context from the live tracking snapshot.</summary>
    private void OnLiveDisplayTick()
    {
        var states = _liveTracking.GetSnapshot();
        UpdateLiveTelemetry(states);
        ProcessVoiceAnnouncements(states);

        var focused = GetFocusedTrackState(states, FocusedNoradId);
        DxStation.Update(focused);

        if (ShowComPortConflict || !_settings.Current.Rig.Enabled)
            return;

        SyncOverlayPassbandFromRig();
        PublishRigTrackingContext(focused);
    }

    private void SyncOverlayPassbandFromRig()
    {
        var rigStatus = _rig.GetStatus();
        Frequencies.SyncRigPassbandAdjustments(rigStatus.ManualReceiveAdjustKHz, rigStatus.ManualTransmitAdjustKHz);
    }

    private void PublishRigTrackingContext(SatelliteTrackState? focused = null)
    {
        if (!_settings.Current.Rig.Enabled || ShowComPortConflict)
            return;

        focused ??= GetFocusedTrackState(_liveTracking.GetSnapshot(), FocusedNoradId);
        _rig.PublishContext(GetRigSettingsForController(), Frequencies.TryBuildRigTrackingContext(focused));
    }

    private RigSettings GetRigSettingsForController()
    {
        var rig = _settings.Current.Rig;
        if (rig.CatUpdatesPaused == RigCatPaused)
            return rig;

        return new RigSettings
        {
            Enabled = rig.Enabled,
            DualRadioEnabled = rig.DualRadioEnabled,
            Downlink = CloneEndpoint(rig.Downlink),
            Uplink = CloneEndpoint(rig.Uplink),
            Type = rig.Type,
            Port = rig.Port,
            BaudRate = rig.BaudRate,
            CivAddress = rig.CivAddress,
            Region = rig.Region,
            DopplerThresholdFmHz = rig.DopplerThresholdFmHz,
            DopplerThresholdLinearHz = rig.DopplerThresholdLinearHz,
            CatDelayMs = rig.CatDelayMs,
            CatUpdatesPaused = RigCatPaused,
            CwKeepSidebandDownlink = rig.CwKeepSidebandDownlink
        };
    }

    private static RigEndpointSettings CloneEndpoint(RigEndpointSettings endpoint) => new()
    {
        Type = endpoint.Type,
        Port = endpoint.Port,
        BaudRate = endpoint.BaudRate,
        Region = endpoint.Region,
        CatDelayMs = endpoint.CatDelayMs
    };

    private void PushCloudlogRadio(SatelliteTrackState? focused)
    {
        var update = Frequencies.TryBuildCloudlogUpdate(focused);
        _cloudlog.Publish(_settings.Current.Cloudlog, update);

        var error = _cloudlog.LastError;
        if (!string.IsNullOrEmpty(error) && !string.Equals(_lastCloudlogErrorShown, error, StringComparison.Ordinal))
        {
            _lastCloudlogErrorShown = error;
            StatusText = $"Cloudlog: {error}";
        }
        else if (string.IsNullOrEmpty(error))
            _lastCloudlogErrorShown = null;
    }

    private void UpdateComPortConflictState()
    {
        ShowComPortConflict = SerialPortConflictHelper.TryDescribeConflict(
            _settings.Current.Rotator,
            _settings.Current.Rig,
            out var message);
        ComPortConflictText = message;
    }

    partial void OnRigCatPausedChanged(bool value)
    {
        if (!_suppressCatPausePersist && _settings.Current.Rig.CatUpdatesPaused != value)
        {
            _settings.Current.Rig.CatUpdatesPaused = value;
            _settings.RequestSave();
        }

        SyncRigAfterOperationalModeChange();
    }

    private void SetRigCatPausedWithoutPersist(bool value)
    {
        _suppressCatPausePersist = true;
        try
        {
            RigCatPaused = value;
        }
        finally
        {
            _suppressCatPausePersist = false;
        }
    }

    internal bool PrepareForShutdown()
    {
        if (!IsStandby || _rigCatPausedBeforeStandby is not { } wasPaused)
            return false;

        if (_settings.Current.Rig.CatUpdatesPaused == wasPaused)
            return false;

        _settings.Current.Rig.CatUpdatesPaused = wasPaused;
        return true;
    }

    internal Task SaveSettingsAsync() => _settings.SaveAsync();

    private void UpdateRotatorDisplay()
    {
        if (!_settings.Current.Rotator.Enabled)
        {
            ShowRotatorStatus = false;
            return;
        }

        ShowRotatorStatus = true;
        var status = _rotator.GetPositionStatus();
        RotatorAzimuthText = FormatRotatorAzimuthText(status);
        RotatorElevationText = status is { IsConnected: true, ElevationDeg: not null }
            ? $"{status.ElevationDeg.Value}°"
            : "—";
        IsRotatorParked = status.IsParked;
        CanParkRotator = status.IsConnected && !IsStandby && !status.IsParked;
    }

    internal static string FormatRotatorAzimuthText(RotatorPositionStatus status)
    {
        if (!status.IsConnected)
            return "—";

        if (status.CommandedAzimuthDeg is { } commanded
            && status.CompassAzimuthDeg is { } compass
            && commanded != compass)
            return $"{commanded}° ({compass}° sat)";

        if (status.AzimuthDeg is { } polled)
            return $"{polled}°";

        if (status.CommandedAzimuthDeg is { } commandedOnly)
            return $"{commandedOnly}°";

        return "—";
    }

    [RelayCommand]
    private void ToggleStandby()
    {
        IsStandby = !IsStandby;

        if (IsStandby)
        {
            _rigCatPausedBeforeStandby = RigCatPaused;
            if (!RigCatPaused)
                SetRigCatPausedWithoutPersist(true);
            _rotator.SetStandby(true, _settings.Current.Rotator);
        }
        else
        {
            var restorePaused = _rigCatPausedBeforeStandby ?? false;
            _rigCatPausedBeforeStandby = null;
            RigCatPaused = restorePaused;
            _rotator.SetStandby(false, _settings.Current.Rotator);
            RefreshRigFromOverlay(reinitializePass: true);
        }

        UpdateRotatorDisplay();
    }

    [RelayCommand(CanExecute = nameof(CanParkRotator))]
    private void ParkRotator()
    {
        _rotator.Park(_settings.Current.Rotator);
        UpdateRotatorDisplay();
    }

    [RelayCommand(CanExecute = nameof(ShowRotatorMenuItem))]
    private async Task OpenRotatorManualAsync()
    {
        var vm = App.Services.GetRequiredService<RotatorManualViewModel>();
        vm.Initialize(UpdateRotatorDisplay);
        var window = new RotatorManualWindow { DataContext = vm };
        if (App.MainWindow is null)
            return;

        await window.ShowDialog(App.MainWindow);
        UpdateRotatorDisplay();
    }

    [RelayCommand(CanExecute = nameof(CanToggleRigCatPause))]
    private void ToggleRigCatPause() => RigCatPaused = !RigCatPaused;

    private bool CanToggleRigCatPause() => !IsStandby && !ShowComPortConflict;

    private void UpdateRigDisplay(RigConnectionStatus? status = null)
    {
        if (!_settings.Current.Rig.Enabled)
        {
            ShowRigStatus = false;
            return;
        }

        ShowRigStatus = true;
        if (ShowComPortConflict)
        {
            RigStatusText = ComPortConflictText;
            RigReceiveText = "—";
            RigTransmitText = "—";
            return;
        }

        status ??= _rig.GetStatus();
        RigStatusText = status.StatusMessage ?? (status.IsConnected ? "Connected" : "Disconnected");
        RigReceiveText = FormatSidebarFrequency(status.LastReceiveHz, Frequencies.RadioReceiveText, status.IsConnected);
        RigTransmitText = FormatSidebarFrequency(status.LastTransmitHz, Frequencies.RadioTransmitText, status.IsConnected);
    }

    private static string FormatSidebarFrequency(long? rigHz, string overlayText, bool rigConnected)
    {
        if (rigHz is { } hz && IcomCivCodec.IsValidSatelliteFrequencyHz(hz))
            return FrequencyDisplayFormat.FormatMHz(hz / 1000.0);

        if (rigConnected && !string.IsNullOrWhiteSpace(overlayText) && overlayText != "—")
            return overlayText;

        return "—";
    }

    private void ConfigurePassListRefreshTimer()
    {
        _passListRefreshTimer?.Stop();
        _passListRefreshTimer = new DispatcherTimer { Interval = PassListRefreshInterval };
        _passListRefreshTimer.Tick += async (_, _) => await RefreshPassesAsync();
        _passListRefreshTimer.Start();
    }

    private void PruneExpiredPasses()
    {
        var now = DateTime.UtcNow;
        var expired = Passes.OfType<PassRowViewModel>().Where(p => p.LosUtc < now).ToList();
        if (expired.Count == 0)
            return;

        foreach (var pass in expired)
            Passes.Remove(pass);

        for (var i = Passes.Count - 1; i >= 0; i--)
        {
            if (Passes[i] is PassDayHeaderViewModel
                && (i + 1 >= Passes.Count || Passes[i + 1] is PassDayHeaderViewModel))
                Passes.RemoveAt(i);
        }
    }

    private SatelliteTrackState? EnrichRotatorTarget(SatelliteTrackState? state)
    {
        if (state is null || state.LookAngles is null)
            return state;

        var rotator = _settings.Current.Rotator;
        if (!rotator.Enabled || !rotator.SmartAzimuth450 || rotator.MaxAzimuthDeg <= 360)
            return state;

        var ahead = _tracking.TryGetAheadAzimuthDeg(state.NoradId);
        if (ahead is null)
            return state;

        return new SatelliteTrackState
        {
            Name = state.Name,
            NoradId = state.NoradId,
            Subpoint = state.Subpoint,
            LookAngles = state.LookAngles,
            AheadAzimuthDeg = ahead,
            GroundTrack = state.GroundTrack,
            Footprint = state.Footprint,
            FootprintRadiusDeg = state.FootprintRadiusDeg,
            IsSunlit = state.IsSunlit
        };
    }

    private static SatelliteTrackState? GetFocusedTrackState(IReadOnlyList<SatelliteTrackState> states, string? focusedNoradId)
    {
        if (string.IsNullOrEmpty(focusedNoradId))
            return null;

        return states.FirstOrDefault(s => string.Equals(s.NoradId, focusedNoradId, StringComparison.Ordinal));
    }

    private void ProcessVoiceAnnouncements(IReadOnlyList<SatelliteTrackState> states)
    {
        var voiceSettings = _settings.Current.VoiceAnnouncements;
        if (voiceSettings is null || !voiceSettings.Enabled)
            return;

        if (!_speech.IsAvailable)
            return;

        _passAnnouncer.Process(states, voiceSettings, text =>
        {
            Log.Information("Voice announcement: {Text}", text);
            var voiceName = voiceSettings.VoiceName;
            _ = SpeakAnnouncementAsync(text, voiceName);
        });
    }

    private async Task SpeakAnnouncementAsync(string text, string voiceName)
    {
        try
        {
            await _speech.SpeakAsync(
                text,
                string.IsNullOrWhiteSpace(voiceName) ? null : voiceName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Voice announcement failed: {Text}", text);
        }
    }

    private void UpdatePassHighlightState()
    {
        SyncRecordingPassIdentity();
        var now = DateTime.UtcNow;
        foreach (var pass in Passes.OfType<PassRowViewModel>())
            pass.UpdateDisplay(now, ImminentPassWindow, IsPassBeingRecorded(pass));
    }

    /// <summary>Remember which list row started recording so later passes with the same name stay unhighlighted.</summary>
    private void SyncRecordingPassIdentity()
    {
        if (!_recording.IsRecording || AudioRecordingSessions.IsManualTest(_recording))
        {
            _recordingPassNoradId = null;
            _recordingPassAosUtc = null;
            return;
        }

        var noradId = _recording.ActiveNoradId;
        if (string.IsNullOrEmpty(noradId))
        {
            _recordingPassNoradId = null;
            _recordingPassAosUtc = null;
            return;
        }

        if (string.Equals(_recordingPassNoradId, noradId, StringComparison.Ordinal)
            && _recordingPassAosUtc is not null
            && Passes.OfType<PassRowViewModel>().Any(p =>
                string.Equals(p.NoradId, noradId, StringComparison.Ordinal) && p.AosUtc == _recordingPassAosUtc))
            return;

        var pass = FindPassForRecording(noradId, DateTime.UtcNow);
        _recordingPassNoradId = noradId;
        _recordingPassAosUtc = pass?.AosUtc;
    }

    private bool IsPassBeingRecorded(PassRowViewModel pass) =>
        _recording.IsRecording
        && !AudioRecordingSessions.IsManualTest(_recording)
        && _recordingPassAosUtc is not null
        && string.Equals(pass.NoradId, _recordingPassNoradId, StringComparison.Ordinal)
        && pass.AosUtc == _recordingPassAosUtc;

    private PassRowViewModel? FindPassForRecording(string noradId, DateTime utcNow)
    {
        var rows = Passes.OfType<PassRowViewModel>()
            .Where(p => string.Equals(p.NoradId, noradId, StringComparison.Ordinal))
            .OrderBy(p => p.AosUtc)
            .ToList();

        if (rows.Count == 0)
            return null;

        var inProgress = rows.LastOrDefault(p => utcNow >= p.AosUtc && utcNow <= p.LosUtc);
        if (inProgress is not null)
            return inProgress;

        // Recording can start above the elevation threshold before horizon AOS in the pass list.
        return rows.FirstOrDefault(p => utcNow < p.LosUtc);
    }

    private void ProcessPassRecording(IReadOnlyList<SatelliteTrackState> states)
    {
        var settings = _settings.Current.PassRecording ?? new PassRecordingSettings();
        if (!settings.Enabled)
        {
            if (_recording.IsRecording && !AudioRecordingSessions.IsManualTest(_recording))
                _recordingTasks.Schedule(() => _recording.StopAsync(), "stop recording (disabled in settings UI)");
            _passRecordingCoordinator.ResetTracking();
            return;
        }

        var focusedNorad = FocusedNoradId;
        var focused = string.IsNullOrEmpty(focusedNorad)
            ? null
            : states.FirstOrDefault(s => string.Equals(s.NoradId, focusedNorad, StringComparison.Ordinal));
        _passRecordingCoordinator.Process(
            focusedNorad,
            focused,
            settings,
            _recording,
            DateTime.UtcNow);
    }

    private void SyncLiveStates(IReadOnlyList<SatelliteTrackState> states)
    {
        _liveStates.Clear();
        foreach (var s in states)
            _liveStates.Add(s);
    }

    private void UpdateLiveTelemetry(IReadOnlyList<SatelliteTrackState> states)
    {
        var target = GetFocusedTrackState(states, FocusedNoradId);

        if (target is null)
        {
            SelectedSatelliteName = "—";
            AzimuthText = "—";
            ElevationText = "—";
            RangeText = "—";
            AltitudeText = "—";
            ShowSunlightStatus = false;
            return;
        }

        SelectedSatelliteName = target.Name;
        AltitudeText = $"{target.Subpoint.AltitudeKm:F0} km";
        ShowSunlightStatus = true;
        IsSatelliteSunlit = target.IsSunlit;

        if (target.LookAngles is not { } la)
        {
            AzimuthText = "—";
            ElevationText = "below horizon";
            RangeText = "—";
            return;
        }

        AzimuthText = $"{la.AzimuthDeg:F1}°";
        ElevationText = $"{la.ElevationDeg:F1}°";
        RangeText = $"{la.RangeKm:F0} km";
    }

    partial void OnSelectedListItemChanged(IPassListItem? value)
    {
        if (value is not PassRowViewModel row)
            return;

        var pass = Passes.OfType<PassRowViewModel>().FirstOrDefault(p => p.NoradId == row.NoradId) ?? row;
        if (!ReferenceEquals(SelectedListItem, pass))
            SelectedListItem = pass;

        if (string.Equals(FocusedNoradId, row.NoradId, StringComparison.Ordinal))
        {
            ApplySatelliteFocus(row.NoradId);
            return;
        }

        FocusedNoradId = row.NoradId;
    }

    partial void OnFocusedNoradIdChanged(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            SoloFocusedSatellite = false;
            return;
        }

        ApplySatelliteFocus(value);

        var pass = Passes.OfType<PassRowViewModel>().FirstOrDefault(p => p.NoradId == value);
        if (pass is not null && !ReferenceEquals(SelectedListItem, pass))
            SelectedListItem = pass;
    }

    private void ApplySatelliteFocus(string noradId)
    {
        var states = _liveStates.Count > 0 ? (IReadOnlyList<SatelliteTrackState>)_liveStates : _liveTracking.GetSnapshot();
        if (states.Count == 0)
            return;

        UpdateLiveTelemetry(states);
        var focused = GetFocusedTrackState(states, noradId);
        Frequencies.Update(focused);
        DxStation.Update(focused);
        PushCloudlogRadio(focused);
        RefreshRigFromOverlay(reinitializePass: true);
    }

    private void SyncRigAfterOperationalModeChange()
    {
        if (!_settings.Current.Rig.Enabled || ShowComPortConflict)
            return;

        PublishRigTrackingContext();
        var focused = GetFocusedTrackState(_liveTracking.GetSnapshot(), FocusedNoradId);
        RefreshRigUi(focused);
    }

    private void UpdateNextPassCountdown()
    {
        var next = Passes.OfType<PassRowViewModel>().FirstOrDefault();
        if (next is null)
        {
            NextPassText = "No upcoming passes";
            return;
        }

        var aos = next.AosUtc;
        var delta = aos - DateTime.UtcNow;
        if (delta.TotalSeconds < 0)
            NextPassText = $"{next.SatelliteName} — in progress";
        else
            NextPassText = $"{next.SatelliteName} AOS in {delta:hh\\:mm\\:ss}";
    }

    private void UpdateStatus()
    {
        var catalogCount = _tleService.Catalog.Count;
        var count = _tleService.GetEnabledSatellites(_settings.Current).Count;
        var source = _tleService.ActiveSourceLabel;
        var tleAge = _tleService.LastFetchedUtc.HasValue
            ? $"TLE {DateTime.UtcNow - _tleService.LastFetchedUtc.Value:hh\\:mm} ago ({source})"
            : catalogCount > 0 ? $"TLE cached ({source})" : "TLE not loaded";

        StatusText = catalogCount == 0
            ? "No TLE data — check Settings → TLE or use Satellites → Refresh TLEs"
            : count == 0
                ? $"{tleAge} | 0 enabled — Satellites menu → enable some"
                : $"{tleAge} | {count} satellite(s) enabled";
    }

    [RelayCommand]
    private async Task OpenPassPlanningAsync()
    {
        var vm = App.Services.GetRequiredService<PassPlanningViewModel>();
        vm.Initialize();
        var window = new PassPlanningWindow { DataContext = vm };
        if (App.MainWindow is null)
            return;

        var appliedActive = await window.ShowDialog<bool?>(App.MainWindow) == true;
        if (appliedActive)
            RefreshGroundStationFromSettings();

        await RefreshPassesAsync();
        if (appliedActive)
            Tick();
    }

    [RelayCommand]
    private void ToggleSoloFocusedSatellite()
    {
        if (!SoloFocusedSatellite)
        {
            if (string.IsNullOrEmpty(FocusedNoradId))
            {
                var states = _liveTracking.GetSnapshot();
                if (states.Count == 0)
                    return;

                FocusedNoradId = states[0].NoradId;
            }
        }

        SoloFocusedSatellite = !SoloFocusedSatellite;
    }

    [RelayCommand]
    private async Task OpenMutualPassFinderAsync()
    {
        var vm = App.Services.GetRequiredService<MutualPassViewModel>();
        vm.Initialize();
        var window = new MutualPassWindow { DataContext = vm };
        if (App.MainWindow is null)
            return;

        await window.ShowDialog(App.MainWindow);
    }

    [RelayCommand]
    private async Task OpenSunlightPredictionAsync()
    {
        var vm = App.Services.GetRequiredService<SunlightPredictionViewModel>();
        await vm.InitializeAsync();
        var window = new SunlightPredictionWindow { DataContext = vm };
        if (App.MainWindow is null)
            return;

        await window.ShowDialog(App.MainWindow);
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var vm = App.Services.GetRequiredService<SettingsViewModel>();
        var window = new SettingsWindow { DataContext = vm };
        if (App.MainWindow is null)
            return;

        var saved = await window.ShowDialog<bool?>(App.MainWindow) == true;

        if (saved)
        {
            ConfigureTleAutoUpdateTimer();
            await ReloadTleCatalogAfterSettingsAsync().ConfigureAwait(true);
            _liveTracking.RequestReload();
            _rotator.Disconnect();
            _rig.Disconnect();
            _cloudlog.ResetThrottle();
            if (!_settings.Current.PassRecording.Enabled && _recording.IsRecording)
                await _recording.StopAsync();
            _passRecordingCoordinator.ResetTracking();
            await RefreshPassesAsync();
            UpdateStatus();
            RefreshGroundStationFromSettings();
            ShowFootprintMotionArrows = _settings.Current.ShowFootprintMotionArrows;
            RigCatPaused = _settings.Current.Rig.CatUpdatesPaused;
            _liveDisplayTimer?.Start();
            Tick();
        }
    }

    private void ConfigureTleAutoUpdateTimer()
    {
        _tleRefreshTimer?.Stop();
        _tleRefreshTimer = null;

        if (_settings.Current.TleAutoUpdate != TleAutoUpdateMode.EverySixHours)
            return;

        _tleRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromHours(TleAutoUpdate.IntervalHours)
        };
        _tleRefreshTimer.Tick += async (_, _) => await MaybeAutoRefreshTlesAsync(force: true);
        _tleRefreshTimer.Start();
    }

    private async Task ReloadTleCatalogAfterSettingsAsync()
    {
        _tleService.InvalidateCatalog();
        try
        {
            StatusText = "Loading TLE catalog…";
            await _tleService.EnsureLoadedAsync().ConfigureAwait(true);

            var source = _settings.Current.TleSource;
            if (TleSourceResolver.UsesNetwork(source)
                || !string.IsNullOrWhiteSpace(TleSourceResolver.TryGetLocalFilePath(source)))
            {
                StatusText = "Refreshing TLEs…";
                await _tleService.RefreshAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TLE reload after settings failed");
            StatusText = $"TLE reload failed: {ex.Message}";
        }
    }

    private async Task MaybeAutoRefreshTlesAsync(bool force = false)
    {
        if (!TleSourceResolver.UsesNetwork(_settings.Current.TleSource))
            return;

        var mode = _settings.Current.TleAutoUpdate;
        if (mode == TleAutoUpdateMode.Manual && !force)
            return;

        if (!force && !TleAutoUpdate.ShouldRefreshOnStartup(mode))
            return;

        if (!force && !_tleService.IsStale(TleAutoUpdate.IntervalHours))
            return;

        try
        {
            StatusText = "Refreshing TLEs…";
            await _tleService.RefreshAsync().ConfigureAwait(true);
            _liveTracking.RequestReload();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TLE auto-refresh failed");
            StatusText = $"TLE refresh failed: {ex.Message}";
        }
    }

    private void RefreshGroundStationFromSettings()
    {
        var gs = _settings.Current.GroundStation;
        GroundStation = new GroundStation
        {
            DisplayName = gs.DisplayName,
            LatitudeDeg = gs.LatitudeDeg,
            LongitudeDeg = gs.LongitudeDeg,
            AltitudeMetersAsl = gs.AltitudeMetersAsl,
            GridSquare = gs.GridSquare
        };
    }

    [RelayCommand]
    private void CloseApplication()
    {
        _liveTracking.Dispose();
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    [RelayCommand]
    private async Task OpenAboutAsync()
    {
        var window = new AboutWindow();
        if (App.MainWindow is null)
            window.Show();
        else
            await window.ShowDialog(App.MainWindow);
    }

    [RelayCommand]
    private void OpenRecordingsFolder()
    {
        try
        {
            RecordingFileNameFormat.OpenOutputFolder(_settings.Current.PassRecording.OutputFolder);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open recordings directory");
            StatusText = "Could not open recordings folder";
        }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            AppLogging.OpenLogDirectory();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open log directory");
            StatusText = "Could not open log folder";
        }
    }

    [RelayCommand]
    private void OpenHelp()
    {
        if (HelpLauncher.TryOpenHelp())
            return;

        Log.Warning("Help folder not found next to the application");
        StatusText = "Help files not found — reinstall or run from a published build";
    }

    [RelayCommand]
    private async Task OpenSatellitesAsync()
    {
        await _tleService.EnsureLoadedAsync();
        var vm = new SatellitePickerViewModel(_settings, _tleService);
        var window = new SatellitePickerWindow { DataContext = vm };
        var saved = App.MainWindow is not null
            && await window.ShowDialog<bool>(App.MainWindow);

        if (saved)
        {
            _liveTracking.RequestReload();
            await RefreshPassesAsync();
            UpdateStatus();
        }
    }

    [RelayCommand]
    private async Task OpenSatelliteDatabaseAsync()
    {
        var vm = App.Services.GetRequiredService<SatelliteDatabaseEditorViewModel>();
        var window = new SatelliteDatabaseWindow { DataContext = vm };
        if (App.MainWindow is null)
            return;

        await window.ShowDialog<bool?>(App.MainWindow);
        Frequencies.ReloadFromDatabase();
        Tick();
    }

    [RelayCommand]
    private async Task UpdateTransponderDatabaseAsync()
    {
        await CheckTransponderDatabaseUpdatesAsync(showWhenUpToDate: true);
    }

    private async Task CheckTransponderDatabaseUpdatesAsync(bool showWhenUpToDate)
    {
        if (App.MainWindow is null)
            return;

        try
        {
            StatusText = "Checking transponder database…";
            var plan = await _transponderDatabaseSync.FetchMergePlanAsync().ConfigureAwait(true);
            if (!plan.HasChanges)
            {
                if (showWhenUpToDate)
                    StatusText = "Transponder database is up to date.";
                else
                    UpdateStatus();

                return;
            }

            if (await TransponderDatabaseMergeDialog.TryShowAsync(App.MainWindow, plan, _transponderDatabaseSync))
            {
                Frequencies.ReloadFromDatabase();
                Tick();
                StatusText = "Transponder database updated.";
            }
            else
            {
                UpdateStatus();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Transponder database update check failed");
            if (showWhenUpToDate)
                StatusText = $"Transponder update failed: {ex.Message}";
            else
                UpdateStatus();
        }
    }

    [RelayCommand]
    private async Task RefreshTlesAsync()
    {
        await MaybeAutoRefreshTlesAsync(force: true);
        await RefreshPassesAsync();
        UpdateStatus();
    }

    private async Task RefreshPassesAsync()
    {
        var selectedNorad = (SelectedListItem as PassRowViewModel)?.NoradId;
        var passes = await _tracking.GetUpcomingPassesAsync().ConfigureAwait(false);

        void Apply()
        {
            Passes.Clear();
            DateOnly? currentDay = null;
            foreach (var p in passes.Take(50))
            {
                var day = PassDisplayFormat.GetLocalDate(p.AosUtc);
                if (currentDay != day)
                {
                    currentDay = day;
                    Passes.Add(new PassDayHeaderViewModel
                    {
                        DateLabel = PassDisplayFormat.FormatDayHeader(p.AosUtc)
                    });
                }

                Passes.Add(PassRowViewModel.From(p));
            }

            if (selectedNorad is not null)
                SelectedListItem = Passes.OfType<PassRowViewModel>().FirstOrDefault(p => p.NoradId == selectedNorad);

            UpdatePassHighlightState();
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            await Dispatcher.UIThread.InvokeAsync(Apply);
    }
}

public interface IPassListItem;

public sealed class PassDayHeaderViewModel : IPassListItem
{
    public string DateLabel { get; init; } = "";
}

public partial class PassRowViewModel : ObservableObject, IPassListItem
{
    [ObservableProperty]
    private PassRowHighlight _highlight;

    [ObservableProperty]
    private string _badgeText = "";

    [ObservableProperty]
    private bool _showBadge;

    public string SatelliteName { get; init; } = "";
    public string NoradId { get; init; } = "";
    public string AosLocal { get; init; } = "";
    public string LosLocal { get; init; } = "";
    public string TcaLocal { get; init; } = "";
    public string TimeRangeLine { get; init; } = "";
    public string DetailsLine { get; init; } = "";
    public DateTime AosUtc { get; init; }
    public DateTime LosUtc { get; init; }

    public void UpdateDisplay(DateTime utcNow, TimeSpan imminentWindow, bool isRecording)
    {
        if (isRecording)
        {
            if (Highlight != PassRowHighlight.Recording)
                Highlight = PassRowHighlight.Recording;
            if (BadgeText != "REC")
                BadgeText = "REC";
            if (!ShowBadge)
                ShowBadge = true;
            return;
        }

        UpdateHighlight(utcNow, imminentWindow);
    }

    public void UpdateHighlight(DateTime utcNow, TimeSpan imminentWindow)
    {
        PassRowHighlight next;
        if (utcNow >= AosUtc && utcNow <= LosUtc)
            next = PassRowHighlight.InProgress;
        else if (utcNow < AosUtc && AosUtc - utcNow <= imminentWindow)
            next = PassRowHighlight.Imminent;
        else
            next = PassRowHighlight.None;

        if (Highlight != next)
            Highlight = next;

        switch (next)
        {
            case PassRowHighlight.Imminent:
            {
                var countdown = PassDisplayFormat.FormatCountdownToAos(utcNow, AosUtc);
                if (BadgeText != countdown)
                    BadgeText = countdown;
                if (!ShowBadge)
                    ShowBadge = true;
                break;
            }
            case PassRowHighlight.InProgress:
            {
                const string passingLabel = "Passing";
                if (BadgeText != passingLabel)
                    BadgeText = passingLabel;
                if (!ShowBadge)
                    ShowBadge = true;
                break;
            }
            default:
                if (ShowBadge)
                    ShowBadge = false;
                if (BadgeText.Length > 0)
                    BadgeText = "";
                break;
        }
    }

    public static PassRowViewModel From(PassInfo p)
    {
        var (aos, los) = PassDisplayFormat.FormatLocalTimes(p.AosUtc, p.LosUtc);

        return new()
        {
            SatelliteName = p.SatelliteName,
            NoradId = p.NoradId,
            AosUtc = p.AosUtc,
            LosUtc = p.LosUtc,
            AosLocal = aos,
            LosLocal = los,
            TcaLocal = PassDisplayFormat.FormatLocal(p.MaxElevationUtc),
            TimeRangeLine = PassDisplayFormat.FormatTimeRangeLine(p.AosUtc, p.LosUtc),
            DetailsLine = PassDisplayFormat.FormatDetailsLine(p.MaxElevationDeg, p.Duration)
        };
    }
}
