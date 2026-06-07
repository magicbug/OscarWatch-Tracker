using System.Collections.ObjectModel;
using System.Diagnostics;
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
using OscarWatch.Localization;
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
    private readonly ICloudlogLookupService _cloudlogLookup;
    private readonly ISatelliteDatabaseSyncService _transponderDatabaseSync;
    private readonly IGitHubReleaseService _githubRelease;
    private readonly IHamsAtRovesService _hamsAtRoves;
    private readonly ILocalizationService _l;
    private readonly DispatcherTimer _timer;
    private DispatcherTimer? _appUpdateCheckTimer;
    private static readonly TimeSpan AppUpdateCheckInterval = TimeSpan.FromHours(24);
    private string? _lastCloudlogErrorShown;
    private string? _recordingPassNoradId;
    private DateTime? _recordingPassAosUtc;

    public FrequencyOverlayViewModel Frequencies { get; }
    public DxStationOverlayViewModel DxStation { get; }
    private DispatcherTimer? _tleRefreshTimer;
    private DispatcherTimer? _passListRefreshTimer;
    private DispatcherTimer? _hamsAtRefreshTimer;
    private DispatcherTimer? _liveDisplayTimer;
    private static readonly TimeSpan PassListRefreshInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LiveDisplayInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ImminentPassWindow = TimeSpan.FromMinutes(15);
    public const double MapTimeOffsetMinMinutes = -120;
    public const double MapTimeOffsetMaxMinutes = 120;
    private const double MapTimeOffsetStepMinutes = 5;
    private const double MapTimeOffsetLargeStepMinutes = 15;
    /// <summary>Coalesce spinner clicks into one CAT write after the user pauses.</summary>

    [ObservableProperty]
    private string _statusText = "";

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

    public string RotatorParkButtonText => IsRotatorParked
        ? _l.Get("Main.Rotator.Parked")
        : _l.Get("Main.Rotator.Park");

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

    public string StandbyButtonText => IsStandby
        ? _l.Get("Main.Standby.Resume")
        : _l.Get("Main.Standby.Pause");

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

    public string RigCatPauseButtonText => RigCatPaused
        ? _l.Get("Main.Radio.CatResume")
        : _l.Get("Main.Radio.CatPause");

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
    private bool _showGreylineOverlay;

    [ObservableProperty]
    private DateTime _mapDisplayUtc = DateTime.UtcNow;

    [ObservableProperty]
    private bool _isSkyPlotExpanded = true;

    [ObservableProperty]
    private bool _isPassesExpanded = true;

    [ObservableProperty]
    private bool _isHamsAtRovesExpanded = true;

    [ObservableProperty]
    private double _hamsAtRovesPanelHeight = 180;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHamsAtRovesStatus))]
    private string _hamsAtRovesStatusText = "";

    public bool ShowHamsAtRovesStatus => !string.IsNullOrWhiteSpace(HamsAtRovesStatusText);

    public ObservableCollection<HamsAtRoveRowViewModel> HamsAtRoves { get; } = [];

    public event Action? SidebarLayoutInvalidated;

    public bool ShowHamsAtRovesPanel =>
        _settings.Current.HamsAt.Enabled
        && !string.IsNullOrWhiteSpace(_settings.Current.HamsAt.ApiKey);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMapTimeScrubbing))]
    [NotifyPropertyChangedFor(nameof(MapTimeStatusText))]
    private double _mapTimeOffsetMinutes;

    public bool IsMapTimeScrubbing => Math.Abs(MapTimeOffsetMinutes) >= 0.01;

    public string MapTimeStatusText
    {
        get
        {
            if (!IsMapTimeScrubbing)
                return _l.Get("Main.MapTimeStatus.Live");

            var offset = TimeSpan.FromMinutes(MapTimeOffsetMinutes);
            var sign = offset >= TimeSpan.Zero ? "+" : "−";
            var magnitude = offset.Duration();
            var spanText = magnitude >= TimeSpan.FromHours(1)
                ? $"{sign}{magnitude:h\\:mm\\:ss}"
                : $"{sign}{magnitude:m\\:ss}";
            return _l.Get("MapTime.FromNow", spanText);
        }
    }

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
        ICloudlogLookupService cloudlogLookup,
        ISatelliteDatabaseSyncService transponderDatabaseSync,
        IGitHubReleaseService githubRelease,
        IHamsAtRovesService hamsAtRoves,
        ILocalizationService localization,
        FrequencyOverlayViewModel frequencies,
        DxStationOverlayViewModel dxStation)
    {
        _l = localization;
        _statusText = _l.Get("Status.LoadingSettings");
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
        _cloudlogLookup = cloudlogLookup;
        _cloudlog.StateChanged += OnCloudlogStateChanged;
        _transponderDatabaseSync = transponderDatabaseSync;
        _githubRelease = githubRelease;
        _hamsAtRoves = hamsAtRoves;
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

    partial void OnIsPassesExpandedChanged(bool value)
    {
        _settings.Current.PassesExpanded = value;
        _settings.RequestSave();
        SidebarLayoutInvalidated?.Invoke();
    }

    partial void OnIsHamsAtRovesExpandedChanged(bool value)
    {
        _settings.Current.HamsAtRovesExpanded = value;
        _settings.RequestSave();
        SidebarLayoutInvalidated?.Invoke();
    }

    public const double HamsAtRovesMinPanelHeight = 80;
    public const double HamsAtRovesMaxPanelHeight = 400;

    private void ApplyHamsAtSidebarSettings()
    {
        IsHamsAtRovesExpanded = _settings.Current.HamsAtRovesExpanded;
        HamsAtRovesPanelHeight = Math.Clamp(
            _settings.Current.HamsAtRovesPanelHeightPx,
            HamsAtRovesMinPanelHeight,
            HamsAtRovesMaxPanelHeight);
        OnPropertyChanged(nameof(ShowHamsAtRovesPanel));
        SidebarLayoutInvalidated?.Invoke();
    }

    public void SetHamsAtRovesPanelHeight(double height, double? maxHeight = null)
    {
        var max = maxHeight ?? HamsAtRovesMaxPanelHeight;
        HamsAtRovesPanelHeight = Math.Clamp(height, HamsAtRovesMinPanelHeight, max);
    }

    public void PersistHamsAtRovesPanelHeight()
    {
        _settings.Current.HamsAtRovesPanelHeightPx = (int)Math.Round(HamsAtRovesPanelHeight);
        _settings.RequestSave();
    }

    partial void OnHamsAtRovesPanelHeightChanged(double value) =>
        SidebarLayoutInvalidated?.Invoke();

    public void OpenHamsAtRove(HamsAtRoveRowViewModel? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.Url))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = row.Url,
            UseShellExecute = true
        });
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
        StatusText = _l.Get("Status.LoadingSettingsFull");
        await _settings.LoadAsync().ConfigureAwait(true);
        AppThemeManager.Apply(_settings.Current.Theme);
        RefreshGroundStationFromSettings();
        ShowFootprintMotionArrows = _settings.Current.ShowFootprintMotionArrows;
        ShowGreylineOverlay = _settings.Current.ShowGreylineOverlay;
        IsSkyPlotExpanded = _settings.Current.SkyPlotExpanded;
        IsPassesExpanded = _settings.Current.PassesExpanded;
        ApplyHamsAtSidebarSettings();
        RigCatPaused = _settings.Current.Rig.CatUpdatesPaused;

        StatusText = _l.Get("Status.LoadingTle");
        await _tleService.EnsureLoadedAsync().ConfigureAwait(true);

        if (TleSourceResolver.UsesNetwork(_settings.Current.TleSource)
            && _tleService.IsStale(_settings.Current.TleStaleHours))
        {
            try
            {
                StatusText = _l.Get("Status.RefreshingTle");
                await _tleService.RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "TLE refresh failed during startup");
                StatusText = _l.Get("Status.TleRefreshFailed", ex.Message);
            }
        }

        _liveTracking.Start();
        _liveTracking.RequestReload();
        Tick();
        _timer.Start();
        ConfigurePassListRefreshTimer();
        ConfigureHamsAtRefreshTimer();
        _liveDisplayTimer?.Start();

        StatusText = _l.Get("Status.ComputingPasses");
        await RefreshPassesAsync().ConfigureAwait(true);
        await RefreshHamsAtRovesAsync().ConfigureAwait(true);
        UpdateStatus();
        Tick();

        if (_settings.Current.TransponderDatabaseCheckOnStartup)
            await CheckTransponderDatabaseUpdatesAsync(showWhenUpToDate: false).ConfigureAwait(true);

        ConfigureAppUpdateCheckTimer();

        if (_settings.Current.AppUpdateCheckEnabled)
            _ = RunStartupAppUpdateCheckAsync();
    }

    private async Task RunStartupAppUpdateCheckAsync()
    {
        try
        {
            await CheckForAppUpdateAsync(manual: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Startup application update check failed");
        }
    }

    private void Tick()
    {
        UpdateUtcClockDisplay();
        MapDisplayUtc = DateTime.UtcNow + TimeSpan.FromMinutes(MapTimeOffsetMinutes);
        MinimumElevationDeg = _settings.Current.MinimumElevationDeg;
        var mapStates = _liveTracking.GetSnapshot();
        SyncLiveStates(mapStates);

        var operationalStates = IsMapTimeScrubbing
            ? _tracking.GetLiveStates(DateTime.UtcNow)
            : mapStates;

        UpdateNextPassCountdown();
        PruneExpiredPasses();
        ProcessPassRecording(operationalStates);
        UpdatePassHighlightState();
        var focusedForOps = GetFocusedTrackState(operationalStates, FocusedNoradId);
        UpdateComPortConflictState();
        _rotator.Update(_settings.Current.Rotator, EnrichRotatorTarget(focusedForOps));
        UpdateRotatorDisplay();
        var focusedForDisplay = GetFocusedTrackState(mapStates, FocusedNoradId);
        Frequencies.Update(focusedForDisplay);
        DxStation.Update(focusedForDisplay);

        if (ShowComPortConflict)
            _rig.Disconnect();

        RefreshRigUi(focusedForOps);
    }

    /// <summary>4 Hz: az/el/range readout and rig doppler context from the live tracking snapshot.</summary>
    private void OnLiveDisplayTick()
    {
        var mapStates = _liveTracking.GetSnapshot();
        UpdateLiveTelemetry(mapStates);

        if (!IsMapTimeScrubbing)
            ProcessVoiceAnnouncements(mapStates);

        var focusedForDisplay = GetFocusedTrackState(mapStates, FocusedNoradId);
        DxStation.Update(focusedForDisplay);

        if (ShowComPortConflict || !_settings.Current.Rig.Enabled)
            return;

        SyncOverlayPassbandFromRig();
        if (IsMapTimeScrubbing)
        {
            var liveFocused = GetFocusedTrackState(_tracking.GetLiveStates(DateTime.UtcNow), FocusedNoradId);
            PublishRigTrackingContext(liveFocused);
        }
        else
        {
            PublishRigTrackingContext(focusedForDisplay);
        }
    }

    private void UpdateUtcClockDisplay()
    {
        var clockFormat = PassDisplayFormat.FromSettings(_settings.Current.Use24HourClock);
        var now = DateTime.UtcNow;
        if (!IsMapTimeScrubbing)
        {
            UtcClock = PassDisplayFormat.FormatUtcClock(now, clockFormat) + " UTC";
            return;
        }

        var mapUtc = now + TimeSpan.FromMinutes(MapTimeOffsetMinutes);
        UtcClock = $"{PassDisplayFormat.FormatUtcClock(mapUtc, clockFormat)} UTC  ({MapTimeStatusText})";
    }

    public void ApplyClockFormatFromSettings()
    {
        UpdateUtcClockDisplay();
        RefreshPassClockDisplay();
        RefreshHamsAtRoveClockDisplay();
    }

    private void RefreshPassClockDisplay()
    {
        var clockFormat = PassDisplayFormat.FromSettings(_settings.Current.Use24HourClock);
        if (Passes.Count == 0)
            return;

        var items = Passes.ToList();
        Passes.Clear();
        foreach (var item in items)
        {
            Passes.Add(item is PassRowViewModel row
                ? row.WithClockFormat(clockFormat)
                : item);
        }

        UpdatePassHighlightState();
    }

    private void RefreshHamsAtRoveClockDisplay()
    {
        if (HamsAtRoves.Count == 0)
            return;

        var clockFormat = PassDisplayFormat.FromSettings(_settings.Current.Use24HourClock);
        var rows = HamsAtRoves.ToList();
        HamsAtRoves.Clear();
        foreach (var row in rows)
            HamsAtRoves.Add(row.WithClockFormat(clockFormat, useUtc: false));
    }

    partial void OnMapTimeOffsetMinutesChanged(double value)
    {
        var clamped = Math.Clamp(value, MapTimeOffsetMinMinutes, MapTimeOffsetMaxMinutes);
        if (Math.Abs(clamped - value) > 0.001)
            MapTimeOffsetMinutes = clamped;

        _liveTracking.MapTimeOffset = TimeSpan.FromMinutes(MapTimeOffsetMinutes);
        _tracking.InvalidateVisualCache();
        MapDisplayUtc = DateTime.UtcNow + TimeSpan.FromMinutes(MapTimeOffsetMinutes);
        OnPropertyChanged(nameof(MapTimeStatusText));
        UpdateUtcClockDisplay();
    }

    [RelayCommand]
    private void ResetMapTimeToNow() => MapTimeOffsetMinutes = 0;

    [RelayCommand]
    private void StepMapTimeBackward() =>
        MapTimeOffsetMinutes = Math.Max(MapTimeOffsetMinMinutes, MapTimeOffsetMinutes - MapTimeOffsetStepMinutes);

    [RelayCommand]
    private void StepMapTimeForward() =>
        MapTimeOffsetMinutes = Math.Min(MapTimeOffsetMaxMinutes, MapTimeOffsetMinutes + MapTimeOffsetStepMinutes);

    [RelayCommand]
    private void StepMapTimeBackwardLarge() =>
        MapTimeOffsetMinutes = Math.Max(MapTimeOffsetMinMinutes, MapTimeOffsetMinutes - MapTimeOffsetLargeStepMinutes);

    [RelayCommand]
    private void StepMapTimeForwardLarge() =>
        MapTimeOffsetMinutes = Math.Min(MapTimeOffsetMaxMinutes, MapTimeOffsetMinutes + MapTimeOffsetLargeStepMinutes);

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
    }

    private void OnCloudlogStateChanged()
    {
        Dispatcher.UIThread.Post(ApplyCloudlogStatus, DispatcherPriority.Normal);
    }

    private void ApplyCloudlogStatus()
    {
        var error = _cloudlog.LastError;
        if (!string.IsNullOrEmpty(error))
        {
            if (string.Equals(_lastCloudlogErrorShown, error, StringComparison.Ordinal))
                return;

            _lastCloudlogErrorShown = error;
            StatusText = _l.Get("Status.CloudlogError", error);
            return;
        }

        if (_lastCloudlogErrorShown is null)
            return;

        _lastCloudlogErrorShown = null;
        UpdateStatus();
    }

    private void UpdateComPortConflictState()
    {
        ShowComPortConflict = SerialPortConflictHelper.TryDescribeConflict(
            _settings.Current.Rotator,
            _settings.Current.Rig,
            out var message);
        ComPortConflictText = ComPortConflictLocalizer.Localize(message, _l);
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
            StopPassRecordingForStandby();
            UpdatePassHighlightState();
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

    [RelayCommand]
    private void OpenFt4Console()
    {
        if (App.MainWindow is null)
            return;

        var vm = App.Services.GetRequiredService<Ft4ConsoleViewModel>();
        var window = new Ft4ConsoleWindow { DataContext = vm };
        window.Show(App.MainWindow);
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
        RigStatusText = RigStatusLocalizer.Localize(_l, status);
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

    private void ConfigureHamsAtRefreshTimer()
    {
        _hamsAtRefreshTimer?.Stop();
        _hamsAtRefreshTimer = null;

        if (!ShowHamsAtRovesPanel)
            return;

        var minutes = Math.Clamp(_settings.Current.HamsAt.RefreshIntervalMinutes, 1, 120);
        _hamsAtRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        _hamsAtRefreshTimer.Tick += async (_, _) => await RefreshHamsAtRovesAsync();
        _hamsAtRefreshTimer.Start();
    }

    private async Task RefreshHamsAtRovesAsync()
    {
        if (!ShowHamsAtRovesPanel)
        {
            HamsAtRoves.Clear();
            HamsAtRovesStatusText = "";
            return;
        }

        var result = await _hamsAtRoves.FetchUpcomingAsync(_settings.Current.HamsAt).ConfigureAwait(false);
        if (!result.Ok)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                HamsAtRoves.Clear();
                HamsAtRovesStatusText = result.ErrorMessage ?? _l.Get("Main.HamsAtRoves.LoadFailed");
            });
            return;
        }

        var workable = result.Alerts.Where(a => a.IsWorkable).ToArray();
        var clockFormat = PassDisplayFormat.FromSettings(_settings.Current.Use24HourClock);
        var cloudlog = _settings.Current.Cloudlog;
        var checkGrids = _cloudlogLookup.CanCheckGrids(cloudlog);
        var rows = new List<HamsAtRoveRowViewModel>(workable.Length);

        foreach (var alert in workable)
        {
            IReadOnlyList<CloudlogGridCheckResult>? gridChecks = null;
            if (checkGrids && alert.Grids.Count > 0)
            {
                var checks = new List<CloudlogGridCheckResult>();
                foreach (var grid in alert.Grids)
                {
                    var check = await _cloudlogLookup.CheckGridWorkedAsync(cloudlog, grid).ConfigureAwait(false);
                    if (check is not null)
                        checks.Add(check);
                }

                if (checks.Count > 0)
                    gridChecks = checks;
            }

            rows.Add(HamsAtRoveRowViewModel.From(alert, useUtc: false, clockFormat, gridChecks));
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            HamsAtRoves.Clear();
            foreach (var row in rows)
                HamsAtRoves.Add(row);

            HamsAtRovesStatusText = HamsAtRoves.Count == 0
                ? _l.Get("Main.HamsAtRoves.Empty")
                : "";
        });
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

    private void StopPassRecordingForStandby()
    {
        if (_recording.IsRecording && !AudioRecordingSessions.IsManualTest(_recording))
            _recordingTasks.Schedule(() => _recording.StopAsync(), "stop recording (standby)");
        _passRecordingCoordinator.ResetTracking();
    }

    private bool IsPassBeingRecorded(PassRowViewModel pass) =>
        !IsStandby
        && _recording.IsRecording
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

        if (IsStandby)
        {
            StopPassRecordingForStandby();
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
            ElevationText = _l.Get("Main.Elevation.BelowHorizon");
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
            NextPassText = _l.Get("Main.Pass.None");
            return;
        }

        var aos = next.AosUtc;
        var delta = aos - DateTime.UtcNow;
        if (delta.TotalSeconds < 0)
            NextPassText = _l.Get("Main.Pass.InProgress", next.SatelliteName);
        else
            NextPassText = _l.Get("Main.Pass.AosIn", next.SatelliteName, delta.ToString(@"hh\:mm\:ss"));
    }

    private void UpdateStatus()
    {
        var catalogCount = _tleService.Catalog.Count;
        var count = _tleService.GetEnabledSatellites(_settings.Current).Count;
        var source = _tleService.ActiveSourceLabel;
        var ageSpan = _tleService.LastFetchedUtc.HasValue
            ? (DateTime.UtcNow - _tleService.LastFetchedUtc.Value).ToString(@"hh\:mm")
            : null;
        var tleAge = ageSpan is not null
            ? _l.Get("Status.TleAge", ageSpan, source)
            : catalogCount > 0
                ? _l.Get("Status.TleCached", source)
                : _l.Get("Status.TleNotLoadedShort");

        StatusText = catalogCount == 0
            ? _l.Get("Status.NoTle")
            : count == 0
                ? _l.Get("Status.LineNoSatellites", tleAge)
                : _l.Get("Status.SatellitesEnabled", tleAge, count);
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
            ConfigureAppUpdateCheckTimer();
            ApplyHamsAtSidebarSettings();
            ConfigureHamsAtRefreshTimer();
            await RefreshHamsAtRovesAsync().ConfigureAwait(true);
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
            ShowGreylineOverlay = _settings.Current.ShowGreylineOverlay;
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
            StatusText = _l.Get("Status.LoadingTle");
            await _tleService.EnsureLoadedAsync().ConfigureAwait(true);

            var source = _settings.Current.TleSource;
            if (TleSourceResolver.UsesNetwork(source)
                || !string.IsNullOrWhiteSpace(TleSourceResolver.TryGetLocalFilePath(source)))
            {
                StatusText = _l.Get("Status.RefreshingTle");
                await _tleService.RefreshAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TLE reload after settings failed");
            StatusText = _l.Get("Status.TleReloadFailed", ex.Message);
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
            StatusText = _l.Get("Status.RefreshingTle");
            await _tleService.RefreshAsync().ConfigureAwait(true);
            _liveTracking.RequestReload();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TLE auto-refresh failed");
            StatusText = _l.Get("Status.TleRefreshFailed", ex.Message);
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
            StatusText = _l.Get("Status.RecordingsFolderFailed");
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
            StatusText = _l.Get("Status.LogsFolderFailed");
        }
    }

    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        try
        {
            var text = DiagnosticsBundleBuilder.Build(_settings, _rig, _rotator);
            if (App.MainWindow?.Clipboard is not { } clipboard)
            {
                StatusText = _l.Get("Status.DiagnosticsCopyFailed");
                return;
            }

            await clipboard.SetTextAsync(text);
            StatusText = _l.Get("Status.DiagnosticsCopied");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not copy diagnostics bundle");
            StatusText = _l.Get("Status.DiagnosticsCopyFailed");
        }
    }

    [RelayCommand]
    private void OpenHelp()
    {
        if (HelpLauncher.TryOpenHelp())
            return;

        Log.Warning("Help folder not found next to the application");
        StatusText = _l.Get("Status.HelpMissing");
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        await CheckForAppUpdateAsync(manual: true).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenReleaseNotesAsync()
    {
        if (App.MainWindow is null)
            return;

        try
        {
            StatusText = _l.Get("ReleaseNotes.Loading");
            var release = await _githubRelease.FetchLatestAsync().ConfigureAwait(true);
            await ReleaseNotesDialog.ShowAsync(App.MainWindow, release).ConfigureAwait(true);
            UpdateStatus();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load release notes");
            StatusText = _l.Get("ReleaseNotes.LoadFailed", ex.Message);
        }
    }

    private void ConfigureAppUpdateCheckTimer()
    {
        _appUpdateCheckTimer?.Stop();
        _appUpdateCheckTimer = null;

        if (!_settings.Current.AppUpdateCheckEnabled)
            return;

        _appUpdateCheckTimer = new DispatcherTimer { Interval = AppUpdateCheckInterval };
        _appUpdateCheckTimer.Tick += async (_, _) =>
            await CheckForAppUpdateAsync(manual: false).ConfigureAwait(true);
        _appUpdateCheckTimer.Start();
    }

    private async Task CheckForAppUpdateAsync(bool manual)
    {
        if (App.MainWindow is null)
            return;

        if (!manual && !_settings.Current.AppUpdateCheckEnabled)
            return;

        var currentVersion = AppVersionHelper.GetCurrentVersion();
        if (currentVersion is null)
        {
            if (manual)
                StatusText = _l.Get("Status.AppUpdateFailed", "Unknown application version.");
            return;
        }

        try
        {
            if (manual)
                StatusText = _l.Get("Status.AppUpdateChecking");

            var result = await _githubRelease
                .CheckForUpdateAsync(currentVersion)
                .ConfigureAwait(true);

            switch (result.Kind)
            {
                case AppUpdateCheckResultKind.UpToDate:
                    if (manual)
                        StatusText = _l.Get("Status.AppUpdateUpToDate", AppVersionHelper.GetDisplayVersionText());
                    else
                        UpdateStatus();
                    return;

                case AppUpdateCheckResultKind.CheckFailed:
                    Log.Warning(result.Error, "Application update check failed");
                    if (manual)
                        StatusText = _l.Get("Status.AppUpdateFailed", result.Error?.Message ?? "Unknown error");
                    else
                        UpdateStatus();
                    return;

                case AppUpdateCheckResultKind.UpdateAvailable:
                    var release = result.Release!;
                    if (!manual
                        && string.Equals(
                            _settings.Current.DismissedAppUpdateTag,
                            release.TagName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        UpdateStatus();
                        return;
                    }

                    var dialogResult = await AppUpdateAvailableDialog.TryShowAsync(
                        App.MainWindow,
                        release,
                        AppVersionHelper.GetDisplayVersionText(),
                        _l).ConfigureAwait(true);

                    if (dialogResult == AppUpdateDialogResult.SkipVersion)
                    {
                        _settings.Current.DismissedAppUpdateTag = release.TagName;
                        _settings.RequestSave();
                    }

                    UpdateStatus();
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Application update check failed");
            if (manual)
                StatusText = _l.Get("Status.AppUpdateFailed", ex.Message);
            else
                UpdateStatus();
        }
    }

    [RelayCommand]
    private async Task OpenSatellitesAsync()
    {
        await _tleService.EnsureLoadedAsync();
        var vm = App.Services.GetRequiredService<SatellitePickerViewModel>();
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
            StatusText = _l.Get("Status.TransponderChecking");
            var plan = await _transponderDatabaseSync.FetchMergePlanAsync().ConfigureAwait(true);
            if (!plan.HasChanges)
            {
                if (showWhenUpToDate)
                    StatusText = _l.Get("Status.TransponderUpToDate");
                else
                    UpdateStatus();

                return;
            }

            if (await TransponderDatabaseMergeDialog.TryShowAsync(App.MainWindow, plan, _transponderDatabaseSync))
            {
                Frequencies.ReloadFromDatabase();
                Tick();
                StatusText = _l.Get("Status.TransponderUpdated");
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
                StatusText = _l.Get("Status.TransponderUpdateFailed", ex.Message);
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

                Passes.Add(PassRowViewModel.From(
                    p,
                    PassDisplayFormat.FromSettings(_settings.Current.Use24HourClock)));
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
    private static ILocalizationService L => LocalizationService.Instance;
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
    public DateTime MaxElevationUtc { get; init; }

    public void UpdateDisplay(DateTime utcNow, TimeSpan imminentWindow, bool isRecording)
    {
        if (isRecording)
        {
            if (Highlight != PassRowHighlight.Recording)
                Highlight = PassRowHighlight.Recording;
            var recLabel = L.Get("Pass.Rec");
            if (BadgeText != recLabel)
                BadgeText = recLabel;
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
                var passingLabel = L.Get("Pass.Passing");
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

    public static PassRowViewModel From(PassInfo p, ClockDisplayFormat clockFormat)
    {
        var (aos, los) = PassDisplayFormat.FormatLocalTimes(p.AosUtc, p.LosUtc, clockFormat: clockFormat);

        return new()
        {
            SatelliteName = p.SatelliteName,
            NoradId = p.NoradId,
            AosUtc = p.AosUtc,
            LosUtc = p.LosUtc,
            MaxElevationUtc = p.MaxElevationUtc,
            AosLocal = aos,
            LosLocal = los,
            TcaLocal = PassDisplayFormat.FormatLocal(p.MaxElevationUtc, clockFormat),
            TimeRangeLine = FormatPassTimeRangeLine(p.AosUtc, p.LosUtc, clockFormat),
            DetailsLine = FormatPassDetailsLine(p.MaxElevationDeg, p.Duration)
        };
    }

    public PassRowViewModel WithClockFormat(ClockDisplayFormat clockFormat)
    {
        var (aos, los) = PassDisplayFormat.FormatLocalTimes(AosUtc, LosUtc, clockFormat: clockFormat);
        return new()
        {
            SatelliteName = SatelliteName,
            NoradId = NoradId,
            AosUtc = AosUtc,
            LosUtc = LosUtc,
            MaxElevationUtc = MaxElevationUtc,
            AosLocal = aos,
            LosLocal = los,
            TcaLocal = PassDisplayFormat.FormatLocal(MaxElevationUtc, clockFormat),
            TimeRangeLine = FormatPassTimeRangeLine(AosUtc, LosUtc, clockFormat),
            DetailsLine = DetailsLine,
            Highlight = Highlight,
            BadgeText = BadgeText,
            ShowBadge = ShowBadge
        };
    }

    private static string FormatPassTimeRangeLine(
        DateTime aosUtc,
        DateTime losUtc,
        ClockDisplayFormat clockFormat)
    {
        var (aos, los) = PassDisplayFormat.FormatLocalTimes(aosUtc, losUtc, clockFormat: clockFormat);
        return L.Get("Pass.TimeRange", aos, los);
    }

    private static string FormatPassDetailsLine(double maxElevationDeg, TimeSpan duration)
    {
        var minutes = duration.TotalSeconds < 30
            ? 0
            : (int)Math.Round(duration.TotalMinutes, MidpointRounding.AwayFromZero);
        var durationText = minutes == 1
            ? L.Get("Pass.DurationOneMinute")
            : L.Get("Pass.DurationMinutes", minutes);
        return L.Get("Pass.Details", durationText, $"{maxElevationDeg:F0}°");
    }
}

public sealed class HamsAtRoveRowViewModel
{
    public string Callsign { get; init; } = "";
    public string GridsText { get; init; } = "";
    public string NeededGridsText { get; init; } = "";
    public string WorkedGridsText { get; init; } = "";
    public string TimeWindowText { get; init; } = "";
    public string SatelliteName { get; init; } = "";
    public string Comment { get; init; } = "";
    public bool ShowComment => !string.IsNullOrWhiteSpace(Comment);
    public bool HasGridLookup => !string.IsNullOrWhiteSpace(NeededGridsText) || !string.IsNullOrWhiteSpace(WorkedGridsText);
    public bool ShowGrids => !HasGridLookup && !string.IsNullOrWhiteSpace(GridsText);
    public bool ShowNeededGrids => !string.IsNullOrWhiteSpace(NeededGridsText);
    public bool ShowWorkedGrids => !string.IsNullOrWhiteSpace(WorkedGridsText);
    public bool ShowSatellite => !string.IsNullOrWhiteSpace(SatelliteName);
    public string Url { get; init; } = "";

    public DateTime AosUtc { get; init; }
    public DateTime LosUtc { get; init; }

    public static HamsAtRoveRowViewModel From(
        HamsAtUpcomingAlert alert,
        bool useUtc,
        ClockDisplayFormat clockFormat,
        IReadOnlyList<CloudlogGridCheckResult>? gridChecks = null) => new()
    {
        Callsign = alert.Callsign,
        GridsText = HamsAtDisplayFormat.FormatGrids(alert.Grids),
        NeededGridsText = FormatGridSubset(alert.Grids, gridChecks, worked: false),
        WorkedGridsText = FormatGridSubset(alert.Grids, gridChecks, worked: true),
        AosUtc = alert.AosUtc,
        LosUtc = alert.LosUtc,
        TimeWindowText = HamsAtDisplayFormat.FormatAlertWindow(
            alert.AosUtc,
            alert.LosUtc,
            useUtc,
            clockFormat),
        SatelliteName = alert.Satellite?.Name ?? "",
        Comment = alert.Comment,
        Url = alert.Url
    };

    public HamsAtRoveRowViewModel WithClockFormat(ClockDisplayFormat clockFormat, bool useUtc) => new()
    {
        Callsign = Callsign,
        GridsText = GridsText,
        NeededGridsText = NeededGridsText,
        WorkedGridsText = WorkedGridsText,
        AosUtc = AosUtc,
        LosUtc = LosUtc,
        TimeWindowText = HamsAtDisplayFormat.FormatAlertWindow(AosUtc, LosUtc, useUtc, clockFormat),
        SatelliteName = SatelliteName,
        Comment = Comment,
        Url = Url
    };

    private static string FormatGridSubset(
        IReadOnlyList<string> alertGrids,
        IReadOnlyList<CloudlogGridCheckResult>? gridChecks,
        bool worked)
    {
        if (gridChecks is null || gridChecks.Count == 0)
            return "";

        var lookup = gridChecks.ToDictionary(c => c.Grid, c => c.IsWorked, StringComparer.OrdinalIgnoreCase);
        var selected = alertGrids
            .Where(g => lookup.TryGetValue(g.Trim().ToUpperInvariant(), out var isWorked) && isWorked == worked)
            .ToArray();

        return selected.Length == 0 ? "" : HamsAtDisplayFormat.FormatGrids(selected);
    }
}
