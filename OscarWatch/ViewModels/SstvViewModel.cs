using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Services;
using OscarWatch.Core.Sstv;
using OscarWatch.Localization;
using OscarWatch.Services;
using OscarWatch.Sstv;
using Serilog;

namespace OscarWatch.ViewModels;

/// <summary>OscarWatch SSTV: receive-only picture decoder window.</summary>
public partial class SstvViewModel : ViewModelBase, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SstvViewModel>();
    private const int MaxGalleryItems = 60;

    private readonly ISettingsService _settings;
    private readonly ISatelliteDatabaseService _satelliteDatabase;
    private (string? Name, string? Norad, SstvModeId[]? Modes, long CheckedMs) _expectedModes;
    private readonly FrequencyOverlayViewModel _frequencyOverlay;
    private readonly ILiveTrackerSnapshotProvider _tracker;
    private readonly IRigController _rig;
    private readonly ILocalizationService _l;
    private readonly SstvReceiverService _receiver;
    private readonly SstvPictureUploader _shareQueue;
    private readonly KeepAwakeHold _keepAwake;
    private readonly SstvRecordingLoader _loader = new();
    private readonly DispatcherTimer _uiTimer;
    private WriteableBitmap? _liveA;
    private WriteableBitmap? _liveB;
    private bool _useA;
    private long _previewVersion;
    private bool _loadingDevices;
    private bool _disposed;
    private string? _fileName;
    private int _filePictures;
    private string? _lastError;
    private readonly SstvAutoStartCoordinator _autoStart = new();
    private bool _startedByAuto;
    private DateTime _lastAutoStartCheckUtc = DateTime.MinValue;
    /// <summary>While stopped, keep the idle or waiting line in step with automatic start.</summary>
    private bool _statusFollowsAutoStart = true;

    public SstvViewModel(
        ISettingsService settings,
        FrequencyOverlayViewModel frequencyOverlay,
        ILiveTrackerSnapshotProvider tracker,
        IRigController rig,
        ILocalizationService localization,
        SstvReceiverService receiver,
        SstvPictureUploader shareQueue,
        ISatelliteDatabaseService satelliteDatabase,
        IKeepAwake keepAwake)
    {
        _settings = settings;
        _keepAwake = new KeepAwakeHold(keepAwake, "OscarWatch SSTV is listening for pictures");
        _satelliteDatabase = satelliteDatabase;
        _frequencyOverlay = frequencyOverlay;
        _tracker = tracker;
        _rig = rig;
        _l = localization;
        _receiver = receiver;
        _shareQueue = shareQueue;

        var s = _settings.Current.Sstv;
        _autoSlant = s.AutoSlant;
        _slantTrimPpm = Math.Clamp(s.SlantTrimPpm, -20000, 20000);
        _autoTune = s.AutoTune;
        _catDopplerFeedForward = s.CatDopplerFeedForward;
        _detectWithoutVis = s.DetectWithoutVis;
        _useSatelliteModes = s.UseSatelliteModes;
        _holdThroughFades = s.HoldThroughFades;
        _adaptiveBandwidth = s.AdaptiveBandwidth;
        _alignAosFragments = s.AlignAosFragments;
        _repairBadLines = s.RepairBadLines;
        _smoothAcrossLines = s.SmoothAcrossLines;
        _combineRepeats = s.CombineRepeats;
        _receiver.CombineRepeats = s.CombineRepeats;
        _autoSavePictures = s.AutoSavePictures;
        _saveSessionAudio = s.SaveSessionAudio;
        _outputFolder = s.OutputFolder ?? "";
        _pictureFolder = SstvReceiverService.ResolveOutputDirectory(_outputFolder);
        _canSharePictures = HasApiToken;
        _sharePictures = s.SharePicturesWithOscarWatch && _canSharePictures;
        if (s.SharePicturesWithOscarWatch && !_sharePictures)
        {
            s.SharePicturesWithOscarWatch = false;
            _settings.RequestSave();
        }
        _autoStartEnabled = s.AutoStartEnabled;
        _autoStartElevationDeg = SstvSettings.ClampAutoStartElevationDeg(s.AutoStartElevationDeg);
        _keepAwakeWhileListening = s.KeepAwakeWhileListening;

        ModeOptions.Add(new SstvModeOption(null, _l.Get("Sstv.Mode.Auto")));
        foreach (var mode in SstvModeTable.All)
            ModeOptions.Add(new SstvModeOption(mode.Id, mode.Name));
        var forced = s.GetForcedMode();
        _selectedModeOption = ModeOptions.FirstOrDefault(o => o.Id == forced) ?? ModeOptions[0];

        _statusText = _l.Get("Sstv.Status.Idle");
        RefreshInputDevices();

        _receiver.SatelliteNameProvider = CurrentSatelliteName;
        _receiver.SatelliteNoradProvider = () => _tracker.FocusedNoradId;
        _receiver.ExpectedModesProvider = ExpectedModes;
        _receiver.PictureReceived += OnPictureReceived;
        _shareQueue.StatusChanged += OnShareStatusChanged;
        _shareStatusText = ShareStatusFor(_shareQueue.Status);
        _receiver.PictureStarted += OnPictureStarted;
        _receiver.Faulted += OnReceiverFaulted;
        _receiver.Stopped += OnReceiverStopped;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) => RefreshUiTick();
    }

    public ObservableCollection<Ft4AudioDeviceOption> InputDeviceOptions { get; } = [];

    public ObservableCollection<SstvModeOption> ModeOptions { get; } = [];

    public ObservableCollection<SstvGalleryItem> Gallery { get; } = [];

    [ObservableProperty]
    private Ft4AudioDeviceOption? _selectedInputDevice;

    [ObservableProperty]
    private SstvModeOption? _selectedModeOption;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSlantTrimEnabled))]
    private bool _autoSlant;

    [ObservableProperty]
    private double _slantTrimPpm;

    [ObservableProperty]
    private bool _autoTune;

    [ObservableProperty]
    private bool _catDopplerFeedForward;

    [ObservableProperty]
    private bool _detectWithoutVis;

    [ObservableProperty]
    private bool _useSatelliteModes;

    [ObservableProperty]
    private bool _holdThroughFades;

    [ObservableProperty]
    private bool _adaptiveBandwidth;

    [ObservableProperty]
    private bool _alignAosFragments;

    [ObservableProperty]
    private bool _repairBadLines;

    [ObservableProperty]
    private bool _smoothAcrossLines;

    [ObservableProperty]
    private bool _combineRepeats;

    [ObservableProperty]
    private bool _autoSavePictures;

    [ObservableProperty]
    private bool _saveSessionAudio;

    /// <summary>Share good pictures to the OscarWatch.org gallery after the pass. Needs the API token.</summary>
    [ObservableProperty]
    private bool _sharePictures;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareNote))]
    private bool _canSharePictures;

    [ObservableProperty]
    private string _shareStatusText = "";

    public string ShareNote => CanSharePictures
        ? _l.Get("Sstv.Share.Tip")
        : _l.Get("Sstv.Share.NeedsToken");

    /// <summary>Start listening when the focused satellite rises. Only runs while this window is open.</summary>
    [ObservableProperty]
    private bool _autoStartEnabled;

    [ObservableProperty]
    private double _autoStartElevationDeg;

    /// <summary>Keep the computer and display awake while listening live.</summary>
    [ObservableProperty]
    private bool _keepAwakeWhileListening;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanDecodeRecording))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(ReSyncCommand), nameof(SkipPictureCommand), nameof(RedecodeLastSessionCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanDecodeRecording))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RedecodeLastSessionCommand))]
    private bool _isBusyLoading;

    /// <summary>Waiting for the operator to click points on the selected picture to fix its slant.</summary>
    [ObservableProperty]
    private bool _isMarkingSlant;

    private (double X, double Y)? _slantFirstPoint;
    private bool _keepSlantPrompt;

    [ObservableProperty]
    private string _slantPromptText = "";

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private string _lineText = "";

    [ObservableProperty]
    private string _lockText = "";

    [ObservableProperty]
    private string _tuningText = "";

    [ObservableProperty]
    private string _clockText = "";

    [ObservableProperty]
    private string _dopplerText = "";

    [ObservableProperty]
    private double _inputLevelPercent;

    [ObservableProperty]
    private double _lineProgressPercent;

    /// <summary>Latest spectrum row for the signal view; each new array adds a waterfall row.</summary>
    [ObservableProperty]
    private float[]? _spectrum;

    [ObservableProperty]
    private Bitmap? _displayedPicture;

    [ObservableProperty]
    private string _displayedCaption = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedPictureCommand), nameof(FixSlantCommand))]
    [NotifyPropertyChangedFor(nameof(SaveButtonText), nameof(SaveButtonTip))]
    private SstvGalleryItem? _selectedGalleryItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInputLevel))]
    private bool _isDecodingFile;

    public bool ShowInputLevel => !IsDecodingFile;

    public string SaveButtonText => SelectedGalleryItem is { SavedPath: not null }
        ? _l.Get("Sstv.Picture.AlreadySaved")
        : _l.Get("Sstv.SavePicture");

    public string SaveButtonTip => SelectedGalleryItem switch
    {
        null => _l.Get("Sstv.SavePicture.SelectTip"),
        { SavedPath: { } path } => path,
        _ => _l.Get("Sstv.SavePicture"),
    };

    public bool IsSlantTrimEnabled => !AutoSlant;

    public bool CanStart => !IsRunning && !IsBusyLoading;

    public bool CanDecodeRecording => !IsRunning && !IsBusyLoading;

    /// <summary>Folder pictures and session audio are saved into (the default when the setting cannot be used).</summary>
    [ObservableProperty]
    private string _pictureFolder;

    /// <summary>Configured save folder. Empty uses the default.</summary>
    [ObservableProperty]
    private string _outputFolder;

    public string OutputFolderWatermark =>
        _l.Get("Sstv.OutputFolder.Watermark", SstvPaths.GetDefaultDirectoryDisplay());

    partial void OnOutputFolderChanged(string value)
    {
        _settings.Current.Sstv.OutputFolder = value.Trim();
        _settings.RequestSave();
        PictureFolder = SstvPaths.ResolveOutputDirectory(value).Path;
    }

    public async Task BrowseOutputFolderAsync(Window owner)
    {
        var storage = TopLevel.GetTopLevel(owner)?.StorageProvider;
        if (storage is null)
            return;

        IStorageFolder? start = null;
        try
        {
            start = await storage.TryGetFolderFromPathAsync(PictureFolder).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SSTV folder picker could not start in {Folder}", PictureFolder);
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _l.Get("Sstv.OutputFolder.Browse"),
            AllowMultiple = false,
            SuggestedStartLocation = start,
        }).ConfigureAwait(true);

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            OutputFolder = path;
    }

    public (double Width, double Height)? SavedWindowSize
    {
        get
        {
            var s = _settings.Current.Sstv;
            return s.WindowWidth is { } w && s.WindowHeight is { } h ? (w, h) : null;
        }
        set
        {
            if (value is not { } size || !double.IsFinite(size.Width) || !double.IsFinite(size.Height))
                return;
            _settings.Current.Sstv.WindowWidth = (int)Math.Round(size.Width);
            _settings.Current.Sstv.WindowHeight = (int)Math.Round(size.Height);
        }
    }

    public void OnWindowOpened()
    {
        // Opening the window mid-pass should listen straight away if the satellite is already up.
        _autoStart.ResetTracking();
        _lastAutoStartCheckUtc = DateTime.MinValue;
        PictureFolder = SstvPaths.ResolveOutputDirectory(OutputFolder).Path;
        RefreshShareAvailability();
        ShowStoppedStatus();
        _uiTimer.Start();
    }

    public void OnWindowClosed()
    {
        _uiTimer.Stop();
        StopReceiver();
        _fileName = null;
        _settings.RequestSave();
    }

    private bool CanStartLive() => CanStart;

    [RelayCommand(CanExecute = nameof(CanStartLive))]
    private void Start() => StartSession(automatic: false);

    private void StartSession(bool automatic)
    {
        if (!_receiver.Audio.IsAvailable)
        {
            _statusFollowsAutoStart = false;
            StatusText = _l.Get("Sstv.Status.NoAudio");
            return;
        }

        try
        {
            _lastError = null;
            SelectedGalleryItem = null;
            _receiver.StartLive(_settings.Current.Sstv, SaveSessionAudio);
            PictureFolder = SstvPaths.ResolveOutputDirectory(OutputFolder).Path;
            IsRunning = true;
            _startedByAuto = automatic;
            _statusFollowsAutoStart = true;
            StatusText = ListeningText();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SSTV start failed");
            _statusFollowsAutoStart = false;
            StatusText = _l.Get("Sstv.Status.Error", ex.Message);
            IsRunning = false;
            _startedByAuto = false;
        }
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => StopReceiver();

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void ReSync() => _receiver.ReSync();

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void SkipPicture() => _receiver.SkipPicture();

    [RelayCommand]
    private void RefreshInputDevices()
    {
        _loadingDevices = true;
        try
        {
            var saved = _settings.Current.Sstv;
            InputDeviceOptions.Clear();
            InputDeviceOptions.Add(new Ft4AudioDeviceOption("", _l.Get("Ft4.Audio.SystemDefault")));
            foreach (var d in _receiver.Audio.GetInputDevices())
                InputDeviceOptions.Add(new Ft4AudioDeviceOption(d.Id, d.DisplayName));

            SelectedInputDevice =
                InputDeviceOptions.FirstOrDefault(o => o.Id.Length > 0 && string.Equals(o.Id, saved.InputDeviceId, StringComparison.OrdinalIgnoreCase))
                ?? InputDeviceOptions.FirstOrDefault(o => o.Id.Length > 0 && string.Equals(o.DisplayName, saved.InputDeviceDisplayName, StringComparison.OrdinalIgnoreCase))
                ?? InputDeviceOptions[0];
        }
        finally
        {
            _loadingDevices = false;
        }
    }

    [RelayCommand]
    private void OpenPictureFolder()
    {
        try
        {
            PictureFolder = SstvReceiverService.ResolveOutputDirectory(OutputFolder);
            Directory.CreateDirectory(PictureFolder);
            Process.Start(new ProcessStartInfo { FileName = PictureFolder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open the SSTV folder");
        }
    }

    private bool CanSaveSelected() => SelectedGalleryItem is { SavedPath: null };

    [RelayCommand(CanExecute = nameof(CanSaveSelected))]
    private void SaveSelectedPicture()
    {
        if (SelectedGalleryItem is not { } item)
            return;

        var dir = SstvReceiverService.ResolveOutputDirectory(OutputFolder);
        PictureFolder = dir;
        var path = SstvReceiverService.SavePicture(item.Picture.Decoded, item.Picture.ReceivedUtc, item.Picture.Satellite, dir);
        if (path is null)
            return;

        item.SavedPath = path;
        StatusText = _l.Get("Sstv.Picture.Saved", path);
        SaveSelectedPictureCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(SaveButtonTip));
    }

    private bool CanRedecodeLastSession() =>
        CanDecodeRecording && _receiver.LastSessionAudioPath is { } path && File.Exists(path);

    /// <summary>Decode the audio saved from the last live session again, with the current settings.</summary>
    [RelayCommand(CanExecute = nameof(CanRedecodeLastSession))]
    private Task RedecodeLastSession() =>
        _receiver.LastSessionAudioPath is { } path
            ? DecodeRecordingAsync(path, _receiver.LastSessionExpectedModes)
            : Task.CompletedTask;

    private bool CanFixSlant() => SelectedGalleryItem is not null;

    /// <summary>Start (or cancel) marking an edge on the selected picture to straighten it.</summary>
    [RelayCommand(CanExecute = nameof(CanFixSlant))]
    private void FixSlant()
    {
        _slantFirstPoint = null;
        IsMarkingSlant = !IsMarkingSlant && SelectedGalleryItem is not null;
        SlantPromptText = IsMarkingSlant ? _l.Get("Sstv.FixSlant.First") : "";
    }

    /// <summary>A click on the displayed picture, in picture pixels, while marking the slant.</summary>
    public void AddSlantPoint(double x, double y)
    {
        if (!IsMarkingSlant || SelectedGalleryItem is not { } item)
            return;

        if (_slantFirstPoint is not { } first)
        {
            _slantFirstPoint = (x, y);
            SlantPromptText = _l.Get("Sstv.FixSlant.Second");
            return;
        }

        var straight = SstvSlantFix.Straighten(item.Picture.Decoded.Image, first.X, first.Y, x, y);
        if (straight is null)
        {
            _slantFirstPoint = null;
            SlantPromptText = _l.Get("Sstv.FixSlant.TooClose");
            return;
        }

        _slantFirstPoint = null;
        IsMarkingSlant = false;
        SlantPromptText = _l.Get("Sstv.FixSlant.Done");
        var picture = item.Picture with
        {
            Decoded = SstvSlantFix.WithImage(item.Picture.Decoded, straight),
            SavedPath = null,
        };
        var fixedItem = new SstvGalleryItem(picture, SstvBitmap.Create(straight), item.Caption);
        var index = Gallery.IndexOf(item);
        if (index >= 0)
            Gallery[index] = fixedItem;
        else
            Gallery.Insert(0, fixedItem);
        _keepSlantPrompt = true;
        SelectedGalleryItem = fixedItem;
        _keepSlantPrompt = false;
    }

    /// <summary>Decode a WAV or MP3 recording (called by the window after the file picker).</summary>
    public async Task DecodeRecordingAsync(string path, IReadOnlyCollection<SstvModeId>? expectedModes = null)
    {
        if (!CanDecodeRecording)
            return;

        _fileName = Path.GetFileName(path);
        _filePictures = 0;
        _lastError = null;
        IsBusyLoading = true;
        StatusText = _l.Get("Sstv.Status.LoadingFile", _fileName);
        try
        {
            var loaded = await _loader.LoadAsync(path).ConfigureAwait(true);
            if (loaded.Samples is null)
            {
                StatusText = _l.Get("Sstv.Status.Error", loaded.Error ?? "");
                return;
            }

            if (SstvInputSilence.IsSilentRecording(loaded.Samples))
            {
                Log.Warning("SSTV recording {File} is silent; nothing to decode", _fileName);
                _statusFollowsAutoStart = false;
                StatusText = _l.Get("Sstv.Status.FileSilent", _fileName);
                _fileName = null;
                return;
            }

            SelectedGalleryItem = null;
            _receiver.StartFile(
                loaded.Samples, loaded.SampleRate, _settings.Current.Sstv, Path.GetFileNameWithoutExtension(path),
                _settings.Current.Sstv.UseSatelliteModes ? expectedModes : null);
            IsRunning = true;
            IsDecodingFile = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SSTV recording load failed");
            StatusText = _l.Get("Sstv.Status.Error", ex.Message);
        }
        finally
        {
            IsBusyLoading = false;
        }
    }

    partial void OnSelectedInputDeviceChanged(Ft4AudioDeviceOption? value)
    {
        if (_loadingDevices || value is null)
            return;
        _settings.Current.Sstv.InputDeviceId = value.Id;
        _settings.Current.Sstv.InputDeviceDisplayName = value.DisplayName;
        _settings.RequestSave();
        if (IsRunning && !_receiver.IsDecodingFile)
        {
            StopReceiver();
            Start();
        }
    }

    partial void OnSelectedModeOptionChanged(SstvModeOption? value)
    {
        _settings.Current.Sstv.ForcedMode = value?.Id?.ToString() ?? "";
        OptionsChanged();
    }

    partial void OnAutoSlantChanged(bool value)
    {
        _settings.Current.Sstv.AutoSlant = value;
        OptionsChanged();
    }

    partial void OnSlantTrimPpmChanged(double value)
    {
        _settings.Current.Sstv.SlantTrimPpm = double.IsFinite(value) ? value : 0;
        OptionsChanged();
    }

    partial void OnAutoTuneChanged(bool value)
    {
        _settings.Current.Sstv.AutoTune = value;
        OptionsChanged();
    }

    partial void OnCatDopplerFeedForwardChanged(bool value)
    {
        _settings.Current.Sstv.CatDopplerFeedForward = value;
        _settings.RequestSave();
    }

    partial void OnDetectWithoutVisChanged(bool value)
    {
        _settings.Current.Sstv.DetectWithoutVis = value;
        OptionsChanged();
    }

    partial void OnUseSatelliteModesChanged(bool value)
    {
        _settings.Current.Sstv.UseSatelliteModes = value;
        _settings.RequestSave();
    }

    partial void OnHoldThroughFadesChanged(bool value)
    {
        _settings.Current.Sstv.HoldThroughFades = value;
        OptionsChanged();
    }

    partial void OnAdaptiveBandwidthChanged(bool value)
    {
        _settings.Current.Sstv.AdaptiveBandwidth = value;
        OptionsChanged();
    }

    partial void OnAlignAosFragmentsChanged(bool value)
    {
        _settings.Current.Sstv.AlignAosFragments = value;
        OptionsChanged();
    }

    partial void OnRepairBadLinesChanged(bool value)
    {
        _settings.Current.Sstv.RepairBadLines = value;
        OptionsChanged();
    }

    partial void OnSmoothAcrossLinesChanged(bool value)
    {
        _settings.Current.Sstv.SmoothAcrossLines = value;
        OptionsChanged();
    }

    partial void OnCombineRepeatsChanged(bool value)
    {
        _settings.Current.Sstv.CombineRepeats = value;
        _receiver.CombineRepeats = value;
        _settings.RequestSave();
    }

    /// <summary>Called on the decoder thread: modes the tracked satellite sends, looked up once per satellite.</summary>
    private IReadOnlyCollection<SstvModeId>? ExpectedModes()
    {
        if (!_settings.Current.Sstv.UseSatelliteModes)
            return null;
        var now = Environment.TickCount64;
        var cached = _expectedModes;
        if (now - cached.CheckedMs < 2000)
            return cached.Modes;
        var name = CurrentSatelliteName();
        var norad = _tracker.FocusedNoradId;
        var modes = cached.Name == name && cached.Norad == norad
            ? cached.Modes
            : name is null ? null : SstvModePriors.For(_satelliteDatabase.TryGetEntry(name, norad));
        _expectedModes = (name, norad, modes, now);
        return modes;
    }

    partial void OnAutoSavePicturesChanged(bool value)
    {
        _settings.Current.Sstv.AutoSavePictures = value;
        _settings.RequestSave();
    }

    partial void OnSaveSessionAudioChanged(bool value)
    {
        _settings.Current.Sstv.SaveSessionAudio = value;
        _settings.RequestSave();
    }

    partial void OnSharePicturesChanged(bool value)
    {
        _settings.Current.Sstv.SharePicturesWithOscarWatch = value;
        _settings.RequestSave();
        if (!value)
            _shareQueue.Clear();
        ShareStatusText = ShareStatusFor(_shareQueue.Status);
    }

    private bool HasApiToken => SstvShareRules.CanShare(_settings.Current.SatelliteStatus);

    /// <summary>
    /// Re-reads whether the OscarWatch API token is set. Without one, sharing is switched off
    /// and stays off when a token is added later.
    /// </summary>
    public void RefreshShareAvailability()
    {
        CanSharePictures = HasApiToken;
        if (!CanSharePictures && SharePictures)
            SharePictures = false;
        ShareStatusText = ShareStatusFor(_shareQueue.Status);
    }

    private void OnShareStatusChanged(object? sender, SstvShareStatus status) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
                ShareStatusText = ShareStatusFor(status);
        });

    private string ShareStatusFor(SstvShareStatus status)
    {
        if (!SharePictures && status.Pending == 0)
            return "";

        return status.State switch
        {
            SstvShareState.WaitingForPass when status.Pending > 0 => _l.Get("Sstv.Share.Status.Waiting", status.Pending),
            SstvShareState.Uploading => _l.Get("Sstv.Share.Status.Uploading", status.Pending),
            SstvShareState.Shared => _l.Get(
                string.Equals(status.Detail, "pending", StringComparison.OrdinalIgnoreCase)
                    ? "Sstv.Share.Status.SharedPending"
                    : "Sstv.Share.Status.Shared",
                status.Pending),
            SstvShareState.Rejected => _l.Get("Sstv.Share.Status.Rejected", status.Detail ?? "", status.Pending),
            SstvShareState.Stopped => _l.Get("Sstv.Share.Status.Stopped", status.Detail ?? ""),
            SstvShareState.RateLimited => _l.Get("Sstv.Share.Status.RateLimited", status.Pending),
            SstvShareState.Retrying => _l.Get("Sstv.Share.Status.Retrying", status.Pending),
            _ => _l.Get("Sstv.Share.Status.On"),
        };
    }

    partial void OnAutoStartEnabledChanged(bool value)
    {
        _settings.Current.Sstv.AutoStartEnabled = value;
        _settings.RequestSave();
    }

    partial void OnAutoStartElevationDegChanged(double value)
    {
        if (!double.IsFinite(value))
            return;
        _settings.Current.Sstv.AutoStartElevationDeg = SstvSettings.ClampAutoStartElevationDeg(value);
        _settings.RequestSave();
    }

    partial void OnKeepAwakeWhileListeningChanged(bool value)
    {
        _settings.Current.Sstv.KeepAwakeWhileListening = value;
        _settings.RequestSave();
        UpdateKeepAwake();
    }

    partial void OnIsRunningChanged(bool value) => UpdateKeepAwake();

    private void UpdateKeepAwake() =>
        _keepAwake.Set(!_disposed && SstvSettings.ShouldKeepAwake(KeepAwakeWhileListening, IsRunning, _receiver.IsDecodingFile));

    partial void OnSelectedGalleryItemChanged(SstvGalleryItem? value)
    {
        if (!_keepSlantPrompt)
        {
            IsMarkingSlant = false;
            _slantFirstPoint = null;
            SlantPromptText = "";
        }

        if (value is null)
        {
            ShowLivePicture();
            return;
        }

        DisplayedPicture = value.Bitmap;
        DisplayedCaption = value.Caption;
    }

    private void OptionsChanged()
    {
        _settings.RequestSave();
        if (IsRunning)
            _receiver.ApplyOptions(_settings.Current.Sstv);
    }

    private void StopReceiver()
    {
        _receiver.Stop();
        IsRunning = false;
        _startedByAuto = false;
        IsDecodingFile = false;
        InputLevelPercent = 0;
        if (_lastError is not null)
        {
            _statusFollowsAutoStart = false;
            StatusText = _lastError;
        }
        else
        {
            ShowStoppedStatus();
        }
    }

    private string? CurrentSatelliteName()
    {
        var name = _tracker.GetCurrent().SatelliteName;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private string ListeningText()
    {
        var sat = CurrentSatelliteName();
        return sat is null ? _l.Get("Sstv.Status.Listening") : _l.Get("Sstv.Status.ListeningSat", sat);
    }

    /// <summary>
    /// Idle line while listening is off. With automatic start armed, say which satellite is being waited for
    /// instead of telling the operator to press Start.
    /// </summary>
    private void ShowStoppedStatus()
    {
        _statusFollowsAutoStart = true;
        StatusText = StoppedStatusText();
    }

    private string StoppedStatusText()
    {
        if (!AutoStartEnabled)
            return _l.Get("Sstv.Status.Idle");

        var snapshot = _tracker.GetCurrent();
        var name = string.IsNullOrWhiteSpace(snapshot.SatelliteName) ? null : snapshot.SatelliteName.Trim();
        if (name is null)
            return _l.Get("Sstv.Status.WaitingNoSatellite");

        if (!_autoStart.IsArmed)
            return _l.Get("Sstv.Status.WaitingNextPass", name);

        var start = FormatElevation(SstvSettings.ClampAutoStartElevationDeg(AutoStartElevationDeg));
        if (snapshot.ElevationDeg is not { } elevation || !double.IsFinite(elevation))
            return _l.Get("Sstv.Status.WaitingNoElevation", name);

        if (elevation >= SstvSettings.ClampAutoStartElevationDeg(AutoStartElevationDeg))
            return _l.Get("Sstv.Status.WaitingAbove", name, start);

        return _l.Get("Sstv.Status.Waiting", name, start);
    }

    private static string FormatElevation(double degrees)
    {
        var text = Math.Abs(degrees).ToString("0.#", CultureInfo.CurrentCulture);
        return degrees < 0 ? "−" + text : text;
    }

    private void RefreshUiTick()
    {
        UpdateDoppler();
        ProcessAutoStartTick();

        if (!IsRunning)
        {
            if (_statusFollowsAutoStart)
                ShowStoppedStatus();
            return;
        }

        foreach (var row in _receiver.TakeSpectrumRows())
            Spectrum = row;

        if (!_receiver.IsDecodingFile)
            InputLevelPercent = Math.Clamp(_receiver.Audio.InputPeak * 100, 0, 100);

        var capture = _receiver.CaptureHealth;
        if (!_receiver.IsDecodingFile && capture.IsReconnecting)
        {
            StatusText = _l.Get("Sstv.Status.CaptureRetrying", capture.ReconnectAttempts);
            LineText = "";
            LineProgressPercent = 0;
            LockText = "";
            TuningText = "";
            ClockText = "";
            if (_receiver.TryGetPreview(ref _previewVersion) is { } stalledPreview)
                UpdateLiveBitmap(stalledPreview);
            return;
        }

        var status = _receiver.Status;
        if (status is { State: SstvDecoderState.Decoding, Mode: { } mode })
        {
            var source = status.FromVis ? _l.Get("Sstv.Status.FromVis") : _l.Get("Sstv.Status.FromSync");
            StatusText = _receiver.IsDecodingFile
                ? _l.Get("Sstv.Status.DecodingFile", _fileName ?? "", _receiver.FileProgress * 100)
                : _l.Get("Sstv.Status.Receiving", mode.Name, source);
            DisplayedCaption = $"{mode.Name} ({source})";
            LineText = _l.Get("Sstv.Line", status.LinesDecoded, mode.TransmittedLines);
            LineProgressPercent = 100.0 * status.LinesDecoded / mode.TransmittedLines;
            LockText = _l.Get("Sstv.Lock", status.LockPercent);
            TuningText = _l.Get("Sstv.Offset", status.OffsetHz);
            ClockText = _l.Get("Sstv.Clock", status.ClockPpm);
        }
        else
        {
            StatusText = _receiver.IsDecodingFile
                ? _l.Get("Sstv.Status.DecodingFile", _fileName ?? "", _receiver.FileProgress * 100)
                : _receiver.IsInputSilent
                    ? _l.Get("Sstv.Status.SilentInput", SelectedInputDevice?.DisplayName ?? _l.Get("Ft4.Audio.SystemDefault"))
                    : ListeningText();
            LineText = "";
            LineProgressPercent = 0;
            LockText = "";
            TuningText = "";
            ClockText = "";
        }

        var preview = _receiver.TryGetPreview(ref _previewVersion);
        if (preview is not null)
            UpdateLiveBitmap(preview);
    }

    /// <summary>
    /// Automatic start and stop for the focused satellite. Runs from the window's UI timer, so it
    /// only operates while the SSTV window is open. Checked at most once a second, because the
    /// coordinator counts consecutive samples.
    /// </summary>
    private void ProcessAutoStartTick()
    {
        var now = DateTime.UtcNow;
        if (now - _lastAutoStartCheckUtc < TimeSpan.FromSeconds(1))
            return;
        _lastAutoStartCheckUtc = now;

        var snapshot = _tracker.GetCurrent();
        double? elevation = snapshot.IsAvailable ? snapshot.ElevationDeg : null;
        var startElevation = SstvSettings.ClampAutoStartElevationDeg(AutoStartElevationDeg);

        var action = _autoStart.Process(new SstvAutoStartInput(
            Enabled: AutoStartEnabled,
            FocusedNoradId: _tracker.FocusedNoradId,
            ElevationDeg: elevation,
            StartElevationDeg: startElevation,
            ReceiverRunning: IsRunning,
            ReceiverAutoStarted: _startedByAuto));

        switch (action)
        {
            case SstvAutoStartAction.Start when CanStart:
                Log.Information("SSTV auto-start: focused satellite above {StartElevationDeg} deg", startElevation);
                StartSession(automatic: true);
                break;
            case SstvAutoStartAction.Stop:
                Log.Information("SSTV auto-stop: focused satellite set");
                StopReceiver();
                break;
        }
    }

    private void UpdateDoppler()
    {
        var doppler = _frequencyOverlay.DownlinkDopplerKHz;
        var mode = _frequencyOverlay.SelectedMode?.DownlinkMode;
        double feedForward = 0;
        if (CatDopplerFeedForward && IsRunning && !_receiver.IsDecodingFile)
        {
            var rig = _rig.GetStatus();
            if (rig.IsConnected && rig.IsTracking)
                feedForward = SstvDopplerFeedForward.AudioOffsetHz(mode, _frequencyOverlay.RadioReceiveKHz, rig.LastReceiveHz);
        }

        _receiver.FeedForwardHz = feedForward;
        _receiver.TimeCompressionPpm = IsRunning && !_receiver.IsDecodingFile
            && _tracker.GetCurrent().RangeRateKmPerSec is { } rangeRate
                ? SstvReceiverService.TimeCompressionPpmFromRangeRate(rangeRate)
                : 0;
        // The pass Doppler says nothing about a recording being decoded.
        if (doppler is not { } kHz || IsDecodingFile)
        {
            DopplerText = "";
            return;
        }

        var text = _l.Get("Sstv.Doppler", $"{kHz:+0.000;-0.000;0.000} kHz");
        if (SstvDopplerFeedForward.SidebandSign(mode) != 0 && CatDopplerFeedForward)
            text += "  " + _l.Get("Sstv.FeedForward", feedForward);
        DopplerText = text;
    }

    private void UpdateLiveBitmap(SstvImage image)
    {
        // Two bitmaps in turn, so the Image control sees a new source and redraws.
        ref var target = ref _useA ? ref _liveA : ref _liveB;
        if (target is null || target.PixelSize.Width != image.Width || target.PixelSize.Height != image.Height)
            target = SstvBitmap.Create(image);
        else
            SstvBitmap.CopyInto(target, image);
        _useA = !_useA;

        if (SelectedGalleryItem is null)
            DisplayedPicture = target;
    }

    private void ShowLivePicture()
    {
        DisplayedPicture = _useA ? _liveB : _liveA;
        DisplayedCaption = "";
    }

    private void OnPictureStarted(object? sender, SstvImageStartedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            SelectedGalleryItem = null;
            _previewVersion = -1;
        });

    private void OnPictureReceived(object? sender, SstvReceivedPicture picture) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
                return;

            _filePictures++;
            var item = new SstvGalleryItem(picture, SstvBitmap.Create(picture.Decoded.Image), Caption(picture));
            Gallery.Insert(0, item);
            while (Gallery.Count > MaxGalleryItems)
                Gallery.RemoveAt(Gallery.Count - 1);

            if (picture.SavedPath is { } path)
                StatusText = _l.Get("Sstv.Picture.Saved", path);
        });

    private string Caption(SstvReceivedPicture picture)
    {
        var d = picture.Decoded;
        var caption = _l.Get("Sstv.Gallery.Item", d.Mode.Name, picture.ReceivedUtc.ToString("HH:mm:ss"), d.LockPercent);
        if (!d.Complete)
            caption += ", " + _l.Get("Sstv.Picture.Partial");
        return caption;
    }

    private void OnReceiverFaulted(object? sender, Exception ex) =>
        Dispatcher.UIThread.Post(() => _lastError = _l.Get("Sstv.Status.Error", ex.Message));

    private void OnReceiverStopped(object? sender, int session) =>
        Dispatcher.UIThread.Post(() =>
        {
            // A stop from an earlier session must not end the one now running.
            if (session != _receiver.SessionId || _disposed)
                return;

            var wasFile = _fileName is not null;
            if (_receiver.TryGetPreview(ref _previewVersion) is { } preview)
                UpdateLiveBitmap(preview);
            _receiver.Stop();
            IsRunning = false;
            IsDecodingFile = false;
            InputLevelPercent = 0;
            LineText = "";
            LockText = "";
            TuningText = "";
            ClockText = "";
            LineProgressPercent = 0;
            if (_lastError is not null)
            {
                _statusFollowsAutoStart = false;
                StatusText = _lastError;
            }
            else if (wasFile)
            {
                _statusFollowsAutoStart = false;
                StatusText = _l.Get("Sstv.Status.FileDone", _fileName!, _filePictures);
            }
            else
            {
                ShowStoppedStatus();
            }
            _fileName = null;
        });

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _uiTimer.Stop();
        _receiver.PictureReceived -= OnPictureReceived;
        _receiver.PictureStarted -= OnPictureStarted;
        _receiver.Faulted -= OnReceiverFaulted;
        _receiver.Stopped -= OnReceiverStopped;
        _shareQueue.StatusChanged -= OnShareStatusChanged;
        _receiver.Stop();
        _keepAwake.Dispose();
    }
}

public sealed record SstvModeOption(SstvModeId? Id, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class SstvGalleryItem : ObservableObject
{
    public SstvGalleryItem(SstvReceivedPicture picture, Bitmap bitmap, string caption)
    {
        Picture = picture;
        Bitmap = bitmap;
        Caption = caption;
        _savedPath = picture.SavedPath;
    }

    public SstvReceivedPicture Picture { get; }

    public Bitmap Bitmap { get; }

    public string Caption { get; }

    [ObservableProperty]
    private string? _savedPath;
}
