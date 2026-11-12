using System.Collections.ObjectModel;
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
    private readonly DispatcherTimer _uiTimer;
    private readonly SstvAutoStartCoordinator _autoStart = new();
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
        QsoLogbookEntryBridge logbook)
    {
        _settings = settings;
        _frequencyOverlay = frequencyOverlay;
        _tracker = tracker;
        _rig = rig;
        _l = localization;
        _receiver = receiver;
        _logbook = logbook;

        var s = _settings.Current.CwDecoder;
        _catDopplerFeedForward = s.CatDopplerFeedForward;
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
        _uiTimer.Start();
    }

    public void OnWindowClosed()
    {
        _uiTimer.Stop();
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
            _receiver.StartLive(_settings.Current.CwDecoder);
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

    private CwCopyWord CreateWord(string text, CwToken token) => token.Kind switch
    {
        CwTokenKind.Callsign => new CwCopyLink(text, token.Kind, token.Value, _l.Get("Cw.Logbook.CallTip", token.Value)),
        CwTokenKind.Locator => new CwCopyLink(text, token.Kind, token.Value, _l.Get("Cw.Logbook.GridTip", token.Value)),
        _ => new CwCopyWord(text),
    };

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

        foreach (var row in _receiver.TakeSpectrumRows())
            Spectrum = row;

        InputLevelPercent = Math.Clamp(_receiver.Audio.InputPeak * 100, 0, 100);
        UpdateTone();

        if (_receiver.CaptureHealth.IsReconnecting)
            StatusText = _l.Get("Cw.Status.CaptureRetrying");
        else if (_receiver.IsInputSilent)
            StatusText = _l.Get("Cw.Status.SilentInput", SelectedInputDevice?.DisplayName ?? _l.Get("Ft4.Audio.SystemDefault"));
        else
            StatusText = ListeningText();
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

        ToneOutOfBand = !CwToneFinder.IsInModelBand(tone, CwModelMetadata.DeepCw());
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

            if (update.CommittedText.Length > 0)
            {
                Copy.Append(update.CommittedText, DateTime.UtcNow);
                HasCopy = !Copy.IsEmpty;
                CopyAppended?.Invoke(this, EventArgs.Empty);
            }

            PendingText = update.Pending.Text;
        });

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
        _receiver.TextDecoded -= OnTextDecoded;
        _receiver.Faulted -= OnReceiverFaulted;
        _receiver.Stopped -= OnReceiverStopped;
        _receiver.Stop();
    }
}
