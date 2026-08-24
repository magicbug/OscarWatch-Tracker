namespace OscarWatch.Core.Models;

public sealed class AppSettings
{
    public GroundStation GroundStation { get; set; } = new();
    public string ActiveStationId { get; set; } = "";
    public List<StationProfile> SavedStations { get; set; } = [];
    public List<string> EnabledSatelliteNames { get; set; } = ["ISS", "SO-50", "AO-91"];
    /// <summary>Catalogue IDs (NORAD / Alpha-5) for enabled satellites. Preferred over names when present.</summary>
    public List<string> EnabledSatelliteNoradIds { get; set; } = ["25544", "27607", "43017"];
    public double MinimumElevationDeg { get; set; } = 5.0;
    public int PassPredictionHours { get; set; } = 48;
    public int PassFilterMinDurationMinutes { get; set; } = 2;
    /// <summary>When true, main UI times (status clock, sidebar passes, hams.at, sunlight) use UTC; otherwise local time.</summary>
    public bool DisplayTimesInUtc { get; set; }

    /// <summary>When true, pass planner and mutual pass dialogues use UTC; otherwise local. Independent of <see cref="DisplayTimesInUtc"/>.</summary>
    public bool PassPlannerUseUtcTime { get; set; }
    /// <summary>When true, UI times use 24-hour clock; otherwise 12-hour with AM/PM per culture.</summary>
    public bool Use24HourClock { get; set; }
    public int TleStaleHours { get; set; } = 6;
    public TleSourceSettings TleSource { get; set; } = new();
    public TleAutoUpdateMode TleAutoUpdate { get; set; } = TleAutoUpdateMode.OnStartup;
    /// <summary>On startup, check tle.oscarwatch.org for new transponder database entries.</summary>
    public bool TransponderDatabaseCheckOnStartup { get; set; } = true;
    /// <summary>On startup and every 24 hours while running, check GitHub for a newer release.</summary>
    public bool AppUpdateCheckEnabled { get; set; } = true;
    /// <summary>Release tag the user skipped; suppresses automatic update prompts only.</summary>
    public string DismissedAppUpdateTag { get; set; } = "";
    public AppThemePreference Theme { get; set; } = AppThemePreference.System;
    /// <summary>UI language code: <c>en-GB</c>, <c>ja</c>, <c>pt-BR</c>, <c>zh-CN</c>, or <c>es</c>. Legacy <c>en</c> is treated as <c>en-GB</c>.</summary>
    public string UiLanguage { get; set; } = "en-GB";
    /// <summary>Show ground-track direction arrows inside satellite footprints on the world map.</summary>
    public bool ShowFootprintMotionArrows { get; set; } = true;
    /// <summary>Show day/night greyline shading on the world map.</summary>
    public bool ShowGreylineOverlay { get; set; }
    /// <summary>Show a faded next-orbit ground track for the focused satellite on the world map.</summary>
    public bool ShowMultiTrackOverlay { get; set; } = true;
    /// <summary>How the world map chooses its centre longitude.</summary>
    public MapCentreMode MapCentreMode { get; set; } = MapCentreMode.Greenwich;
    /// <summary>Centre longitude when <see cref="MapCentreMode"/> is <see cref="MapCentreMode.Custom"/> (−180…180).</summary>
    public double MapCentreLongitudeDeg { get; set; }
    public VoiceAnnouncementSettings VoiceAnnouncements { get; set; } = new();
    /// <summary>Lead time and sound/alert options for scheduled upcoming passes.</summary>
    public PassScheduleSettings PassSchedule { get; set; } = new();
    /// <summary>Passes marked Scheduled on the upcoming list (NORAD id + AOS UTC).</summary>
    public List<ScheduledPassEntry> ScheduledPasses { get; set; } = [];
    public Dictionary<string, SatelliteFrequencySelection> FrequencySelections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public double FrequencyOverlayX { get; set; } = 12;
    public double FrequencyOverlayY { get; set; } = 12;
    public bool FrequencyOverlayCollapsed { get; set; }
    public string RemoteStationGridSquare { get; set; } = "";
    public double DxOverlayX { get; set; } = 12;
    public double DxOverlayY { get; set; } = 56;
    public bool DxOverlayCollapsed { get; set; } = true;
    /// <summary>Whether the sidebar sky plot expander is open.</summary>
    public bool SkyPlotExpanded { get; set; } = true;
    /// <summary>Whether the sidebar upcoming passes expander is open.</summary>
    public bool PassesExpanded { get; set; } = true;
    /// <summary>Whether the pass elevation timeline panel below the map is expanded.</summary>
    public bool IsTimelineExpanded { get; set; } = true;
    /// <summary>When true, the pass elevation timeline is in a floating window instead of the docked panel.</summary>
    public bool IsTimelineDetached { get; set; }
    /// <summary>Lookahead duration in minutes for the pass elevation timeline (30–360). Updated by mouse-wheel zoom on the chart.</summary>
    public int TimelineWindowMinutes { get; set; } = TimelineWindowLimits.DefaultMinutes;
    /// <summary>Height in pixels of the pass elevation timeline when expanded.</summary>
    public int TimelinePanelHeightPx { get; set; } = 110;
    /// <summary>Floating pass elevation timeline window width.</summary>
    public int? TimelineDetachedWindowWidth { get; set; }
    /// <summary>Floating pass elevation timeline window height.</summary>
    public int? TimelineDetachedWindowHeight { get; set; }
    /// <summary>Floating pass elevation timeline window X position.</summary>
    public int? TimelineDetachedWindowX { get; set; }
    /// <summary>Floating pass elevation timeline window Y position.</summary>
    public int? TimelineDetachedWindowY { get; set; }
    /// <summary>Whether the sidebar hams.at roves expander is open.</summary>
    public bool HamsAtRovesExpanded { get; set; } = true;
    /// <summary>Height in pixels of the hams.at roves list when expanded.</summary>
    public int HamsAtRovesPanelHeightPx { get; set; } = 180;
    public int? MainWindowWidth { get; set; }
    public int? MainWindowHeight { get; set; }
    public int? MainWindowX { get; set; }
    public int? MainWindowY { get; set; }
    public bool MainWindowMaximized { get; set; }
    public HamsAtSettings HamsAt { get; set; } = new();
    public SatelliteStatusSettings SatelliteStatus { get; set; } = new();
    public RotatorSettings Rotator { get; set; } = new();
    public GpsSettings Gps { get; set; } = new();
    public RigSettings Rig { get; set; } = new();
    public CloudlogSettings Cloudlog { get; set; } = new();
    public SatelliteLinkSettings SatelliteLink { get; set; } = new();
    public PassRecordingSettings PassRecording { get; set; } = new();
    public QsoLogbookSettings QsoLogbook { get; set; } = new();
    /// <summary>Transponder conflicts the user confirmed keeping locally instead of the published version.</summary>
    public List<TransponderConflictAcknowledgment> TransponderConflictAcknowledgments { get; set; } = [];
}
