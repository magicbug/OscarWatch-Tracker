using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.CwDecode;
using OscarWatch.Core.Services;
using OscarWatch.Core.Sstv;
using OscarWatch.Cw;
using OscarWatch.Help;
using OscarWatch.Localization;
using OscarWatch.Services;
using Serilog;

namespace OscarWatch.ViewModels;

/// <summary>OscarWatch CW: receive-only Morse decoder window using the DeepCW model.</summary>
public partial class CwViewModel : ViewModelBase, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<CwViewModel>();

    private readonly ISettingsService _settings;
    private readonly FrequencyOverlayViewModel _frequencyOverlay;
    private readonly ILiveTrackerSnapshotProvider _tracker;
    private readonly IRigController _rig;
    private readonly ILocalizationService _l;
    private readonly CwReceiverService _receiver;
    private readonly QsoLogbookEntryBridge _logbook;
    private readonly IQsoLogbookRepository _logbookRepository;
    /// <summary>Null until the logbook has been read; words are left unshaded until then.</summary>
    private IReadOnlySet<string>? _workedCalls;
    private IReadOnlySet<string>? _workedGridFields;
    private bool _watchingLogbook;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _waterfallTimer;
    private const int MaxWaterfallBacklog = 8;
    private readonly Queue<float[]> _waterfallBacklog = new();
    private double _waterfallCredit;
    private long _lastWaterfallTick = Stopwatch.GetTimestamp();
    private readonly SstvAutoStartCoordinator _autoStart = new();
    private static readonly CwModelMetadata CwModel = CwModelMetadata.DeepCw();
    /// <summary>Speed readings are averaged over a few steps, and dropped after a quiet spell.</summary>
    private const double SpeedAverageAlpha = 0.3;
    private static readonly TimeSpan SpeedHoldTime = TimeSpan.FromSeconds(8);
    private readonly CwNoiseSquelch _squelch = new();
    private double _wpm = double.NaN;
    private DateTime _lastSpeedUtc = DateTime.MinValue;
    private bool _loadingDevices;
    private bool _disposed;
    private string? _lastError;
    private bool _startedByAuto;
    private DateTime _lastAutoStartCheckUtc = DateTime.MinValue;
    /// <summary>While stopped, keep the idle or waiting line in step with automatic start.</summary>
    private bool _statusFollowsAutoStart = true;

    public CwViewModel(
        ISettingsService settings,
        FrequencyOverlayViewModel frequencyOverlay,
        ILiveTrackerSnapshotProvider tracker,
        IRigController rig,
        ILocalizationService localization,
        CwReceiverService receiver,
        QsoLogbookEntryBridge logbook,
        IQsoLogbookRepository logbookRepository)
    {
        _logbookRepository = logbookRepository;
        _settings = settings;
        _frequencyOverlay = frequencyOverlay;
        _tracker = tracker;
        _rig = rig;
        _l = localization;
        _receiver = receiver;
        _logbook = logbook;

        var s = _settings.Current.CwDecoder;
        _catDopplerFeedForward = s.CatDopplerFeedForward;
        _squelchNoise = s.SquelchNoise;
        _followNote = s.FollowNote;
        _copyFontSize = CwDecodeSettings.ClampCopyFontSize(s.CopyFontSize);
        _autoStartEnabled = s.AutoStartEnabled;
        _autoStartElevationDeg = SstvSettings.ClampAutoStartElevationDeg(s.AutoStartElevationDeg);

        Copy = new CwCopyLog(CreateWord);
        _statusText = _l.Get("Cw.Status.Idle");
        RefreshInputDevices();

        _receiver.TextDecoded += OnTextDecoded;
        _receiver.Faulted += OnReceiverFaulted;
        _receiver.Stopped += OnReceiverStopped;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) => RefreshUiTick();
        _waterfallTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _waterfallTimer.Tick += (_, _) => WaterfallTick();
    }

    /// <summary>Semi-bold for true: callsigns in the copy and a note outside the model band.</summary>
    public static readonly IValueConverter BoolToEmphasis =
        new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);

    public CwCopyLog Copy { get; }

    public ObservableCollection<CwCopyLine> Lines => Copy.Lines;

    public ObservableCollection<Ft4AudioDeviceOption> InputDeviceOptions { get; } = [];

    /// <summary>Raised on the UI thread after settled copy is added, so the window can follow it.</summary>
    public event EventHandler? CopyAppended;

    [ObservableProperty]
    private Ft4AudioDeviceOption? _selectedInputDevice;

    [ObservableProperty]
    private bool _catDopplerFeedForward;

    /// <summary>Start listening when the focused satellite rises and the frequency panel is on CW.</summary>
    [ObservableProperty]
    private bool _autoStartEnabled;

    [ObservableProperty]
    private double _autoStartElevationDeg;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCopyCommand), nameof(ClearCommand))]
    private bool _hasCopy;

    [ObservableProperty]
    private string _statusText;

    /// <summary>Letters the decoder may still revise, shown dimmed after the settled copy.</summary>
    [ObservableProperty]
    private string _pendingText = "";

    [ObservableProperty]
    private string _toneText = "";

    /// <summary>True when the strongest note is outside the band the model reads.</summary>
    [ObservableProperty]
    private bool _toneOutOfBand;

    [ObservableProperty]
    private string _dopplerText = "";

    [ObservableProperty]
    private string _logbookText = "";

    [ObservableProperty]
    private double _inputLevelPercent;

    /// <summary>Strongest steady note for the waterfall marker, or NaN.</summary>
    [ObservableProperty]
    private double _toneHz = double.NaN;

    /// <summary>Latest waterfall row; each new array adds a row.</summary>
    [ObservableProperty]
    private float[]? _spectrum;

    /// <summary>Hide one- and two-letter words the model is unsure of.</summary>
    [ObservableProperty]
    private bool _squelchNoise;

    /// <summary>Keep the steady note centred in the decode band as it drifts.</summary>
    [ObservableProperty]
    private bool _followNote;

    /// <summary>Text size of the decoded copy, in pixels.</summary>
    [ObservableProperty]
    private double _copyFontSize;

    public double MinCopyFontSize => CwDecodeSettings.MinCopyFontSize;
    public double MaxCopyFontSize => CwDecodeSettings.MaxCopyFontSize;

    /// <summary>
    /// How far the decode band sits from its home of 400 to 1200 Hz on the waterfall. The waterfall
    /// already shows audio after the rig control correction, so this is the operator's tuning only.
    /// </summary>
    [ObservableProperty]
    private double _bandOffsetHz;

    /// <summary>Measured sending speed, or empty when none is heard.</summary>
    [ObservableProperty]
    private string _speedText = "";

    public bool CanStart => !IsRunning;

    public string LagText => _l.Get("Cw.Lag");

    public (double Width, double Height)? SavedWindowSize
    {
        get
        {
            var s = _settings.Current.CwDecoder;
            return s.WindowWidth is { } w && s.WindowHeight is { } h ? (w, h) : null;
        }
        set
        {
            if (value is not { } size || !double.IsFinite(size.Width) || !double.IsFinite(size.Height))
                return;
            _settings.Current.CwDecoder.WindowWidth = (int)Math.Round(size.Width);
            _settings.Current.CwDecoder.WindowHeight = (int)Math.Round(size.Height);
        }
    }

    public void OnWindowOpened()
    {
        // Opening the window mid-pass should listen straight away if the satellite is already up.
        _autoStart.ResetTracking();
        _lastAutoStartCheckUtc = DateTime.MinValue;
        UpdateOwnStation();
        ShowStoppedStatus();
        if (!_watchingLogbook)
        {
            _watchingLogbook = true;
            _logbookRepository.QsosChanged += OnLogbookQsosChanged;
        }

        _ = RefreshWorkedAsync();
        _uiTimer.Start();
        _waterfallTimer.Start();
    }

    public void OnWindowClosed()
    {
        _uiTimer.Stop();
        _waterfallTimer.Stop();
        StopReceiver();
        _settings.RequestSave();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start() => StartSession(automatic: false);

    private void StartSession(bool automatic)
    {
        if (!_receiver.Audio.IsAvailable)
        {
            _statusFollowsAutoStart = false;
            StatusText = _l.Get("Cw.Status.NoAudio");
            return;
        }

        try
        {
            _lastError = null;
            UpdateOwnStation();
            _squelch.Flush();
            ResetSpeed();
            _receiver.StartLive(_settings.Current.CwDecoder);
            BandOffsetHz = _receiver.TuneOffsetHz;
            IsRunning = true;
            _startedByAuto = automatic;
            _statusFollowsAutoStart = true;
            StatusText = ListeningText();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CW start failed");
            _statusFollowsAutoStart = false;
            StatusText = _l.Get("Cw.Status.Error", ex.Message);
            IsRunning = false;
            _startedByAuto = false;
        }
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => StopReceiver();

    private bool CanClear() => HasCopy || PendingText.Length > 0;

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear()
    {
        Copy.Clear();
        PendingText = "";
        HasCopy = false;
        LogbookText = "";
        _squelch.Flush();
        if (IsRunning)
            _receiver.ResetDecoder();
    }

    private bool CanSaveCopy() => HasCopy;

    /// <summary>Asks the window for a file name; the window calls <see cref="SaveCopyTo"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveCopy))]
    private Task SaveCopy() => SaveCopyRequested?.Invoke() ?? Task.CompletedTask;

    /// <summary>Set by the window: shows the save picker and writes the copy.</summary>
    public Func<Task>? SaveCopyRequested { get; set; }

    public string SuggestedCopyFileName =>
        $"OscarWatch-CW-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}Z.txt";

    public async Task SaveCopyTo(Stream stream, string displayPath)
    {
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(Copy.ToText()).ConfigureAwait(true);
        StatusText = _l.Get("Cw.Saved", displayPath);
        _statusFollowsAutoStart = false;
    }

    [RelayCommand]
    private void OpenHelp() => HelpLauncher.TryOpenHelp("cw");

    [RelayCommand]
    private void RefreshInputDevices()
    {
        _loadingDevices = true;
        try
        {
            var saved = _settings.Current.CwDecoder;
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

    /// <summary>A callsign or locator in the copy was clicked (or activated from the keyboard).</summary>
    public void SendToLogbook(CwCopyLink link)
    {
        var result = link.IsCallsign
            ? _logbook.Fill(link.Value, null)
            : _logbook.Fill(null, link.Value);
        LogbookText = result switch
        {
            QsoLogbookFillResult.Filled => _l.Get(link.IsCallsign ? "Cw.Logbook.FilledCall" : "Cw.Logbook.FilledGrid", link.Value),
            QsoLogbookFillResult.OpeningLogbook => _l.Get("Cw.Logbook.Opening", link.Value),
            _ => _l.Get("Cw.Logbook.Editing"),
        };
    }

    partial void OnSelectedInputDeviceChanged(Ft4AudioDeviceOption? value)
    {
        if (_loadingDevices || value is null)
            return;
        _settings.Current.CwDecoder.InputDeviceId = value.Id;
        _settings.Current.CwDecoder.InputDeviceDisplayName = value.DisplayName;
        _settings.RequestSave();
        if (IsRunning)
        {
            StopReceiver();
            Start();
        }
    }

    partial void OnCatDopplerFeedForwardChanged(bool value)
    {
        _settings.Current.CwDecoder.CatDopplerFeedForward = value;
        _settings.RequestSave();
    }

    partial void OnSquelchNoiseChanged(bool value)
    {
        _settings.Current.CwDecoder.SquelchNoise = value;
        _settings.RequestSave();
        _squelch.Flush();
    }

    partial void OnCopyFontSizeChanged(double value)
    {
        var size = CwDecodeSettings.ClampCopyFontSize(value);
        if (size != value)
        {
            CopyFontSize = size;
            return;
        }

        _settings.Current.CwDecoder.CopyFontSize = size;
        _settings.RequestSave();
    }

    partial void OnFollowNoteChanged(bool value)
    {
        _settings.Current.CwDecoder.FollowNote = value;
        _settings.RequestSave();
        _receiver.FollowNote = value;
    }

    /// <summary>Decode the note at <paramref name="hz"/> on the waterfall (a click on its trace).</summary>
    public void TuneTo(double hz)
    {
        if (!IsRunning || !double.IsFinite(hz))
            return;
        var centre = (CwModel.SpectrogramMinFreqHz + CwModel.SpectrogramMaxFreqHz) / 2;
        _receiver.TuneOffsetHz = CwTuner.ClampOffset(hz - centre);
        BandOffsetHz = _receiver.TuneOffsetHz;
    }

    /// <summary>Put the decode band back to 400 to 1200 Hz.</summary>
    public void ResetTuning()
    {
        _receiver.TuneOffsetHz = 0;
        BandOffsetHz = _receiver.TuneOffsetHz;
    }

    partial void OnAutoStartEnabledChanged(bool value)
    {
        _settings.Current.CwDecoder.AutoStartEnabled = value;
        _settings.RequestSave();
    }

    partial void OnAutoStartElevationDegChanged(double value)
    {
        if (!double.IsFinite(value))
            return;
        _settings.Current.CwDecoder.AutoStartElevationDeg = SstvSettings.ClampAutoStartElevationDeg(value);
        _settings.RequestSave();
    }

    partial void OnPendingTextChanged(string value) => ClearCommand.NotifyCanExecuteChanged();

    private CwCopyWord CreateWord(string text, CwToken token)
    {
        switch (token.Kind)
        {
            case CwTokenKind.Callsign:
            case CwTokenKind.Locator:
                var link = new CwCopyLink(text, token.Kind, token.Value, "");
                Mark(link);
                return link;
            case CwTokenKind.OwnCallsign:
                return new CwCopyWord(text, CwCopyHighlight.You);
            default:
                return new CwCopyWord(text);
        }
    }

    /// <summary>Shade a callsign or locator as new or worked before, and say so in its tip.</summary>
    private void Mark(CwCopyLink link)
    {
        if (link.IsCallsign)
        {
            var status = CwWorkedLookup.ForCall(link.Value, _workedCalls);
            link.Highlight = status switch
            {
                CwWorkedStatus.New => CwCopyHighlight.NewCall,
                CwWorkedStatus.Worked => CwCopyHighlight.WorkedCall,
                _ => CwCopyHighlight.None,
            };
            link.Tip = TipWithStatus(_l.Get("Cw.Logbook.CallTip", link.Value), status, "Cw.Worked.NewCall", "Cw.Worked.Call");
        }
        else
        {
            var status = CwWorkedLookup.ForLocator(link.Value, _workedGridFields);
            link.Highlight = status switch
            {
                CwWorkedStatus.New => CwCopyHighlight.NewGrid,
                CwWorkedStatus.Worked => CwCopyHighlight.WorkedGrid,
                _ => CwCopyHighlight.None,
            };
            link.Tip = TipWithStatus(_l.Get("Cw.Logbook.GridTip", link.Value), status, "Cw.Worked.NewGrid", "Cw.Worked.Grid");
        }
    }

    private string TipWithStatus(string action, CwWorkedStatus status, string newKey, string workedKey) => status switch
    {
        CwWorkedStatus.New => _l.Get(newKey) + Environment.NewLine + action,
        CwWorkedStatus.Worked => _l.Get(workedKey) + Environment.NewLine + action,
        _ => action,
    };

    /// <summary>Read the worked calls and grid fields from every logbook, then re-mark the copy.</summary>
    private async Task RefreshWorkedAsync()
    {
        try
        {
            var (calls, grids) = await _logbookRepository.LoadWorkedCallAndGridFieldsAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed)
                    return;
                _workedCalls = calls;
                _workedGridFields = grids;
                foreach (var link in Copy.AllWords.OfType<CwCopyLink>())
                    Mark(link);
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CW worked-call refresh failed");
        }
    }

    private void OnLogbookQsosChanged(long logbookId) => _ = RefreshWorkedAsync();

    private void UpdateOwnStation()
    {
        var station = _settings.Current.GroundStation;
        Copy.OwnCallsign = station?.Callsign;
        Copy.OwnLocator = station?.GridSquare;
    }

    /// <summary>True when the frequency panel is set up for CW, so automatic start may listen.</summary>
    private bool IsFrequencyPanelOnCw() =>
        _frequencyOverlay.IsCwOperatingStyleSelected
        || (_frequencyOverlay.SelectedMode?.DownlinkMode?.Trim().StartsWith("CW", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Mode the radio receives in: CW when the panel sends CW and moves the downlink with it.</summary>
    private string? ReceiveMode()
    {
        var downlink = _frequencyOverlay.SelectedMode?.DownlinkMode;
        return _frequencyOverlay.IsCwOperatingStyleSelected && _settings.Current.Rig?.CwKeepSidebandDownlink != true
            ? "CW"
            : downlink;
    }

    private void StopReceiver()
    {
        _receiver.Stop();
        IsRunning = false;
        _startedByAuto = false;
        InputLevelPercent = 0;
        ToneHz = double.NaN;
        ToneText = "";
        ToneOutOfBand = false;
        BandOffsetHz = 0;
        ResetSpeed();
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
        return sat is null ? _l.Get("Cw.Status.Listening") : _l.Get("Cw.Status.ListeningSat", sat);
    }

    private void ShowStoppedStatus()
    {
        _statusFollowsAutoStart = true;
        StatusText = StoppedStatusText();
    }

    private string StoppedStatusText()
    {
        if (!AutoStartEnabled)
            return _l.Get("Cw.Status.Idle");

        var snapshot = _tracker.GetCurrent();
        var name = string.IsNullOrWhiteSpace(snapshot.SatelliteName) ? null : snapshot.SatelliteName.Trim();
        if (name is null)
            return _l.Get("Cw.Status.WaitingNoSatellite");

        if (!IsFrequencyPanelOnCw())
            return _l.Get("Cw.Status.WaitingNotCw", name);

        if (!_autoStart.IsArmed)
            return _l.Get("Cw.Status.WaitingNextPass", name);

        var startElevation = SstvSettings.ClampAutoStartElevationDeg(AutoStartElevationDeg);
        if (snapshot.ElevationDeg is not { } elevation || !double.IsFinite(elevation))
            return _l.Get("Cw.Status.WaitingNoElevation", name);

        return _l.Get("Cw.Status.Waiting", name, FormatElevation(startElevation));
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

        InputLevelPercent = Math.Clamp(_receiver.Audio.InputPeak * 100, 0, 100);
        UpdateTone();
        if (SpeedText.Length > 0 && DateTime.UtcNow - _lastSpeedUtc > SpeedHoldTime)
            ResetSpeed();

        if (_receiver.CaptureHealth.IsReconnecting)
            StatusText = _l.Get("Cw.Status.CaptureRetrying");
        else if (_receiver.IsInputSilent)
            StatusText = _l.Get("Cw.Status.SilentInput", SelectedInputDevice?.DisplayName ?? _l.Get("Ft4.Audio.SystemDefault"));
        else
            StatusText = ListeningText();
    }

    /// <summary>
    /// Hands waterfall rows to the window at the rate they were made, rather than in the bursts
    /// they arrive in, so the picture scrolls evenly. A backlog beyond a few rows is caught up.
    /// </summary>
    private void WaterfallTick()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastWaterfallTick, now).TotalSeconds;
        _lastWaterfallTick = now;
        if (!IsRunning)
        {
            _waterfallBacklog.Clear();
            _waterfallCredit = 0;
            return;
        }

        BandOffsetHz = _receiver.TuneOffsetHz;
        foreach (var row in _receiver.TakeSpectrumRows())
            _waterfallBacklog.Enqueue(row);

        _waterfallCredit = Math.Min(_waterfallCredit + elapsed * CwReceiverService.SpectrumRowsPerSecond, 4);
        var due = (int)_waterfallCredit;
        if (_waterfallBacklog.Count > MaxWaterfallBacklog)
            due = Math.Max(due, _waterfallBacklog.Count - MaxWaterfallBacklog / 2);

        while (due > 0 && _waterfallBacklog.TryDequeue(out var next))
        {
            Spectrum = next;
            due--;
            _waterfallCredit = Math.Max(0, _waterfallCredit - 1);
        }

        if (_waterfallBacklog.Count == 0)
            _waterfallCredit = Math.Min(_waterfallCredit, 1);
    }

    private void UpdateTone()
    {
        var tone = _receiver.ToneHz;
        ToneHz = tone;
        if (!double.IsFinite(tone))
        {
            ToneText = _l.Get("Cw.Tone.None");
            ToneOutOfBand = false;
            return;
        }

        ToneOutOfBand = !CwToneFinder.IsInModelBand(tone - BandOffsetHz, CwModel);
        ToneText = ToneOutOfBand ? _l.Get("Cw.Tone.OutOfBand", tone) : _l.Get("Cw.Tone", tone);
    }

    /// <summary>
    /// Automatic start and stop for the focused satellite, only while the frequency panel is on CW.
    /// Runs from the window's UI timer, so it only operates while the CW window is open.
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
            Enabled: AutoStartEnabled && IsFrequencyPanelOnCw(),
            FocusedNoradId: _tracker.FocusedNoradId,
            ElevationDeg: elevation,
            StartElevationDeg: startElevation,
            ReceiverRunning: IsRunning,
            ReceiverAutoStarted: _startedByAuto));

        switch (action)
        {
            case SstvAutoStartAction.Start when CanStart:
                Log.Information("CW auto-start: focused satellite above {StartElevationDeg} deg", startElevation);
                StartSession(automatic: true);
                break;
            case SstvAutoStartAction.Stop:
                Log.Information("CW auto-stop: focused satellite set or the frequency panel left CW");
                StopReceiver();
                break;
        }
    }

    private void UpdateDoppler()
    {
        var doppler = _frequencyOverlay.DownlinkDopplerKHz;
        var mode = ReceiveMode();
        double feedForward = 0;
        if (CatDopplerFeedForward && IsRunning)
        {
            var rig = _rig.GetStatus();
            if (rig.IsConnected && rig.IsTracking)
                feedForward = CwDopplerFeedForward.AudioOffsetHz(mode, _frequencyOverlay.RadioReceiveKHz, rig.LastReceiveHz);
        }

        _receiver.FeedForwardHz = feedForward;
        if (doppler is not { } kHz)
        {
            DopplerText = "";
            return;
        }

        var text = _l.Get("Cw.Doppler", $"{kHz:+0.000;-0.000;0.000} kHz");
        if (CatDopplerFeedForward && IsRunning && feedForward != 0)
            text += "  " + _l.Get("Cw.FeedForward", feedForward);
        DopplerText = text;
    }

    private void OnTextDecoded(object? sender, CwDecodeUpdate update) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
                return;

            var text = SquelchNoise
                ? _squelch.Process(update.CommittedText, update.CommittedConfidence)
                : update.CommittedText;
            if (text.Length > 0)
            {
                Copy.Append(text, DateTime.UtcNow);
                HasCopy = !Copy.IsEmpty;
                CopyAppended?.Invoke(this, EventArgs.Empty);
            }

            PendingText = SquelchNoise && CwNoiseSquelch.IsLikelyNoise(update.Pending) ? "" : update.Pending.Text;
            if (update.Wpm is { } wpm)
            {
                _wpm = double.IsFinite(_wpm) ? _wpm + SpeedAverageAlpha * (wpm - _wpm) : wpm;
                _lastSpeedUtc = DateTime.UtcNow;
                SpeedText = _l.Get("Cw.Speed", Math.Round(_wpm));
            }
        });

    private void ResetSpeed()
    {
        _wpm = double.NaN;
        _lastSpeedUtc = DateTime.MinValue;
        SpeedText = "";
    }

    private void OnReceiverFaulted(object? sender, Exception ex) =>
        Dispatcher.UIThread.Post(() => _lastError = _l.Get("Cw.Status.Error", ex.Message));

    private void OnReceiverStopped(object? sender, int session) =>
        Dispatcher.UIThread.Post(() =>
        {
            // A stop from an earlier session must not end the one now running.
            if (session != _receiver.SessionId || _disposed)
                return;

            PendingText = "";
            if (!IsRunning)
                return;
            StopReceiver();
        });

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _uiTimer.Stop();
        _waterfallTimer.Stop();
        if (_watchingLogbook)
            _logbookRepository.QsosChanged -= OnLogbookQsosChanged;
        _receiver.TextDecoded -= OnTextDecoded;
        _receiver.Faulted -= OnReceiverFaulted;
        _receiver.Stopped -= OnReceiverStopped;
        _receiver.Stop();
    }
}
