using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Cloudlog;
using OscarWatch.Core.Display;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Rotator;
using OscarWatch.Theme;

namespace OscarWatch.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly ISpeechService _speech;
    private readonly IAudioRecordingService _recording;
    private readonly ICloudlogRadioSyncService _cloudlog;
    private readonly GroundStation _draft = new();
    private bool _isSynchronizing;

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private double _latitudeDeg;

    [ObservableProperty]
    private double _longitudeDeg;

    [ObservableProperty]
    private double _altitudeMeters;

    [ObservableProperty]
    private string _gridSquare = "";

    [ObservableProperty]
    private double _minimumElevationDeg = 5;

    [ObservableProperty]
    private int _passPredictionHours = 48;

    [ObservableProperty]
    private AppThemePreference _themePreference = AppThemePreference.System;

    [ObservableProperty]
    private bool _showFootprintMotionArrows = true;

    [ObservableProperty]
    private bool _showGrayline = true;

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

    public IReadOnlyList<TleSourceOption> TleSourceOptions { get; } =
    [
        new(TleSourceMode.OscarWatch, "OscarWatch (tle.oscarwatch.org)"),
        new(TleSourceMode.AmsatOrg, "AMSAT.org (nasabare.txt)"),
        new(TleSourceMode.CustomUrl, "Custom URL"),
        new(TleSourceMode.LocalFile, "Local file")
    ];

    public string TleCustomUrlWatermark { get; } = TleSourceResolver.CelestrakAmsatExampleUrl;

    public bool ShowTleCustomUrl => TleSourceOption?.Mode == TleSourceMode.CustomUrl;

    public bool ShowTleLocalFile => TleSourceOption?.Mode == TleSourceMode.LocalFile;

    [ObservableProperty]
    private bool _voiceAnnouncementsEnabled;

    [ObservableProperty]
    private double _announceElevationDeg = -3;

    [ObservableProperty]
    private SpeechVoiceOption? _selectedSpeechVoice;

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
    private string _recordingTestStatus = "";

    public ObservableCollection<RecordingDeviceOption> RecordingDeviceOptions { get; } = [];

    public bool RecordingAvailable { get; }

    public bool RecordingUnavailable => !RecordingAvailable;

    public string RecordingUnavailableText =>
        _recording.UnavailableReason ?? "Audio recording is not available on this system.";

    public IReadOnlyList<RecordingFormatOption> RecordingFormatOptions { get; } =
    [
        new(RecordingFormatPreset.Mono44100, RecordingFormatPreset.Mono44100.GetLabel()),
        new(RecordingFormatPreset.Mono48000, RecordingFormatPreset.Mono48000.GetLabel()),
        new(RecordingFormatPreset.Stereo44100, RecordingFormatPreset.Stereo44100.GetLabel())
    ];

    [ObservableProperty]
    private bool _rotatorEnabled;

    [ObservableProperty]
    private string? _selectedComPort;

    [ObservableProperty]
    private int _rotatorBaudRate = 4800;

    [ObservableProperty]
    private double _rotatorTrackStartElevationDeg = -3;

    [ObservableProperty]
    private double _rotatorParkAzimuthDeg;

    [ObservableProperty]
    private double _rotatorParkElevationDeg;

    [ObservableProperty]
    private double _rotatorAzimuthOffsetDeg;

    [ObservableProperty]
    private double _rotatorElevationOffsetDeg;

    public ObservableCollection<string> AvailableComPorts { get; } = [];

    public bool SpeechAvailable { get; }

    public bool SpeechUnavailable => !SpeechAvailable;

    public string VoicePreviewText { get; } = SatelliteNamePhonetics.SampleAnnouncementText;

    public IReadOnlyList<SpeechVoiceOption> SpeechVoiceOptions { get; }

    public AppThemePreference[] ThemeOptions { get; } =
        Enum.GetValues<AppThemePreference>();

    public IReadOnlyList<TleAutoUpdateOption> TleAutoUpdateOptions { get; } =
    [
        new(TleAutoUpdateMode.Manual, "Manual only"),
        new(TleAutoUpdateMode.OnStartup, "On startup (if older than 6 hours)"),
        new(TleAutoUpdateMode.EverySixHours, "Every 6 hours while running")
    ];

    public int[] RotatorBaudRateOptions { get; } = [600, 1200, 2400, 4800, 9600, 19200];

    public int[] RigBaudRateOptions { get; } = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];

    public IReadOnlyList<RotatorTypeOption> RotatorTypeChoices { get; } =
    [
        new(RotatorType.YaesuGs232, "Yaesu GS-232"),
        new(RotatorType.Spid, "SPID (Rot1Prog / Rot2Prog)"),
        new(RotatorType.EasyComm, "EasyComm")
    ];

    [ObservableProperty]
    private RotatorTypeOption? _selectedRotatorTypeChoice;

    public IReadOnlyList<RotatorAzimuthOption> AzimuthRangeChoices { get; } =
    [
        new(RotatorAzimuthRange.Deg360, "0–360°"),
        new(RotatorAzimuthRange.Deg450, "0–450°")
    ];

    public IReadOnlyList<RotatorElevationOption> ElevationRangeChoices { get; } =
    [
        new(RotatorElevationRange.Deg90, "0–90°"),
        new(RotatorElevationRange.Deg180, "0–180°")
    ];

    [ObservableProperty]
    private RotatorAzimuthOption? _selectedAzimuthRangeChoice;

    [ObservableProperty]
    private RotatorElevationOption? _selectedElevationRangeChoice;

    [ObservableProperty]
    private bool _rotatorSmartAzimuth450 = true;

    public bool IsRotatorSmartAzimuth450Enabled =>
        SelectedAzimuthRangeChoice?.Value == RotatorAzimuthRange.Deg450;

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
    private int _rigCatDelayMs = 50;

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
    private RigRegionOption? _selectedRigRegionChoice;

    [ObservableProperty]
    private bool _showComPortConflict;

    [ObservableProperty]
    private string _comPortConflictText = "";

    [ObservableProperty]
    private bool _cloudlogEnabled;

    [ObservableProperty]
    private string _cloudlogBaseUrl = "";

    [ObservableProperty]
    private string _cloudlogApiKey = "";

    [ObservableProperty]
    private string _cloudlogRadioName = "OscarWatch";

    [ObservableProperty]
    private int _cloudlogMinUpdateIntervalMs = 1000;

    [ObservableProperty]
    private string _cloudlogTestStatus = "";

    public IReadOnlyList<RigTypeOption> RigTypeChoices { get; } =
    [
        new(RigType.IcomIc910, "ICOM IC-910"),
        new(RigType.IcomIc9100, "ICOM IC-9100"),
        new(RigType.IcomIc9700, "ICOM IC-9700"),
        new(RigType.YaesuFt847, "Yaesu FT-847"),
        new(RigType.KenwoodTs2000, "Kenwood TS-2000"),
        new(RigType.Dummy, "Dummy Rig")
    ];

    public IReadOnlyList<RigTypeOption> RigDualTypeChoices { get; } =
    [
        new(RigType.YaesuFt817, "Yaesu FT-817"),
        new(RigType.YaesuFt818, "Yaesu FT-818"),
        new(RigType.YaesuFt991, "Yaesu FT-991"),
        new(RigType.YaesuFt991a, "Yaesu FT-991A"),
        new(RigType.IcomIc705, "ICOM IC-705")
    ];

    public bool ShowRigSingleConfig => !DualRadioEnabled;

    public bool ShowRigDualConfig => DualRadioEnabled;

    public bool ShowRigCivAddress =>
        SelectedRigTypeChoice?.Value is RigType.IcomIc910 or RigType.IcomIc9100 or RigType.IcomIc9700;

    public bool ShowRigFt847CatHint =>
        SelectedRigTypeChoice?.Value == RigType.YaesuFt847;

    public bool ShowRigTs2000CatHint =>
        SelectedRigTypeChoice?.Value == RigType.KenwoodTs2000;

    public bool ShowRigFt817CatHint =>
        DualRadioEnabled
        && (SelectedDownlinkRigTypeChoice?.Value is RigType.YaesuFt817 or RigType.YaesuFt818
            || SelectedUplinkRigTypeChoice?.Value is RigType.YaesuFt817 or RigType.YaesuFt818);

    public bool ShowDownlinkCivAddress =>
        DualRadioEnabled && SelectedDownlinkRigTypeChoice?.Value == RigType.IcomIc705;

    public bool ShowUplinkCivAddress =>
        DualRadioEnabled && SelectedUplinkRigTypeChoice?.Value == RigType.IcomIc705;

    public bool ShowRigIc705CatHint =>
        DualRadioEnabled
        && (SelectedDownlinkRigTypeChoice?.Value == RigType.IcomIc705
            || SelectedUplinkRigTypeChoice?.Value == RigType.IcomIc705);

    public bool ShowRigFt991CatHint =>
        DualRadioEnabled
        && (SelectedDownlinkRigTypeChoice?.Value is RigType.YaesuFt991 or RigType.YaesuFt991a
            || SelectedUplinkRigTypeChoice?.Value is RigType.YaesuFt991 or RigType.YaesuFt991a);

    public IReadOnlyList<RigRegionOption> RigRegionChoices { get; } =
    [
        new(RigRegion.EU, "EU"),
        new(RigRegion.USA, "USA")
    ];

    public SettingsViewModel(
        ISettingsService settings,
        ISpeechService speech,
        IAudioRecordingService recording,
        ICloudlogRadioSyncService cloudlog)
    {
        _settings = settings;
        _speech = speech;
        _recording = recording;
        _cloudlog = cloudlog;
        RecordingAvailable = recording.IsAvailable;
        SpeechAvailable = speech.IsAvailable;
        SpeechVoiceOptions = speech.GetAvailableVoices();
        CopyGroundStation(settings.Current.GroundStation, _draft);
        RefreshComPorts();
        RefreshRecordingDevices();
        LoadFromDraft();
    }

    [RelayCommand]
    private void RefreshRecordingDevices()
    {
        RecordingDeviceOptions.Clear();
        foreach (var device in _recording.GetInputDevices())
            RecordingDeviceOptions.Add(new RecordingDeviceOption(device.Id, device.DisplayName));

        if (SelectedRecordingDevice is not null)
        {
            SelectedRecordingDevice = RecordingDeviceOptions.FirstOrDefault(d =>
                d.Id == SelectedRecordingDevice.Id)
                ?? RecordingDeviceOptions.FirstOrDefault(d =>
                    d.DisplayName == SelectedRecordingDevice.DisplayName);
        }

        if (SelectedRecordingDevice is null)
            SelectedRecordingDevice = RecordingDeviceOptions.FirstOrDefault();
    }

    [RelayCommand]
    private void RefreshComPorts()
    {
        AvailableComPorts.Clear();
        foreach (var port in SerialPortDiscovery.GetAvailablePorts())
            AvailableComPorts.Add(port);

        if (SelectedComPort is not null && !AvailableComPorts.Contains(SelectedComPort))
            AvailableComPorts.Add(SelectedComPort);
        if (SelectedRigComPort is not null && !AvailableComPorts.Contains(SelectedRigComPort))
            AvailableComPorts.Add(SelectedRigComPort);
    }

    public async Task SaveAsync()
    {
        _settings.Current.GroundStation = new GroundStation
        {
            DisplayName = DisplayName,
            LatitudeDeg = LatitudeDeg,
            LongitudeDeg = LongitudeDeg,
            AltitudeMetersAsl = AltitudeMeters,
            GridSquare = NormalizeGridSquare(GridSquare)
        };
        _settings.Current.MinimumElevationDeg = MinimumElevationDeg;
        _settings.Current.PassPredictionHours = PassPredictionHours;
        _settings.Current.Theme = ThemePreference;
        _settings.Current.ShowFootprintMotionArrows = ShowFootprintMotionArrows;
        _settings.Current.ShowGrayline = ShowGrayline;
        _settings.Current.TleSource = new TleSourceSettings
        {
            Mode = TleSourceOption?.Mode ?? TleSourceMode.OscarWatch,
            CustomUrl = TleCustomUrl.Trim(),
            LocalFilePath = TleLocalFilePath.Trim()
        };
        if (TleAutoUpdateOption is not null)
            _settings.Current.TleAutoUpdate = TleAutoUpdateOption.Mode;
        _settings.Current.TransponderDatabaseCheckOnStartup = TransponderDatabaseCheckOnStartup;
        _settings.Current.VoiceAnnouncements = new VoiceAnnouncementSettings
        {
            Enabled = VoiceAnnouncementsEnabled,
            AnnounceElevationDeg = AnnounceElevationDeg,
            VoiceName = SelectedSpeechVoice?.Id ?? ""
        };
        var stopElevation = Math.Min(RecordingStopElevationDeg, RecordingStartElevationDeg);
        _settings.Current.PassRecording = new PassRecordingSettings
        {
            Enabled = PassRecordingEnabled,
            DeviceId = SelectedRecordingDevice?.Id ?? "",
            DeviceDisplayName = SelectedRecordingDevice?.DisplayName ?? "",
            Format = SelectedRecordingFormat?.Value ?? RecordingFormatPreset.Mono44100,
            StartElevationDeg = RecordingStartElevationDeg,
            StopElevationDeg = stopElevation,
            OutputFolder = RecordingOutputFolder.Trim()
        };
        _settings.Current.Rotator = new RotatorSettings
        {
            Enabled = RotatorEnabled,
            Type = SelectedRotatorTypeChoice?.Value ?? RotatorType.YaesuGs232,
            Port = SelectedComPort ?? "",
            BaudRate = RotatorBaudRate,
            AzimuthRange = SelectedAzimuthRangeChoice?.Value ?? RotatorAzimuthRange.Deg450,
            ElevationRange = SelectedElevationRangeChoice?.Value ?? RotatorElevationRange.Deg180,
            TrackStartElevationDeg = Math.Clamp(RotatorTrackStartElevationDeg, -90, 90),
            ParkAzimuthDeg = RotatorParkAzimuthDeg,
            ParkElevationDeg = RotatorParkElevationDeg,
            AzimuthOffsetDeg = RotatorAzimuthOffsetDeg,
            ElevationOffsetDeg = RotatorElevationOffsetDeg,
            SmartAzimuth450 = RotatorSmartAzimuth450
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
                CivAddress = DownlinkCivAddress.Trim()
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
            Region = SelectedRigRegionChoice?.Value ?? RigRegion.EU,
            DopplerThresholdFmHz = RigDopplerThresholdFmHz,
            DopplerThresholdLinearHz = RigDopplerThresholdLinearHz,
            CatDelayMs = RigCatDelayMs,
            CatUpdatesPaused = _settings.Current.Rig.CatUpdatesPaused,
            CwKeepSidebandDownlink = RigCwKeepSidebandDownlink
        };
        _settings.Current.Cloudlog = new CloudlogSettings
        {
            Enabled = CloudlogEnabled,
            BaseUrl = CloudlogUrlHelper.NormalizeBaseUrl(CloudlogBaseUrl),
            ApiKey = CloudlogApiKey.Trim(),
            RadioName = string.IsNullOrWhiteSpace(CloudlogRadioName) ? "OscarWatch" : CloudlogRadioName.Trim(),
            MinUpdateIntervalMs = Math.Clamp(CloudlogMinUpdateIntervalMs, 250, 60_000)
        };
        _cloudlog.ResetThrottle();
        _settings.SyncActiveStationFromGroundStation();
        AppThemeManager.Apply(ThemePreference);
        await _settings.SaveAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanTestVoiceAnnouncement))]
    private async Task TestVoiceAnnouncementAsync()
    {
        var voiceName = SelectedSpeechVoice?.Id;
        await _speech.SpeakAsync(
            VoicePreviewText,
            string.IsNullOrWhiteSpace(voiceName) ? null : voiceName).ConfigureAwait(true);
    }

    private bool CanTestVoiceAnnouncement() => SpeechAvailable;

    private void LoadFromDraft()
    {
        _isSynchronizing = true;
        try
        {
            DisplayName = _draft.DisplayName;
            LatitudeDeg = _draft.LatitudeDeg;
            LongitudeDeg = _draft.LongitudeDeg;
            AltitudeMeters = _draft.AltitudeMetersAsl;
            GridSquare = NormalizeGridSquare(_draft.GridSquare);
            MinimumElevationDeg = _settings.Current.MinimumElevationDeg;
            PassPredictionHours = _settings.Current.PassPredictionHours;
            ThemePreference = _settings.Current.Theme;
            ShowFootprintMotionArrows = _settings.Current.ShowFootprintMotionArrows;
            ShowGrayline = _settings.Current.ShowGrayline;
            var tleSource = _settings.Current.TleSource ?? new TleSourceSettings();
            TleSourceOption = TleSourceOptions.FirstOrDefault(o => o.Mode == tleSource.Mode)
                ?? TleSourceOptions[0];
            TleCustomUrl = tleSource.CustomUrl;
            TleLocalFilePath = tleSource.LocalFilePath;
            TleAutoUpdateOption = TleAutoUpdateOptions.FirstOrDefault(o => o.Mode == _settings.Current.TleAutoUpdate)
                ?? TleAutoUpdateOptions[1];
            TransponderDatabaseCheckOnStartup = _settings.Current.TransponderDatabaseCheckOnStartup;

            var voice = _settings.Current.VoiceAnnouncements ?? new VoiceAnnouncementSettings();
            VoiceAnnouncementsEnabled = voice.Enabled;
            AnnounceElevationDeg = voice.AnnounceElevationDeg;
            SelectedSpeechVoice = SpeechVoiceOptions.FirstOrDefault(v => v.Id == voice.VoiceName)
                ?? SpeechVoiceOptions.FirstOrDefault();

            var recording = _settings.Current.PassRecording ?? new PassRecordingSettings();
            PassRecordingEnabled = recording.Enabled;
            RecordingStartElevationDeg = recording.StartElevationDeg;
            RecordingStopElevationDeg = recording.StopElevationDeg;
            RecordingOutputFolder = recording.OutputFolder;
            SelectedRecordingFormat = RecordingFormatOptions.FirstOrDefault(o => o.Value == recording.Format)
                ?? RecordingFormatOptions[0];
            RefreshRecordingDevices();
            if (!string.IsNullOrWhiteSpace(recording.DeviceId))
            {
                SelectedRecordingDevice = RecordingDeviceOptions.FirstOrDefault(d => d.Id == recording.DeviceId)
                    ?? RecordingDeviceOptions.FirstOrDefault(d => d.DisplayName == recording.DeviceDisplayName);
            }
            RecordingTestStatus = "";

            var rotator = _settings.Current.Rotator ?? new RotatorSettings();
            RotatorEnabled = rotator.Enabled;
            SelectedRotatorTypeChoice = RotatorTypeChoices.FirstOrDefault(o => o.Value == rotator.Type)
                ?? RotatorTypeChoices[0];
            SelectedComPort = string.IsNullOrWhiteSpace(rotator.Port) ? null : rotator.Port;
            RotatorBaudRate = rotator.BaudRate;
            SelectedAzimuthRangeChoice = AzimuthRangeChoices.FirstOrDefault(o => o.Value == rotator.AzimuthRange)
                ?? AzimuthRangeChoices[1];
            SelectedElevationRangeChoice = ElevationRangeChoices.FirstOrDefault(o => o.Value == rotator.ElevationRange)
                ?? ElevationRangeChoices[1];
            RotatorTrackStartElevationDeg = rotator.TrackStartElevationDeg;
            RotatorParkAzimuthDeg = rotator.ParkAzimuthDeg;
            RotatorParkElevationDeg = rotator.ParkElevationDeg;
            RotatorAzimuthOffsetDeg = rotator.AzimuthOffsetDeg;
            RotatorElevationOffsetDeg = rotator.ElevationOffsetDeg;
            RotatorSmartAzimuth450 = rotator.SmartAzimuth450;

            var rig = _settings.Current.Rig ?? new RigSettings();
            rig.MigrateFt817818ToDualOnly();
            RigEnabled = rig.Enabled;
            DualRadioEnabled = rig.DualRadioEnabled;
            SelectedRigTypeChoice = RigTypeChoices.FirstOrDefault(o => o.Value == rig.Type)
                ?? RigTypeChoices[0];
            SelectedRigComPort = string.IsNullOrWhiteSpace(rig.Port) ? null : rig.Port;
            RigBaudRate = rig.BaudRate;
            RigCivAddress = rig.CivAddress;
            SelectedRigRegionChoice = RigRegionChoices.FirstOrDefault(o => o.Value == rig.Region)
                ?? RigRegionChoices[0];
            var down = rig.Downlink ?? new RigEndpointSettings();
            var up = rig.Uplink ?? new RigEndpointSettings();
            SelectedDownlinkRigTypeChoice = RigDualTypeChoices.FirstOrDefault(o => o.Value == down.Type)
                ?? RigDualTypeChoices[0];
            SelectedUplinkRigTypeChoice = RigDualTypeChoices.FirstOrDefault(o => o.Value == up.Type)
                ?? RigDualTypeChoices[1];
            SelectedDownlinkComPort = string.IsNullOrWhiteSpace(down.Port) ? null : down.Port;
            SelectedUplinkComPort = string.IsNullOrWhiteSpace(up.Port) ? null : up.Port;
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
            RigCatDelayMs = rig.CatDelayMs;
            RigCwKeepSidebandDownlink = rig.CwKeepSidebandDownlink;
            var cloudlog = _settings.Current.Cloudlog ?? new CloudlogSettings();
            CloudlogEnabled = cloudlog.Enabled;
            CloudlogBaseUrl = cloudlog.BaseUrl;
            CloudlogApiKey = cloudlog.ApiKey;
            CloudlogRadioName = string.IsNullOrWhiteSpace(cloudlog.RadioName) ? "OscarWatch" : cloudlog.RadioName;
            CloudlogMinUpdateIntervalMs = cloudlog.MinUpdateIntervalMs <= 0 ? 1000 : cloudlog.MinUpdateIntervalMs;
            CloudlogTestStatus = "";
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
        if (SelectedRecordingDevice is null)
        {
            RecordingTestStatus = "Select an input device first.";
            return;
        }

        var format = SelectedRecordingFormat?.Value ?? RecordingFormatPreset.Mono44100;
        var tempDir = Path.Combine(Path.GetTempPath(), "OscarWatch-recording-test");
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, $"test-{DateTime.UtcNow:yyyyMMdd-HHmmss}.wav");

        try
        {
            TestRecordingCommand.NotifyCanExecuteChanged();
            RecordingTestStatus = "Recording 5 s test clip…";
            await _recording.StartAsync(
                AudioRecordingSessions.ManualTestNoradId,
                "Test",
                SelectedRecordingDevice.Id,
                format,
                outputPath).ConfigureAwait(true);
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            await _recording.StopAsync().ConfigureAwait(true);
            RecordingTestStatus = $"Saved test clip to {outputPath}";
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
        RecordingAvailable && SelectedRecordingDevice is not null && !_recording.IsRecording;

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
            Title = "Choose TLE file",
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
            Title = "Choose recording output folder",
            AllowMultiple = false
        }).ConfigureAwait(true);

        if (folders.Count > 0)
            RecordingOutputFolder = folders[0].Path.LocalPath;
    }

    public async Task TestCloudlogAsync()
    {
        try
        {
            CloudlogTestStatus = "Testing…";
            var settings = new CloudlogSettings
            {
                Enabled = true,
                BaseUrl = CloudlogUrlHelper.NormalizeBaseUrl(CloudlogBaseUrl),
                ApiKey = CloudlogApiKey.Trim(),
                RadioName = string.IsNullOrWhiteSpace(CloudlogRadioName) ? "OscarWatch" : CloudlogRadioName.Trim()
            };

            if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                CloudlogTestStatus = "Enter URL and API key (tab out of each field or click Test again).";
                return;
            }

            var endpoint = $"{settings.BaseUrl}/index.php/api/radio";
            var ok = await _cloudlog.TestConnectionAsync(settings).ConfigureAwait(true);
            CloudlogTestStatus = ok
                ? $"Connection OK — posted test CAT to {endpoint}"
                : _cloudlog.LastError ?? "Connection failed.";
        }
        catch (Exception ex)
        {
            CloudlogTestStatus = ex.Message;
        }
    }

    private void RefreshComPortConflict()
    {
        var rotator = new RotatorSettings
        {
            Enabled = RotatorEnabled,
            Port = SelectedComPort ?? ""
        };
        var rig = BuildRigSettingsForConflictCheck();
        ShowComPortConflict = SerialPortConflictHelper.TryDescribeConflict(rotator, rig, out var message);
        ComPortConflictText = message;
    }

    partial void OnRotatorEnabledChanged(bool value) => RefreshComPortConflictIfReady();

    partial void OnSelectedRotatorTypeChoiceChanged(RotatorTypeOption? value)
    {
        if (_isSynchronizing || value is null)
            return;

        if (value.Value == RotatorType.Spid)
            RotatorBaudRate = 600;
    }

    partial void OnSelectedAzimuthRangeChoiceChanged(RotatorAzimuthOption? value)
    {
        OnPropertyChanged(nameof(IsRotatorSmartAzimuth450Enabled));
    }
    partial void OnRigEnabledChanged(bool value) => RefreshComPortConflictIfReady();
    partial void OnDualRadioEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowRigSingleConfig));
        OnPropertyChanged(nameof(ShowRigDualConfig));
        OnPropertyChanged(nameof(ShowRigFt817CatHint));
        OnPropertyChanged(nameof(ShowDownlinkCivAddress));
        OnPropertyChanged(nameof(ShowUplinkCivAddress));
        OnPropertyChanged(nameof(ShowRigIc705CatHint));
        OnPropertyChanged(nameof(ShowRigFt991CatHint));
        RefreshComPortConflictIfReady();
    }

    partial void OnSelectedComPortChanged(string? value) => RefreshComPortConflictIfReady();
    partial void OnSelectedRigComPortChanged(string? value) => RefreshComPortConflictIfReady();
    partial void OnSelectedDownlinkComPortChanged(string? value) => RefreshComPortConflictIfReady();
    partial void OnSelectedUplinkComPortChanged(string? value) => RefreshComPortConflictIfReady();

    partial void OnSelectedRigTypeChoiceChanged(RigTypeOption? value)
    {
        OnPropertyChanged(nameof(ShowRigCivAddress));
        OnPropertyChanged(nameof(ShowRigFt847CatHint));
        OnPropertyChanged(nameof(ShowRigTs2000CatHint));
        OnPropertyChanged(nameof(ShowRigFt817CatHint));
        RefreshComPortConflictIfReady();
        if (_isSynchronizing || value is null)
            return;

        if (value.Value is RigType.YaesuFt847 or RigType.KenwoodTs2000)
            RigBaudRate = 57600;

        if (value.Value is not (RigType.IcomIc910 or RigType.IcomIc9100 or RigType.IcomIc9700))
            return;

        var suggested = RigSettings.DefaultCivAddressFor(value.Value);
        if (string.IsNullOrWhiteSpace(RigCivAddress)
            || RigCivAddress is "60" or "7C" or "A2")
            RigCivAddress = suggested;
    }

    partial void OnSelectedDownlinkRigTypeChoiceChanged(RigTypeOption? value)
    {
        OnPropertyChanged(nameof(ShowRigFt817CatHint));
        OnPropertyChanged(nameof(ShowDownlinkCivAddress));
        OnPropertyChanged(nameof(ShowRigIc705CatHint));
        OnPropertyChanged(nameof(ShowRigFt991CatHint));
        if (_isSynchronizing || value is null)
            return;

        if (value.Value is RigType.YaesuFt817 or RigType.YaesuFt818)
            DownlinkBaudRate = RigSettings.Ft817818DefaultBaudRate;

        if (value.Value is RigType.YaesuFt991 or RigType.YaesuFt991a)
            DownlinkBaudRate = RigSettings.Ft991DefaultBaudRate;

        if (value.Value == RigType.IcomIc705)
        {
            DownlinkBaudRate = RigSettings.Ic705DefaultBaudRate;
            if (ShouldSuggestCivAddress(DownlinkCivAddress))
                DownlinkCivAddress = RigSettings.DefaultCivAddressFor(RigType.IcomIc705);
        }
    }

    partial void OnSelectedUplinkRigTypeChoiceChanged(RigTypeOption? value)
    {
        OnPropertyChanged(nameof(ShowRigFt817CatHint));
        OnPropertyChanged(nameof(ShowUplinkCivAddress));
        OnPropertyChanged(nameof(ShowRigIc705CatHint));
        OnPropertyChanged(nameof(ShowRigFt991CatHint));
        if (_isSynchronizing || value is null)
            return;

        if (value.Value is RigType.YaesuFt817 or RigType.YaesuFt818)
            UplinkBaudRate = RigSettings.Ft817818DefaultBaudRate;

        if (value.Value is RigType.YaesuFt991 or RigType.YaesuFt991a)
            UplinkBaudRate = RigSettings.Ft991DefaultBaudRate;

        if (value.Value == RigType.IcomIc705)
        {
            UplinkBaudRate = RigSettings.Ic705DefaultBaudRate;
            if (ShouldSuggestCivAddress(UplinkCivAddress))
                UplinkCivAddress = RigSettings.DefaultCivAddressFor(RigType.IcomIc705);
        }
    }

    private static bool ShouldSuggestCivAddress(string? address) =>
        string.IsNullOrWhiteSpace(address)
        || address is "60" or "7C" or "A2";

    private RigSettings BuildRigSettingsForConflictCheck() => new()
    {
        Enabled = RigEnabled,
        DualRadioEnabled = DualRadioEnabled,
        Type = SelectedRigTypeChoice?.Value ?? RigType.None,
        Port = SelectedRigComPort ?? "",
        Downlink = new RigEndpointSettings
        {
            Type = SelectedDownlinkRigTypeChoice?.Value ?? RigType.YaesuFt817,
            Port = SelectedDownlinkComPort ?? ""
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

    private static void CopyGroundStation(GroundStation source, GroundStation target)
    {
        target.DisplayName = source.DisplayName;
        target.LatitudeDeg = source.LatitudeDeg;
        target.LongitudeDeg = source.LongitudeDeg;
        target.AltitudeMetersAsl = source.AltitudeMetersAsl;
        target.GridSquare = NormalizeGridSquare(source.GridSquare);
    }

    private static string NormalizeGridSquare(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant();

    private void SyncGridFromDraftLatLon()
    {
        _draft.LatitudeDeg = LatitudeDeg;
        _draft.LongitudeDeg = LongitudeDeg;
        _draft.GridSquare = NormalizeGridSquare(MaidenheadGrid.FromLatLon(LatitudeDeg, LongitudeDeg));
        var updated = _draft.GridSquare;
        if (!string.Equals(GridSquare, updated, StringComparison.Ordinal))
            GridSquare = updated;
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
    }

    partial void OnThemePreferenceChanged(AppThemePreference value)
    {
        if (_isSynchronizing)
            return;

        AppThemeManager.Apply(value);
    }

    partial void OnShowFootprintMotionArrowsChanged(bool value)
    {
        if (_isSynchronizing)
            return;

        if (App.MainWindow?.DataContext is MainViewModel main)
            main.ShowFootprintMotionArrows = value;
    }

    partial void OnShowGraylineChanged(bool value)
    {
        if (_isSynchronizing)
            return;

        if (App.MainWindow?.DataContext is MainViewModel main)
            main.ShowGrayline = value;
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

        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length < 4)
            return;

        _isSynchronizing = true;
        try
        {
            _draft.GridSquare = normalized;
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
    }
}

public sealed record TleSourceOption(TleSourceMode Mode, string Label);

public sealed record TleAutoUpdateOption(TleAutoUpdateMode Mode, string Label);

public sealed record RotatorTypeOption(RotatorType Value, string Label);

public sealed record RotatorAzimuthOption(RotatorAzimuthRange Value, string Label);

public sealed record RotatorElevationOption(RotatorElevationRange Value, string Label);

public sealed record RigTypeOption(RigType Value, string Label);

public sealed record RigRegionOption(RigRegion Value, string Label);

public sealed record RecordingDeviceOption(string Id, string DisplayName);

public sealed record RecordingFormatOption(RecordingFormatPreset Value, string Label);
