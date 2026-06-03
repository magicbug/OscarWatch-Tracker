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
    public int TleStaleHours { get; set; } = 6;
    public TleSourceSettings TleSource { get; set; } = new();
    public TleAutoUpdateMode TleAutoUpdate { get; set; } = TleAutoUpdateMode.OnStartup;
    /// <summary>On startup, check tle.oscarwatch.org for new transponder database entries.</summary>
    public bool TransponderDatabaseCheckOnStartup { get; set; } = true;
    public AppThemePreference Theme { get; set; } = AppThemePreference.System;
    /// <summary>Show ground-track direction arrows inside satellite footprints on the world map.</summary>
    public bool ShowFootprintMotionArrows { get; set; } = true;
    public bool ShowGrayline { get; set; } = true;
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
    public RotatorSettings Rotator { get; set; } = new();
    public RigSettings Rig { get; set; } = new();
    public CloudlogSettings Cloudlog { get; set; } = new();
    public PassRecordingSettings PassRecording { get; set; } = new();
}
