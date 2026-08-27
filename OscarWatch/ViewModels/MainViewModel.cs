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
using OscarWatch.Core.Rotator;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Services;
using OscarWatch.Theme;
using OscarWatch.Diagnostics;
using OscarWatch.Help;
using OscarWatch.Localization;
using OscarWatch.Orbit;
using OscarWatch.Services;
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
    private readonly IAlertSoundService _alertSound;
    private readonly RisingPassAnnouncer _passAnnouncer;
    private readonly ScheduledPassReminder _scheduledPassReminder;
    private readonly PassRecordingCoordinator _passRecordingCoordinator;
    private readonly IAudioRecordingService _recording;
    private readonly IRecordingTaskScheduler _recordingTasks;
    private readonly IRotatorController _rotator;
    private readonly IRigController _rig;
    private readonly IGpsService _gps;
    private readonly ICloudlogRadioSyncService _cloudlog;
    private readonly ICloudlogLookupService _cloudlogLookup;
    private readonly ISatelliteLinkBroadcastService _satelliteLink;
    private readonly ISatelliteDatabaseSyncService _transponderDatabaseSync;
    private readonly IGitHubReleaseService _githubRelease;
    private readonly IHamsAtRovesService _hamsAtRoves;
    private readonly ISatelliteStatusReportService _satelliteStatus;
    private readonly ISatelliteDatabaseService _satelliteDatabase;
    private readonly ILocalizationService _l;
    private readonly LiveTrackerSnapshotProvider _trackerSnapshot;
    private readonly DispatcherTimer _timer;
    private DispatcherTimer? _appUpdateCheckTimer;
    private DispatcherTimer? _satelliteStatusRefreshTimer;
    private SatelliteCommunityCatalog? _communityStatusCatalog;
    private DateTime _communityStatusFetchedAtUtc;
    private bool _communityStatusFeatureUnavailable;
    private bool _communityStatusFetchInFlight;
    private DateTime _communityStatusLastWarnUtc = DateTime.MinValue;
    private static readonly TimeSpan AppUpdateCheckInterval = TimeSpan.FromHours(24);
    private string? _lastCloudlogErrorShown;
    private string? _recordingPassNoradId;
    private DateTime? _recordingPassAosUtc;
    private DateTime? _recordingStartedUtc;
    private DateTime _lastGpsStationPersistUtc = DateTime.MinValue;

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
    [NotifyCanExecuteChangedFor(nameof(StopRotatorCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeRotatorTrackingCommand))]
    private bool _canParkRotator;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopRotatorCommand))]
    private bool _canStopRotator;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeRotatorTrackingCommand))]
    private bool _isRotatorTrackingHeld;

    [ObservableProperty]
    private bool _isKeyholeAvoidanceActive;

    [ObservableProperty]
    private bool _isPrePositioning;

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
    private int _hardwareShutdownStarted;

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
    [NotifyPropertyChangedFor(nameof(RigCatPauseToolTip))]
    private bool _rigCatPaused;

    public string RigCatPauseButtonText => RigCatPaused
        ? _l.Get("Main.Radio.CatResume")
        : _l.Get("Main.Radio.CatPause");

    public string RigCatPauseToolTip => RigCatPaused
        ? _l.Get("Main.Radio.CatResume")
        : _l.Get("Main.Radio.CatPauseTip");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRigCatPauseCommand))]
    private bool _showComPortConflict;

    [ObservableProperty]
    private string _comPortConflictText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpsNoFix))]
    [NotifyPropertyChangedFor(nameof(GpsTimeInactive))]
    private bool _showGpsStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpsNoFix))]
    private bool _gpsHasFix;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpsTimeInactive))]
    private bool _showGpsTimeStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpsTimeInactive))]
    private bool _gpsTimeActive;

    [ObservableProperty]
    private string _gpsStatusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSatelliteLinkOk))]
    [NotifyPropertyChangedFor(nameof(ShowSatelliteLinkWaiting))]
    [NotifyPropertyChangedFor(nameof(ShowSatelliteLinkError))]
    private bool _showSatelliteLinkStatus;

    [ObservableProperty]
    private string _satelliteLinkStatusText = "";

    [ObservableProperty]
    private string _satelliteLinkStatusTooltip = "";

    [ObservableProperty]
    private bool _showSatelliteLinkOk;

    [ObservableProperty]
    private bool _showSatelliteLinkWaiting;

    [ObservableProperty]
    private bool _showSatelliteLinkError;

    public bool GpsNoFix => ShowGpsStatus && !GpsHasFix;

    public bool GpsTimeInactive => ShowGpsTimeStatus && !GpsTimeActive;

    [ObservableProperty]
    private bool _soloFocusedSatellite;

    [ObservableProperty]
    private string? _focusedNoradId;

    private string? _ts2000SatlWarningPassKey;

    /// <summary>Last pass identity pushed to the rotator (<c>NoradId|AosUtc</c> ticks).</summary>
    private string? _rotatorActivePassKey;

    public ObservableCollection<IPassListItem> Passes { get; } = [];

    [ObservableProperty]
    private IReadOnlyList<PassInfo>? _timelinePasses;

    [ObservableProperty]
    private bool _displayTimesInUtc;

    [ObservableProperty]
    private bool _use24HourClock;

    [ObservableProperty]
    private IPassListItem? _selectedListItem;
    [ObservableProperty]
    private IReadOnlyList<SatelliteTrackState> _liveStates = [];

    [ObservableProperty]
    private GroundStation _groundStation = new();

    [ObservableProperty]
    private double _minimumElevationDeg = 5;

    [ObservableProperty]
    private IReadOnlyList<SkyPlotPathPoint> _skyPlotPassPath = [];

    [ObservableProperty]
    private double? _skyPlotRotatorAzimuthDeg;

    [ObservableProperty]
    private double? _skyPlotRotatorElevationDeg;

    [ObservableProperty]
    private bool _showFootprintMotionArrows = true;

    [ObservableProperty]
    private bool _showGreylineOverlay;

    [ObservableProperty]
    private bool _showMultiTrackOverlay = true;

    [ObservableProperty]
    private DateTime _mapDisplayUtc = DateTime.UtcNow;

    /// <summary>Effective longitude at mid-map (0 = Greenwich). Bound to the world map control.</summary>
    [ObservableProperty]
    private double _mapCentreLongitude;

    [ObservableProperty]
    private MapCentreMode _mapCentreMode = MapCentreMode.Greenwich;

    [ObservableProperty]
    private double _mapCentreCustomLongitudeDeg;

    [ObservableProperty]
    private bool _isSkyPlotExpanded = true;

    [ObservableProperty]
    private bool _isPassesExpanded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimelineDockedVisible))]
    [NotifyCanExecuteChangedFor(nameof(DetachPassElevationTimelineCommand))]
    private bool _isTimelineExpanded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimelineDockedVisible))]
    [NotifyCanExecuteChangedFor(nameof(DetachPassElevationTimelineCommand))]
    private bool _isTimelineDetached;

    /// <summary>Docked bottom panel is shown only when expanded and not in a floating window.</summary>
    public bool IsTimelineDockedVisible => IsTimelineExpanded && !IsTimelineDetached;

    [ObservableProperty]
    private int _timelineWindowMinutes = TimelineWindowLimits.DefaultMinutes;

    [ObservableProperty]
    private double _timelinePanelHeight = 110;

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

    public bool HasHamsAtApiKey =>
        !string.IsNullOrWhiteSpace(_settings.Current.HamsAt.ApiKey);

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
        IAlertSoundService alertSound,
        RisingPassAnnouncer passAnnouncer,
        ScheduledPassReminder scheduledPassReminder,
        PassRecordingCoordinator passRecordingCoordinator,
        IAudioRecordingService recording,
        IRecordingTaskScheduler recordingTasks,
        IRotatorController rotator,
        IRigController rig,
        IGpsService gps,
        ICloudlogRadioSyncService cloudlog,
        ICloudlogLookupService cloudlogLookup,
        ISatelliteLinkBroadcastService satelliteLink,
        ISatelliteDatabaseSyncService transponderDatabaseSync,
        IGitHubReleaseService githubRelease,
        IHamsAtRovesService hamsAtRoves,
        ISatelliteStatusReportService satelliteStatus,
        ISatelliteDatabaseService satelliteDatabase,
        ILocalizationService localization,
        FrequencyOverlayViewModel frequencies,
        DxStationOverlayViewModel dxStation,
        LiveTrackerSnapshotProvider trackerSnapshot)
    {
        _l = localization;
        _trackerSnapshot = trackerSnapshot;
        _statusText = _l.Get("Status.LoadingSettings");
        _settings = settings;
        _tleService = tleService;
        _tracking = tracking;
        _liveTracking = liveTracking;
        _speech = speech;
        _alertSound = alertSound;
        _passAnnouncer = passAnnouncer;
        _scheduledPassReminder = scheduledPassReminder;
        _passRecordingCoordinator = passRecordingCoordinator;
        _recording = recording;
        _recordingTasks = recordingTasks;
        _rotator = rotator;
        _rig = rig;
        _gps = gps;
        _cloudlog = cloudlog;
        _cloudlogLookup = cloudlogLookup;
        _satelliteLink = satelliteLink;
        _cloudlog.StateChanged += OnCloudlogStateChanged;
        _satelliteLink.StateChanged += OnSatelliteLinkStateChanged;
        _transponderDatabaseSync = transponderDatabaseSync;
        _githubRelease = githubRelease;
        _hamsAtRoves = hamsAtRoves;
        _satelliteStatus = satelliteStatus;
        _satelliteDatabase = satelliteDatabase;
        Frequencies = frequencies;
        DxStation = dxStation;
        Frequencies.OffsetsChanged += (_, reinitializePass) =>
        {
            RefreshRigFromOverlay(reinitializePass);
            UpdateCommunityStatusDisplays();
            _ = RefreshCommunityStatusAsync();
        };
        Frequencies.CommunityStatusContextChanged += (_, _) =>
        {
            UpdateCommunityStatusDisplays();
            _ = RefreshCommunityStatusAsync();
        };
        Frequencies.CtcssChanged += (_, _) => OnCtcssSelectorChanged();
        Frequencies.LeadTuningChanged += (_, _) => RefreshRigFromOverlay(reinitializePass: false);
        Frequencies.ReportSatelliteStatusRequested += (_, _) => _ = OpenSatelliteStatusReportAsync();

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
        _rig.PublishContext(rigSettings, context, catPausedOverride: GetCatPausedOverride());
        RefreshRigUi(focused);
    }

    private void RefreshRigFromOverlay(bool reinitializePass = true)
    {
        if (ShowComPortConflict)
            return;

        var focused = GetFocusedTrackState(_liveTracking.GetSnapshot(), FocusedNoradId);
        var context = Frequencies.TryBuildRigTrackingContext(focused);
        _rig.PublishContext(GetRigSettingsForController(), context, reinitializePass, catPausedOverride: GetCatPausedOverride());
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

    partial void OnIsTimelineExpandedChanged(bool value)
    {
        _settings.Current.IsTimelineExpanded = value;
        _settings.RequestSave();
        if (!value && IsTimelineDetached)
            DockPassElevationTimeline();
        else if (value && IsTimelineDetached)
            EnsureDetachedTimelineWindowOpen();
    }

    partial void OnIsTimelineDetachedChanged(bool value)
    {
        _settings.Current.IsTimelineDetached = value;
        _settings.RequestSave();
        if (value)
            EnsureDetachedTimelineWindowOpen();
        else if (!_suppressTimelineWindowClose)
            CloseDetachedTimelineWindow();
    }

    [RelayCommand]
    private void HidePassElevationTimeline() => IsTimelineExpanded = false;

    [RelayCommand(CanExecute = nameof(CanDetachPassElevationTimeline))]
    private void DetachPassElevationTimeline()
    {
        IsTimelineExpanded = true;
        IsTimelineDetached = true;
    }

    private bool CanDetachPassElevationTimeline() => !IsTimelineDetached;

    private void DockPassElevationTimeline()
    {
        IsTimelineDetached = false;
        IsTimelineExpanded = true;
    }

    /// <summary>Called when the floating timeline window is closed by the operator.</summary>
    public void DockPassElevationTimelineFromWindowClose()
    {
        if (!IsTimelineDetached)
            return;

        _suppressTimelineWindowClose = true;
        try
        {
            _openTimelineWindow = null;
            IsTimelineDetached = false;
            IsTimelineExpanded = true;
        }
        finally
        {
            _suppressTimelineWindowClose = false;
        }
    }

    private static PassElevationTimelineWindow? _openTimelineWindow;
    private bool _suppressTimelineWindowClose;

    private void EnsureDetachedTimelineWindowOpen()
    {
        if (!IsTimelineDetached || !IsTimelineExpanded)
            return;

        if (_openTimelineWindow is { IsVisible: true })
        {
            _openTimelineWindow.Activate();
            return;
        }

        var window = new PassElevationTimelineWindow { DataContext = this };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openTimelineWindow, window))
                _openTimelineWindow = null;
        };
        _openTimelineWindow = window;
        if (App.MainWindow is null)
            window.Show();
        else
            window.Show(App.MainWindow);
    }

    private void CloseDetachedTimelineWindow()
    {
        if (_openTimelineWindow is null)
            return;

        var window = _openTimelineWindow;
        _openTimelineWindow = null;
        window.Close();
    }

    partial void OnTimelineWindowMinutesChanged(int value)
    {
        var clamped = TimelineWindowLimits.Clamp(value);
        if (clamped != value)
        {
            TimelineWindowMinutes = clamped;
            return;
        }

        _settings.Current.TimelineWindowMinutes = value;
        _settings.RequestSave();
    }

    public const double TimelineMinPanelHeight = 80;
    public const double TimelineMaxPanelHeight = 280;
    public const double TimelineDefaultPanelHeight = 110;

    public void SetTimelinePanelHeight(double height, double? maxHeight = null)
    {
        var max = maxHeight ?? TimelineMaxPanelHeight;
        TimelinePanelHeight = Math.Clamp(height, TimelineMinPanelHeight, max);
    }

    public void PersistTimelinePanelHeight()
    {
        _settings.Current.TimelinePanelHeightPx = (int)Math.Round(TimelinePanelHeight);
        _settings.RequestSave();
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
        OnPropertyChanged(nameof(HasHamsAtApiKey));
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

    public async Task PostHamsAtActivationAsync(PassRowViewModel row)
    {
        if (App.MainWindow is null)
            return;

        var posted = await HamsAtActivationCoordinator.PostAsync(
            App.MainWindow,
            row.Source,
            GroundStation,
            _settings.Current.GroundStation.Callsign,
            _settings.Current.HamsAt,
            _hamsAtRoves,
            _l,
            row.TimeRangeLine,
            row.DetailsLine,
            Frequencies,
            status => StatusText = status,
            RefreshHamsAtRovesAfterActivationAsync,
            App.Services.GetRequiredService<ISatelliteDatabaseService>(),
            _settings.Current.FrequencySelections,
            _settings.Current.Rig?.CwKeepSidebandDownlink == true).ConfigureAwait(true);

        if (posted)
            EnsurePassScheduled(row);
    }

    private void EnsurePassScheduled(PassRowViewModel row)
    {
        var current = _settings.Current.ScheduledPasses ?? [];
        if (ScheduledPassReminder.IsScheduled(current, row.NoradId, row.AosUtc))
        {
            ApplyScheduledFlagsToPassList();
            return;
        }

        _settings.Current.ScheduledPasses = ScheduledPassReminder.EnsureScheduled(
            current,
            row.NoradId,
            row.AosUtc);
        _settings.RequestSave();
        ApplyScheduledFlagsToPassList();
    }

    private async Task RefreshHamsAtRovesAfterActivationAsync()
    {
        if (!ShowHamsAtRovesPanel)
            return;

        _hamsAtRoves.InvalidateCache();
        await RefreshHamsAtRovesAsync(bypassCache: true).ConfigureAwait(false);
    }

    private void RefreshRigUi(SatelliteTrackState? focused)
    {
        var rigStatus = _rig.GetStatus();
        SyncOverlayPassbandFromRig();
        UpdateRigDisplay(rigStatus);
        PushCloudlogRadio(focused);
        PushSatelliteLink(focused);
    }

    public async Task InitializeAsync()
    {
        var startupStopwatch = Stopwatch.StartNew();

        // Phase 1: Parallel I/O — load settings and TLE concurrently
        StatusText = _l.Get("Status.LoadingSettingsFull");
        var phase1Stopwatch = Stopwatch.StartNew();

        var settingsTask = LoadSettingsSafeAsync();
        var tleTask = LoadTleSafeAsync();

        await Task.WhenAll(settingsTask, tleTask).ConfigureAwait(true);

        var settingsLoaded = await settingsTask.ConfigureAwait(true);
        var tleLoaded = await tleTask.ConfigureAwait(true);
        Log.Information(
            "Startup phase 1 (load settings+tle) completed in {ElapsedMs} ms (settingsLoaded={SettingsLoaded}, tleLoaded={TleLoaded})",
            phase1Stopwatch.ElapsedMilliseconds,
            settingsLoaded,
            tleLoaded);

        // Phase 2: Apply loaded settings and show window interactive
        var phase2Stopwatch = Stopwatch.StartNew();
        if (settingsLoaded)
        {
            AppThemeManager.Apply(_settings.Current.Theme);
            RefreshGroundStationFromSettings();
            ShowFootprintMotionArrows = _settings.Current.ShowFootprintMotionArrows;
            ShowGreylineOverlay = _settings.Current.ShowGreylineOverlay;
            ShowMultiTrackOverlay = _settings.Current.ShowMultiTrackOverlay;
            ApplyMapCentreFromSettings();
            IsSkyPlotExpanded = _settings.Current.SkyPlotExpanded;
            IsPassesExpanded = _settings.Current.PassesExpanded;
            IsTimelineExpanded = _settings.Current.IsTimelineExpanded;
            IsTimelineDetached = _settings.Current.IsTimelineDetached && IsTimelineExpanded;
            TimelineWindowMinutes = TimelineWindowLimits.Clamp(_settings.Current.TimelineWindowMinutes);
            TimelinePanelHeight = Math.Clamp(
                _settings.Current.TimelinePanelHeightPx,
                TimelineMinPanelHeight,
                TimelineMaxPanelHeight);
            DisplayTimesInUtc = _settings.Current.DisplayTimesInUtc;
            Use24HourClock = _settings.Current.Use24HourClock;
            ApplyHamsAtSidebarSettings();
            RigCatPaused = _settings.Current.Rig.CatUpdatesPaused;
            Frequencies.ReloadLayoutFromSettings();
            DxStation.ReloadLayoutFromSettings();
        }

        Log.Information("Startup phase 2 (apply settings to UI) completed in {ElapsedMs} ms", phase2Stopwatch.ElapsedMilliseconds);

        if (tleLoaded
            && TleSourceResolver.UsesNetwork(_settings.Current.TleSource)
            && _tleService.IsStale(_settings.Current.TleStaleHours))
        {
            try
            {
                var tleRefreshStopwatch = Stopwatch.StartNew();
                StatusText = _l.Get("Status.RefreshingTle");
                await _tleService.RefreshAsync().ConfigureAwait(true);
                Log.Information("Startup stale TLE refresh completed in {ElapsedMs} ms", tleRefreshStopwatch.ElapsedMilliseconds);
                TryBackfillEnabledSatelliteIds();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "TLE refresh failed during startup");
                StatusText = _l.Get("Status.TleRefreshFailed", ex.Message);
            }
        }

        _gps.Update(_settings.Current.Gps);
        ApplySatelliteLinkSettings();
        _liveTracking.Start();
        _liveTracking.RequestReload();
        Tick();
        _timer.Start();
        ConfigurePassListRefreshTimer();
        ConfigureHamsAtRefreshTimer();
        ConfigureSatelliteStatusRefreshTimer();
        _ = RefreshCommunityStatusAsync();
        _liveDisplayTimer?.Start();

        // Phase 3: Fire pass prediction on background thread (non-blocking)
        UpdateStatus();
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshPassesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Background pass prediction failed during startup");
            }
        });

        _ = RefreshHamsAtRovesAsyncSafeAsync();
        Tick();

        if (_settings.Current.TransponderDatabaseCheckOnStartup)
            _ = RunStartupTransponderCheckAsync();

        ConfigureAppUpdateCheckTimer();

        if (_settings.Current.AppUpdateCheckEnabled)
            _ = RunStartupAppUpdateCheckAsync();

        Log.Information("Startup interactive initialization completed in {ElapsedMs} ms", startupStopwatch.ElapsedMilliseconds);
    }

    private async Task<bool> LoadSettingsSafeAsync()
    {
        try
        {
            await _settings.LoadAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Settings load failed during startup — using defaults");
            return false;
        }
    }

    private async Task<bool> LoadTleSafeAsync()
    {
        try
        {
            await _tleService.EnsureLoadedAsync().ConfigureAwait(false);
            LogTleLoadDiagnostics();
            TryBackfillEnabledSatelliteIds();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TLE load failed during startup");
            return false;
        }
    }

    /// <summary>
    /// After the catalog is available, append normalised catalogue IDs for name-matched
    /// enabled satellites so TLE source renames do not drop selections.
    /// </summary>
    private void TryBackfillEnabledSatelliteIds()
    {
        if (!SatelliteCatalogMatching.TryMigrateEnabledSatelliteIds(_settings.Current, _tleService.Catalog))
            return;

        _settings.RequestSave();
    }

    private void LogTleLoadDiagnostics()
    {
        if (_tleService.LastLoadDiagnostics is not { } diagnostics)
            return;

        Log.Information(
            "TLE catalog loaded: source={Source}, origin={Origin}, parsed={ParsedCount}, skippedIncomplete={SkippedIncomplete}, skippedSanity={SkippedOrbitalSanity}, total={TotalRecords}",
            _tleService.ActiveSourceLabel,
            diagnostics.Origin,
            diagnostics.ParsedCount,
            diagnostics.SkippedIncomplete,
            diagnostics.SkippedOrbitalSanity,
            diagnostics.TotalRecords);
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

    private async Task RunStartupTransponderCheckAsync()
    {
        var checkStopwatch = Stopwatch.StartNew();
        try
        {
            await CheckTransponderDatabaseUpdatesAsync(showWhenUpToDate: false).ConfigureAwait(true);
            Log.Information("Startup transponder database check completed in {ElapsedMs} ms", checkStopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Startup transponder database check failed");
        }
    }

    private void Tick()
    {
        try
        {
            UpdateUtcClockDisplay();
            MapDisplayUtc = DateTime.UtcNow + TimeSpan.FromMinutes(MapTimeOffsetMinutes);
            MinimumElevationDeg = _settings.Current.MinimumElevationDeg;
            var mapStates = _liveTracking.GetSnapshot();

            var operationalStates = IsMapTimeScrubbing
                ? _liveTracking.GetLiveNowSnapshot()
                : mapStates;

            // Keep pass-list maintenance first so a later failure cannot leave expired rows stuck.
            UpdateNextPassCountdown();
            PruneExpiredPasses();
            ProcessPassRecording(operationalStates);
            UpdatePassHighlightState();
            ProcessScheduledPassReminders();
            var focusedForOps = GetFocusedTrackState(operationalStates, FocusedNoradId);
            UpdateComPortConflictState();
            TryApplyGpsStationUpdate();
            UpdateGpsStatusDisplay();
            // Publish active pass before target so keyhole plans can build from the pass
            // NORAD id before the first track command.
            PublishActivePassForRotator(focusedForOps);
            _rotator.Update(_settings.Current.Rotator, EnrichRotatorTarget(focusedForOps));
            UpdateRotatorDisplay();

            if (ShowComPortConflict)
                _rig.Disconnect();

            RefreshRigUi(focusedForOps);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Main timer tick failed");
            try
            {
                PruneExpiredPasses();
                UpdatePassHighlightState();
            }
            catch (Exception pruneEx)
            {
                Log.Warning(pruneEx, "Pass list prune after tick failure also failed");
            }
        }
    }

    /// <summary>4 Hz: az/el/range readout and rig doppler context from the live tracking snapshot.</summary>
    private void OnLiveDisplayTick()
    {
        var mapStates = _liveTracking.GetSnapshot();
        SyncLiveStates(mapStates);
        UpdateLiveTelemetry(mapStates);

        if (!IsMapTimeScrubbing)
            ProcessVoiceAnnouncements(mapStates);

        var focusedForDisplay = GetFocusedTrackState(mapStates, FocusedNoradId);
        DxStation.Update(focusedForDisplay);

        if (ShowComPortConflict || !_settings.Current.Rig.Enabled)
        {
            Frequencies.Update(focusedForDisplay);
            return;
        }

        SyncOverlayPassbandFromRig();
        Frequencies.Update(focusedForDisplay);
        UpdateRigDisplay();
        if (IsMapTimeScrubbing)
        {
            var liveFocused = GetFocusedTrackState(_liveTracking.GetLiveNowSnapshot(), FocusedNoradId);
            PublishRigTrackingContext(liveFocused);
        }
        else
        {
            PublishRigTrackingContext(focusedForDisplay);
        }

        PushSatelliteLink(focusedForDisplay);
    }

    private void UpdateUtcClockDisplay()
    {
        var clockFormat = PassDisplayFormat.FromSettings(_settings.Current.Use24HourClock);
        var useUtc = _settings.Current.DisplayTimesInUtc;
        var now = DateTime.UtcNow;
        var label = PassDisplayFormat.FormatTimeZoneLabel(useUtc);
        if (!IsMapTimeScrubbing)
        {
            UtcClock = $"{PassDisplayFormat.FormatStatusClock(now, clockFormat, useUtc)} {label}";
            return;
        }

        var mapUtc = now + TimeSpan.FromMinutes(MapTimeOffsetMinutes);
        UtcClock = $"{PassDisplayFormat.FormatStatusClock(mapUtc, clockFormat, useUtc)} {label}  ({MapTimeStatusText})";
    }

    public void ApplyClockFormatFromSettings()
    {
        DisplayTimesInUtc = _settings.Current.DisplayTimesInUtc;
        Use24HourClock = _settings.Current.Use24HourClock;
        UpdateUtcClockDisplay();
        RefreshPassTimeDisplay();
        RefreshHamsAtRoveClockDisplay();
    }

    private void RefreshPassTimeDisplay()
    {
        var clockFormat = PassDisplayFormat.FromSettings(_settings.Current.Use24HourClock);
        var useUtc = _settings.Current.DisplayTimesInUtc;
        if (Passes.Count == 0)
            return;

        var rows = Passes.OfType<PassRowViewModel>().ToList();
        if (rows.Count == 0)
            return;

        var items = new List<IPassListItem>(rows.Count + 4);
        DateOnly? currentDay = null;
        foreach (var row in rows)
        {
            var day = PassDisplayFormat.GetDisplayDate(row.AosUtc, useUtc);
            if (currentDay != day)
            {
                currentDay = day;
                items.Add(new PassDayHeaderViewModel
                {
                    DateLabel = PassDisplayFormat.FormatDayHeader(row.AosUtc, useUtc: useUtc)
                });
            }

            items.Add(row.WithTimeDisplay(clockFormat, useUtc));
        }

        ApplyPassListHighlights(items);
        ReplacePassList(items);
        UpdateCommunityStatusDisplays();
    }

    private void RefreshHamsAtRoveClockDisplay()
    {
        if (HamsAtRoves.Count == 0)
            return;

        var clockFormat = PassDisplayFormat.FromSettings(_settings.Current.Use24HourClock);
        var useUtc = _settings.Current.DisplayTimesInUtc;
        var rows = HamsAtRoves.ToList();
        HamsAtRoves.Clear();
        foreach (var row in rows)
            HamsAtRoves.Add(row.WithClockFormat(clockFormat, useUtc));
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
        _rig.PublishContext(GetRigSettingsForController(), Frequencies.TryBuildRigTrackingContext(focused), catPausedOverride: GetCatPausedOverride());
    }

    private RigSettings GetRigSettingsForController() => _settings.Current.Rig;

    private bool? GetCatPausedOverride()
    {
        var rig = _settings.Current.Rig;
        return RigCatPaused != rig.CatUpdatesPaused ? RigCatPaused : null;
    }

    private void PushCloudlogRadio(SatelliteTrackState? focused)
    {
        var update = Frequencies.TryBuildCloudlogUpdate(focused);
        _cloudlog.Publish(_settings.Current.Cloudlog, update);
    }

    private void PushSatelliteLink(SatelliteTrackState? focused, bool force = false)
    {
        var context = Frequencies.TryBuildRigTrackingContext(focused);
        _satelliteLink.Publish(focused, context, force);
    }

    private void ApplySatelliteLinkSettings()
    {
        _satelliteLink.ApplySettings(_settings.Current.SatelliteLink ?? new SatelliteLinkSettings());
        UpdateSatelliteLinkStatusDisplay();
    }

    private void OnSatelliteLinkStateChanged()
    {
        Dispatcher.UIThread.Post(UpdateSatelliteLinkStatusDisplay, DispatcherPriority.Normal);
    }

    private void UpdateSatelliteLinkStatusDisplay()
    {
        var settings = _settings.Current.SatelliteLink ?? new SatelliteLinkSettings();
        ShowSatelliteLinkStatus = settings.Enabled;
        if (!settings.Enabled)
            return;

        var port = SatelliteLinkSettings.NormalizePort(settings.Port);
        var bindScope = settings.AllowLanClients
            ? _l.Get("Main.SatelliteLink.Bind.Lan")
            : _l.Get("Main.SatelliteLink.Bind.Local");
        var error = _satelliteLink.LastError;

        ShowSatelliteLinkOk = false;
        ShowSatelliteLinkWaiting = false;
        ShowSatelliteLinkError = false;

        if (!string.IsNullOrWhiteSpace(error) && !_satelliteLink.IsListening)
        {
            ShowSatelliteLinkError = true;
            SatelliteLinkStatusText = _l.Get("Main.SatelliteLink.Error");
            SatelliteLinkStatusTooltip = _l.Get("Main.SatelliteLink.Tooltip.Error", error);
            return;
        }

        var clients = _satelliteLink.ClientCount;
        if (clients > 0)
        {
            ShowSatelliteLinkOk = true;
            SatelliteLinkStatusText = _l.Get("Main.SatelliteLink.Clients", clients);
            SatelliteLinkStatusTooltip = _l.Get(
                "Main.SatelliteLink.Tooltip.Clients",
                port,
                bindScope,
                clients);
            return;
        }

        ShowSatelliteLinkWaiting = true;
        SatelliteLinkStatusText = _l.Get("Main.SatelliteLink.Waiting");
        SatelliteLinkStatusTooltip = _l.Get("Main.SatelliteLink.Tooltip.Waiting", port, bindScope);
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
            _settings.Current.Gps,
            out var message);
        ComPortConflictText = ComPortConflictLocalizer.Localize(message, _l);
    }

    private void UpdateGpsStatusDisplay()
    {
        var gpsSettings = _settings.Current.Gps;
        var status = _gps.GetStatus();
        ShowGpsStatus = GpsStatusHelper.ShowGpsIndicator(gpsSettings);
        GpsHasFix = ShowGpsStatus && GpsStatusHelper.HasFix(status);
        ShowGpsTimeStatus = GpsStatusHelper.ShowGpsTimeIndicator(gpsSettings);
        GpsTimeActive = GpsStatusHelper.IsGpsTimeActive(gpsSettings, _gps.GetTrackingUtc());
        var grid = GpsStatusHelper.GridSquareForStatus(gpsSettings, GroundStation.GridSquare);
        GpsStatusText = grid is not null
            ? _l.Get("Main.Gps.StatusWithGrid", grid)
            : _l.Get("Main.Gps.Status");
        OnPropertyChanged(nameof(GpsNoFix));
        OnPropertyChanged(nameof(GpsTimeInactive));
    }

    private void TryApplyGpsStationUpdate()
    {
        var gpsSettings = _settings.Current.Gps;
        if (!gpsSettings.AutoUpdateStation || !gpsSettings.Enabled)
            return;

        var status = _gps.GetStatus();
        if (!status.HasFix
            || status.LatitudeDeg is not { } lat
            || status.LongitudeDeg is not { } lon)
            return;

        var gs = _settings.Current.GroundStation;
        var altChanged = gpsSettings.UseGpsAltitude
            && status.AltitudeMeters is { } alt
            && Math.Abs(gs.AltitudeMetersAsl - alt) > 0.5;
        var posChanged = Math.Abs(gs.LatitudeDeg - lat) > 0.00005
            || Math.Abs(gs.LongitudeDeg - lon) > 0.00005
            || altChanged;

        if (!posChanged)
            return;

        gs.LatitudeDeg = lat;
        gs.LongitudeDeg = lon;
        if (gpsSettings.UseGpsAltitude && status.AltitudeMeters is { } newAlt)
            gs.AltitudeMetersAsl = newAlt;

        _settings.SyncGridFromLatLon();
        _settings.SyncActiveStationFromGroundStation();
        RefreshGroundStationFromSettings();
        _liveTracking.RequestReload();

        if (DateTime.UtcNow - _lastGpsStationPersistUtc >= TimeSpan.FromSeconds(60))
        {
            _settings.RequestSave();
            _lastGpsStationPersistUtc = DateTime.UtcNow;
        }
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

    /// <summary>Disconnect CAT/rotator/GPS hardware before process exit so COM ports are released cleanly.</summary>
    internal void DisconnectHardwareForShutdown()
    {
        if (Interlocked.Exchange(ref _hardwareShutdownStarted, 1) != 0)
            return;

        AppShutdownWatchdog.Start();

        // Stop UI ticks first so they cannot re-publish Update and reopen ports after Disconnect.
        _timer.Stop();
        _liveDisplayTimer?.Stop();

        try
        {
            _liveTracking.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Live tracking dispose during shutdown failed");
        }

        try
        {
            _rig.DisconnectAndWait();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Rig disconnect during shutdown failed");
        }

        try
        {
            _rotator.DisconnectAndWait();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Rotator disconnect during shutdown failed");
        }

        try
        {
            _gps.DisconnectAndWait();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GPS disconnect during shutdown failed");
        }
    }

    internal Task SaveSettingsAsync() => _settings.SaveAsync();

    private void UpdateRotatorDisplay()
    {
        if (!_settings.Current.Rotator.Enabled)
        {
            ShowRotatorStatus = false;
            SkyPlotRotatorAzimuthDeg = null;
            SkyPlotRotatorElevationDeg = null;
            return;
        }

        ShowRotatorStatus = true;
        var status = _rotator.GetPositionStatus();
        RotatorAzimuthText = FormatRotatorAzimuthText(status);
        RotatorElevationText = FormatRotatorElevationText(status);
        IsRotatorParked = status.IsParked;
        CanParkRotator = status.IsConnected;
        CanStopRotator = status.IsConnected;
        IsRotatorTrackingHeld = status.IsTrackingHeld;
        IsKeyholeAvoidanceActive = status.IsKeyholeAvoidanceActive;
        IsPrePositioning = status.IsPrePositioning;
        UpdateSkyPlotRotatorPosition(status);
    }

    private void UpdateSkyPlotRotatorPosition(RotatorPositionStatus status)
    {
        if (!status.IsConnected)
        {
            SkyPlotRotatorAzimuthDeg = null;
            SkyPlotRotatorElevationDeg = null;
            return;
        }

        var elevation = status.ElevationDeg ?? status.CommandedElevationDeg;
        if (elevation is null)
        {
            SkyPlotRotatorAzimuthDeg = null;
            SkyPlotRotatorElevationDeg = null;
            return;
        }

        var settings = _settings.Current.Rotator;
        var compassAz = status.CompassAzimuthDeg
            ?? (status.AzimuthDeg is { } mechanical
                ? (int)Math.Round(RotatorAzimuthPlanner.Normalize360(mechanical - settings.AzimuthOffsetDeg))
                : status.CommandedAzimuthDeg);

        if (compassAz is null)
        {
            SkyPlotRotatorAzimuthDeg = null;
            SkyPlotRotatorElevationDeg = null;
            return;
        }

        SkyPlotRotatorAzimuthDeg = compassAz.Value;
        SkyPlotRotatorElevationDeg = Math.Max(0, elevation.Value);
    }

    private void UpdateSkyPlotPassPath()
    {
        var pass = FindSkyPlotPass(FocusedNoradId);
        SkyPlotPassPath = pass is null
            ? []
            : _tracking.BuildSkyPlotPassPath(pass, GroundStation);
    }

    internal static PassInfo? FindSkyPlotPass(string? noradId, IReadOnlyList<PassInfo> candidates, DateTime utcNow)
    {
        if (string.IsNullOrEmpty(noradId) || candidates.Count == 0)
            return null;

        var inProgress = candidates.FirstOrDefault(p =>
            string.Equals(p.NoradId, noradId, StringComparison.Ordinal)
            && p.AosUtc <= utcNow
            && utcNow < p.LosUtc);
        if (inProgress is not null)
            return inProgress;

        return candidates.FirstOrDefault(p =>
            string.Equals(p.NoradId, noradId, StringComparison.Ordinal)
            && p.LosUtc > utcNow);
    }

    private PassInfo? FindSkyPlotPass(string? noradId)
    {
        var candidates = TimelinePasses
            ?? Passes.OfType<PassRowViewModel>().Select(p => p.Source).ToList();
        return FindSkyPlotPass(noradId, candidates, DateTime.UtcNow);
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

    internal static string FormatRotatorElevationText(RotatorPositionStatus status)
    {
        if (!status.IsConnected)
            return "—";

        if (status.ElevationDeg is { } polled)
            return $"{polled}°";

        if (status.CommandedElevationDeg is { } commanded)
            return $"{commanded}°";

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

    [RelayCommand(CanExecute = nameof(CanStopRotator))]
    private void StopRotator()
    {
        _rotator.Stop(_settings.Current.Rotator);
        UpdateRotatorDisplay();
    }

    [RelayCommand(CanExecute = nameof(IsRotatorTrackingHeld))]
    private void ResumeRotatorTracking()
    {
        _rotator.ResumeTracking(_settings.Current.Rotator);
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
        RigStatusText = RigStatusLocalizer.Localize(_l, status);
        RigReceiveText = FormatSidebarFrequency(status.LastReceiveHz, Frequencies.RadioReceiveText, status.IsConnected);
        RigTransmitText = FormatSidebarFrequency(status.LastTransmitHz, Frequencies.RadioTransmitText, status.IsConnected);
        TryShowTs2000SatlWarning(status);
    }

    private void TryShowTs2000SatlWarning(RigConnectionStatus status)
    {
        if (status.StatusKind != RigStatusKind.Ts2000SatlUnconfirmed)
            return;

        var passKey = FocusedNoradId;
        if (string.IsNullOrWhiteSpace(passKey)
            || string.Equals(_ts2000SatlWarningPassKey, passKey, StringComparison.Ordinal))
            return;

        _ts2000SatlWarningPassKey = passKey;
        _ = ShowTs2000SatlWarningAsync();
    }

    private async Task ShowTs2000SatlWarningAsync()
    {
        if (App.MainWindow is null)
            return;

        try
        {
            await Ts2000SatlWarningDialog.ShowAsync(App.MainWindow, _l).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to show TS-2000 SATL warning dialogue");
        }
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

    private async Task RefreshHamsAtRovesAsyncSafeAsync()
    {
        try
        {
            await RefreshHamsAtRovesAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Hams.at rove refresh failed");
        }
    }

    private async Task RefreshHamsAtRovesAsync(bool bypassCache = false)
    {
        if (!ShowHamsAtRovesPanel)
        {
            HamsAtRoves.Clear();
            HamsAtRovesStatusText = "";
            return;
        }

        var result = await _hamsAtRoves.FetchUpcomingAsync(
            _settings.Current.HamsAt,
            bypassCache: bypassCache).ConfigureAwait(false);
        if (!result.Ok)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                HamsAtRovesStatusText = FormatHamsAtError(result);
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
                foreach (var grid in HamsAtRoveRowViewModel.UniqueGrids(alert.Grids))
                {
                    var check = await _cloudlogLookup.CheckGridWorkedAsync(cloudlog, grid).ConfigureAwait(false);
                    if (check is not null)
                        checks.Add(check);
                }

                if (checks.Count > 0)
                    gridChecks = checks;
            }

            rows.Add(HamsAtRoveRowViewModel.From(
                alert,
                _settings.Current.DisplayTimesInUtc,
                clockFormat,
                gridChecks));
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

    private string FormatHamsAtError(HamsAtFetchResult result) => result.ErrorKind switch
    {
        HamsAtFetchErrorKind.InvalidApiKey => _l.Get("Main.HamsAtRoves.InvalidApiKey"),
        HamsAtFetchErrorKind.RateLimited => _l.Get("Main.HamsAtRoves.RateLimited"),
        HamsAtFetchErrorKind.Timeout => _l.Get("Main.HamsAtRoves.Timeout"),
        HamsAtFetchErrorKind.Unavailable => _l.Get("Main.HamsAtRoves.Unavailable"),
        HamsAtFetchErrorKind.Network => _l.Get("Main.HamsAtRoves.Network"),
        HamsAtFetchErrorKind.UnexpectedResponse => _l.Get("Main.HamsAtRoves.UnexpectedResponse"),
        _ => _l.Get("Main.HamsAtRoves.LoadFailed")
    };

    private void PruneExpiredPasses()
    {
        var now = PassUtc.Normalize(DateTime.UtcNow);
        var removedAny = false;

        for (var i = Passes.Count - 1; i >= 0; i--)
        {
            if (Passes[i] is PassRowViewModel p && PassUtc.Normalize(p.LosUtc) <= now)
            {
                Passes.RemoveAt(i);
                removedAny = true;
            }
        }

        if (!removedAny)
            return;

        // Remove orphaned day headers (reverse scan)
        for (var i = Passes.Count - 1; i >= 0; i--)
        {
            if (Passes[i] is PassDayHeaderViewModel
                && (i + 1 >= Passes.Count || Passes[i + 1] is PassDayHeaderViewModel))
                Passes.RemoveAt(i);
        }
    }

    private void PublishActivePassForRotator(SatelliteTrackState? focused)
    {
        if (!_settings.Current.Rotator.Enabled)
        {
            if (_rotatorActivePassKey is not null)
            {
                _rotatorActivePassKey = null;
                _rotator.SetActivePass(null);
            }

            return;
        }

        var noradId = focused?.NoradId ?? FocusedNoradId;
        var pass = FindSkyPlotPass(noradId);
        var key = pass is null
            ? null
            : $"{pass.NoradId}|{pass.AosUtc.Ticks}";

        if (string.Equals(key, _rotatorActivePassKey, StringComparison.Ordinal))
            return;

        _rotatorActivePassKey = key;
        _rotator.SetActivePass(pass);
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
            MotionHeadingDeg = state.MotionHeadingDeg,
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

        for (var i = 0; i < states.Count; i++)
        {
            if (string.Equals(states[i].NoradId, focusedNoradId, StringComparison.Ordinal))
                return states[i];
        }

        return null;
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

    private void ProcessScheduledPassReminders()
    {
        var schedule = _settings.Current.PassSchedule ?? new PassScheduleSettings();
        if (!schedule.SoundEnabled && !schedule.AlertEnabled)
            return;

        var scheduled = _settings.Current.ScheduledPasses;
        if (scheduled is null || scheduled.Count == 0)
            return;

        var upcoming = Passes.OfType<PassRowViewModel>().Select(p => p.Source).ToList();
        if (upcoming.Count == 0)
            return;

        var due = _scheduledPassReminder.Process(
            DateTime.UtcNow,
            scheduled,
            upcoming,
            schedule.LeadMinutesBeforeAos);

        foreach (var pass in due)
            ShowScheduledPassAlert(pass, schedule);
    }

    private void ShowScheduledPassAlert(PassInfo pass, PassScheduleSettings schedule)
    {
        Log.Information(
            "Scheduled pass reminder: {Satellite} AOS {AosUtc:u}",
            pass.SatelliteName,
            PassUtc.Normalize(pass.AosUtc));

        if (schedule.SoundEnabled)
        {
            try
            {
                _alertSound.PlayAlert();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Scheduled pass alert sound failed");
            }
        }

        if (!schedule.AlertEnabled)
            return;

        var owner = App.MainWindow;
        try
        {
            owner?.Activate();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to activate main window for scheduled pass alert");
        }

        var useUtc = _settings.Current.DisplayTimesInUtc;
        var clockFormat = PassDisplayFormat.FromSettings(_settings.Current.Use24HourClock);
        var aosText = PassDisplayFormat.FormatLocal(pass.AosUtc, clockFormat, useUtc: useUtc);
        var countdown = PassDisplayFormat.FormatCountdownToAos(DateTime.UtcNow, pass.AosUtc);
        var plotData = TryBuildScheduledPassPolarPlot(pass);
        ScheduledPassAlertWindow.Show(
            owner,
            pass.SatelliteName,
            countdown,
            aosText,
            plotData,
            _settings.Current.MinimumElevationDeg,
            GroundStation.HorizonMask,
            useUtc,
            _settings.Current.Use24HourClock);
    }

    private PassPolarPlotData? TryBuildScheduledPassPolarPlot(PassInfo pass)
    {
        try
        {
            var satellite = _tleService.Catalog.FirstOrDefault(s => s.NoradId == pass.NoradId);
            if (satellite is null)
                return null;

            var propagator = App.Services.GetRequiredService<IOrbitPropagator>();
            if (!propagator.HasSatellite(pass.NoradId))
                propagator.LoadSatellite(satellite);

            return PassPolarPlotBuilder.Build(
                satellite,
                propagator,
                GroundStation,
                pass,
                useFullPass: true,
                pass.AosUtc,
                pass.LosUtc,
                _settings.Current.MinimumElevationDeg,
                includeMutualMarkers: false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Scheduled pass polar plot build failed for {Satellite}", pass.SatelliteName);
            return null;
        }
    }

    public void TogglePassScheduled(PassRowViewModel row)
    {
        var updated = ScheduledPassReminder.Toggle(
            _settings.Current.ScheduledPasses ?? [],
            row.NoradId,
            row.AosUtc);
        _settings.Current.ScheduledPasses = updated;
        _settings.RequestSave();
        ApplyScheduledFlagsToPassList();
    }

    public bool IsPassContextScheduled(PassRowViewModel? row) =>
        row is not null
        && ScheduledPassReminder.IsScheduled(
            _settings.Current.ScheduledPasses ?? [],
            row.NoradId,
            row.AosUtc);

    private void ApplyScheduledFlagsToPassList()
    {
        var scheduled = _settings.Current.ScheduledPasses ?? [];
        foreach (var pass in Passes.OfType<PassRowViewModel>())
            pass.IsScheduled = ScheduledPassReminder.IsScheduled(scheduled, pass.NoradId, pass.AosUtc);
    }

    private void RematchScheduledPasses(IReadOnlyList<PassInfo> upcoming)
    {
        var rematched = ScheduledPassReminder.RematchAndPrune(
            _settings.Current.ScheduledPasses ?? [],
            upcoming,
            DateTime.UtcNow);
        var previous = _settings.Current.ScheduledPasses ?? [];
        if (ScheduledPassListsEqual(previous, rematched))
            return;

        _settings.Current.ScheduledPasses = rematched;
        _settings.RequestSave();
    }

    private static bool ScheduledPassListsEqual(
        IReadOnlyList<ScheduledPassEntry> a,
        IReadOnlyList<ScheduledPassEntry> b)
    {
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].NoradId, b[i].NoradId, StringComparison.Ordinal))
                return false;
            if (PassUtc.Normalize(a[i].AosUtc) != PassUtc.Normalize(b[i].AosUtc))
                return false;
        }

        return true;
    }

    private async Task SpeakAnnouncementAsync(string text, string voiceName)
    {
        var selectedVoice = string.IsNullOrWhiteSpace(voiceName) ? null : voiceName;
        try
        {
            await _speech.SpeakAsync(
                text,
                selectedVoice).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Bluetooth/headset handover can transiently fail TTS output.
            // Retry once after a short delay and let the OS pick the default voice.
            Log.Warning(ex, "Voice announcement failed; retrying with default voice: {Text}", text);

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);
                await _speech.SpeakAsync(text, voiceName: null).ConfigureAwait(false);
                Log.Information("Voice announcement retry succeeded: {Text}", text);
            }
            catch (Exception retryEx)
            {
                Log.Warning(retryEx, "Voice announcement retry failed: {Text}", text);
            }
        }
    }

    private void UpdatePassHighlightState()
    {
        SyncRecordingPassIdentity();
        var now = DateTime.UtcNow;
        foreach (var pass in Passes.OfType<PassRowViewModel>())
            pass.UpdateDisplay(now, ImminentPassWindow, IsPassBeingRecorded(pass));
    }

    /// <summary>
    /// Apply REC / pass badges to rows before they hit the bound collection so a list rebuild
    /// cannot flash an empty or non-REC badge for a frame.
    /// </summary>
    private void ApplyPassListHighlights(IReadOnlyList<IPassListItem> items)
    {
        var rows = items.OfType<PassRowViewModel>().ToList();
        SyncRecordingPassIdentity(rows.Select(p => p.Source).ToList());
        var now = DateTime.UtcNow;
        foreach (var pass in rows)
            pass.UpdateDisplay(now, ImminentPassWindow, IsPassBeingRecorded(pass));
    }

    private void ReplacePassList(IReadOnlyList<IPassListItem> items)
    {
        var common = Math.Min(Passes.Count, items.Count);
        for (var i = 0; i < common; i++)
        {
            if (!ReferenceEquals(Passes[i], items[i]))
                Passes[i] = items[i];
        }

        for (var i = common; i < items.Count; i++)
            Passes.Add(items[i]);

        for (var i = Passes.Count - 1; i >= items.Count; i--)
            Passes.RemoveAt(i);
    }

    /// <summary>Remember which list row started recording so later passes with the same name stay unhighlighted.</summary>
    private void SyncRecordingPassIdentity(IReadOnlyList<PassInfo>? candidatePasses = null)
    {
        if (!_recording.IsRecording || AudioRecordingSessions.IsManualTest(_recording))
        {
            _recordingPassNoradId = null;
            _recordingPassAosUtc = null;
            _recordingStartedUtc = null;
            return;
        }

        var noradId = _recording.ActiveNoradId;
        if (string.IsNullOrEmpty(noradId))
        {
            _recordingPassNoradId = null;
            _recordingPassAosUtc = null;
            _recordingStartedUtc = null;
            return;
        }

        if (!string.Equals(_recordingPassNoradId, noradId, StringComparison.Ordinal))
        {
            _recordingPassNoradId = noradId;
            _recordingPassAosUtc = null;
            _recordingStartedUtc = DateTime.UtcNow;
        }

        _recordingStartedUtc ??= DateTime.UtcNow;

        // Re-bind after every pass-list refresh — predicted AOS can shift while recording continues.
        // Keep the previous AOS if lookup briefly fails so the REC badge does not flicker to Passing.
        var pass = FindPassForRecording(noradId, DateTime.UtcNow, candidatePasses);
        _recordingPassNoradId = noradId;
        if (pass is not null)
            _recordingPassAosUtc = pass.AosUtc;
    }

    private void StopPassRecordingForStandby()
    {
        if (_recording.IsRecording && !AudioRecordingSessions.IsManualTest(_recording))
            _recordingTasks.Schedule(() => _recording.StopAsync(), "stop recording (standby)");
        _passRecordingCoordinator.ResetTracking();
    }

    private bool IsPassBeingRecorded(PassRowViewModel pass) =>
        PassSidebarMerge.IsPassRecordingTarget(
            pass.Source,
            _recordingPassNoradId,
            _recordingPassAosUtc,
            _recordingStartedUtc,
            DateTime.UtcNow,
            isRecording: !IsStandby
                && _recording.IsRecording
                && !AudioRecordingSessions.IsManualTest(_recording));

    private PassInfo? FindPassForRecording(
        string noradId,
        DateTime utcNow,
        IReadOnlyList<PassInfo>? candidatePasses = null)
    {
        var rows = candidatePasses
            ?? Passes.OfType<PassRowViewModel>().Select(p => p.Source).ToList();
        return PassSidebarMerge.FindPassForRecording(rows, noradId, utcNow, _recordingStartedUtc);
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

    /// <summary>
    /// Publishes the tracking worker snapshot to map/sky-plot bindings.
    /// Assigns the list reference once (no ObservableCollection clear/re-add churn).
    /// </summary>
    private void SyncLiveStates(IReadOnlyList<SatelliteTrackState> states)
    {
        if (ReferenceEquals(LiveStates, states))
            return;

        LiveStates = states;
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
        _liveTracking.FocusedNoradId = value;
        _trackerSnapshot.FocusedNoradId = value;

        if (string.IsNullOrEmpty(value))
        {
            SoloFocusedSatellite = false;
            SkyPlotPassPath = [];
            return;
        }

        ApplySatelliteFocus(value);
        UpdateSkyPlotPassPath();

        var pass = Passes.OfType<PassRowViewModel>().FirstOrDefault(p => p.NoradId == value);
        if (pass is not null && !ReferenceEquals(SelectedListItem, pass))
            SelectedListItem = pass;
    }

    private void ApplySatelliteFocus(string noradId)
    {
        var states = LiveStates.Count > 0 ? LiveStates : _liveTracking.GetSnapshot();
        if (states.Count == 0)
            return;

        UpdateLiveTelemetry(states);
        var focused = GetFocusedTrackState(states, noradId);
        Frequencies.Update(focused);
        DxStation.Update(focused);
        PushCloudlogRadio(focused);
        PushSatelliteLink(focused, force: true);
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
        var now = DateTime.UtcNow;
        var rows = Passes.OfType<PassRowViewModel>();
        PassRowViewModel? next = null;
        if (!string.IsNullOrEmpty(FocusedNoradId))
        {
            next = rows.FirstOrDefault(p =>
                string.Equals(p.NoradId, FocusedNoradId, StringComparison.Ordinal)
                && PassUtc.Normalize(p.LosUtc) >= PassUtc.Normalize(now));
        }

        next ??= rows.FirstOrDefault(p => PassUtc.Normalize(p.LosUtc) >= PassUtc.Normalize(now))
                 ?? rows.FirstOrDefault();

        if (next is null
            || !PassDisplayFormat.TryGetSidebarCountdown(now, next.AosUtc, next.LosUtc, out var toLos, out var remaining))
        {
            NextPassText = _l.Get("Main.Pass.None");
            return;
        }

        var countdown = PassDisplayFormat.FormatCountdownHms(remaining);
        NextPassText = toLos
            ? _l.Get("Main.Pass.LosIn", next.SatelliteName, countdown)
            : _l.Get("Main.Pass.AosIn", next.SatelliteName, countdown);
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

    public bool CanOpenPassVisualizer(PassRowViewModel? row) =>
        row is not null && _tleService.Catalog.Any(s => s.NoradId == row.NoradId);

    public PassVisualizerViewModel? CreatePassVisualizerViewModel(PassRowViewModel row)
    {
        if (!CanOpenPassVisualizer(row))
            return null;

        var vm = App.Services.GetRequiredService<PassVisualizerViewModel>();
        vm.Initialize(
            row.Source,
            GroundStation,
            _settings.Current.DisplayTimesInUtc,
            _settings.Current.Use24HourClock,
            MinimumElevationDeg);
        return vm;
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
    private async Task OpenDopplerPassInsightsAsync()
    {
        var vm = App.Services.GetRequiredService<DopplerPassInsightsViewModel>();
        await vm.LoadLatestCommand.ExecuteAsync(null).ConfigureAwait(true);
        var window = new DopplerPassInsightsWindow { DataContext = vm };
        if (App.MainWindow is null)
            return;

        await window.ShowDialog(App.MainWindow);
    }

    private static QsoLogbookWindow? _openLogbookWindow;

    [RelayCommand]
    private void OpenQsoLogbook()
    {
        _trackerSnapshot.FocusedNoradId = FocusedNoradId;
        if (_openLogbookWindow is { IsVisible: true })
        {
            _openLogbookWindow.Activate();
            return;
        }

        var vm = App.Services.GetRequiredService<QsoLogbookViewModel>();
        var window = new QsoLogbookWindow { DataContext = vm };
        window.Closed += (_, _) => _openLogbookWindow = null;
        _openLogbookWindow = window;
        if (App.MainWindow is null)
            return;

        window.Show(App.MainWindow);
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var vm = App.Services.GetRequiredService<SettingsViewModel>();
        try
        {
            var window = new SettingsWindow { DataContext = vm };
            if (App.MainWindow is null)
                return;

            var saved = await window.ShowDialog<bool?>(App.MainWindow) == true;

            if (saved)
            {
                await ApplyPersistedSettingsAsync().ConfigureAwait(true);

                if (vm.LanguageChangedOnLastSave
                    && await LanguageRestartWindow.ShowAsync(App.MainWindow).ConfigureAwait(true))
                {
                    AppRestart.Request();
                }
            }
        }
        finally
        {
            vm.Dispose();
        }
    }

    private async Task OpenSatelliteStatusReportAsync()
    {
        if (App.MainWindow is null)
            return;

        var satellite = Frequencies.SatelliteName;
        var modeType = Frequencies.SelectedMode?.Type;
        if (string.IsNullOrWhiteSpace(satellite)
            || satellite == "—"
            || string.IsNullOrWhiteSpace(modeType))
            return;

        var cfg = _settings.Current.SatelliteStatus;
        if (cfg is not { Enabled: true } || string.IsNullOrWhiteSpace(cfg.ApiToken))
            return;

        var elevationDeg = Frequencies.TryGetFocusedElevationDeg();
        if (!SatelliteStatusReportFormatting.IsElevationReportable(elevationDeg))
        {
            StatusText = _l.Get("SatStatus.Report.BelowElevation");
            return;
        }

        var window = new SatelliteStatusReportWindow(satellite, modeType, _l);
        var confirmed = await window.ShowDialog<bool?>(App.MainWindow).ConfigureAwait(true) == true;
        if (!confirmed)
            return;

        var request = new SatelliteStatusReportRequest(
            satellite,
            modeType,
            window.SelectedStatus,
            DateTime.UtcNow,
            NormalizeReportGridsquare(_settings.Current.GroundStation.GridSquare),
            $"OscarWatch-Tracker/{AppVersionHelper.GetDisplayVersionText()}");

        SatelliteStatusReportResult result;
        try
        {
            result = await _satelliteStatus.SubmitReportAsync(cfg, request).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = _l.Get("SatStatus.Report.Failed", ex.Message);
            return;
        }

        StatusText = result.Ok
            ? (result.Stored
                ? _l.Get("SatStatus.Report.Stored")
                : _l.Get("SatStatus.Report.Duplicate"))
            : _l.Get("SatStatus.Report.Failed", result.Message);

        if (result.Ok)
            _ = RefreshCommunityStatusAsync(force: true);
    }

    private void ConfigureSatelliteStatusRefreshTimer()
    {
        _satelliteStatusRefreshTimer?.Stop();
        _satelliteStatusRefreshTimer = null;

        if (!_settings.Current.SatelliteStatus.Enabled)
        {
            ClearCommunityStatusUi();
            return;
        }

        _communityStatusFeatureUnavailable = false;

        _satelliteStatusRefreshTimer = new DispatcherTimer();
        // When the API previously returned 404, force retries so a temporary outage can recover.
        // Reschedule with a fresh jittered delay each tick so clients do not stay phase-locked.
        _satelliteStatusRefreshTimer.Tick += (_, _) =>
        {
            ScheduleNextCommunityStatusRefresh();
            _ = RefreshCommunityStatusAsync(force: _communityStatusFeatureUnavailable);
        };
        ScheduleNextCommunityStatusRefresh();
    }

    private void ScheduleNextCommunityStatusRefresh()
    {
        if (_satelliteStatusRefreshTimer is null)
            return;

        _satelliteStatusRefreshTimer.Stop();
        _satelliteStatusRefreshTimer.Interval = SatelliteStatusCommunityPresentation.NextRefreshDelay();
        _satelliteStatusRefreshTimer.Start();
    }

    private async Task RefreshCommunityStatusAsync(bool force = false)
    {
        if (!_settings.Current.SatelliteStatus.Enabled)
        {
            ClearCommunityStatusUi();
            return;
        }

        if (_communityStatusFeatureUnavailable && !force)
            return;

        var now = DateTime.UtcNow;
        if (!force
            && _communityStatusCatalog is not null
            && !SatelliteStatusCommunityPresentation.IsRefreshDue(_communityStatusFetchedAtUtc, now))
        {
            await Dispatcher.UIThread.InvokeAsync(UpdateCommunityStatusDisplays);
            return;
        }

        if (_communityStatusFetchInFlight)
            return;

        _communityStatusFetchInFlight = true;
        try
        {
            var settings = _settings.Current.SatelliteStatus;
            var result = await _satelliteStatus.FetchCommunityAsync(settings).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (result.FeatureUnavailable)
                {
                    _communityStatusFeatureUnavailable = true;
                    _communityStatusCatalog = null;
                    ClearCommunityStatusUi();
                    LogCommunityStatusWarn(
                        now,
                        "Community satellite status unavailable (HTTP {StatusCode}): {Message}",
                        result.HttpStatusCode,
                        result.Message);
                    return;
                }

                if (result.Ok && result.Catalog is not null)
                {
                    _communityStatusFeatureUnavailable = false;
                    _communityStatusCatalog = result.Catalog;
                    _communityStatusFetchedAtUtc = result.Catalog.FetchedAtUtc;
                    UpdateCommunityStatusDisplays();
                    Log.Information(
                        "Community satellite status refreshed: satellites={Count}, windowHours={WindowHours}, http={StatusCode}",
                        result.Catalog.Satellites.Count,
                        result.Catalog.WindowHours,
                        result.HttpStatusCode);
                    return;
                }

                // Soft fail: keep last-good while still within TTL; only clear once the cache expires.
                ApplyCommunityStatusSoftFailure(now, result.Message, result.HttpStatusCode, log: true);
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Community satellite status fetch failed");
            await Dispatcher.UIThread.InvokeAsync(() =>
                ApplyCommunityStatusSoftFailure(DateTime.UtcNow, ex.Message, httpStatusCode: 0, log: false));
        }
        finally
        {
            _communityStatusFetchInFlight = false;
        }
    }

    private void ApplyCommunityStatusSoftFailure(DateTime nowUtc, string message, int httpStatusCode, bool log)
    {
        if (_communityStatusCatalog is not null
            && SatelliteStatusCommunityPresentation.IsCacheFresh(_communityStatusFetchedAtUtc, nowUtc))
        {
            UpdateCommunityStatusDisplays();
            if (log)
            {
                LogCommunityStatusWarn(
                    nowUtc,
                    "Community satellite status fetch failed (keeping last-good): http={StatusCode}, {Message}",
                    httpStatusCode,
                    message);
            }

            return;
        }

        _communityStatusCatalog = null;
        ClearCommunityStatusUi();
        if (log)
        {
            LogCommunityStatusWarn(
                nowUtc,
                "Community satellite status fetch failed (cleared stale cache): http={StatusCode}, {Message}",
                httpStatusCode,
                message);
        }
    }

    private void LogCommunityStatusWarn(DateTime nowUtc, string template, int httpStatusCode, string message)
    {
        if (nowUtc - _communityStatusLastWarnUtc <= TimeSpan.FromMinutes(5))
            return;

        _communityStatusLastWarnUtc = nowUtc;
        Log.Warning(template, httpStatusCode, message);
    }

    private void ClearCommunityStatusUi()
    {
        Frequencies.ClearCommunityStatus();
        foreach (var row in Passes.OfType<PassRowViewModel>())
            row.ClearCommunityStatus();
    }

    private void UpdateCommunityStatusDisplays()
    {
        // Keep painting last-good data even when the fetch cache is past TTL; RefreshCommunityStatusAsync
        // clears the catalog only after a failed refresh once the TTL has expired.
        if (!_settings.Current.SatelliteStatus.Enabled
            || _communityStatusFeatureUnavailable
            || _communityStatusCatalog is null)
        {
            ClearCommunityStatusUi();
            return;
        }

        var utcNow = DateTime.UtcNow;
        var catalog = _communityStatusCatalog;
        string Localize(string key, params object?[] args) =>
            args.Length == 0 ? _l.Get(key) : _l.Get(key, args!);

        // Overlay: selected mode.
        var satName = Frequencies.SatelliteName;
        var modeType = Frequencies.SelectedMode?.Type;
        if (string.IsNullOrWhiteSpace(satName)
            || satName == "—"
            || string.IsNullOrWhiteSpace(modeType))
        {
            Frequencies.ClearCommunityStatus();
        }
        else
        {
            var mode = catalog.TryGetMode(satName, modeType);
            if (mode is null)
            {
                Frequencies.ClearCommunityStatus();
            }
            else
            {
                var stale = SatelliteStatusCommunityPresentation.IsStale(mode.NewestReportUtc, utcNow);
                Frequencies.ApplyCommunityStatus(
                    show: true,
                    mode.Kind,
                    SatelliteStatusCommunityPresentation.ShortLabel(
                        mode.Kind,
                        (key, args) => Localize(key, args)),
                    SatelliteStatusCommunityPresentation.BuildOverlayToolTip(
                        mode,
                        catalog.WindowHours,
                        utcNow,
                        (key, args) => Localize(key, args)),
                    stale);
            }
        }

        // Pass list dots.
        foreach (var row in Passes.OfType<PassRowViewModel>())
        {
            var selections = _settings.Current.FrequencySelections;
            var resolvedMode = SatelliteStatusCommunityPresentation.ResolvePassRowModeType(
                row.SatelliteName,
                row.NoradId,
                selections,
                _satelliteDatabase);

            if (string.IsNullOrWhiteSpace(resolvedMode))
            {
                row.ClearCommunityStatus();
                continue;
            }

            var communitySat = catalog.TryGetSatellite(row.SatelliteName);
            // Prefer catalogue name from DB for lookup.
            var entry = _satelliteDatabase.TryGetEntry(row.SatelliteName, row.NoradId);
            var lookupName = entry?.Name ?? row.SatelliteName;
            communitySat ??= catalog.TryGetSatellite(lookupName);

            var modeStatus = catalog.TryGetMode(lookupName, resolvedMode)
                             ?? catalog.TryGetMode(row.SatelliteName, resolvedMode);
            if (modeStatus is null && communitySat is null)
            {
                row.ClearCommunityStatus();
                continue;
            }

            var kind = modeStatus?.Kind ?? SatelliteCommunityStatusKind.Unknown;
            var stale = SatelliteStatusCommunityPresentation.IsStale(modeStatus?.NewestReportUtc, utcNow);
            var storageKey = entry?.Name?.Trim() ?? row.SatelliteName.Trim();
            var fromSelection = selections.TryGetValue(storageKey, out var sel)
                                && string.Equals(sel.ModeType, resolvedMode, StringComparison.OrdinalIgnoreCase);

            var catalogueModes = SatelliteStatusCommunityPresentation.CatalogueModeTypes(
                row.SatelliteName,
                row.NoradId,
                _satelliteDatabase);

            var tip = SatelliteStatusCommunityPresentation.BuildPassToolTip(
                lookupName,
                resolvedMode,
                fromSelection,
                catalogueModes,
                communitySat,
                catalog.WindowHours,
                utcNow,
                (key, args) => Localize(key, args));

            var autoName = _l.Get(
                "SatStatus.Community.PassAutomation",
                lookupName,
                resolvedMode,
                SatelliteStatusCommunityPresentation.FullLabel(
                    kind,
                    modeStatus?.StatusLabel,
                    (key, args) => Localize(key, args)));

            row.ApplyCommunityStatus(kind, stale, tip, autoName);
        }
    }

    private static string? NormalizeReportGridsquare(string? grid) =>
        SatelliteStatusReportFormatting.NormalizeGridsquare(grid);

    [RelayCommand]
    private async Task ExportSettingsAsync()
    {
        if (App.MainWindow is null)
            return;

        var status = await AppDataFileCommands.ExportSettingsAsync(App.MainWindow, _settings, _l)
            .ConfigureAwait(true);
        if (status is not null)
            StatusText = status;
    }

    [RelayCommand]
    private async Task ImportSettingsAsync()
    {
        if (App.MainWindow is null)
            return;

        var (applied, status) = await AppDataFileCommands.ImportSettingsAsync(App.MainWindow, _settings, _l)
            .ConfigureAwait(true);
        if (status is not null)
            StatusText = status;

        if (applied)
            await ApplyPersistedSettingsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ExportTransponderDatabaseAsync()
    {
        if (App.MainWindow is null)
            return;

        var status = await AppDataFileCommands.ExportTransponderDatabaseAsync(
                App.MainWindow,
                _transponderDatabaseSync,
                _l)
            .ConfigureAwait(true);
        if (status is not null)
            StatusText = status;
    }

    [RelayCommand]
    private async Task ImportTransponderDatabaseAsync()
    {
        if (App.MainWindow is null)
            return;

        var (applied, status) = await AppDataFileCommands.ImportTransponderDatabaseAsync(
                App.MainWindow,
                _transponderDatabaseSync,
                _l)
            .ConfigureAwait(true);

        if (status is not null)
            StatusText = status;

        if (applied)
        {
            Frequencies.ReloadFromDatabase();
            Tick();
        }
    }

    private async Task ApplyPersistedSettingsAsync()
    {
        ConfigureTleAutoUpdateTimer();
        ConfigureAppUpdateCheckTimer();
        ApplyHamsAtSidebarSettings();
        ConfigureHamsAtRefreshTimer();
        await RefreshHamsAtRovesAsync().ConfigureAwait(true);
        ConfigureSatelliteStatusRefreshTimer();
        _ = RefreshCommunityStatusAsync();
        await ReloadTleCatalogAfterSettingsAsync().ConfigureAwait(true);
        _liveTracking.RequestReload();
        _rotator.Disconnect();
        // Non-blocking: DisconnectAndWait on the UI thread freezes/crashes if the rig worker
        // is busy (e.g. Yaesu set-command timeouts). Connect is re-enqueued below after drain.
        _rig.Disconnect();
        _gps.Disconnect();
        _gps.Update(_settings.Current.Gps);
        ApplySatelliteLinkSettings();
        _cloudlog.ResetThrottle();
        if (!_settings.Current.PassRecording.Enabled && _recording.IsRecording)
            await _recording.StopAsync();
        _passRecordingCoordinator.ResetTracking();
        await RefreshPassesAsync();
        UpdateStatus();
        RefreshGroundStationFromSettings();
        ShowFootprintMotionArrows = _settings.Current.ShowFootprintMotionArrows;
        ShowGreylineOverlay = _settings.Current.ShowGreylineOverlay;
        ShowMultiTrackOverlay = _settings.Current.ShowMultiTrackOverlay;
        ApplyMapCentreFromSettings();
        RigCatPaused = _settings.Current.Rig.CatUpdatesPaused;
        Frequencies.RefreshSatelliteStatusReportAvailability();
        _liveDisplayTimer?.Start();
        Tick();
        if (_settings.Current.Rig.Enabled && !ShowComPortConflict)
            RefreshRigFromOverlay(reinitializePass: true);
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

            LogTleLoadDiagnostics();
            TryBackfillEnabledSatelliteIds();
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
            TryBackfillEnabledSatelliteIds();
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
            GridSquare = gs.GridSquare,
            HorizonMask = gs.HorizonMask?.Clone() ?? new HorizonMask()
        };
        RefreshMapCentreLongitude();
    }

    /// <summary>Applies persisted map-centre settings and recomputes the effective mid-map longitude.</summary>
    public void ApplyMapCentreFromSettings()
    {
        MapCentreMode = _settings.Current.MapCentreMode;
        MapCentreCustomLongitudeDeg = _settings.Current.MapCentreLongitudeDeg;
        RefreshMapCentreLongitude();
    }

    /// <summary>Live preview from Settings before save.</summary>
    public void PreviewMapCentre(MapCentreMode mode, double customLongitudeDeg, double stationLongitudeDeg)
    {
        MapCentreMode = mode;
        MapCentreCustomLongitudeDeg = customLongitudeDeg;
        MapCentreLongitude = ResolveMapCentreLongitude(mode, customLongitudeDeg, stationLongitudeDeg);
    }

    private void RefreshMapCentreLongitude()
    {
        var stationLon = GroundStation?.LongitudeDeg
            ?? _settings.Current.GroundStation.LongitudeDeg;
        MapCentreLongitude = ResolveMapCentreLongitude(
            MapCentreMode,
            MapCentreCustomLongitudeDeg,
            stationLon);
    }

    internal static double ResolveMapCentreLongitude(
        MapCentreMode mode,
        double customLongitudeDeg,
        double stationLongitudeDeg) =>
        mode switch
        {
            MapCentreMode.Station => stationLongitudeDeg,
            MapCentreMode.Custom => customLongitudeDeg,
            _ => 0.0
        };

    [RelayCommand]
    private void CloseApplication()
    {
        DisconnectHardwareForShutdown();
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
        TryBackfillEnabledSatelliteIds();
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
        await RefreshPassesAsync();
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
                await RefreshPassesAsync();
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
        try
        {
            var selectedNorad = (SelectedListItem as PassRowViewModel)?.NoradId;
            var passes = await _tracking.GetUpcomingPassesAsync().ConfigureAwait(false);

            void Apply()
            {
                var utcNow = PassUtc.Normalize(DateTime.UtcNow);
                var inProgress = Passes.OfType<PassRowViewModel>()
                    .Where(p => PassUtc.Normalize(p.AosUtc) <= utcNow && PassUtc.Normalize(p.LosUtc) > utcNow)
                    .Select(p => p.Source)
                    .ToList();
                var merged = PassSidebarMerge.MergeInProgressPasses(passes, inProgress, utcNow)
                    .Where(p => PassUtc.Normalize(p.LosUtc) > utcNow)
                    .ToList();

                var useUtc = _settings.Current.DisplayTimesInUtc;
                var clockFormat = PassDisplayFormat.FromSettings(_settings.Current.Use24HourClock);
                var scheduled = _settings.Current.ScheduledPasses ?? [];
                var items = new List<IPassListItem>(Math.Min(merged.Count, 50) + 8);
                DateOnly? currentDay = null;
                foreach (var p in merged.Take(50))
                {
                    var day = PassDisplayFormat.GetDisplayDate(p.AosUtc, useUtc);
                    if (currentDay != day)
                    {
                        currentDay = day;
                        items.Add(new PassDayHeaderViewModel
                        {
                            DateLabel = PassDisplayFormat.FormatDayHeader(p.AosUtc, useUtc: useUtc)
                        });
                    }

                    var row = PassRowViewModel.From(p, clockFormat, useUtc);
                    row.IsScheduled = ScheduledPassReminder.IsScheduled(scheduled, p.NoradId, p.AosUtc);
                    items.Add(row);
                }

                RematchScheduledPasses(merged);
                ApplyPassListHighlights(items);
                ReplacePassList(items);
                ApplyScheduledFlagsToPassList();

                if (selectedNorad is not null)
                    SelectedListItem = Passes.OfType<PassRowViewModel>().FirstOrDefault(p => p.NoradId == selectedNorad);

                UpdateCommunityStatusDisplays();

                // Update the timeline passes for the elevation timeline control
                TimelinePasses = merged.Take(50).ToList();
                UpdateSkyPlotPassPath();
            }

            if (Dispatcher.UIThread.CheckAccess())
                Apply();
            else
                await Dispatcher.UIThread.InvokeAsync(Apply);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Upcoming pass list refresh failed");
        }
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
    [NotifyPropertyChangedFor(nameof(ShowScheduleButton))]
    private PassRowHighlight _highlight;

    [ObservableProperty]
    private string _badgeText = "";

    [ObservableProperty]
    private bool _showBadge;

    [ObservableProperty]
    private bool _showCommunityStatusDot;

    [ObservableProperty]
    private bool _isCommunityStatusDotStale;

    [ObservableProperty]
    private double _communityStatusDotOpacity = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommunityStatusIsOn))]
    [NotifyPropertyChangedFor(nameof(CommunityStatusIsOff))]
    [NotifyPropertyChangedFor(nameof(CommunityStatusIsTelem))]
    [NotifyPropertyChangedFor(nameof(CommunityStatusIsUnknown))]
    private SatelliteCommunityStatusKind _communityStatusKind = SatelliteCommunityStatusKind.Unknown;

    [ObservableProperty]
    private string _communityStatusToolTip = "";

    [ObservableProperty]
    private string _communityStatusAutomationName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleGlyph))]
    [NotifyPropertyChangedFor(nameof(ScheduleToolTip))]
    [NotifyPropertyChangedFor(nameof(ScheduleAutomationName))]
    private bool _isScheduled;

    public string ScheduleGlyph => IsScheduled ? "●" : "○";

    public string ScheduleToolTip => IsScheduled
        ? L.Get("Pass.Schedule.RemoveTooltip")
        : L.Get("Pass.Schedule.AddTooltip");

    public string ScheduleAutomationName => IsScheduled
        ? L.Get("Pass.Schedule.Remove")
        : L.Get("Pass.Schedule.Add");

    /// <summary>Hide schedule control once the pass has started (too late to mark).</summary>
    public bool ShowScheduleButton =>
        Highlight is not PassRowHighlight.InProgress and not PassRowHighlight.Recording;

    public bool CommunityStatusIsOn => CommunityStatusKind == SatelliteCommunityStatusKind.On;
    public bool CommunityStatusIsOff => CommunityStatusKind == SatelliteCommunityStatusKind.Off;
    public bool CommunityStatusIsTelem => CommunityStatusKind == SatelliteCommunityStatusKind.TelemetryOnly;
    public bool CommunityStatusIsUnknown => CommunityStatusKind == SatelliteCommunityStatusKind.Unknown;

    public PassInfo Source { get; init; } = null!;
    public string SatelliteName { get; init; } = "";
    public string NoradId { get; init; } = "";
    public string AosLocal { get; init; } = "";
    public string LosLocal { get; init; } = "";
    public string TcaLocal { get; init; } = "";
    public string TimeRangeLine { get; init; } = "";
    public string DetailsLine { get; init; } = "";
    public string DurationText { get; init; } = "";
    public string MaxElevationText { get; init; } = "";
    public DateTime AosUtc { get; init; }
    public DateTime LosUtc { get; init; }
    public DateTime MaxElevationUtc { get; init; }

    public void ClearCommunityStatus()
    {
        ShowCommunityStatusDot = false;
        IsCommunityStatusDotStale = false;
        CommunityStatusDotOpacity = 1.0;
        CommunityStatusKind = SatelliteCommunityStatusKind.Unknown;
        CommunityStatusToolTip = "";
        CommunityStatusAutomationName = "";
    }

    public void ApplyCommunityStatus(
        SatelliteCommunityStatusKind kind,
        bool stale,
        string toolTip,
        string automationName)
    {
        ShowCommunityStatusDot = true;
        CommunityStatusKind = kind;
        IsCommunityStatusDotStale = stale;
        CommunityStatusDotOpacity = stale ? 0.55 : 1.0;
        CommunityStatusToolTip = toolTip;
        CommunityStatusAutomationName = automationName;
    }

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
        var now = PassUtc.Normalize(utcNow);
        var aos = PassUtc.Normalize(AosUtc);
        var los = PassUtc.Normalize(LosUtc);

        PassRowHighlight next;
        if (now > los)
            next = PassRowHighlight.None;
        else if (now >= aos)
            next = PassRowHighlight.InProgress;
        else if (aos - now <= imminentWindow)
            next = PassRowHighlight.Imminent;
        else
            next = PassRowHighlight.None;

        if (Highlight != next)
            Highlight = next;

        switch (next)
        {
            case PassRowHighlight.Imminent:
            {
                var countdown = PassDisplayFormat.FormatCountdownToAos(now, aos);
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

    public static PassRowViewModel From(PassInfo p, ClockDisplayFormat clockFormat, bool useUtc)
    {
        var (aos, los) = PassDisplayFormat.FormatLocalTimes(p.AosUtc, p.LosUtc, useUtc: useUtc, clockFormat: clockFormat);

        return new()
        {
            Source = p,
            SatelliteName = p.SatelliteName,
            NoradId = p.NoradId,
            AosUtc = p.AosUtc,
            LosUtc = p.LosUtc,
            MaxElevationUtc = p.MaxElevationUtc,
            AosLocal = aos,
            LosLocal = los,
            TcaLocal = PassDisplayFormat.FormatLocal(p.MaxElevationUtc, clockFormat, useUtc: useUtc),
            TimeRangeLine = FormatPassTimeRangeLine(p.AosUtc, p.LosUtc, clockFormat, useUtc),
            DetailsLine = FormatPassDetailsLine(p.MaxElevationDeg, p.Duration),
            DurationText = FormatPassDurationText(p.Duration),
            MaxElevationText = FormatPassMaxElevationText(p.MaxElevationDeg)
        };
    }

    public PassRowViewModel WithTimeDisplay(ClockDisplayFormat clockFormat, bool useUtc)
    {
        var (aos, los) = PassDisplayFormat.FormatLocalTimes(AosUtc, LosUtc, useUtc: useUtc, clockFormat: clockFormat);
        return new()
        {
            Source = Source,
            SatelliteName = SatelliteName,
            NoradId = NoradId,
            AosUtc = AosUtc,
            LosUtc = LosUtc,
            MaxElevationUtc = MaxElevationUtc,
            AosLocal = aos,
            LosLocal = los,
            TcaLocal = PassDisplayFormat.FormatLocal(MaxElevationUtc, clockFormat, useUtc: useUtc),
            TimeRangeLine = FormatPassTimeRangeLine(AosUtc, LosUtc, clockFormat, useUtc),
            DetailsLine = DetailsLine,
            DurationText = DurationText,
            MaxElevationText = MaxElevationText,
            Highlight = Highlight,
            BadgeText = BadgeText,
            ShowBadge = ShowBadge,
            IsScheduled = IsScheduled
        };
    }

    private static string FormatPassTimeRangeLine(
        DateTime aosUtc,
        DateTime losUtc,
        ClockDisplayFormat clockFormat,
        bool useUtc)
    {
        var (aos, los) = PassDisplayFormat.FormatLocalTimes(aosUtc, losUtc, useUtc: useUtc, clockFormat: clockFormat);
        return L.Get("Pass.TimeRange", aos, los);
    }

    private static string FormatPassDetailsLine(double maxElevationDeg, TimeSpan duration)
    {
        return L.Get("Pass.Details", FormatPassDurationValue(duration), $"{maxElevationDeg:F0}°");
    }

    private static string FormatPassDurationText(TimeSpan duration)
        => L.Get("Pass.Duration", FormatPassDurationValue(duration));

    private static string FormatPassMaxElevationText(double maxElevationDeg)
        => L.Get("Pass.MaxElevation", $"{maxElevationDeg:F0}°");

    private static string FormatPassDurationValue(TimeSpan duration)
    {
        var minutes = duration.TotalSeconds < 30
            ? 0
            : (int)Math.Round(duration.TotalMinutes, MidpointRounding.AwayFromZero);
        return minutes == 1
            ? L.Get("Pass.DurationOneMinute")
            : L.Get("Pass.DurationMinutes", minutes);
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
        GridsText = HamsAtDisplayFormat.FormatGrids(UniqueGrids(alert.Grids).ToArray()),
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

        var lookup = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var check in gridChecks)
        {
            var key = check.Grid.Trim();
            if (key.Length == 0)
                continue;
            lookup[key] = check.IsWorked;
        }

        if (lookup.Count == 0)
            return "";

        var selected = UniqueGrids(alertGrids)
            .Where(g => lookup.TryGetValue(g, out var isWorked) && isWorked == worked)
            .ToArray();

        return selected.Length == 0 ? "" : HamsAtDisplayFormat.FormatGrids(selected);
    }

    internal static IEnumerable<string> UniqueGrids(IEnumerable<string> grids)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var grid in grids)
        {
            var trimmed = grid.Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed))
                continue;
            yield return trimmed;
        }
    }
}
