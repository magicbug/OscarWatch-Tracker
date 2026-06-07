using OscarWatch.Ft4.Core.Models;

namespace OscarWatch.Core.Models;

public sealed class AppSettings
{
    public GroundStation GroundStation { get; set; } = new();
    public string ActiveStationId { get; set; } = "";
    public List<StationProfile> SavedStations { get; set; } = [];
    public List<string> EnabledSatelliteNames { get; set; } = ["ISS", "SO-50", "AO-91"];
    public double MinimumElevationDeg { get; set; } = 5.0;
    public int PassPredictionHours { get; set; } = 48;
    public int PassFilterMinDurationMinutes { get; set; } = 2;
    /// <summary>Show pass planner and mutual pass times in UTC instead of local time.</summary>
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
    /// <summary>UI language code: <c>en</c> or <c>ja</c>. Applied on next startup.</summary>
    public string UiLanguage { get; set; } = "en";
    /// <summary>Show ground-track direction arrows inside satellite footprints on the world map.</summary>
    public bool ShowFootprintMotionArrows { get; set; } = true;
    /// <summary>Show day/night greyline shading on the world map.</summary>
    public bool ShowGreylineOverlay { get; set; }
    public VoiceAnnouncementSettings VoiceAnnouncements { get; set; } = new();
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
    /// <summary>Whether the sidebar hams.at roves expander is open.</summary>
    public bool HamsAtRovesExpanded { get; set; } = true;
    /// <summary>Height in pixels of the hams.at roves list when expanded.</summary>
    public int HamsAtRovesPanelHeightPx { get; set; } = 180;
    public HamsAtSettings HamsAt { get; set; } = new();
    public RotatorSettings Rotator { get; set; } = new();
    public RigSettings Rig { get; set; } = new();
    public CloudlogSettings Cloudlog { get; set; } = new();
    public PassRecordingSettings PassRecording { get; set; } = new();
    public Ft4Settings Ft4 { get; set; } = new();
}
