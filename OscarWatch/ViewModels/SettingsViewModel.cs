using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Cloudlog;
using OscarWatch.Core.Display;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using OscarWatch.Core.SatelliteLink;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using OscarWatch.Rig;
using OscarWatch.Rotator;
using OscarWatch.Theme;

namespace OscarWatch.ViewModels;

public partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly ILocalizationService _l;
    private readonly ISpeechService _speech;
    private readonly IAlertSoundService _alertSound;
    private readonly IAudioRecordingService _recording;
    private readonly OscarWatch.Core.Recording.FfmpegLocator _ffmpegLocator;
    private readonly ICloudlogRadioSyncService _cloudlog;
    private readonly ICloudlogLookupService _cloudlogLookup;
    private readonly IHamsAtRovesService _hamsAtRoves;
    private readonly IGpsService _gps;
    private readonly ISatelliteLinkBroadcastService _satelliteLink;
    private readonly ISatelliteStatusReportService _satelliteStatus;
    private readonly FlexDiscoveryService _flexDiscovery = new();
    private readonly GroundStation _draft = new();
    private bool _isSynchronizing;
    private bool _disposed;
    private string _uiLanguageAtOpen = LocalizationCulture.DefaultLanguage;
    private int _recordingDeviceLoadGeneration;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _flexDiscovery.RadiosChanged -= OnFlexDiscoveryRadiosChanged;
        _flexDiscovery.Dispose();
        GC.SuppressFinalize(this);
    }

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private string _callsign = "";

    [ObservableProperty]
    private double _latitudeDeg;

    [ObservableProperty]
    private double _longitudeDeg;

    [ObservableProperty]
    private double _altitudeMeters;

    [ObservableProperty]
    private string _gridSquare = "";

    public ObservableCollection<HorizonMaskPoint> HorizonMaskPoints { get; } = [];

    [ObservableProperty]
    private double _minimumElevationDeg = 5;

    [ObservableProperty]
    private int _passPredictionHours = 48;

    [ObservableProperty]
    private AppThemePreference _themePreference = AppThemePreference.System;

    [ObservableProperty]
    private bool _showFootprintMotionArrows = true;

    [ObservableProperty]
    private bool _showGreylineOverlay;

    [ObservableProperty]
    private bool _showMultiTrackOverlay = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomMapCentreLongitudeEnabled))]
    private MapCentreMode _mapCentreMode = MapCentreMode.Greenwich;

    [ObservableProperty]
    private double _mapCentreLongitudeDeg;

    [ObservableProperty]
    private bool _use24HourClock;

    [ObservableProperty]
    private bool _displayTimesInUtc;

    public IReadOnlyList<string> ClockFormatLabels { get; }

    public IReadOnlyList<string> TimeDisplayLabels { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLanguageRestartNote))]
    private LanguageOption? _selectedLanguage;

    public bool ShowLanguageRestartNote =>
        !string.Equals(
            LocalizationCulture.NormalizeLanguageCode(SelectedLanguage?.Code),
            _uiLanguageAtOpen,
            StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    [ObservableProperty]
    private TleSourceOption? _tleSourceOption;

    [ObservableProperty]
    private string _tleCustomUrl = "";

    [ObservableProperty]
    private string _tleLocalFilePath = "";

    [ObservableProperty]
    private TleAutoUpdateOption? _tleAutoUpdateOption;

    [ObservableProperty]
    private bool _transponderDatabaseCheckOnStartup = true;

    [ObservableProperty]
    private bool _appUpdateCheckEnabled = true;

    public IReadOnlyList<TleSourceOption> TleSourceOptions { get; }

    public string TleCustomUrlWatermark { get; } = TleSourceResolver.CelestrakAmsatJsonExampleUrl;

    public bool ShowTleCustomUrl => TleSourceOption?.Mode == TleSourceMode.CustomUrl;

    public bool ShowTleLocalFile => TleSourceOption?.Mode == TleSourceMode.LocalFile;

    [ObservableProperty]
    private bool _voiceAnnouncementsEnabled;

    [ObservableProperty]
    private double _announceElevationDeg = -3;

    [ObservableProperty]
    private SpeechVoiceOption? _selectedSpeechVoice;

    [ObservableProperty]
    private int _passScheduleLeadMinutes = PassScheduleSettings.DefaultLeadMinutesBeforeAos;

    [ObservableProperty]
    private bool _passScheduleSoundEnabled = true;

    [ObservableProperty]
    private bool _passScheduleAlertEnabled = true;

    [ObservableProperty]
    private bool _passRecordingEnabled;

    [ObservableProperty]
    private double _recordingStartElevationDeg = 5;

    [ObservableProperty]
    private double _recordingStopElevationDeg = 3;

    [ObservableProperty]
    private string _recordingOutputFolder = "";

    [ObservableProperty]
    private RecordingDeviceOption? _selectedRecordingDevice;

    [ObservableProperty]
    private RecordingFormatOption? _selectedRecordingFormat;

    [ObservableProperty]
    private RecordingContainerOption? _selectedRecordingContainer;

    [ObservableProperty]
    private string _recordingFfmpegStatus = "";

    [ObservableProperty]
    private string _recordingTestStatus = "";

    public ObservableCollection<RecordingDeviceOption> RecordingDeviceOptions { get; } = [];

    [ObservableProperty]
    private bool _recordingAvailable = true;

    [ObservableProperty]
    private bool _recordingDevicesLoading;

    [ObservableProperty]
    private bool _recordingDevicesLoaded;

    public bool RecordingUnavailable => RecordingDevicesLoaded && !RecordingAvailable;

    public bool RecordingInputEnabled => RecordingAvailable && !RecordingDevicesLoading;

    public string RecordingUnavailableText =>
        _recording.UnavailableReason ?? _l.Get("Settings.Recording.Unavailable");

    public IReadOnlyList<RecordingFormatOption> RecordingFormatOptions { get; }

    public IReadOnlyList<RecordingContainerOption> RecordingContainerOptions { get; }

    [ObservableProperty]
    private bool _rotatorEnabled;

    [ObservableProperty]
    private string? _selectedComPort;

    [ObservableProperty]
    private string? _selectedElevationComPort;

    [ObservableProperty]
    private int _rotatorBaudRate = 4800;

    [ObservableProperty]
    private string _rotatorNetworkHost = RotatorSettings.DefaultNetworkHost;

    [ObservableProperty]
    private int _rotatorNetworkPort = RotatorSettings.DefaultNetworkPort;

    [ObservableProperty]
    private double _rotatorTrackStartElevationDeg = -3;

    [ObservableProperty]
    private double _rotatorParkAzimuthDeg;

    [ObservableProperty]
    private double _rotatorParkElevationDeg;

    [ObservableProperty]
    private bool _rotatorParkAfterPass = true;

    [ObservableProperty]
    private double _rotatorAzimuthOffsetDeg;

    [ObservableProperty]
    private double _rotatorElevationOffsetDeg;

    [ObservableProperty]
    private double _rotatorMovementThresholdDeg = 1.0;

    public ObservableCollection<string> AvailableComPorts { get; } = [];

    public bool ShowLinuxSerialPortHint => OperatingSystem.IsLinux();

    public bool SpeechAvailable { get; }

    public bool SpeechUnavailable => !SpeechAvailable;

    public string VoicePreviewText { get; } = SatelliteNamePhonetics.SampleAnnouncementText;

    public IReadOnlyList<SpeechVoiceOption> SpeechVoiceOptions { get; }

    public IReadOnlyList<ThemeOption> ThemeOptions { get; }

    public IReadOnlyList<MapCentreModeOption> MapCentreModeOptions { get; }

    [ObservableProperty]
    private ThemeOption? _selectedThemeOption;

    [ObservableProperty]
    private MapCentreModeOption? _selectedMapCentreModeOption;

    public IReadOnlyList<TleAutoUpdateOption> TleAutoUpdateOptions { get; }

    public int[] RotatorBaudRateOptions { get; } = [600, 1200, 2400, 4800, 9600, 19200];

    public int[] RigBaudRateOptions { get; } = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];

    public IReadOnlyList<RotatorTypeOption> RotatorTypeChoices { get; }

    [ObservableProperty]
    private RotatorTypeOption? _selectedRotatorTypeChoice;

    public IReadOnlyList<RotatorTransportOption> RotatorTransportChoices { get; }

    [ObservableProperty]
    private RotatorTransportOption? _selectedRotatorTransportChoice;

    public IReadOnlyList<RotatorAzimuthOption> AzimuthRangeChoices { get; }

    public IReadOnlyList<RotatorElevationOption> ElevationRangeChoices { get; }

    [ObservableProperty]
    private RotatorAzimuthOption? _selectedAzimuthRangeChoice;

    [ObservableProperty]
    private RotatorElevationOption? _selectedElevationRangeChoice;

    [ObservableProperty]
    private bool _rotatorSmartAzimuth450 = true;

    public bool IsRotatorSmartAzimuth450Enabled =>
        SelectedAzimuthRangeChoice?.Value == RotatorAzimuthRange.Deg450;

    public bool IsRotatorKeyholeSettingsVisible =>
        SelectedElevationRangeChoice?.Value == RotatorElevationRange.Deg180;

    public bool ShowRotatorTransportFields =>
        SelectedRotatorTypeChoice?.Value is not (RotatorType.UrcTcp or RotatorType.GreenHeronRt21);

    public bool ShowRotatorSerialFields =>
        ShowRotatorTransportFields
        && SelectedRotatorTransportChoice?.Value != RotatorTransportKind.Tcp;

    public bool ShowRotatorDualSerialFields =>
        SelectedRotatorTypeChoice?.Value == RotatorType.GreenHeronRt21;

    public bool ShowRotatorNetworkFields =>
        SelectedRotatorTypeChoice?.Value == RotatorType.UrcTcp
        || (ShowRotatorTransportFields
            && SelectedRotatorTransportChoice?.Value == RotatorTransportKind.Tcp);

    public bool ShowRotatorUrcNetworkNote =>
        SelectedRotatorTypeChoice?.Value == RotatorType.UrcTcp;

    public bool ShowRotatorTcpSerialNetworkNote =>
        ShowRotatorTransportFields
        && SelectedRotatorTransportChoice?.Value == RotatorTransportKind.Tcp;

    [ObservableProperty]
    private bool _rotatorKeyholeAvoidanceEnabled;

    [ObservableProperty]
    private double _rotatorSlewRateDegPerSec = 3.0;

    [ObservableProperty]
    private double _rotatorKeyholeThresholdDeg = 80.0;

    [ObservableProperty]
    private bool _rigEnabled;

    [ObservableProperty]
    private string? _selectedRigComPort;

    [ObservableProperty]
    private int _rigBaudRate = 19200;

    [ObservableProperty]
    private string _rigCivAddress = "60";

    [ObservableProperty]
    private int _rigDopplerThresholdFmHz = 350;

    [ObservableProperty]
    private int _rigDopplerThresholdLinearHz = 50;

    [ObservableProperty]
    private int _rigInteractiveDialSettleMs = InteractiveDialResumePolicy.DefaultSettleMs;

    [ObservableProperty]
    private int _rigInteractiveUplinkResumeMs = InteractiveDialResumePolicy.DefaultUplinkResumeMs;

    [ObservableProperty]
    private bool _rigDopplerAdaptiveThresholdEnabled = true;

    [ObservableProperty]
    private bool _rigDopplerPassLogEnabled;

    [ObservableProperty]
    private int _rigCatDelayMs = 50;

    [ObservableProperty]
    private bool _rigKenwoodHardwareRtsEnabled = true;

    [ObservableProperty]
    private bool _rigKenwoodTraceEnabled = true;

    [ObservableProperty]
    private bool _rigDopplerCatLeadEnabled = true;

    [ObservableProperty]
    private int _rigDopplerCatLeadMs = RigSettings.DefaultDopplerCatLeadMs;

    [ObservableProperty]
    private int _rigDopplerCatLeadGainPercent = RigSettings.DefaultDopplerCatLeadGainPercent;

    [ObservableProperty]
    private bool _rigCwKeepSidebandDownlink;

    [ObservableProperty]
    private bool _dualRadioEnabled;

    [ObservableProperty]
    private RigTypeOption? _selectedRigTypeChoice;

    [ObservableProperty]
    private RigTypeOption? _selectedDownlinkRigTypeChoice;

    [ObservableProperty]
    private RigTypeOption? _selectedUplinkRigTypeChoice;

    [ObservableProperty]
    private string? _selectedDownlinkComPort;

    [ObservableProperty]
    private string? _selectedUplinkComPort;

    [ObservableProperty]
    private int _downlinkBaudRate = RigSettings.Ft817818DefaultBaudRate;

    [ObservableProperty]
    private int _uplinkBaudRate = RigSettings.Ft817818DefaultBaudRate;

    [ObservableProperty]
    private RigRegionOption? _selectedDownlinkRegionChoice;

    [ObservableProperty]
    private RigRegionOption? _selectedUplinkRegionChoice;

    [ObservableProperty]
    private int _downlinkCatDelayMs = 50;

    [ObservableProperty]
    private int _uplinkCatDelayMs = 50;

    [ObservableProperty]
    private string _downlinkCivAddress = "";

    [ObservableProperty]
    private string _uplinkCivAddress = "";

    [ObservableProperty]
    private string _downlinkNetworkHost = RigEndpointSettings.SdrRigCtlDefaultHost;

    [ObservableProperty]
    private int _downlinkNetworkPort = RigEndpointSettings.SdrRigCtlDefaultPort;

    [ObservableProperty]
    private string _downlinkSdrTestStatus = "";

    [ObservableProperty]
    private string _rigNetworkHost = "";

    [ObservableProperty]
    private int _rigNetworkPort = RigSettings.FlexSmartSdrDefaultPort;

    [ObservableProperty]
    private string _rigFlexRadioSerial = "";

    [ObservableProperty]
    private string _rigFlexTestStatus = "";

    [ObservableProperty]
    private FlexAntennaPortOption? _selectedFlexVhfRxAnt;

    [ObservableProperty]
    private FlexAntennaPortOption? _selectedFlexUhfRxAnt;

    [ObservableProperty]
    private FlexAntennaPortOption? _selectedFlexVhfTxAnt;

    [ObservableProperty]
    private FlexAntennaPortOption? _selectedFlexUhfTxAnt;

    [ObservableProperty]
    private FlexDiscoveredRadioOption? _selectedFlexDiscoveredRadio;

    [ObservableProperty]
    private RigRegionOption? _selectedRigRegionChoice;

    [ObservableProperty]
    private bool _showComPortConflict;

    [ObservableProperty]
    private string _comPortConflictText = "";

    [ObservableProperty]
    private bool _showDualRadioIncomplete;

    [ObservableProperty]
    private string _dualRadioIncompleteText = "";

    [ObservableProperty]
    private bool _gpsEnabled;

    [ObservableProperty]
    private GpsConnectionOption? _selectedGpsConnectionChoice;

    [ObservableProperty]
    private string? _selectedGpsComPort;

    [ObservableProperty]
    private int _gpsBaudRate = GpsSettings.DefaultBaudRate;

    [ObservableProperty]
    private string _gpsdHost = GpsSettings.DefaultGpsdHost;

    [ObservableProperty]
    private int _gpsdPort = GpsSettings.DefaultGpsdPort;

    [ObservableProperty]
    private bool _gpsAutoUpdateStation;

    [ObservableProperty]
    private bool _gpsUseAltitude = true;

    [ObservableProperty]
    private bool _gpsUseTimeForTracking;

    [ObservableProperty]
    private int _gpsMinSatellites = 3;

    [ObservableProperty]
    private string _gpsStatusText = "";

    public int[] GpsBaudRateOptions { get; } = [4800, 9600, 38400, 57600, 115200];

    public IReadOnlyList<GpsConnectionOption> GpsConnectionChoices { get; }

    public bool ShowGpsSerialFields =>
        SelectedGpsConnectionChoice?.Value != GpsConnectionKind.Gpsd;

    public bool ShowGpsGpsdFields =>
        SelectedGpsConnectionChoice?.Value == GpsConnectionKind.Gpsd;

    [ObservableProperty]
    private bool _cloudlogEnabled;

    [ObservableProperty]
    private string _cloudlogBaseUrl = "";

    [ObservableProperty]
    private string _cloudlogApiKey = "";

    [ObservableProperty]
    private string _cloudlogRadioName = "OscarWatch";

    [ObservableProperty]
    private int _cloudlogMinUpdateIntervalMs = CloudlogRadioPublishPolicy.DefaultKeepaliveIntervalMs;

    [ObservableProperty]
    private string _cloudlogTestStatus = "";

    [ObservableProperty]
    private bool _cloudlogCheckRoveGrids = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloudlogLogbookPicker))]
    private CloudlogLogbookOption? _selectedCloudlogLogbook;

    public ObservableCollection<CloudlogLogbookOption> CloudlogLogbooks { get; } = [];

    public bool ShowCloudlogLogbookPicker => CloudlogLogbooks.Count > 0;

    [ObservableProperty]
    private bool _satelliteLinkEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SatelliteLinkUrlPreview))]
    private int _satelliteLinkPort = SatelliteLinkSettings.DefaultPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SatelliteLinkUrlPreview))]
    private bool _satelliteLinkAllowLanClients;

    [ObservableProperty]
    private bool _satelliteLinkOnlyWhenInRange;

    [ObservableProperty]
    private int _satelliteLinkUpdateIntervalMs = SatelliteLinkSettings.DefaultUpdateIntervalMs;

    [ObservableProperty]
    private string _satelliteLinkTestStatus = "";

    public string SatelliteLinkUrlPreview =>
        SatelliteLinkListenPrefixBuilder.FormatEndpointPreview(new SatelliteLinkSettings
        {
            Port = SatelliteLinkPort,
            AllowLanClients = SatelliteLinkAllowLanClients
        });

    [ObservableProperty]
    private bool _hamsAtEnabled;

    [ObservableProperty]
    private string _hamsAtApiKey = "";

    [ObservableProperty]
    private int _hamsAtRefreshIntervalMinutes = 10;

    [ObservableProperty]
    private string _hamsAtTestStatus = "";

    [ObservableProperty]
    private bool _satelliteStatusEnabled;

    [ObservableProperty]
    private bool _satelliteStatusAutoReportOnQso;

    [ObservableProperty]
    private string _satelliteStatusBaseUrl = "https://oscarwatch.org";

    [ObservableProperty]
    private string _satelliteStatusApiToken = "";

    [ObservableProperty]
    private string _satelliteStatusTestStatus = "";

    public IReadOnlyList<RigTypeOption> RigTypeChoices { get; }

    public IReadOnlyList<RigTypeOption> RigDualTypeChoices { get; }

    public IReadOnlyList<RigTypeOption> RigDualDownlinkTypeChoices { get; }

    public IReadOnlyList<RigTypeOption> RigDualUplinkTypeChoices { get; }

    public bool ShowDownlinkSerialFields =>
        DualRadioEnabled
        && SelectedDownlinkRigTypeChoice?.Value != RigType.SdrRigCtlTcp;

    public bool ShowDownlinkSdrFields =>
        DualRadioEnabled
        && SelectedDownlinkRigTypeChoice?.Value == RigType.SdrRigCtlTcp;

    public bool ShowUplinkSerialFields =>
        DualRadioEnabled
        && SelectedUplinkRigTypeChoice?.Value != RigType.Dummy;

    public bool ShowRigSingleConfig => !DualRadioEnabled;

    public bool ShowRigDualConfig => DualRadioEnabled;

    public bool ShowRigSerialFields =>
        !DualRadioEnabled
        && SelectedRigTypeChoice?.Value is not (RigType.FlexSmartSdr or RigType.Dummy);

    public bool ShowRigFlexFields =>
        !DualRadioEnabled
        && SelectedRigTypeChoice?.Value == RigType.FlexSmartSdr;

    public bool ShowRigCivAddress =>
        SelectedRigTypeChoice?.Value is RigType.IcomIc910 or RigType.IcomIc9100 or RigType.IcomIc9700
            or RigType.IcomIc821h;

    public bool ShowRigFt847CatHint =>
        SelectedRigTypeChoice?.Value == RigType.YaesuFt847;

    public bool ShowRigTs2000CatHint =>
        SelectedRigTypeChoice?.Value == RigType.KenwoodTs2000;

    public bool ShowRigFlexHint =>
        SelectedRigTypeChoice?.Value == RigType.FlexSmartSdr;

    public bool ShowRigFlexDuplexWarning =>
        ShowRigFlexFields
        && SelectedFlexDiscoveredRadio is { Radio: not null } opt
        && !string.IsNullOrWhiteSpace(opt.Radio.Model)
        && !FlexDiscoveryCodec.LooksDuplexCapable(opt.Radio.Model);

    public ObservableCollection<FlexDiscoveredRadioOption> DiscoveredFlexRadios { get; } = [];

    public IReadOnlyList<FlexAntennaPortOption> FlexAntennaPortChoices { get; }

    public bool ShowRigFt817CatHint =>
        DualRadioEnabled
        && (SelectedDownlinkRigTypeChoice?.Value is RigType.YaesuFt817 or RigType.YaesuFt818
            || SelectedUplinkRigTypeChoice?.Value is RigType.YaesuFt817 or RigType.YaesuFt818);

    public bool ShowDownlinkCivAddress =>
        DualRadioEnabled
        && (SelectedDownlinkRigTypeChoice?.Value is RigType.IcomIc705 or RigType.IcomIc7300 or RigType.IcomIc905
            || RigSettings.IsIc706SeriesEndpoint(SelectedDownlinkRigTypeChoice?.Value ?? RigType.None));

    public bool ShowUplinkCivAddress =>
        DualRadioEnabled
        && (SelectedUplinkRigTypeChoice?.Value is RigType.IcomIc705 or RigType.IcomIc7300 or RigType.IcomIc905
            || RigSettings.IsIc706SeriesEndpoint(SelectedUplinkRigTypeChoice?.Value ?? RigType.None));

    public bool ShowDownlinkIc705CivHint =>
        DualRadioEnabled && SelectedDownlinkRigTypeChoice?.Value == RigType.IcomIc705;

    public bool ShowDownlinkIc7300CivHint =>
        DualRadioEnabled && SelectedDownlinkRigTypeChoice?.Value == RigType.IcomIc7300;

    public bool ShowDownlinkIc905CivHint =>
        DualRadioEnabled && SelectedDownlinkRigTypeChoice?.Value == RigType.IcomIc905;

    public bool ShowDownlinkIc706CivHint =>
        DualRadioEnabled && SelectedDownlinkRigTypeChoice?.Value == RigType.IcomIc706;

    public bool ShowDownlinkIc706MkiiCivHint =>
        DualRadioEnabled && SelectedDownlinkRigTypeChoice?.Value == RigType.IcomIc706Mkii;

    public bool ShowDownlinkIc706MkiiGCivHint =>
        DualRadioEnabled && SelectedDownlinkRigTypeChoice?.Value == RigType.IcomIc706MkiiG;

    public bool ShowUplinkIc705CivHint =>
        DualRadioEnabled && SelectedUplinkRigTypeChoice?.Value == RigType.IcomIc705;

    public bool ShowUplinkIc7300CivHint =>
        DualRadioEnabled && SelectedUplinkRigTypeChoice?.Value == RigType.IcomIc7300;

    public bool ShowUplinkIc905CivHint =>
        DualRadioEnabled && SelectedUplinkRigTypeChoice?.Value == RigType.IcomIc905;

    public bool ShowUplinkIc706CivHint =>
        DualRadioEnabled && SelectedUplinkRigTypeChoice?.Value == RigType.IcomIc706;

    public bool ShowUplinkIc706MkiiCivHint =>
        DualRadioEnabled && SelectedUplinkRigTypeChoice?.Value == RigType.IcomIc706Mkii;

    public bool ShowUplinkIc706MkiiGCivHint =>
        DualRadioEnabled && SelectedUplinkRigTypeChoice?.Value == RigType.IcomIc706MkiiG;

    public bool ShowRigIc705CatHint =>
        DualRadioEnabled
        && (SelectedDownlinkRigTypeChoice?.Value == RigType.IcomIc705
            || SelectedUplinkRigTypeChoice?.Value == RigType.IcomIc705);

    public bool ShowRigIc7300CatHint =>
        DualRadioEnabled
        && (SelectedDownlinkRigTypeChoice?.Value == RigType.IcomIc7300
            || SelectedUplinkRigTypeChoice?.Value == RigType.IcomIc7300);

    public bool ShowRigIc905CatHint =>
        DualRadioEnabled
        && (SelectedDownlinkRigTypeChoice?.Value == RigType.IcomIc905
            || SelectedUplinkRigTypeChoice?.Value == RigType.IcomIc905);

    public bool ShowRigIc706CatHint =>
        DualRadioEnabled
        && (SelectedDownlinkRigTypeChoice?.Value == RigType.IcomIc706
            || SelectedUplinkRigTypeChoice?.Value == RigType.IcomIc706);

    public bool ShowRigIc706MkiiCatHint =>
        DualRadioEnabled
        && (SelectedDownlinkRigTypeChoice?.Value == RigType.IcomIc706Mkii
            || SelectedUplinkRigTypeChoice?.Value == RigType.IcomIc706Mkii);

    public bool ShowRigIc706MkiiGCatHint =>
        DualRadioEnabled
        && (SelectedDownlinkRigTypeChoice?.Value == RigType.IcomIc706MkiiG
            || SelectedUplinkRigTypeChoice?.Value == RigType.IcomIc706MkiiG);

    public bool ShowRigFt991CatHint =>
        DualRadioEnabled
        && (SelectedDownlinkRigTypeChoice?.Value is RigType.YaesuFt991 or RigType.YaesuFt991a
            || SelectedUplinkRigTypeChoice?.Value is RigType.YaesuFt991 or RigType.YaesuFt991a);

    public bool ShowRigFtx1CatHint =>
        DualRadioEnabled
        && (SelectedDownlinkRigTypeChoice?.Value == RigType.YaesuFtx1
            || SelectedUplinkRigTypeChoice?.Value == RigType.YaesuFtx1);

    public IReadOnlyList<RigRegionOption> RigRegionChoices { get; }

    public SettingsViewModel(
        ISettingsService settings,
        ILocalizationService localization,
        ISpeechService speech,
        IAlertSoundService alertSound,
        IAudioRecordingService recording,
        ICloudlogRadioSyncService cloudlog,
        ICloudlogLookupService cloudlogLookup,
        IHamsAtRovesService hamsAtRoves,
        IGpsService gps,
        ISatelliteLinkBroadcastService satelliteLink,
        ISatelliteStatusReportService satelliteStatus,
        OscarWatch.Core.Recording.FfmpegLocator? ffmpegLocator = null)
    {
        _l = localization;
        _cloudlogLookup = cloudlogLookup;
        _hamsAtRoves = hamsAtRoves;
        _gps = gps;
        _satelliteLink = satelliteLink;
        _satelliteStatus = satelliteStatus;
        _ffmpegLocator = ffmpegLocator ?? new OscarWatch.Core.Recording.FfmpegLocator();
        LanguageOptions =
        [
            new LanguageOption(LocalizationCulture.DefaultLanguage, _l.Get("Settings.Language.English")),
            new LanguageOption(LocalizationCulture.JapaneseLanguage, _l.Get("Settings.Language.Japanese")),
            new LanguageOption(LocalizationCulture.PortugueseBrazilLanguage, _l.Get("Settings.Language.PortugueseBrazil")),
            new LanguageOption(LocalizationCulture.SimplifiedChineseLanguage, _l.Get("Settings.Language.SimplifiedChinese")),
            new LanguageOption(LocalizationCulture.SpanishLanguage, _l.Get("Settings.Language.Spanish"))
        ];
        TleSourceOptions =
        [
            new(TleSourceMode.OscarWatch, _l.Get("Settings.Tle.Source.OscarWatch")),
            new(TleSourceMode.AmsatOrg, _l.Get("Settings.Tle.Source.Amsat")),
            new(TleSourceMode.CustomUrl, _l.Get("Settings.Tle.Source.CustomUrl")),
            new(TleSourceMode.LocalFile, _l.Get("Settings.Tle.Source.LocalFile"))
        ];
        TleAutoUpdateOptions =
        [
            new(TleAutoUpdateMode.Manual, _l.Get("Settings.Tle.Update.Manual")),
            new(TleAutoUpdateMode.OnStartup, _l.Get("Settings.Tle.Update.OnStartup")),
            new(TleAutoUpdateMode.EverySixHours, _l.Get("Settings.Tle.Update.EverySixHours"))
        ];
        ThemeOptions =
        [
            new(AppThemePreference.System, _l.Get("Settings.Theme.System")),
            new(AppThemePreference.Light, _l.Get("Settings.Theme.Light")),
            new(AppThemePreference.Dark, _l.Get("Settings.Theme.Dark"))
        ];
        MapCentreModeOptions =
        [
            new(MapCentreMode.Greenwich, _l.Get("Settings.MapCentre.Greenwich")),
            new(MapCentreMode.Station, _l.Get("Settings.MapCentre.Station")),
            new(MapCentreMode.Custom, _l.Get("Settings.MapCentre.Custom"))
        ];
        ClockFormatLabels =
        [
            _l.Get("Settings.ClockFormat.12Hour"),
            _l.Get("Settings.ClockFormat.24Hour")
        ];
        TimeDisplayLabels =
        [
            _l.Get("Pass.Time.Local"),
            _l.Get("Pass.Time.Utc")
        ];
        RotatorTypeChoices =
        [
            new(RotatorType.YaesuGs232, "Yaesu GS-232"),
            new(RotatorType.Spid, "SPID (Rot1Prog / Rot2Prog)"),
            new(RotatorType.EasyComm, "EasyComm"),
            new(RotatorType.Saebrt, "SAEBRTrack"),
            new(RotatorType.UrcTcp, "OZ9AAR URC (TCP)"),
            new(RotatorType.GreenHeronRt21, "Green Heron RT-21 Az-El")
        ];
        RotatorTransportChoices =
        [
            new(RotatorTransportKind.Serial, _l.Get("Settings.Rotator.Transport.Serial")),
            new(RotatorTransportKind.Tcp, _l.Get("Settings.Rotator.Transport.Tcp"))
        ];
        AzimuthRangeChoices =
        [
            new(RotatorAzimuthRange.Deg360, _l.Get("Settings.Rotator.AzimuthRange360")),
            new(RotatorAzimuthRange.Deg450, _l.Get("Settings.Rotator.AzimuthRange450"))
        ];
        ElevationRangeChoices =
        [
            new(RotatorElevationRange.Deg90, _l.Get("Settings.Rotator.ElevationRange90")),
            new(RotatorElevationRange.Deg180, _l.Get("Settings.Rotator.ElevationRange180"))
        ];
        RigTypeChoices =
        [
            new(RigType.IcomIc910, "ICOM IC-910"),
            new(RigType.IcomIc9100, "ICOM IC-9100"),
            new(RigType.IcomIc9700, "ICOM IC-9700"),
            new(RigType.IcomIc821h, "ICOM IC-821H"),
            new(RigType.YaesuFt847, "Yaesu FT-847"),
            new(RigType.KenwoodTs2000, "Kenwood TS-2000"),
            new(RigType.FlexSmartSdr, _l.Get("Settings.Radio.FlexSmartSdr")),
            new(RigType.Dummy, "Dummy Rig")
        ];
        RigDualTypeChoices =
        [
            new(RigType.YaesuFt817, "Yaesu FT-817"),
            new(RigType.YaesuFt818, "Yaesu FT-818"),
            new(RigType.YaesuFt991, "Yaesu FT-991"),
            new(RigType.YaesuFt991a, "Yaesu FT-991A"),
            new(RigType.YaesuFtx1, "Yaesu FTX-1"),
            new(RigType.IcomIc705, "ICOM IC-705"),
            new(RigType.IcomIc7300, "ICOM IC-7300"),
            new(RigType.IcomIc905, "ICOM IC-905"),
            new(RigType.IcomIc706, "ICOM IC-706"),
            new(RigType.IcomIc706Mkii, "ICOM IC-706MKII"),
            new(RigType.IcomIc706MkiiG, "ICOM IC-706MKIIG"),
            new(RigType.KenwoodThD74, "Kenwood TH-D74"),
            new(RigType.KenwoodThD75, "Kenwood TH-D75")
        ];
        RigDualDownlinkTypeChoices =
        [
            new(RigType.SdrRigCtlTcp, _l.Get("Settings.Radio.SdrRigCtl")),
            .. RigDualTypeChoices
        ];
        RigDualUplinkTypeChoices =
        [
            new(RigType.Dummy, _l.Get("Settings.Radio.DummyUplink")),
            .. RigDualTypeChoices
        ];
        RigRegionChoices =
        [
            new(RigRegion.EU, "EU"),
            new(RigRegion.USA, "USA")
        ];
        FlexAntennaPortChoices = BuildFlexAntennaPortChoices();
        GpsConnectionChoices =
        [
            new(GpsConnectionKind.Serial, _l.Get("Settings.Gps.Connection.Serial")),
            new(GpsConnectionKind.Gpsd, _l.Get("Settings.Gps.Connection.Gpsd"))
        ];
        RecordingFormatOptions =
        [
            new(RecordingFormatPreset.Mono44100, _l.Get("Settings.Recording.Format.Mono44100")),
            new(RecordingFormatPreset.Mono48000, _l.Get("Settings.Recording.Format.Mono48000")),
            new(RecordingFormatPreset.Stereo44100, _l.Get("Settings.Recording.Format.Stereo44100"))
        ];
        RecordingContainerOptions =
        [
            new(RecordingContainerFormat.Wav, _l.Get("Settings.Recording.FileFormat.Wav")),
            new(RecordingContainerFormat.Mp3, _l.Get("Settings.Recording.FileFormat.Mp3"))
        ];
        _settings = settings;
        _speech = speech;
        _alertSound = alertSound;
        _recording = recording;
        _cloudlog = cloudlog;
        SpeechAvailable = speech.IsAvailable;
        SpeechVoiceOptions = speech.GetAvailableVoices();
        CopyGroundStation(settings.Current.GroundStation, _draft);
        LoadFromDraft();
        SeedSavedComPorts();
        _flexDiscovery.RadiosChanged += OnFlexDiscoveryRadiosChanged;
        Dispatcher.UIThread.Post(MergeDiscoveredComPorts, DispatcherPriority.Background);
    }

    private void SeedSavedComPorts()
    {
        EnsureSavedPortListed(SelectedComPort);
        EnsureSavedPortListed(SelectedElevationComPort);
        EnsureSavedPortListed(SelectedRigComPort);
        EnsureSavedPortListed(SelectedDownlinkComPort);
        EnsureSavedPortListed(SelectedUplinkComPort);
        EnsureSavedPortListed(SelectedGpsComPort);
    }

    private void MergeDiscoveredComPorts()
    {
        foreach (var port in SerialPortDiscovery.GetAvailablePorts())
            EnsureSavedPortListed(port);
    }

    /// <summary>Called when the Recording tab is selected; probes PortAudio only then.</summary>
    public void OnRecordingTabSelected()
    {
        RefreshFfmpegStatus();
        if (!RecordingDevicesLoaded && !RecordingDevicesLoading)
            _ = RefreshRecordingDevicesAsync();
    }

    private void RefreshFfmpegStatus()
    {
        var probe = _ffmpegLocator.Probe(forceRefresh: true);
        RecordingFfmpegStatus = probe.IsAvailable
            ? _l.Get("Settings.Recording.FfmpegAvailable")
            : _l.Get("Settings.Recording.FfmpegUnavailable");
    }

    [RelayCommand]
    private async Task RefreshRecordingDevicesAsync()
    {
        var generation = Interlocked.Increment(ref _recordingDeviceLoadGeneration);
        RecordingDevicesLoading = true;
        OnPropertyChanged(nameof(RecordingInputEnabled));
        RecordingTestStatus = "";

        try
        {
            var previousId = SelectedRecordingDevice?.Id;
            var previousDisplayName = SelectedRecordingDevice?.DisplayName;

            var probeResult = await Task.Run(() =>
            {
                var available = _recording.TryInitialize();
                if (!available)
                {
                    return (
                        Available: false,
                        Devices: (IReadOnlyList<AudioInputDevice>)Array.Empty<AudioInputDevice>(),
                        Reason: _recording.UnavailableReason);
                }

                return (Available: true, Devices: _recording.GetInputDevices(), Reason: (string?)null);
            }).ConfigureAwait(true);

            if (generation != _recordingDeviceLoadGeneration)
                return;

            RecordingAvailable = probeResult.Available;
            RecordingDeviceOptions.Clear();
            foreach (var device in probeResult.Devices)
                RecordingDeviceOptions.Add(new RecordingDeviceOption(device.Id, device.DisplayName));

            SelectedRecordingDevice = FindRecordingDeviceOption(previousId, previousDisplayName);
            // Mark loaded only after options are populated so waiters never observe an empty list.
            RecordingDevicesLoaded = true;

            if (!probeResult.Available && !string.IsNullOrWhiteSpace(probeResult.Reason))
                RecordingTestStatus = probeResult.Reason;
        }
        catch (Exception ex)
        {
            if (generation != _recordingDeviceLoadGeneration)
                return;

            RecordingAvailable = false;
            RecordingDeviceOptions.Clear();
            RecordingTestStatus = ex.Message;
            RecordingDevicesLoaded = true;
        }
        finally
        {
            if (generation == _recordingDeviceLoadGeneration)
            {
                RecordingDevicesLoading = false;
                OnPropertyChanged(nameof(RecordingInputEnabled));
                OnPropertyChanged(nameof(RecordingUnavailable));
            }

            TestRecordingCommand.NotifyCanExecuteChanged();
        }
    }

    private void ApplySavedRecordingDevicePlaceholder(string? deviceId, string? deviceDisplayName)
    {
        RecordingDeviceOptions.Clear();
        if (string.IsNullOrWhiteSpace(deviceId) && string.IsNullOrWhiteSpace(deviceDisplayName))
        {
            SelectedRecordingDevice = null;
            return;
        }

        var id = string.IsNullOrWhiteSpace(deviceId) ? deviceDisplayName! : deviceId;
        var name = string.IsNullOrWhiteSpace(deviceDisplayName) ? deviceId! : deviceDisplayName;
        var placeholder = new RecordingDeviceOption(id, name);
        RecordingDeviceOptions.Add(placeholder);
        SelectedRecordingDevice = placeholder;
    }

    private RecordingDeviceOption? FindRecordingDeviceOption(string? deviceId, string? deviceDisplayName)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var byId = RecordingDeviceOptions.FirstOrDefault(d =>
                string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(deviceDisplayName))
        {
            return RecordingDeviceOptions.FirstOrDefault(d =>
                string.Equals(d.DisplayName, deviceDisplayName, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    [RelayCommand]
    private void RefreshComPorts()
    {
        // Snapshot first: clearing the shared ItemsSource can null every ComboBox's
        // TwoWay Selected*ComPort binding, which would wipe radio/rotator/GPS together.
        var rotatorPort = SelectedComPort;
        var rotatorElevationPort = SelectedElevationComPort;
        var rigPort = SelectedRigComPort;
        var downlinkPort = SelectedDownlinkComPort;
        var uplinkPort = SelectedUplinkComPort;
        var gpsPort = SelectedGpsComPort;

        AvailableComPorts.Clear();
        foreach (var port in SerialPortDiscovery.GetAvailablePorts(forceRefresh: true))
            AvailableComPorts.Add(port);

        EnsureSavedPortListed(rotatorPort);
        EnsureSavedPortListed(rotatorElevationPort);
        EnsureSavedPortListed(rigPort);
        EnsureSavedPortListed(downlinkPort);
        EnsureSavedPortListed(uplinkPort);
        EnsureSavedPortListed(gpsPort);

        SelectedComPort = rotatorPort;
        SelectedElevationComPort = rotatorElevationPort;
        SelectedRigComPort = rigPort;
        SelectedDownlinkComPort = downlinkPort;
        SelectedUplinkComPort = uplinkPort;
        SelectedGpsComPort = gpsPort;
    }

    private void EnsureSavedPortListed(string? port)
    {
        if (string.IsNullOrWhiteSpace(port))
            return;

        if (AvailableComPorts.Any(existing =>
                string.Equals(existing, port, StringComparison.OrdinalIgnoreCase)))
            return;

        AvailableComPorts.Add(port.Trim());
    }

    public async Task<bool> SaveAsync()
    {
        LanguageChangedOnLastSave = false;

        var rigDraft = BuildRigSettingsForConflictCheck();
        if (DualRadioConfigHelper.IsIncomplete(rigDraft))
        {
            throw new InvalidOperationException(
                DualRadioConfigLocalizer.Localize(DualRadioConfigHelper.IncompleteCode(rigDraft), _l));
        }

        var newLanguage = LocalizationCulture.NormalizeLanguageCode(
            SelectedLanguage?.Code ?? LocalizationCulture.DefaultLanguage);
        var languageChanged = !string.Equals(newLanguage, _uiLanguageAtOpen, StringComparison.OrdinalIgnoreCase);

        _settings.Current.GroundStation = new GroundStation
        {
            DisplayName = DisplayName,
            Callsign = MaidenheadLocator.NormalizeCallsign(Callsign),
            LatitudeDeg = LatitudeDeg,
            LongitudeDeg = LongitudeDeg,
            AltitudeMetersAsl = AltitudeMeters,
            GridSquare = NormalizeGridSquare(GridSquare),
            HorizonMask = BuildHorizonMaskFromEditor()
        };
        _settings.Current.MinimumElevationDeg = MinimumElevationDeg;
        _settings.Current.PassPredictionHours = PassPredictionHours;
        _settings.Current.Theme = ThemePreference;
        _settings.Current.UiLanguage = newLanguage;
        _settings.Current.ShowFootprintMotionArrows = ShowFootprintMotionArrows;
        _settings.Current.ShowGreylineOverlay = ShowGreylineOverlay;
        _settings.Current.ShowMultiTrackOverlay = ShowMultiTrackOverlay;
        _settings.Current.MapCentreMode = MapCentreMode;
        _settings.Current.MapCentreLongitudeDeg = MapCentreLongitudeDeg;
        _settings.Current.Use24HourClock = Use24HourClock;
        _settings.Current.DisplayTimesInUtc = DisplayTimesInUtc;
        _settings.Current.TleSource = new TleSourceSettings
        {
            Mode = TleSourceOption?.Mode ?? TleSourceMode.OscarWatch,
            CustomUrl = TleCustomUrl.Trim(),
            LocalFilePath = TleLocalFilePath.Trim()
        };
        if (TleAutoUpdateOption is not null)
            _settings.Current.TleAutoUpdate = TleAutoUpdateOption.Mode;
        _settings.Current.TransponderDatabaseCheckOnStartup = TransponderDatabaseCheckOnStartup;
        _settings.Current.AppUpdateCheckEnabled = AppUpdateCheckEnabled;
        _settings.Current.VoiceAnnouncements = new VoiceAnnouncementSettings
        {
            Enabled = VoiceAnnouncementsEnabled,
            AnnounceElevationDeg = AnnounceElevationDeg,
            VoiceName = SelectedSpeechVoice?.Id ?? ""
        };
        _settings.Current.PassSchedule = new PassScheduleSettings
        {
            LeadMinutesBeforeAos = PassScheduleSettings.ClampLeadMinutes(PassScheduleLeadMinutes),
            SoundEnabled = PassScheduleSoundEnabled,
            AlertEnabled = PassScheduleAlertEnabled
        };
        var stopElevation = Math.Min(RecordingStopElevationDeg, RecordingStartElevationDeg);
        _settings.Current.PassRecording = new PassRecordingSettings
        {
            Enabled = PassRecordingEnabled,
            DeviceId = SelectedRecordingDevice?.Id ?? "",
            DeviceDisplayName = SelectedRecordingDevice?.DisplayName ?? "",
            Format = SelectedRecordingFormat?.Value ?? RecordingFormatPreset.Mono44100,
            Container = SelectedRecordingContainer?.Value ?? RecordingContainerFormat.Wav,
            StartElevationDeg = RecordingStartElevationDeg,
            StopElevationDeg = stopElevation,
            OutputFolder = RecordingOutputFolder.Trim()
        };
        _settings.Current.Rotator = new RotatorSettings
        {
            Enabled = RotatorEnabled,
            Type = SelectedRotatorTypeChoice?.Value ?? RotatorType.YaesuGs232,
            TransportKind = SelectedRotatorTypeChoice?.Value is RotatorType.UrcTcp or RotatorType.GreenHeronRt21
                ? RotatorTransportKind.Serial
                : SelectedRotatorTransportChoice?.Value ?? RotatorTransportKind.Serial,
            Port = SelectedComPort ?? "",
            ElevationPort = SelectedElevationComPort ?? "",
            BaudRate = RotatorBaudRate,
            NetworkHost = string.IsNullOrWhiteSpace(RotatorNetworkHost)
                ? RotatorSettings.DefaultNetworkHost
                : RotatorNetworkHost.Trim(),
            NetworkPort = RotatorNetworkPort is > 0 and <= 65535
                ? RotatorNetworkPort
                : RotatorSettings.DefaultNetworkPort,
            AzimuthRange = SelectedAzimuthRangeChoice?.Value ?? RotatorAzimuthRange.Deg450,
            ElevationRange = SelectedElevationRangeChoice?.Value ?? RotatorElevationRange.Deg180,
            TrackStartElevationDeg = Math.Clamp(RotatorTrackStartElevationDeg, -90, 90),
            ParkAzimuthDeg = RotatorParkAzimuthDeg,
            ParkElevationDeg = RotatorParkElevationDeg,
            ParkAfterPass = RotatorParkAfterPass,
            AzimuthOffsetDeg = RotatorAzimuthOffsetDeg,
            ElevationOffsetDeg = RotatorElevationOffsetDeg,
            SmartAzimuth450 = RotatorSmartAzimuth450,
            KeyholeAvoidanceEnabled = RotatorKeyholeAvoidanceEnabled
                && SelectedElevationRangeChoice?.Value == RotatorElevationRange.Deg180,
            SlewRateDegPerSec = RotatorSlewRateDegPerSec,
            KeyholeThresholdDeg = RotatorKeyholeThresholdDeg,
            MovementThresholdDeg = RotatorMovementThresholdDeg
        };
        _settings.Current.Rig = new RigSettings
        {
            Enabled = RigEnabled,
            DualRadioEnabled = DualRadioEnabled,
            Downlink = new RigEndpointSettings
            {
                Type = SelectedDownlinkRigTypeChoice?.Value ?? RigType.YaesuFt817,
                Port = SelectedDownlinkComPort ?? "",
                BaudRate = DownlinkBaudRate,
                Region = SelectedDownlinkRegionChoice?.Value ?? RigRegion.EU,
                CatDelayMs = DownlinkCatDelayMs,
                CivAddress = DownlinkCivAddress.Trim(),
                NetworkHost = DownlinkNetworkHost.Trim(),
                NetworkPort = DownlinkNetworkPort
            },
            Uplink = new RigEndpointSettings
            {
                Type = SelectedUplinkRigTypeChoice?.Value ?? RigType.YaesuFt818,
                Port = SelectedUplinkComPort ?? "",
                BaudRate = UplinkBaudRate,
                Region = SelectedUplinkRegionChoice?.Value ?? RigRegion.EU,
                CatDelayMs = UplinkCatDelayMs,
                CivAddress = UplinkCivAddress.Trim()
            },
            Type = SelectedRigTypeChoice?.Value ?? RigType.None,
            Port = SelectedRigComPort ?? "",
            BaudRate = RigBaudRate,
            CivAddress = RigCivAddress.Trim(),
            NetworkHost = RigNetworkHost.Trim(),
            NetworkPort = RigNetworkPort is > 0 and <= 65535
                ? RigNetworkPort
                : RigSettings.FlexSmartSdrDefaultPort,
            FlexRadioSerial = RigFlexRadioSerial.Trim(),
            FlexVhfRxAnt = SelectedFlexVhfRxAnt?.Token ?? "",
            FlexUhfRxAnt = SelectedFlexUhfRxAnt?.Token ?? "",
            FlexVhfTxAnt = SelectedFlexVhfTxAnt?.Token ?? "",
            FlexUhfTxAnt = SelectedFlexUhfTxAnt?.Token ?? "",
            Region = SelectedRigRegionChoice?.Value ?? RigRegion.EU,
            DopplerThresholdFmHz = RigDopplerThresholdFmHz,
            DopplerThresholdLinearHz = RigDopplerThresholdLinearHz,
            InteractiveDialSettleMs = InteractiveDialResumePolicy.ResolveSettleMs(RigInteractiveDialSettleMs),
            InteractiveUplinkResumeMs = InteractiveDialResumePolicy.ResolveUplinkResumeMs(RigInteractiveUplinkResumeMs),
            DopplerAdaptiveThresholdEnabled = RigDopplerAdaptiveThresholdEnabled,
            DopplerPassLogEnabled = RigDopplerPassLogEnabled,
            CatDelayMs = RigCatDelayMs,
            KenwoodHardwareRtsEnabled = RigKenwoodHardwareRtsEnabled,
            KenwoodTraceEnabled = RigKenwoodTraceEnabled,
            DopplerCatLeadEnabled = RigDopplerCatLeadEnabled,
            DopplerCatLeadMs = RigDopplerCatLeadMs,
            DopplerCatLeadGainPercent = RigDopplerCatLeadGainPercent,
            CatUpdatesPaused = _settings.Current.Rig.CatUpdatesPaused,
            CwKeepSidebandDownlink = RigCwKeepSidebandDownlink
        };
        _settings.Current.Cloudlog = new CloudlogSettings
        {
            Enabled = CloudlogEnabled,
            BaseUrl = CloudlogUrlHelper.NormalizeBaseUrl(CloudlogBaseUrl),
            ApiKey = CloudlogApiKey.Trim(),
            RadioName = string.IsNullOrWhiteSpace(CloudlogRadioName) ? "OscarWatch" : CloudlogRadioName.Trim(),
            MinUpdateIntervalMs = CloudlogRadioPublishPolicy.NormalizeKeepaliveIntervalMs(CloudlogMinUpdateIntervalMs),
            LogbookPublicSlug = SelectedCloudlogLogbook?.PublicSlug?.Trim() ?? "",
            CheckRoveGrids = CloudlogCheckRoveGrids
        };
        _settings.Current.HamsAt = new HamsAtSettings
        {
            Enabled = HamsAtEnabled,
            ApiKey = HamsAtApiKey.Trim(),
            RefreshIntervalMinutes = Math.Clamp(HamsAtRefreshIntervalMinutes, 1, 120)
        };
        _settings.Current.SatelliteStatus = new SatelliteStatusSettings
        {
            Enabled = SatelliteStatusEnabled,
            BaseUrl = string.IsNullOrWhiteSpace(SatelliteStatusBaseUrl)
                ? "https://oscarwatch.org"
                : SatelliteStatusBaseUrl.Trim().TrimEnd('/'),
            ApiToken = SatelliteStatusApiToken.Trim(),
            AutoReportOnQso = SatelliteStatusAutoReportOnQso
        };
        _settings.Current.Gps = new GpsSettings
        {
            Enabled = GpsEnabled,
            ConnectionKind = SelectedGpsConnectionChoice?.Value ?? GpsConnectionKind.Serial,
            Port = SelectedGpsComPort ?? "",
            BaudRate = GpsBaudRate,
            GpsdHost = GpsdHost.Trim(),
            GpsdPort = Math.Clamp(GpsdPort, 1, 65535),
            AutoUpdateStation = GpsAutoUpdateStation,
            UseGpsAltitude = GpsUseAltitude,
            UseGpsTimeForTracking = GpsUseTimeForTracking,
            MinSatellites = Math.Clamp(GpsMinSatellites, 1, 20)
        };
        _gps.Update(_settings.Current.Gps);
        _settings.Current.SatelliteLink = new SatelliteLinkSettings
        {
            Enabled = SatelliteLinkEnabled,
            Port = SatelliteLinkSettings.NormalizePort(SatelliteLinkPort),
            AllowLanClients = SatelliteLinkAllowLanClients,
            OnlyWhenInRange = SatelliteLinkOnlyWhenInRange,
            UpdateIntervalMs = SatelliteLinkSettings.NormalizeUpdateIntervalMs(SatelliteLinkUpdateIntervalMs)
        };
        _cloudlog.ResetThrottle();
        _settings.SyncActiveStationFromGroundStation();
        AppThemeManager.Apply(ThemePreference);
        await _settings.SaveAsync().ConfigureAwait(true);
        if (languageChanged)
            _uiLanguageAtOpen = newLanguage;

        LanguageChangedOnLastSave = languageChanged;
        return languageChanged;
    }

    public bool LanguageChangedOnLastSave { get; private set; }

    [RelayCommand(CanExecute = nameof(CanTestVoiceAnnouncement))]
    private async Task TestVoiceAnnouncementAsync()
    {
        var voiceName = SelectedSpeechVoice?.Id;
        await _speech.SpeakAsync(
            VoicePreviewText,
            string.IsNullOrWhiteSpace(voiceName) ? null : voiceName).ConfigureAwait(true);
    }

    private bool CanTestVoiceAnnouncement() => SpeechAvailable;

    [RelayCommand]
    private void TestPassScheduleSound() => _alertSound.PlayAlert();

    private void LoadFromDraft()
    {
        _isSynchronizing = true;
        try
        {
            DisplayName = _draft.DisplayName;
            Callsign = MaidenheadLocator.NormalizeCallsign(_draft.Callsign);
            LatitudeDeg = _draft.LatitudeDeg;
            LongitudeDeg = _draft.LongitudeDeg;
            AltitudeMeters = _draft.AltitudeMetersAsl;
            GridSquare = NormalizeGridSquare(_draft.GridSquare);
            HorizonMaskPoints.Clear();
            foreach (var p in (_draft.HorizonMask ?? new HorizonMask()).Points)
                HorizonMaskPoints.Add(new HorizonMaskPoint(p.AzimuthDeg, p.ElevationDeg));
            MinimumElevationDeg = _settings.Current.MinimumElevationDeg;
            PassPredictionHours = _settings.Current.PassPredictionHours;
            ThemePreference = _settings.Current.Theme;
            SelectedThemeOption = ThemeOptions.FirstOrDefault(o => o.Value == ThemePreference)
                ?? ThemeOptions[0];
            ShowFootprintMotionArrows = _settings.Current.ShowFootprintMotionArrows;
            ShowGreylineOverlay = _settings.Current.ShowGreylineOverlay;
            ShowMultiTrackOverlay = _settings.Current.ShowMultiTrackOverlay;
            MapCentreMode = _settings.Current.MapCentreMode;
            MapCentreLongitudeDeg = _settings.Current.MapCentreLongitudeDeg;
            SelectedMapCentreModeOption = MapCentreModeOptions.FirstOrDefault(o => o.Value == MapCentreMode)
                ?? MapCentreModeOptions[0];
            Use24HourClock = _settings.Current.Use24HourClock;
            DisplayTimesInUtc = _settings.Current.DisplayTimesInUtc;
            var langCode = LocalizationCulture.NormalizeLanguageCode(_settings.Current.UiLanguage);
            _uiLanguageAtOpen = langCode;
            SelectedLanguage = LanguageOptions.FirstOrDefault(o =>
                string.Equals(o.Code, langCode, StringComparison.OrdinalIgnoreCase))
                ?? LanguageOptions[0];
            var tleSource = _settings.Current.TleSource ?? new TleSourceSettings();
            TleSourceOption = TleSourceOptions.FirstOrDefault(o => o.Mode == tleSource.Mode)
                ?? TleSourceOptions[0];
            TleCustomUrl = tleSource.CustomUrl;
            TleLocalFilePath = tleSource.LocalFilePath;
            TleAutoUpdateOption = TleAutoUpdateOptions.FirstOrDefault(o => o.Mode == _settings.Current.TleAutoUpdate)
                ?? TleAutoUpdateOptions[1];
            TransponderDatabaseCheckOnStartup = _settings.Current.TransponderDatabaseCheckOnStartup;
            AppUpdateCheckEnabled = _settings.Current.AppUpdateCheckEnabled;

            var voice = _settings.Current.VoiceAnnouncements ?? new VoiceAnnouncementSettings();
            VoiceAnnouncementsEnabled = voice.Enabled;
            AnnounceElevationDeg = voice.AnnounceElevationDeg;
            SelectedSpeechVoice = SpeechVoiceOptions.FirstOrDefault(v => v.Id == voice.VoiceName)
                ?? SpeechVoiceOptions.FirstOrDefault();

            var passSchedule = _settings.Current.PassSchedule ?? new PassScheduleSettings();
            PassScheduleLeadMinutes = PassScheduleSettings.ClampLeadMinutes(passSchedule.LeadMinutesBeforeAos);
            PassScheduleSoundEnabled = passSchedule.SoundEnabled;
            PassScheduleAlertEnabled = passSchedule.AlertEnabled;

            var recording = _settings.Current.PassRecording ?? new PassRecordingSettings();
            PassRecordingEnabled = recording.Enabled;
            RecordingStartElevationDeg = recording.StartElevationDeg;
            RecordingStopElevationDeg = recording.StopElevationDeg;
            RecordingOutputFolder = recording.OutputFolder;
            SelectedRecordingFormat = RecordingFormatOptions.FirstOrDefault(o => o.Value == recording.Format)
                ?? RecordingFormatOptions[0];
            SelectedRecordingContainer = RecordingContainerOptions.FirstOrDefault(o => o.Value == recording.Container)
                ?? RecordingContainerOptions[0];
            ApplySavedRecordingDevicePlaceholder(recording.DeviceId, recording.DeviceDisplayName);
            RecordingTestStatus = "";
            RecordingFfmpegStatus = "";

            var rotator = _settings.Current.Rotator ?? new RotatorSettings();
            RotatorEnabled = rotator.Enabled;
            SelectedRotatorTypeChoice = RotatorTypeChoices.FirstOrDefault(o => o.Value == rotator.Type)
                ?? RotatorTypeChoices[0];
            SelectedRotatorTransportChoice = RotatorTransportChoices.FirstOrDefault(o => o.Value == rotator.TransportKind)
                ?? RotatorTransportChoices[0];
            SelectedComPort = string.IsNullOrWhiteSpace(rotator.Port) ? null : rotator.Port;
            SelectedElevationComPort = string.IsNullOrWhiteSpace(rotator.ElevationPort)
                ? null
                : rotator.ElevationPort;
            RotatorBaudRate = rotator.BaudRate;
            RotatorNetworkHost = string.IsNullOrWhiteSpace(rotator.NetworkHost)
                ? RotatorSettings.DefaultNetworkHost
                : rotator.NetworkHost.Trim();
            RotatorNetworkPort = rotator.NetworkPort is > 0 and <= 65535
                ? rotator.NetworkPort
                : RotatorSettings.DefaultNetworkPort;
            SelectedAzimuthRangeChoice = AzimuthRangeChoices.FirstOrDefault(o => o.Value == rotator.AzimuthRange)
                ?? AzimuthRangeChoices[1];
            SelectedElevationRangeChoice = ElevationRangeChoices.FirstOrDefault(o => o.Value == rotator.ElevationRange)
                ?? ElevationRangeChoices[1];
            RotatorTrackStartElevationDeg = rotator.TrackStartElevationDeg;
            RotatorParkAzimuthDeg = rotator.ParkAzimuthDeg;
            RotatorParkElevationDeg = rotator.ParkElevationDeg;
            RotatorParkAfterPass = rotator.ParkAfterPass;
            RotatorAzimuthOffsetDeg = rotator.AzimuthOffsetDeg;
            RotatorElevationOffsetDeg = rotator.ElevationOffsetDeg;
            RotatorSmartAzimuth450 = rotator.SmartAzimuth450;
            RotatorKeyholeAvoidanceEnabled = rotator.KeyholeAvoidanceEnabled;
            RotatorSlewRateDegPerSec = rotator.SlewRateDegPerSec;
            RotatorKeyholeThresholdDeg = rotator.KeyholeThresholdDeg;
            RotatorMovementThresholdDeg = rotator.MovementThresholdDeg;

            var rig = _settings.Current.Rig ?? new RigSettings();
            rig.MigrateFt817818ToDualOnly();
            RigEnabled = rig.Enabled;
            DualRadioEnabled = rig.DualRadioEnabled;
            SelectedRigTypeChoice = RigTypeChoices.FirstOrDefault(o => o.Value == rig.Type)
                ?? RigTypeChoices[0];
            SelectedRigComPort = string.IsNullOrWhiteSpace(rig.Port) ? null : rig.Port;
            RigBaudRate = rig.BaudRate;
            RigCivAddress = rig.CivAddress;
            RigNetworkHost = rig.NetworkHost ?? "";
            RigNetworkPort = rig.NetworkPort > 0 ? rig.NetworkPort : RigSettings.FlexSmartSdrDefaultPort;
            RigFlexRadioSerial = rig.FlexRadioSerial ?? "";
            RigFlexTestStatus = "";
            SelectedFlexVhfRxAnt = SelectFlexAntennaPortChoice(rig.FlexVhfRxAnt);
            SelectedFlexUhfRxAnt = SelectFlexAntennaPortChoice(rig.FlexUhfRxAnt);
            SelectedFlexVhfTxAnt = SelectFlexAntennaPortChoice(rig.FlexVhfTxAnt);
            SelectedFlexUhfTxAnt = SelectFlexAntennaPortChoice(rig.FlexUhfTxAnt);
            SelectedRigRegionChoice = RigRegionChoices.FirstOrDefault(o => o.Value == rig.Region)
                ?? RigRegionChoices[0];
            RefreshDiscoveredFlexRadiosFromService();
            if (!string.IsNullOrWhiteSpace(rig.FlexRadioSerial))
            {
                SelectedFlexDiscoveredRadio = DiscoveredFlexRadios
                    .FirstOrDefault(o => string.Equals(o.Radio.Serial, rig.FlexRadioSerial, StringComparison.OrdinalIgnoreCase));
            }
            var down = rig.Downlink ?? new RigEndpointSettings();
            var up = rig.Uplink ?? new RigEndpointSettings();
            SelectedDownlinkRigTypeChoice = RigDualDownlinkTypeChoices.FirstOrDefault(o => o.Value == down.Type)
                ?? RigDualDownlinkTypeChoices[1];
            SelectedUplinkRigTypeChoice = RigDualUplinkTypeChoices.FirstOrDefault(o => o.Value == up.Type)
                ?? RigDualUplinkTypeChoices[1];
            SelectedDownlinkComPort = string.IsNullOrWhiteSpace(down.Port) ? null : down.Port;
            SelectedUplinkComPort = string.IsNullOrWhiteSpace(up.Port) ? null : up.Port;
            DownlinkNetworkHost = string.IsNullOrWhiteSpace(down.NetworkHost)
                ? RigEndpointSettings.SdrRigCtlDefaultHost
                : down.NetworkHost;
            DownlinkNetworkPort = down.NetworkPort > 0
                ? down.NetworkPort
                : RigEndpointSettings.SdrRigCtlDefaultPort;
            DownlinkSdrTestStatus = "";
            DownlinkBaudRate = down.BaudRate > 0 ? down.BaudRate : RigSettings.Ft817818DefaultBaudRate;
            UplinkBaudRate = up.BaudRate > 0 ? up.BaudRate : RigSettings.Ft817818DefaultBaudRate;
            SelectedDownlinkRegionChoice = RigRegionChoices.FirstOrDefault(o => o.Value == down.Region)
                ?? RigRegionChoices[0];
            SelectedUplinkRegionChoice = RigRegionChoices.FirstOrDefault(o => o.Value == up.Region)
                ?? RigRegionChoices[0];
            DownlinkCatDelayMs = down.CatDelayMs;
            UplinkCatDelayMs = up.CatDelayMs;
            DownlinkCivAddress = string.IsNullOrWhiteSpace(down.CivAddress)
                ? RigSettings.DefaultCivAddressFor(down.Type)
                : down.CivAddress;
            UplinkCivAddress = string.IsNullOrWhiteSpace(up.CivAddress)
                ? RigSettings.DefaultCivAddressFor(up.Type)
                : up.CivAddress;
            RigDopplerThresholdFmHz = rig.DopplerThresholdFmHz;
            RigDopplerThresholdLinearHz = rig.DopplerThresholdLinearHz;
            RigInteractiveDialSettleMs = InteractiveDialResumePolicy.ResolveSettleMs(rig.InteractiveDialSettleMs);
            RigInteractiveUplinkResumeMs = InteractiveDialResumePolicy.ResolveUplinkResumeMs(rig.InteractiveUplinkResumeMs);
            RigDopplerAdaptiveThresholdEnabled = rig.DopplerAdaptiveThresholdEnabled;
            RigDopplerPassLogEnabled = rig.DopplerPassLogEnabled;
            RigCatDelayMs = rig.CatDelayMs;
            RigKenwoodHardwareRtsEnabled = rig.KenwoodHardwareRtsEnabled;
            RigKenwoodTraceEnabled = rig.KenwoodTraceEnabled;
            RigDopplerCatLeadEnabled = rig.DopplerCatLeadEnabled;
            RigDopplerCatLeadMs = Math.Clamp(rig.DopplerCatLeadMs, 0, DopplerCatLead.UserLeadMsMax);
            RigDopplerCatLeadGainPercent = rig.DopplerCatLeadGainPercent is > 0 and <= 100
                ? rig.DopplerCatLeadGainPercent
                : RigSettings.DefaultDopplerCatLeadGainPercent;
            RigCwKeepSidebandDownlink = rig.CwKeepSidebandDownlink;
            var cloudlog = _settings.Current.Cloudlog ?? new CloudlogSettings();
            CloudlogEnabled = cloudlog.Enabled;
            CloudlogBaseUrl = cloudlog.BaseUrl;
            CloudlogApiKey = cloudlog.ApiKey;
            CloudlogRadioName = string.IsNullOrWhiteSpace(cloudlog.RadioName) ? "OscarWatch" : cloudlog.RadioName;
            CloudlogMinUpdateIntervalMs = CloudlogRadioPublishPolicy.MigrateKeepaliveIntervalMs(cloudlog.MinUpdateIntervalMs);
            CloudlogCheckRoveGrids = cloudlog.CheckRoveGrids;
            CloudlogTestStatus = "";
            CloudlogLogbooks.Clear();
            if (!string.IsNullOrWhiteSpace(cloudlog.LogbookPublicSlug))
            {
                var saved = new CloudlogLogbookOption(cloudlog.LogbookPublicSlug, cloudlog.LogbookPublicSlug, null);
                CloudlogLogbooks.Add(saved);
                SelectedCloudlogLogbook = saved;
            }
            else
            {
                SelectedCloudlogLogbook = null;
            }

            var satelliteLink = _settings.Current.SatelliteLink ?? new SatelliteLinkSettings();
            SatelliteLinkEnabled = satelliteLink.Enabled;
            SatelliteLinkPort = satelliteLink.Port > 0 ? satelliteLink.Port : SatelliteLinkSettings.DefaultPort;
            SatelliteLinkAllowLanClients = satelliteLink.AllowLanClients;
            SatelliteLinkOnlyWhenInRange = satelliteLink.OnlyWhenInRange;
            SatelliteLinkUpdateIntervalMs = satelliteLink.UpdateIntervalMs > 0
                ? satelliteLink.UpdateIntervalMs
                : SatelliteLinkSettings.DefaultUpdateIntervalMs;
            SatelliteLinkTestStatus = "";

            var hamsAt = _settings.Current.HamsAt ?? new HamsAtSettings();
            HamsAtEnabled = hamsAt.Enabled;
            HamsAtApiKey = hamsAt.ApiKey;
            HamsAtRefreshIntervalMinutes = hamsAt.RefreshIntervalMinutes <= 0 ? 10 : hamsAt.RefreshIntervalMinutes;
            HamsAtTestStatus = "";
            var satStatus = _settings.Current.SatelliteStatus ?? new SatelliteStatusSettings();
            SatelliteStatusEnabled = satStatus.Enabled;
            SatelliteStatusAutoReportOnQso = satStatus.AutoReportOnQso;
            SatelliteStatusBaseUrl = string.IsNullOrWhiteSpace(satStatus.BaseUrl)
                ? "https://oscarwatch.org"
                : satStatus.BaseUrl;
            SatelliteStatusApiToken = satStatus.ApiToken;
            SatelliteStatusTestStatus = "";
            var gps = _settings.Current.Gps ?? new GpsSettings();
            GpsEnabled = gps.Enabled;
            SelectedGpsConnectionChoice =
                GpsConnectionChoices.FirstOrDefault(c => c.Value == gps.ConnectionKind)
                ?? GpsConnectionChoices[0];
            SelectedGpsComPort = string.IsNullOrWhiteSpace(gps.Port) ? null : gps.Port;
            GpsBaudRate = gps.BaudRate > 0 ? gps.BaudRate : GpsSettings.DefaultBaudRate;
            GpsdHost = string.IsNullOrWhiteSpace(gps.GpsdHost) ? GpsSettings.DefaultGpsdHost : gps.GpsdHost.Trim();
            GpsdPort = gps.GpsdPort > 0 ? gps.GpsdPort : GpsSettings.DefaultGpsdPort;
            GpsAutoUpdateStation = gps.AutoUpdateStation;
            GpsUseAltitude = gps.UseGpsAltitude;
            GpsUseTimeForTracking = gps.UseGpsTimeForTracking;
            GpsMinSatellites = gps.MinSatellites > 0 ? gps.MinSatellites : 3;
            PushDraftGpsToService();
            RefreshComPortConflict();
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTestRecording))]
    private async Task TestRecordingAsync()
    {
        if (!RecordingDevicesLoaded)
            await RefreshRecordingDevicesAsync().ConfigureAwait(true);

        if (SelectedRecordingDevice is null)
        {
            RecordingTestStatus = _l.Get("Settings.Recording.SelectDeviceFirst");
            return;
        }

        var format = SelectedRecordingFormat?.Value ?? RecordingFormatPreset.Mono44100;
        var container = SelectedRecordingContainer?.Value ?? RecordingContainerFormat.Wav;
        var tempDir = Path.Combine(Path.GetTempPath(), "OscarWatch-recording-test");
        Directory.CreateDirectory(tempDir);
        var preferredPath = Path.Combine(
            tempDir,
            $"test-{DateTime.UtcNow:yyyyMMdd-HHmmss}{container.GetExtension()}");
        var capturePath = RecordingFileNameFormat.GetCaptureWavPath(preferredPath);

        try
        {
            TestRecordingCommand.NotifyCanExecuteChanged();
            RecordingTestStatus = _l.Get("Settings.Recording.TestInProgress");
            await _recording.StartAsync(
                AudioRecordingSessions.ManualTestNoradId,
                "Test",
                SelectedRecordingDevice.Id,
                format,
                capturePath,
                SelectedRecordingDevice.DisplayName,
                container).ConfigureAwait(true);
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            await _recording.StopAsync().ConfigureAwait(true);
            var savedPath = _recording.LastCompletedOutputPath ?? capturePath;
            if (container == RecordingContainerFormat.Mp3
                && !savedPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                RecordingTestStatus = _ffmpegLocator.Probe().IsAvailable
                    ? _l.Get("Settings.Recording.TestSavedMp3Failed", savedPath)
                    : _l.Get("Settings.Recording.TestSavedMp3Fallback", savedPath);
            }
            else
            {
                RecordingTestStatus = _l.Get("Settings.Recording.TestSaved", savedPath);
            }
        }
        catch (Exception ex)
        {
            RecordingTestStatus = ex.Message;
            if (_recording.IsRecording)
                await _recording.StopAsync().ConfigureAwait(true);
        }
        finally
        {
            TestRecordingCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnSelectedRecordingDeviceChanged(RecordingDeviceOption? value) =>
        TestRecordingCommand.NotifyCanExecuteChanged();

    private bool CanTestRecording() =>
        RecordingAvailable
        && RecordingDevicesLoaded
        && SelectedRecordingDevice is not null
        && !_recording.IsRecording;

    partial void OnRecordingAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordingUnavailable));
        OnPropertyChanged(nameof(RecordingInputEnabled));
        TestRecordingCommand.NotifyCanExecuteChanged();
    }

    partial void OnRecordingDevicesLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordingInputEnabled));
        TestRecordingCommand.NotifyCanExecuteChanged();
    }

    partial void OnRecordingDevicesLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordingUnavailable));
        TestRecordingCommand.NotifyCanExecuteChanged();
    }

    partial void OnTleSourceOptionChanged(TleSourceOption? value)
    {
        OnPropertyChanged(nameof(ShowTleCustomUrl));
        OnPropertyChanged(nameof(ShowTleLocalFile));
    }

    public async Task BrowseTleLocalFileAsync(Window owner)
    {
        var storage = TopLevel.GetTopLevel(owner)?.StorageProvider;
        if (storage is null)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _l.Get("Settings.Browse.TleFile"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("TLE files")
                {
                    Patterns = ["*.txt", "*.tle", "*.*"]
                }
            ]
        }).ConfigureAwait(true);

        if (files.Count > 0)
            TleLocalFilePath = files[0].Path.LocalPath;
    }

    public async Task BrowseRecordingOutputFolderAsync(Window owner)
    {
        var storage = TopLevel.GetTopLevel(owner)?.StorageProvider;
        if (storage is null)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _l.Get("Settings.Browse.RecordingFolder"),
            AllowMultiple = false
        }).ConfigureAwait(true);

        if (folders.Count > 0)
            RecordingOutputFolder = folders[0].Path.LocalPath;
    }

    [RelayCommand]
    private void OpenDopplerPassLogFolder() =>
        DopplerPassLogFileNameFormat.OpenLogDirectory(null);

    [RelayCommand]
    private void ApplyDownlinkSdrPlusPreset()
    {
        DownlinkNetworkHost = RigEndpointSettings.SdrRigCtlDefaultHost;
        DownlinkNetworkPort = RigEndpointSettings.SdrRigCtlDefaultPort;
    }

    [RelayCommand]
    private void ApplyDownlinkSdrConnectPreset()
    {
        DownlinkNetworkHost = RigEndpointSettings.SdrRigCtlDefaultHost;
        DownlinkNetworkPort = RigEndpointSettings.SdrConnectRigCtlPort;
    }

    [RelayCommand]
    public async Task TestDownlinkSdrConnectionAsync()
    {
        try
        {
            DownlinkSdrTestStatus = _l.Get("Settings.Radio.SdrTesting");
            if (string.IsNullOrWhiteSpace(DownlinkNetworkHost) || DownlinkNetworkPort is <= 0 or > 65535)
            {
                DownlinkSdrTestStatus = _l.Get("Settings.Radio.SdrEnterHostPort");
                return;
            }

            await Task.Run(() =>
            {
                using var driver = new RigCtlTcpDriver(DownlinkNetworkHost.Trim(), DownlinkNetworkPort);
                driver.Open();
                return driver.ReadFrequencyHz(RigVfo.Main);
            }).ConfigureAwait(true);

            DownlinkSdrTestStatus = _l.Get("Settings.Radio.SdrConnectionOk");
        }
        catch (Exception ex)
        {
            DownlinkSdrTestStatus = _l.Get("Settings.Radio.SdrConnectionFailed", ex.Message);
        }
    }

    [RelayCommand]
    private void RefreshFlexDiscovery()
    {
        try
        {
            _flexDiscovery.Start();
            RefreshDiscoveredFlexRadiosFromService();
            RigFlexTestStatus = DiscoveredFlexRadios.Count == 0
                ? _l.Get("Settings.Radio.FlexDiscoveryListening")
                : _l.Get("Settings.Radio.FlexDiscoveryFound", DiscoveredFlexRadios.Count);
        }
        catch (Exception ex)
        {
            RigFlexTestStatus = _l.Get("Settings.Radio.FlexDiscoveryFailed", ex.Message);
        }
    }

    [RelayCommand]
    public async Task TestFlexConnectionAsync()
    {
        try
        {
            RigFlexTestStatus = _l.Get("Settings.Radio.FlexTesting");
            if (string.IsNullOrWhiteSpace(RigNetworkHost) || RigNetworkPort is <= 0 or > 65535)
            {
                RigFlexTestStatus = _l.Get("Settings.Radio.FlexEnterHostPort");
                return;
            }

            await Task.Run(() =>
            {
                using var driver = new FlexRadioDriver(RigNetworkHost.Trim(), RigNetworkPort, RigCatDelayMs);
                driver.Open();
                return driver.IsConnected;
            }).ConfigureAwait(true);

            RigFlexTestStatus = _l.Get("Settings.Radio.FlexConnectionOk");
        }
        catch (Exception ex)
        {
            RigFlexTestStatus = _l.Get("Settings.Radio.FlexConnectionFailed", ex.Message);
        }
    }

    private void RefreshDiscoveredFlexRadiosFromService()
    {
        var previousSerial = SelectedFlexDiscoveredRadio?.Radio.Serial ?? RigFlexRadioSerial;
        DiscoveredFlexRadios.Clear();
        foreach (var radio in _flexDiscovery.Radios)
            DiscoveredFlexRadios.Add(new FlexDiscoveredRadioOption(radio, FlexDiscoveryCodec.FormatDisplayName(radio)));

        if (!string.IsNullOrWhiteSpace(previousSerial))
        {
            SelectedFlexDiscoveredRadio = DiscoveredFlexRadios
                .FirstOrDefault(o => string.Equals(o.Radio.Serial, previousSerial, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void OnFlexDiscoveryRadiosChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RefreshDiscoveredFlexRadiosFromService, DispatcherPriority.Background);

    public async Task TestSatelliteLinkAsync()
    {
        try
        {
            SatelliteLinkTestStatus = _l.Get("Settings.SatelliteLink.Testing");
            var settings = new SatelliteLinkSettings
            {
                Enabled = true,
                Port = SatelliteLinkSettings.NormalizePort(SatelliteLinkPort),
                AllowLanClients = SatelliteLinkAllowLanClients
            };

            var ok = await _satelliteLink.TestBindAsync(settings).ConfigureAwait(true);
            SatelliteLinkTestStatus = ok
                ? _l.Get("Settings.SatelliteLink.TestOk", settings.Port)
                : _l.Get("Settings.SatelliteLink.TestFailed", settings.Port, _satelliteLink.LastError ?? "");
        }
        catch (Exception ex)
        {
            SatelliteLinkTestStatus = ex.Message;
        }
    }

    public void OpenSatelliteLinkHelp() => Help.HelpLauncher.TryOpenHelp("satellite-link.html");

    public async Task TestHamsAtAsync()
    {
        try
        {
            HamsAtTestStatus = _l.Get("Settings.HamsAt.Testing");
            var settings = new HamsAtSettings
            {
                Enabled = true,
                ApiKey = HamsAtApiKey.Trim()
            };

            if (string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                HamsAtTestStatus = _l.Get("Settings.HamsAt.EnterApiKey");
                return;
            }

            var (ok, message) = await _hamsAtRoves.TestConnectionAsync(settings).ConfigureAwait(true);
            HamsAtTestStatus = ok
                ? _l.Get("Settings.HamsAt.ConnectionOk", message)
                : _l.Get("Settings.HamsAt.ConnectionFailed", message);
        }
        catch (Exception ex)
        {
            HamsAtTestStatus = ex.Message;
        }
    }

    public async Task TestSatelliteStatusAsync()
    {
        try
        {
            SatelliteStatusTestStatus = _l.Get("Settings.SatelliteStatus.Testing");
            var settings = new SatelliteStatusSettings
            {
                Enabled = true,
                BaseUrl = SatelliteStatusBaseUrl.Trim(),
                ApiToken = SatelliteStatusApiToken.Trim()
            };

            if (string.IsNullOrWhiteSpace(settings.ApiToken))
            {
                SatelliteStatusTestStatus = _l.Get("Settings.SatelliteStatus.EnterApiToken");
                return;
            }

            var result = await _satelliteStatus.TestTokenAsync(settings).ConfigureAwait(true);
            SatelliteStatusTestStatus = result.Ok
                ? _l.Get("Settings.SatelliteStatus.ConnectionOk", result.Message)
                : _l.Get("Settings.SatelliteStatus.ConnectionFailed", result.Message);
        }
        catch (Exception ex)
        {
            SatelliteStatusTestStatus = ex.Message;
        }
    }

    public async Task TestCloudlogAsync()
    {
        try
        {
            CloudlogTestStatus = _l.Get("Settings.Cloudlog.Testing");
            var settings = new CloudlogSettings
            {
                Enabled = true,
                BaseUrl = CloudlogUrlHelper.NormalizeBaseUrl(CloudlogBaseUrl),
                ApiKey = CloudlogApiKey.Trim(),
                RadioName = string.IsNullOrWhiteSpace(CloudlogRadioName) ? "OscarWatch" : CloudlogRadioName.Trim(),
                LogbookPublicSlug = SelectedCloudlogLogbook?.PublicSlug ?? ""
            };

            if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                CloudlogTestStatus = _l.Get("Settings.Cloudlog.EnterCredentials");
                return;
            }

            var savedSlug = SelectedCloudlogLogbook?.PublicSlug;
            var logbooksResult = await _cloudlogLookup.FetchLogbooksAsync(settings).ConfigureAwait(true);
            if (!logbooksResult.Ok)
            {
                CloudlogTestStatus = _l.Get("Settings.Cloudlog.LoadFailed", logbooksResult.ErrorMessage ?? "");
                return;
            }

            CloudlogLogbooks.Clear();
            foreach (var logbook in logbooksResult.Logbooks)
                CloudlogLogbooks.Add(CloudlogLogbookOption.From(logbook));

            SelectedCloudlogLogbook = CloudlogLogbooks.FirstOrDefault(l =>
                !string.IsNullOrWhiteSpace(savedSlug)
                && string.Equals(l.PublicSlug, savedSlug, StringComparison.OrdinalIgnoreCase))
                ?? CloudlogLogbooks.FirstOrDefault();

            OnPropertyChanged(nameof(ShowCloudlogLogbookPicker));

            var radioOk = await _cloudlog.TestConnectionAsync(settings).ConfigureAwait(true);
            var logbookMessage = _l.Get("Settings.Cloudlog.LogbooksLoaded", logbooksResult.Logbooks.Count);
            CloudlogTestStatus = radioOk
                ? _l.Get("Settings.Cloudlog.ConnectionOkWithLogbooks", logbookMessage)
                : _l.Get("Settings.Cloudlog.LogbooksOnly", logbookMessage, _cloudlog.LastError ?? _l.Get("Settings.Cloudlog.ConnectionFailed"));
        }
        catch (Exception ex)
        {
            CloudlogTestStatus = ex.Message;
        }
    }

    [RelayCommand]
    private void RefreshGpsStatus()
    {
        var status = _gps.GetStatus();
        if (!GpsEnabled)
            GpsStatusText = _l.Get("Settings.Gps.StatusDisabled");
        else if (SelectedGpsConnectionChoice?.Value == GpsConnectionKind.Gpsd
                 && string.IsNullOrWhiteSpace(GpsdHost))
            GpsStatusText = _l.Get("Settings.Gps.StatusNoGpsdHost");
        else if (ShowGpsSerialFields && string.IsNullOrWhiteSpace(SelectedGpsComPort))
            GpsStatusText = _l.Get("Settings.Gps.StatusNoPort");
        else if (!status.IsConnected)
            GpsStatusText = string.IsNullOrWhiteSpace(status.Detail)
                ? _l.Get("Settings.Gps.StatusNotConnected")
                : _l.Get("Settings.Gps.StatusNotConnectedDetail", status.Detail);
        else if (!status.HasFix)
            GpsStatusText = _l.Get("Settings.Gps.StatusNoFix");
        else
            GpsStatusText = _l.Get(
                "Settings.Gps.StatusFix",
                status.LatitudeDeg!.Value.ToString("F4"),
                status.LongitudeDeg!.Value.ToString("F4"),
                status.Satellites ?? 0);
        ApplyGpsFixNowCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanApplyGpsFix))]
    private void ApplyGpsFixNow()
    {
        var status = _gps.GetStatus();
        if (!status.HasFix || status.LatitudeDeg is null || status.LongitudeDeg is null)
            return;

        LatitudeDeg = status.LatitudeDeg.Value;
        LongitudeDeg = status.LongitudeDeg.Value;
        if (GpsUseAltitude && status.AltitudeMeters is { } alt)
            AltitudeMeters = alt;
    }

    private bool CanApplyGpsFix() =>
        GpsEnabled
        && _gps.GetStatus() is { HasFix: true, LatitudeDeg: not null, LongitudeDeg: not null };

    private void PushDraftGpsToService()
    {
        _gps.Update(BuildGpsSettingsDraft());
        RefreshGpsStatus();
    }

    private GpsSettings BuildGpsSettingsDraft() => new()
    {
        Enabled = GpsEnabled,
        ConnectionKind = SelectedGpsConnectionChoice?.Value ?? GpsConnectionKind.Serial,
        Port = SelectedGpsComPort ?? "",
        BaudRate = GpsBaudRate,
        GpsdHost = GpsdHost.Trim(),
        GpsdPort = Math.Clamp(GpsdPort, 1, 65535),
        AutoUpdateStation = GpsAutoUpdateStation,
        UseGpsAltitude = GpsUseAltitude,
        UseGpsTimeForTracking = GpsUseTimeForTracking,
        MinSatellites = Math.Clamp(GpsMinSatellites, 1, 20)
    };

    private void RefreshComPortConflict()
    {
        var rotator = new RotatorSettings
        {
            Enabled = RotatorEnabled,
            Type = SelectedRotatorTypeChoice?.Value ?? RotatorType.YaesuGs232,
            TransportKind = SelectedRotatorTypeChoice?.Value is RotatorType.UrcTcp or RotatorType.GreenHeronRt21
                ? RotatorTransportKind.Serial
                : SelectedRotatorTransportChoice?.Value ?? RotatorTransportKind.Serial,
            Port = SelectedComPort ?? "",
            ElevationPort = SelectedElevationComPort ?? ""
        };
        var rig = BuildRigSettingsForConflictCheck();
        var gps = BuildGpsSettingsDraft();
        ShowComPortConflict = SerialPortConflictHelper.TryDescribeConflict(rotator, rig, gps, out var message);
        ComPortConflictText = ComPortConflictLocalizer.Localize(message, _l);
        ShowDualRadioIncomplete = DualRadioConfigHelper.TryDescribeIncomplete(rig, out var incompleteCode);
        DualRadioIncompleteText = DualRadioConfigLocalizer.Localize(incompleteCode, _l);
    }

    partial void OnRotatorEnabledChanged(bool value) => RefreshComPortConflictIfReady();

    partial void OnSelectedRotatorTypeChoiceChanged(RotatorTypeOption? value)
    {
        NotifyRotatorConnectionVisibility();

        if (_isSynchronizing || value is null)
            return;

        if (value.Value == RotatorType.Spid)
            RotatorBaudRate = 600;
        else if (value.Value is RotatorType.EasyComm or RotatorType.Saebrt)
            RotatorBaudRate = 9600;
        else if (value.Value == RotatorType.GreenHeronRt21)
        {
            RotatorBaudRate = 4800;
            SelectedElevationRangeChoice = ElevationRangeChoices.FirstOrDefault(
                o => o.Value == RotatorElevationRange.Deg90) ?? ElevationRangeChoices[0];
        }

        RefreshComPortConflictIfReady();
    }

    partial void OnSelectedRotatorTransportChoiceChanged(RotatorTransportOption? value)
    {
        NotifyRotatorConnectionVisibility();
        if (!_isSynchronizing)
            RefreshComPortConflictIfReady();
    }

    private void NotifyRotatorConnectionVisibility()
    {
        OnPropertyChanged(nameof(ShowRotatorTransportFields));
        OnPropertyChanged(nameof(ShowRotatorSerialFields));
        OnPropertyChanged(nameof(ShowRotatorDualSerialFields));
        OnPropertyChanged(nameof(ShowRotatorNetworkFields));
        OnPropertyChanged(nameof(ShowRotatorUrcNetworkNote));
        OnPropertyChanged(nameof(ShowRotatorTcpSerialNetworkNote));
    }

    partial void OnSelectedAzimuthRangeChoiceChanged(RotatorAzimuthOption? value)
    {
        OnPropertyChanged(nameof(IsRotatorSmartAzimuth450Enabled));
    }

    partial void OnSelectedElevationRangeChoiceChanged(RotatorElevationOption? value)
    {
        OnPropertyChanged(nameof(IsRotatorKeyholeSettingsVisible));
        if (value?.Value != RotatorElevationRange.Deg180)
            RotatorKeyholeAvoidanceEnabled = false;
    }

    partial void OnRigEnabledChanged(bool value) => RefreshComPortConflictIfReady();
    partial void OnGpsEnabledChanged(bool value)
    {
        PushDraftGpsToServiceIfReady();
        RefreshComPortConflictIfReady();
    }

    partial void OnSelectedGpsConnectionChoiceChanged(GpsConnectionOption? value)
    {
        OnPropertyChanged(nameof(ShowGpsSerialFields));
        OnPropertyChanged(nameof(ShowGpsGpsdFields));
        PushDraftGpsToServiceIfReady();
        RefreshComPortConflictIfReady();
    }

    partial void OnSelectedGpsComPortChanged(string? value)
    {
        PushDraftGpsToServiceIfReady();
        RefreshComPortConflictIfReady();
    }

    partial void OnGpsBaudRateChanged(int value) => PushDraftGpsToServiceIfReady();

    partial void OnGpsdHostChanged(string value) => PushDraftGpsToServiceIfReady();

    partial void OnGpsdPortChanged(int value) => PushDraftGpsToServiceIfReady();
    partial void OnDualRadioEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowRigSingleConfig));
        OnPropertyChanged(nameof(ShowRigDualConfig));
        OnPropertyChanged(nameof(ShowRigFt817CatHint));
        OnPropertyChanged(nameof(ShowDownlinkCivAddress));
        OnPropertyChanged(nameof(ShowDownlinkSerialFields));
        OnPropertyChanged(nameof(ShowDownlinkSdrFields));
        OnPropertyChanged(nameof(ShowUplinkSerialFields));
        OnPropertyChanged(nameof(ShowUplinkCivAddress));
        OnPropertyChanged(nameof(ShowDownlinkIc705CivHint));
        OnPropertyChanged(nameof(ShowDownlinkIc7300CivHint));
        OnPropertyChanged(nameof(ShowDownlinkIc905CivHint));
        NotifyIc706SeriesVisibility();
        OnPropertyChanged(nameof(ShowUplinkIc705CivHint));
        OnPropertyChanged(nameof(ShowUplinkIc7300CivHint));
        OnPropertyChanged(nameof(ShowUplinkIc905CivHint));
        OnPropertyChanged(nameof(ShowRigIc705CatHint));
        OnPropertyChanged(nameof(ShowRigIc7300CatHint));
        OnPropertyChanged(nameof(ShowRigIc905CatHint));
        OnPropertyChanged(nameof(ShowRigFt991CatHint));
        OnPropertyChanged(nameof(ShowRigFtx1CatHint));
        RefreshComPortConflictIfReady();
    }

    partial void OnSelectedComPortChanged(string? value) => RefreshComPortConflictIfReady();
    partial void OnSelectedElevationComPortChanged(string? value) => RefreshComPortConflictIfReady();
    partial void OnSelectedRigComPortChanged(string? value) => RefreshComPortConflictIfReady();
    partial void OnSelectedDownlinkComPortChanged(string? value) => RefreshComPortConflictIfReady();
    partial void OnSelectedUplinkComPortChanged(string? value) => RefreshComPortConflictIfReady();

    private IReadOnlyList<FlexAntennaPortOption> BuildFlexAntennaPortChoices()
    {
        var leaveUnchanged = _l.Get("Settings.Radio.FlexAntennaLeaveUnchanged");
        return
        [
            new("", leaveUnchanged),
            new("ANT1", "ANT1"),
            new("ANT2", "ANT2"),
            new("RX_A", "RX A"),
            new("RX_B", "RX B"),
            new("XVTR", "XVTR")
        ];
    }

    private FlexAntennaPortOption SelectFlexAntennaPortChoice(string? token)
    {
        var normalized = FlexAntennaPortResolver.NormalizeToken(token) ?? "";
        return FlexAntennaPortChoices.FirstOrDefault(o => o.Token == normalized)
            ?? FlexAntennaPortChoices[0];
    }

    partial void OnSelectedRigTypeChoiceChanged(RigTypeOption? value)
    {
        OnPropertyChanged(nameof(ShowRigCivAddress));
        OnPropertyChanged(nameof(ShowRigSerialFields));
        OnPropertyChanged(nameof(ShowRigFlexFields));
        OnPropertyChanged(nameof(ShowRigFt847CatHint));
        OnPropertyChanged(nameof(ShowRigTs2000CatHint));
        OnPropertyChanged(nameof(ShowRigFlexHint));
        OnPropertyChanged(nameof(ShowRigFlexDuplexWarning));
        OnPropertyChanged(nameof(ShowRigFt817CatHint));
        RefreshComPortConflictIfReady();
        if (_isSynchronizing || value is null)
            return;

        if (value.Value is RigType.YaesuFt847 or RigType.KenwoodTs2000)
            RigBaudRate = 57600;

        if (value.Value == RigType.FlexSmartSdr)
        {
            if (RigNetworkPort is <= 0 or > 65535)
                RigNetworkPort = RigSettings.FlexSmartSdrDefaultPort;
            if (RigCatDelayMs < 50)
                RigCatDelayMs = 50;
            RefreshFlexDiscovery();
            return;
        }

        if (value.Value is not (RigType.IcomIc910 or RigType.IcomIc9100 or RigType.IcomIc9700 or RigType.IcomIc821h))
            return;

        var suggested = RigSettings.DefaultCivAddressFor(value.Value);
        if (string.IsNullOrWhiteSpace(RigCivAddress)
            || RigCivAddress is "60" or "7C" or "A2" or "4C")
            RigCivAddress = suggested;
    }

    partial void OnSelectedFlexDiscoveredRadioChanged(FlexDiscoveredRadioOption? value)
    {
        OnPropertyChanged(nameof(ShowRigFlexDuplexWarning));
        if (_isSynchronizing || value is null)
            return;

        RigNetworkHost = value.Radio.IpAddress;
        RigNetworkPort = value.Radio.Port > 0 ? value.Radio.Port : RigSettings.FlexSmartSdrDefaultPort;
        RigFlexRadioSerial = value.Radio.Serial ?? "";
    }

    partial void OnSelectedDownlinkRigTypeChoiceChanged(RigTypeOption? value)
    {
        OnPropertyChanged(nameof(ShowRigFt817CatHint));
        OnPropertyChanged(nameof(ShowDownlinkCivAddress));
        OnPropertyChanged(nameof(ShowDownlinkSerialFields));
        OnPropertyChanged(nameof(ShowDownlinkSdrFields));
        OnPropertyChanged(nameof(ShowDownlinkIc705CivHint));
        OnPropertyChanged(nameof(ShowDownlinkIc7300CivHint));
        OnPropertyChanged(nameof(ShowDownlinkIc905CivHint));
        NotifyIc706SeriesVisibility();
        OnPropertyChanged(nameof(ShowRigIc705CatHint));
        OnPropertyChanged(nameof(ShowRigIc7300CatHint));
        OnPropertyChanged(nameof(ShowRigIc905CatHint));
        OnPropertyChanged(nameof(ShowRigFt991CatHint));
        OnPropertyChanged(nameof(ShowRigFtx1CatHint));
        if (_isSynchronizing || value is null)
            return;

        if (value.Value is RigType.YaesuFt817 or RigType.YaesuFt818)
            DownlinkBaudRate = RigSettings.Ft817818DefaultBaudRate;

        if (value.Value is RigType.YaesuFt991 or RigType.YaesuFt991a)
            DownlinkBaudRate = RigSettings.Ft991DefaultBaudRate;

        if (value.Value == RigType.YaesuFtx1)
            DownlinkBaudRate = RigSettings.Ftx1DefaultBaudRate;
        if (RigSettings.IsKenwoodThD7xEndpoint(value.Value))
            DownlinkBaudRate = RigSettings.KenwoodThD7xDefaultBaudRate;

        if (value.Value == RigType.IcomIc705)
        {
            DownlinkBaudRate = RigSettings.Ic705DefaultBaudRate;
            if (ShouldSuggestCivAddress(DownlinkCivAddress))
                DownlinkCivAddress = RigSettings.DefaultCivAddressFor(RigType.IcomIc705);
        }

        if (value.Value == RigType.IcomIc7300)
        {
            DownlinkBaudRate = RigSettings.Ic7300DefaultBaudRate;
            if (ShouldSuggestCivAddress(DownlinkCivAddress))
                DownlinkCivAddress = RigSettings.DefaultCivAddressFor(RigType.IcomIc7300);
        }

        if (value.Value == RigType.IcomIc905)
        {
            DownlinkBaudRate = RigSettings.Ic905DefaultBaudRate;
            if (ShouldSuggestCivAddress(DownlinkCivAddress))
                DownlinkCivAddress = RigSettings.DefaultCivAddressFor(RigType.IcomIc905);
        }

        if (RigSettings.IsIc706SeriesEndpoint(value.Value))
            ApplyIc706SeriesDefaults(value.Value, v => DownlinkBaudRate = v, v => DownlinkCivAddress = v, DownlinkCivAddress);

        if (value.Value == RigType.SdrRigCtlTcp && DownlinkCatDelayMs < 100)
            DownlinkCatDelayMs = 100;
    }

    partial void OnSelectedUplinkRigTypeChoiceChanged(RigTypeOption? value)
    {
        if (!_isSynchronizing && value?.Value == RigType.SdrRigCtlTcp)
        {
            SelectedUplinkRigTypeChoice = RigDualUplinkTypeChoices.First(o => o.Value == RigType.YaesuFt818);
            return;
        }

        OnPropertyChanged(nameof(ShowRigFt817CatHint));
        OnPropertyChanged(nameof(ShowUplinkCivAddress));
        OnPropertyChanged(nameof(ShowUplinkSerialFields));
        OnPropertyChanged(nameof(ShowUplinkIc705CivHint));
        OnPropertyChanged(nameof(ShowUplinkIc7300CivHint));
        OnPropertyChanged(nameof(ShowUplinkIc905CivHint));
        NotifyIc706SeriesVisibility();
        OnPropertyChanged(nameof(ShowRigIc705CatHint));
        OnPropertyChanged(nameof(ShowRigIc7300CatHint));
        OnPropertyChanged(nameof(ShowRigIc905CatHint));
        OnPropertyChanged(nameof(ShowRigFt991CatHint));
        OnPropertyChanged(nameof(ShowRigFtx1CatHint));
        if (_isSynchronizing || value is null)
            return;

        if (value.Value is RigType.YaesuFt817 or RigType.YaesuFt818)
            UplinkBaudRate = RigSettings.Ft817818DefaultBaudRate;

        if (value.Value is RigType.YaesuFt991 or RigType.YaesuFt991a)
            UplinkBaudRate = RigSettings.Ft991DefaultBaudRate;

        if (value.Value == RigType.YaesuFtx1)
            UplinkBaudRate = RigSettings.Ftx1DefaultBaudRate;
        if (RigSettings.IsKenwoodThD7xEndpoint(value.Value))
            UplinkBaudRate = RigSettings.KenwoodThD7xDefaultBaudRate;

        if (value.Value == RigType.IcomIc705)
        {
            UplinkBaudRate = RigSettings.Ic705DefaultBaudRate;
            if (ShouldSuggestCivAddress(UplinkCivAddress))
                UplinkCivAddress = RigSettings.DefaultCivAddressFor(RigType.IcomIc705);
        }

        if (value.Value == RigType.IcomIc7300)
        {
            UplinkBaudRate = RigSettings.Ic7300DefaultBaudRate;
            if (ShouldSuggestCivAddress(UplinkCivAddress))
                UplinkCivAddress = RigSettings.DefaultCivAddressFor(RigType.IcomIc7300);
        }

        if (value.Value == RigType.IcomIc905)
        {
            UplinkBaudRate = RigSettings.Ic905DefaultBaudRate;
            if (ShouldSuggestCivAddress(UplinkCivAddress))
                UplinkCivAddress = RigSettings.DefaultCivAddressFor(RigType.IcomIc905);
        }

        if (RigSettings.IsIc706SeriesEndpoint(value.Value))
            ApplyIc706SeriesDefaults(value.Value, v => UplinkBaudRate = v, v => UplinkCivAddress = v, UplinkCivAddress);
    }

    private static bool ShouldSuggestCivAddress(string? address) =>
        string.IsNullOrWhiteSpace(address)
        || address is "60" or "7C" or "A2" or "A4" or "94" or "AC" or "48" or "4C" or "58";

    private void NotifyIc706SeriesVisibility()
    {
        OnPropertyChanged(nameof(ShowDownlinkIc706CivHint));
        OnPropertyChanged(nameof(ShowDownlinkIc706MkiiCivHint));
        OnPropertyChanged(nameof(ShowDownlinkIc706MkiiGCivHint));
        OnPropertyChanged(nameof(ShowUplinkIc706CivHint));
        OnPropertyChanged(nameof(ShowUplinkIc706MkiiCivHint));
        OnPropertyChanged(nameof(ShowUplinkIc706MkiiGCivHint));
        OnPropertyChanged(nameof(ShowRigIc706CatHint));
        OnPropertyChanged(nameof(ShowRigIc706MkiiCatHint));
        OnPropertyChanged(nameof(ShowRigIc706MkiiGCatHint));
    }

    private static void ApplyIc706SeriesDefaults(
        RigType type,
        Action<int> setBaudRate,
        Action<string> setCivAddress,
        string? currentCivAddress)
    {
        setBaudRate(RigSettings.Ic706SeriesDefaultBaudRate);
        if (ShouldSuggestCivAddress(currentCivAddress))
            setCivAddress(RigSettings.DefaultCivAddressFor(type));
    }

    private RigSettings BuildRigSettingsForConflictCheck() => new()
    {
        Enabled = RigEnabled,
        DualRadioEnabled = DualRadioEnabled,
        Type = SelectedRigTypeChoice?.Value ?? RigType.None,
        Port = SelectedRigComPort ?? "",
        Downlink = new RigEndpointSettings
        {
            Type = SelectedDownlinkRigTypeChoice?.Value ?? RigType.YaesuFt817,
            Port = SelectedDownlinkComPort ?? "",
            NetworkHost = DownlinkNetworkHost.Trim(),
            NetworkPort = DownlinkNetworkPort
        },
        Uplink = new RigEndpointSettings
        {
            Type = SelectedUplinkRigTypeChoice?.Value ?? RigType.YaesuFt818,
            Port = SelectedUplinkComPort ?? ""
        }
    };

    private void RefreshComPortConflictIfReady()
    {
        if (_isSynchronizing)
            return;
        RefreshComPortConflict();
    }

    private void PushDraftGpsToServiceIfReady()
    {
        if (_isSynchronizing)
            return;
        PushDraftGpsToService();
    }

    private static void CopyGroundStation(GroundStation source, GroundStation target)
    {
        target.DisplayName = source.DisplayName;
        target.Callsign = MaidenheadLocator.NormalizeCallsign(source.Callsign);
        target.LatitudeDeg = source.LatitudeDeg;
        target.LongitudeDeg = source.LongitudeDeg;
        target.AltitudeMetersAsl = source.AltitudeMetersAsl;
        target.GridSquare = NormalizeGridSquare(source.GridSquare);
        target.HorizonMask = source.HorizonMask?.Clone() ?? new HorizonMask();
    }

    private HorizonMask BuildHorizonMaskFromEditor()
    {
        var mask = new HorizonMask();
        foreach (var p in HorizonMaskPoints)
            mask.Points.Add(new HorizonMaskPoint(p.AzimuthDeg, p.ElevationDeg));
        mask.Normalize();
        return mask;
    }

    private static string NormalizeGridSquare(string? value) =>
        MaidenheadLocator.NormalizeGrids(value);

    private void SyncGridFromDraftLatLon()
    {
        _draft.LatitudeDeg = LatitudeDeg;
        _draft.LongitudeDeg = LongitudeDeg;
        _draft.GridSquare = NormalizeGridSquare(MaidenheadGrid.FromLatLon(LatitudeDeg, LongitudeDeg));
        var updated = _draft.GridSquare;
        if (!string.Equals(GridSquare, updated, StringComparison.Ordinal))
            GridSquare = updated;
    }

    partial void OnDisplayNameChanged(string value)
    {
        if (_isSynchronizing)
            return;

        _draft.DisplayName = value;
    }

    partial void OnCallsignChanged(string value)
    {
        if (_isSynchronizing)
            return;

        var normalized = MaidenheadLocator.NormalizeCallsign(value);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            _isSynchronizing = true;
            try
            {
                Callsign = normalized;
            }
            finally
            {
                _isSynchronizing = false;
            }
            return;
        }

        _draft.Callsign = normalized;
    }

    partial void OnLatitudeDegChanged(double value)
    {
        if (_isSynchronizing)
            return;

        _isSynchronizing = true;
        try
        {
            SyncGridFromDraftLatLon();
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    partial void OnLongitudeDegChanged(double value)
    {
        if (_isSynchronizing)
            return;

        _isSynchronizing = true;
        try
        {
            SyncGridFromDraftLatLon();
        }
        finally
        {
            _isSynchronizing = false;
        }

        if (MapCentreMode == MapCentreMode.Station)
            PushMapCentrePreview();
    }

    partial void OnSelectedThemeOptionChanged(ThemeOption? value)
    {
        if (_isSynchronizing || value is null)
            return;

        if (ThemePreference != value.Value)
            ThemePreference = value.Value;
    }

    partial void OnThemePreferenceChanged(AppThemePreference value)
    {
        if (_isSynchronizing)
            return;

        AppThemeManager.Apply(value);
        var option = ThemeOptions.FirstOrDefault(o => o.Value == value);
        if (option is not null && !ReferenceEquals(SelectedThemeOption, option))
            SelectedThemeOption = option;
    }

    partial void OnShowFootprintMotionArrowsChanged(bool value)
    {
        if (_isSynchronizing)
            return;

        if (App.MainWindow?.DataContext is MainViewModel main)
            main.ShowFootprintMotionArrows = value;
    }

    partial void OnShowGreylineOverlayChanged(bool value)
    {
        if (_isSynchronizing)
            return;

        if (App.MainWindow?.DataContext is MainViewModel main)
            main.ShowGreylineOverlay = value;
    }

    partial void OnShowMultiTrackOverlayChanged(bool value)
    {
        if (_isSynchronizing)
            return;

        if (App.MainWindow?.DataContext is MainViewModel main)
            main.ShowMultiTrackOverlay = value;
    }

    partial void OnSelectedMapCentreModeOptionChanged(MapCentreModeOption? value)
    {
        if (_isSynchronizing || value is null)
            return;

        MapCentreMode = value.Value;
    }

    partial void OnMapCentreModeChanged(MapCentreMode value)
    {
        var option = MapCentreModeOptions.FirstOrDefault(o => o.Value == value);
        if (option is not null && !ReferenceEquals(SelectedMapCentreModeOption, option))
            SelectedMapCentreModeOption = option;

        PushMapCentrePreview();
    }

    partial void OnMapCentreLongitudeDegChanged(double value)
    {
        PushMapCentrePreview();
    }

    public bool IsCustomMapCentreLongitudeEnabled => MapCentreMode == MapCentreMode.Custom;

    private void PushMapCentrePreview()
    {
        if (_isSynchronizing)
            return;

        if (App.MainWindow?.DataContext is MainViewModel main)
            main.PreviewMapCentre(MapCentreMode, MapCentreLongitudeDeg, LongitudeDeg);
    }

    public int ClockFormatIndex
    {
        get => Use24HourClock ? 1 : 0;
        set
        {
            if (value is not (0 or 1) || Use24HourClock == (value == 1))
                return;

            Use24HourClock = value == 1;
        }
    }

    partial void OnUse24HourClockChanged(bool value)
    {
        if (_isSynchronizing)
            return;

        _settings.Current.Use24HourClock = value;
        if (App.MainWindow?.DataContext is MainViewModel main)
            main.ApplyClockFormatFromSettings();
    }

    public int TimeDisplayIndex
    {
        get => DisplayTimesInUtc ? 1 : 0;
        set
        {
            if (value is not (0 or 1) || DisplayTimesInUtc == (value == 1))
                return;

            DisplayTimesInUtc = value == 1;
        }
    }

    partial void OnDisplayTimesInUtcChanged(bool value)
    {
        if (_isSynchronizing)
            return;

        _settings.Current.DisplayTimesInUtc = value;
        if (App.MainWindow?.DataContext is MainViewModel main)
            main.ApplyClockFormatFromSettings();
    }

    partial void OnGridSquareChanged(string value)
    {
        if (_isSynchronizing)
            return;

        var normalized = NormalizeGridSquare(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            _isSynchronizing = true;
            try
            {
                GridSquare = normalized;
            }
            finally
            {
                _isSynchronizing = false;
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(normalized))
            return;

        _isSynchronizing = true;
        try
        {
            _draft.GridSquare = normalized;
            if (normalized.Contains(',', StringComparison.Ordinal) || normalized.Length < 4)
                return;

            var (lat, lon) = MaidenheadGrid.ToLatLonCenter(_draft.GridSquare);
            _draft.LatitudeDeg = lat;
            _draft.LongitudeDeg = lon;
            if (!LatitudeDeg.Equals(lat))
                LatitudeDeg = lat;
            if (!LongitudeDeg.Equals(lon))
                LongitudeDeg = lon;
        }
        catch
        {
            // invalid grid square
        }
        finally
        {
            _isSynchronizing = false;
        }

        if (MapCentreMode == MapCentreMode.Station)
            PushMapCentrePreview();
    }
}

public sealed record ThemeOption(AppThemePreference Value, string Label);

public sealed record MapCentreModeOption(MapCentreMode Value, string Label);

public sealed record TleSourceOption(TleSourceMode Mode, string Label);

public sealed record TleAutoUpdateOption(TleAutoUpdateMode Mode, string Label);

public sealed record RotatorTypeOption(RotatorType Value, string Label);

public sealed record RotatorTransportOption(RotatorTransportKind Value, string Label);

public sealed record GpsConnectionOption(GpsConnectionKind Value, string Label);

public sealed record RotatorAzimuthOption(RotatorAzimuthRange Value, string Label);

public sealed record RotatorElevationOption(RotatorElevationRange Value, string Label);

public sealed record RigTypeOption(RigType Value, string Label);

public sealed record FlexDiscoveredRadioOption(FlexDiscoveredRadio Radio, string Label);

public sealed record FlexAntennaPortOption(string Token, string Label);

public sealed record RigRegionOption(RigRegion Value, string Label);

public sealed record RecordingDeviceOption(string Id, string DisplayName);

public sealed record RecordingFormatOption(RecordingFormatPreset Value, string Label);

public sealed record RecordingContainerOption(RecordingContainerFormat Value, string Label);

public sealed record CloudlogLogbookOption(string PublicSlug, string LogbookName, string? AccessLevel)
{
    public string DisplayName => string.IsNullOrWhiteSpace(AccessLevel)
        ? LogbookName
        : $"{LogbookName} ({AccessLevel})";

    public static CloudlogLogbookOption From(CloudlogLogbookInfo info) =>
        new(info.PublicSlug, info.LogbookName, info.AccessLevel);
}
