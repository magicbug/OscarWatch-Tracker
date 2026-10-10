using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Services;
using OscarWatch.Core.Sstv;
using OscarWatch.Localization;
using OscarWatch.Sstv;
using Serilog;

namespace OscarWatch.ViewModels;

/// <summary>OscarWatch SSTV: receive-only picture decoder window.</summary>
public partial class SstvViewModel : ViewModelBase, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SstvViewModel>();
    private const int MaxGalleryItems = 60;

    private readonly ISettingsService _settings;
    private readonly FrequencyOverlayViewModel _frequencyOverlay;
    private readonly ILiveTrackerSnapshotProvider _tracker;
    private readonly IRigController _rig;
    private readonly ILocalizationService _l;
    private readonly SstvReceiverService _receiver;
    private readonly SstvRecordingLoader _loader = new();
    private readonly DispatcherTimer _uiTimer;
    private WriteableBitmap? _liveA;
    private WriteableBitmap? _liveB;
    private bool _useA;
    private long _previewVersion;
    private long _spectrumVersion;
    private bool _loadingDevices;
    private bool _disposed;
    private string? _fileName;
    private int _filePictures;
    private string? _lastError;
    private readonly SstvAutoStartCoordinator _autoStart = new();
    private bool _startedByAuto;
    private DateTime _lastAutoStartCheckUtc = DateTime.MinValue;

    public SstvViewModel(
        ISettingsService settings,
        FrequencyOverlayViewModel frequencyOverlay,
        ILiveTrackerSnapshotProvider tracker,
        IRigController rig,
        ILocalizationService localization,
        SstvReceiverService receiver)
    {
        _settings = settings;
        _frequencyOverlay = frequencyOverlay;
        _tracker = tracker;
        _rig = rig;
        _l = localization;
        _receiver = receiver;

        var s = _settings.Current.Sstv;
        _autoSlant = s.AutoSlant;
        _slantTrimPpm = Math.Clamp(s.SlantTrimPpm, -20000, 20000);
        _autoTune = s.AutoTune;
        _catDopplerFeedForward = s.CatDopplerFeedForward;
        _detectWithoutVis = s.DetectWithoutVis;
        _autoSavePictures = s.AutoSavePictures;
        _saveSessionAudio = s.SaveSessionAudio;
        _autoStartEnabled = s.AutoStartEnabled;
        _autoStartElevationDeg = s.AutoStartElevationDeg;

        ModeOptions.Add(new SstvModeOption(null, _l.Get("Sstv.Mode.Auto")));
        foreach (var mode in SstvModeTable.All)
            ModeOptions.Add(new SstvModeOption(mode.Id, mode.Name));
        var forced = s.GetForcedMode();
        _selectedModeOption = ModeOptions.FirstOrDefault(o => o.Id == forced) ?? ModeOptions[0];

        _statusText = _l.Get("Sstv.Status.Idle");
        RefreshInputDevices();

        _receiver.SatelliteNameProvider = CurrentSatelliteName;
        _receiver.PictureReceived += OnPictureReceived;
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
    private bool _autoSavePictures;

    [ObservableProperty]
    private bool _saveSessionAudio;

    /// <summary>Start listening when the focused satellite rises. Only runs while this window is open.</summary>
    [ObservableProperty]
    private bool _autoStartEnabled;

    [ObservableProperty]
    private double _autoStartElevationDeg;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanDecodeRecording))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(ReSyncCommand), nameof(SkipPictureCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanDecodeRecording))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isBusyLoading;

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
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedPictureCommand))]
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

    public string PictureFolder => SstvPaths.GetDefaultDirectory();

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
            StatusText = _l.Get("Sstv.Status.NoAudio");
            return;
        }

        try
        {
            _lastError = null;
            SelectedGalleryItem = null;
            _receiver.StartLive(_settings.Current.Sstv, SaveSessionAudio);
            IsRunning = true;
            _startedByAuto = automatic;
            StatusText = ListeningText();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SSTV start failed");
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

        var path = SstvReceiverService.SavePicture(item.Picture.Decoded, item.Picture.ReceivedUtc, item.Picture.Satellite);
        if (path is null)
            return;

        item.SavedPath = path;
        StatusText = _l.Get("Sstv.Picture.Saved", path);
        SaveSelectedPictureCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(SaveButtonTip));
    }

    /// <summary>Decode a WAV or MP3 recording (called by the window after the file picker).</summary>
    public async Task DecodeRecordingAsync(string path)
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

            SelectedGalleryItem = null;
            _receiver.StartFile(loaded.Samples, loaded.SampleRate, _settings.Current.Sstv, Path.GetFileNameWithoutExtension(path));
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

    partial void OnAutoStartEnabledChanged(bool value)
    {
        _settings.Current.Sstv.AutoStartEnabled = value;
        _settings.RequestSave();
    }

    partial void OnAutoStartElevationDegChanged(double value)
    {
        if (!double.IsFinite(value))
            return;
        _settings.Current.Sstv.AutoStartElevationDeg = Math.Clamp(value, 0, 30);
        _settings.RequestSave();
    }

    partial void OnSelectedGalleryItemChanged(SstvGalleryItem? value)
    {
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
        StatusText = _lastError ?? _l.Get("Sstv.Status.Idle");
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

    private void RefreshUiTick()
    {
        UpdateDoppler();
        ProcessAutoStartTick();

        if (!IsRunning)
            return;

        if (_receiver.TryGetSpectrum(ref _spectrumVersion) is { } row)
            Spectrum = row;

        if (!_receiver.IsDecodingFile)
            InputLevelPercent = Math.Clamp(_receiver.Audio.InputPeak * 100, 0, 100);

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
        var startElevation = double.IsFinite(AutoStartElevationDeg)
            ? Math.Clamp(AutoStartElevationDeg, 0, 30)
            : 5;

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
                StatusText = _lastError;
            else if (wasFile)
                StatusText = _l.Get("Sstv.Status.FileDone", _fileName!, _filePictures);
            else
                StatusText = _l.Get("Sstv.Status.Idle");
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
        _receiver.Stop();
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
