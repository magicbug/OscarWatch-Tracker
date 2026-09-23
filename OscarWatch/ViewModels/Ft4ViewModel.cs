using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Controls;
using OscarWatch.Core.Ft4;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Services;
using OscarWatch.Ft4;
using OscarWatch.Localization;
using OscarWatch.Rotator;
using Serilog;

namespace OscarWatch.ViewModels;

/// <summary>FT4 modem window: decode list, TX controls, and Doppler status.</summary>
public partial class Ft4ViewModel : ViewModelBase, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<Ft4ViewModel>();

    private readonly ISettingsService _settings;
    private readonly FrequencyOverlayViewModel _frequencyOverlay;
    private readonly ILiveTrackerSnapshotProvider _tracker;
    private readonly ILocalizationService _l;
    private readonly Ft4ModemService _modem;
    private readonly DispatcherTimer _uiTimer;
    private bool _disposed;
    private bool _loadingDevices;
    private bool _loadingEchoCalibration;

    public Ft4ViewModel(
        ISettingsService settings,
        FrequencyOverlayViewModel frequencyOverlay,
        ILiveTrackerSnapshotProvider tracker,
        ILocalizationService localization,
        Ft4ModemService modem)
    {
        _settings = settings;
        _frequencyOverlay = frequencyOverlay;
        _tracker = tracker;
        _l = localization;
        _modem = modem;

        PttMethodOptions =
        [
            new Ft4PttMethodOption(Ft4PttMethod.Vox, _l.Get("Ft4.Ptt.Vox")),
            new Ft4PttMethodOption(Ft4PttMethod.Cat, _l.Get("Ft4.Ptt.Cat")),
            new Ft4PttMethodOption(Ft4PttMethod.CatPortHandshake, _l.Get("Ft4.Ptt.CatPortHandshake")),
            new Ft4PttMethodOption(Ft4PttMethod.SeparateComPort, _l.Get("Ft4.Ptt.SeparateComPort")),
            new Ft4PttMethodOption(Ft4PttMethod.Manual, _l.Get("Ft4.Ptt.Manual")),
        ];

        PttLineOptions =
        [
            new Ft4PttLineOption(Ft4PttLine.Rts, _l.Get("Ft4.Ptt.Rts")),
            new Ft4PttLineOption(Ft4PttLine.Dtr, _l.Get("Ft4.Ptt.Dtr")),
        ];

        var ft4 = _settings.Current.Ft4;
        _skipRrr = ft4.SkipRrr;
        _txAudioHz = Math.Clamp(ft4.TxAudioHz, 200, 3000);
        _rxAudioHz = _txAudioHz;
        _txLevel = Math.Clamp(ft4.TxLevel, 0.05, 1.0);
        _holdTxFrequency = ft4.HoldTxFrequency;
        _audioDopplerTx = ft4.AudioDopplerTx;
        _audioDopplerRx = ft4.AudioDopplerRx;
        _pttLeadMs = Math.Clamp(ft4.PttLeadMs, 0, 2000);
        _pttTailMs = Math.Clamp(ft4.PttTailMs, 0, 2000);
        _decodeFontSize = Math.Clamp(ft4.DecodeFontSize, 10, 28);
        _preferEvenSlot = false;
        _pttInvert = ft4.PttInvert;
        _selectedPttMethod = PttMethodOptions.FirstOrDefault(o => o.Value == ft4.PttMethod)
            ?? PttMethodOptions[0];
        _selectedPttLine = PttLineOptions.FirstOrDefault(o => o.Value == ft4.PttLine)
            ?? PttLineOptions[0];
        _separatePttPort = ft4.SeparatePttPort ?? "";

        StatusLine = _l.Get("Ft4.Status.Idle");
        WaterfallStatusText = _l.Get("Ft4.Waterfall.Unavailable");
        SlotClockText = "-";
        DopplerUplinkText = "-";
        DopplerDownlinkText = "-";

        RefreshAudioDevices();
        RefreshPttPorts();

        _modem.SetManualPromptHandler(SetManualPttPrompt);
        _modem.Changed += OnModemChanged;
        ((INotifyCollectionChanged)_modem.Decodes).CollectionChanged += OnDecodesChanged;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) => RefreshUiTick();
        SlotPeriodLabel = _l.Get("Ft4.Slot.Rx");
        SlotProgressText = "0%";
    }

    public ObservableCollection<Ft4DecodedMessage> Decodes { get; } = [];

    public ObservableCollection<Ft4AudioDeviceOption> InputDeviceOptions { get; } = [];

    public ObservableCollection<Ft4AudioDeviceOption> OutputDeviceOptions { get; } = [];

    public ObservableCollection<string> PttPortOptions { get; } = [];

    public ObservableCollection<string> EchoCalibrationSatelliteOptions { get; } = [];

    public IReadOnlyList<Ft4PttMethodOption> PttMethodOptions { get; }

    public IReadOnlyList<Ft4PttLineOption> PttLineOptions { get; }

    public string StationCallsign =>
        Ft4MessageCodec.NormalizeCall(_settings.Current.GroundStation.Callsign ?? "");

    public string StationGrid =>
        (_settings.Current.GroundStation.GridSquare ?? "").Trim().ToUpperInvariant();

    /// <summary>Example CQ using Settings → Station (not the old CALL/GRID placeholders).</summary>
    public string TxMessageWatermark
    {
        get
        {
            var call = StationCallsign;
            var grid = StationGrid;
            if (call.Length == 0 || grid.Length < 4)
                return _l.Get("Ft4.TxMessage.Watermark");
            return Ft4MessageCodec.BuildCq(call, grid[..4]);
        }
    }

    public bool ShowHandshakePttOptions =>
        SelectedPttMethod?.Value is Ft4PttMethod.CatPortHandshake or Ft4PttMethod.SeparateComPort;

    public bool ShowSeparatePttPort =>
        SelectedPttMethod?.Value == Ft4PttMethod.SeparateComPort;

    public bool ShowSeparatePttConflict =>
        ShowSeparatePttPort && !string.IsNullOrWhiteSpace(SeparatePttConflictText);

    public bool HasDoppler =>
        !string.IsNullOrWhiteSpace(DopplerUplinkText) && DopplerUplinkText != "-"
        || !string.IsNullOrWhiteSpace(DopplerDownlinkText) && DopplerDownlinkText != "-";

    public bool HasEchoCalibrationSatellite =>
        !string.IsNullOrWhiteSpace(SelectedEchoCalibrationSatellite);

    public bool ShowClockWarning => !string.IsNullOrWhiteSpace(ClockWarningText);

    [ObservableProperty] private string _waterfallStatusText = "";
    [ObservableProperty] private string _slotClockText = "";
    [ObservableProperty] private string _slotPeriodLabel = "";
    [ObservableProperty] private string _slotProgressText = "";
    [ObservableProperty] private double _slotProgressPercent;
    [ObservableProperty] private bool _isTxSlot;
    [ObservableProperty] private string _dopplerUplinkText = "";
    [ObservableProperty] private string _dopplerDownlinkText = "";
    [ObservableProperty] private string _currentTxMessage = "";
    [ObservableProperty] private string _manualPttPrompt = "";
    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private string _clockWarningText = "";
    [ObservableProperty] private string _separatePttConflictText = "";
    [ObservableProperty] private double _txPlaybackPeakPercent;
    [ObservableProperty] private bool _skipRrr;
    [ObservableProperty] private bool _preferEvenSlot;
    [ObservableProperty] private bool _holdTxFrequency = true;
    [ObservableProperty] private bool _audioDopplerTx = true;
    [ObservableProperty] private bool _audioDopplerRx = true;
    [ObservableProperty] private int _pttLeadMs = 200;
    [ObservableProperty] private int _pttTailMs = 100;
    [ObservableProperty] private double _decodeFontSize = 12;
    [ObservableProperty] private string? _selectedEchoCalibrationSatellite;
    [ObservableProperty] private double _echoCalibrationHz;
    [ObservableProperty] private double _txAudioHz = 1500;
    [ObservableProperty] private double _rxAudioHz = 1500;
    [ObservableProperty] private double _txLevel = 0.35;
    [ObservableProperty] private bool _txEnabled;
    [ObservableProperty] private bool _pttInvert;
    [ObservableProperty] private string _separatePttPort = "";
    [ObservableProperty] private Ft4PttMethodOption? _selectedPttMethod;
    [ObservableProperty] private Ft4PttLineOption? _selectedPttLine;
    [ObservableProperty] private Ft4AudioDeviceOption? _selectedInputDevice;
    [ObservableProperty] private Ft4AudioDeviceOption? _selectedOutputDevice;
    [ObservableProperty] private Ft4DecodedMessage? _selectedDecode;
    [ObservableProperty] private float[]? _spectrumBins;

    partial void OnSkipRrrChanged(bool value)
    {
        _settings.Current.Ft4.SkipRrr = value;
        _settings.RequestSave();
    }

    partial void OnHoldTxFrequencyChanged(bool value)
    {
        _settings.Current.Ft4.HoldTxFrequency = value;
        _settings.RequestSave();

        // WSJT-X: without Hold Tx, TX follows RX. Snap them together when Hold is cleared.
        if (!value)
            TxAudioHz = RxAudioHz;
    }

    partial void OnAudioDopplerTxChanged(bool value)
    {
        _settings.Current.Ft4.AudioDopplerTx = value;
        _settings.RequestSave();
    }

    partial void OnAudioDopplerRxChanged(bool value)
    {
        _settings.Current.Ft4.AudioDopplerRx = value;
        _settings.RequestSave();
    }

    partial void OnPttLeadMsChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, 2000);
        if (clamped != value)
        {
            PttLeadMs = clamped;
            return;
        }

        _settings.Current.Ft4.PttLeadMs = clamped;
        _settings.RequestSave();
    }

    partial void OnPttTailMsChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, 2000);
        if (clamped != value)
        {
            PttTailMs = clamped;
            return;
        }

        _settings.Current.Ft4.PttTailMs = clamped;
        _settings.RequestSave();
    }

    partial void OnDecodeFontSizeChanged(double value)
    {
        var clamped = Math.Clamp(value, 10, 28);
        if (Math.Abs(clamped - value) > 0.01)
        {
            DecodeFontSize = clamped;
            return;
        }

        _settings.Current.Ft4.DecodeFontSize = clamped;
        _settings.RequestSave();
    }

    partial void OnSelectedEchoCalibrationSatelliteChanged(string? value)
    {
        if (_loadingEchoCalibration)
            return;
        LoadEchoCalibrationHzForSelected();
        OnPropertyChanged(nameof(HasEchoCalibrationSatellite));
        ClearEchoCalibrationCommand.NotifyCanExecuteChanged();
    }

    partial void OnEchoCalibrationHzChanged(double value)
    {
        if (_loadingEchoCalibration)
            return;

        var sat = SelectedEchoCalibrationSatellite?.Trim();
        if (string.IsNullOrEmpty(sat))
            return;

        var clamped = Math.Clamp(value, -20000, 20000);
        if (Math.Abs(clamped - value) > 0.01)
        {
            EchoCalibrationHz = clamped;
            return;
        }

        _settings.Current.Ft4.SetUplinkCalibrationKHz(sat, clamped / 1000.0);
        _settings.RequestSave();
        ClearEchoCalibrationCommand.NotifyCanExecuteChanged();
        ClearAllEchoCalibrationsCommand.NotifyCanExecuteChanged();
    }

    partial void OnTxAudioHzChanged(double value)
    {
        var clamped = Math.Clamp(value, 200, 3000);
        if (Math.Abs(clamped - value) > 0.01)
        {
            TxAudioHz = clamped;
            return;
        }

        _settings.Current.Ft4.TxAudioHz = clamped;
        if (_modem.Sequencer is not null)
            _modem.Sequencer.TxAudioHz = clamped;
        _settings.RequestSave();

        // Without Hold Tx, keep RX locked to TX (WSJT-X behaviour).
        if (!_settings.Current.Ft4.HoldTxFrequency)
            RxAudioHz = clamped;
    }

    private void RefreshRxMarker()
    {
        foreach (var d in Decodes)
        {
            if (!d.IsReceiveActivity)
                continue;
            RxAudioHz = Math.Clamp(d.FreqHz, 200, 3000);
            return;
        }

        // Keep an operator-chosen RX offset when Hold Tx is on; otherwise lock RX to TX.
        if (!_settings.Current.Ft4.HoldTxFrequency)
            RxAudioHz = TxAudioHz;
    }

    partial void OnTxLevelChanged(double value)
    {
        var clamped = Math.Clamp(value, 0.05, 1.0);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            TxLevel = clamped;
            return;
        }

        _settings.Current.Ft4.TxLevel = clamped;
        _settings.RequestSave();
    }

    partial void OnPreferEvenSlotChanged(bool value)
    {
        if (_modem.Sequencer is not null)
            _modem.Sequencer.PreferEvenSlot = value;
    }

    partial void OnSelectedPttMethodChanged(Ft4PttMethodOption? value)
    {
        if (value is null)
            return;
        _settings.Current.Ft4.PttMethod = value.Value;
        _settings.RequestSave();
        OnPropertyChanged(nameof(ShowHandshakePttOptions));
        OnPropertyChanged(nameof(ShowSeparatePttPort));
        RefreshSeparatePttConflict();
    }

    partial void OnSelectedPttLineChanged(Ft4PttLineOption? value)
    {
        if (value is null)
            return;
        _settings.Current.Ft4.PttLine = value.Value;
        _settings.RequestSave();
    }

    partial void OnPttInvertChanged(bool value)
    {
        _settings.Current.Ft4.PttInvert = value;
        _settings.RequestSave();
    }

    partial void OnSeparatePttPortChanged(string value)
    {
        _settings.Current.Ft4.SeparatePttPort = value?.Trim() ?? "";
        _settings.RequestSave();
        RefreshSeparatePttConflict();
    }

    partial void OnClockWarningTextChanged(string value) =>
        OnPropertyChanged(nameof(ShowClockWarning));

    partial void OnSeparatePttConflictTextChanged(string value) =>
        OnPropertyChanged(nameof(ShowSeparatePttConflict));

    private void RefreshSeparatePttConflict()
    {
        if (!ShowSeparatePttPort)
        {
            SeparatePttConflictText = "";
            return;
        }

        if (SerialPortConflictHelper.TryDescribeFt4SeparatePttConflict(
                SeparatePttPort,
                _settings.Current.Rig,
                _settings.Current.Rotator,
                _settings.Current.Gps,
                out var message))
        {
            SeparatePttConflictText = ComPortConflictLocalizer.Localize(message, _l);
            return;
        }

        SeparatePttConflictText = "";
    }

    partial void OnSelectedInputDeviceChanged(Ft4AudioDeviceOption? value)
    {
        if (_loadingDevices || value is null)
            return;
        _settings.Current.Ft4.InputDeviceId = value.Id;
        _settings.Current.Ft4.InputDeviceDisplayName = value.DisplayName;
        _settings.RequestSave();
        _modem.RestartCaptureFromSettings();
    }

    partial void OnSelectedOutputDeviceChanged(Ft4AudioDeviceOption? value)
    {
        if (_loadingDevices || value is null)
            return;
        _settings.Current.Ft4.OutputDeviceId = value.Id;
        _settings.Current.Ft4.OutputDeviceDisplayName = value.DisplayName;
        _settings.RequestSave();
        _modem.RestartOutputFromSettings();
    }

    partial void OnSelectedDecodeChanged(Ft4DecodedMessage? value)
    {
        if (value is null)
            return;
        AnswerDecode(value);
    }

    public Task OnWindowOpenedAsync()
    {
        RefreshAudioDevices();
        RefreshPttPorts();
        _uiTimer.Start();
        RefreshUiTick();
        if (!_modem.IsRunning)
            StartSession();
        return Task.CompletedTask;
    }

    public Task OnWindowClosedAsync()
    {
        _uiTimer.Stop();
        return Task.CompletedTask;
    }

    public void StartSession()
    {
        if (!_modem.NativeAvailable)
        {
            StatusLine = _l.Get("Ft4.NativeUnavailable");
            WaterfallStatusText = _l.Get("Ft4.Waterfall.Unavailable");
            Log.Warning("FT4 native library unavailable");
            return;
        }

        if (StationCallsign.Length == 0 || StationGrid.Length < 4)
        {
            StatusLine = _l.Get("Ft4.Status.NeedStation");
            return;
        }

        _modem.Start();
        StatusLine = string.IsNullOrWhiteSpace(_modem.Status)
            ? _l.Get("Ft4.Status.Ready")
            : _modem.Status;
        WaterfallStatusText = _l.Get("Ft4.Waterfall.Listening");
    }

    public async Task StopSessionAsync()
    {
        await _modem.StopAsync().ConfigureAwait(true);
        TxEnabled = false;
        StatusLine = _l.Get("Ft4.Status.Idle");
        WaterfallStatusText = _l.Get("Ft4.Waterfall.Unavailable");
    }

    [RelayCommand(CanExecute = nameof(CanEnableTx))]
    private void EnableTx()
    {
        PushTxMessageToModem();
        _modem.EnableTx();
        TxEnabled = _modem.Sequencer?.TransmitEnabled == true;
        CurrentTxMessage = _modem.Sequencer?.CurrentTxMessage ?? CurrentTxMessage;
        StatusLine = _l.Get("Ft4.Status.TxEnabled");
    }

    private bool CanEnableTx() => !TxEnabled;

    [RelayCommand(CanExecute = nameof(CanHaltTx))]
    private void HaltTx()
    {
        _modem.HaltTx();
        TxEnabled = false;
        ManualPttPrompt = "";
        StatusLine = _l.Get("Ft4.Status.TxHalted");
    }

    private bool CanHaltTx() => TxEnabled;

    partial void OnTxEnabledChanged(bool value)
    {
        EnableTxCommand.NotifyCanExecuteChanged();
        HaltTxCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void StartCq()
    {
        // Rebuild from Settings → Station so portable callsigns (e.g. MM9SQL/M) pack correctly.
        _modem.StartCq(PreferEvenSlot);
        CurrentTxMessage = _modem.Sequencer?.CurrentTxMessage ?? "";
        TxEnabled = _modem.Sequencer?.TransmitEnabled == true;
        StatusLine = _l.Get("Ft4.Status.CallingCq");
        OnPropertyChanged(nameof(CanManualLog));
        ManualLogCommand.NotifyCanExecuteChanged();
    }

    private void PushTxMessageToModem()
    {
        var text = (CurrentTxMessage ?? "").Trim();
        if (text.Length == 0 || IsPlaceholderTxMessage(text))
            return;

        _modem.Sequencer?.SetTxMessage(text);
    }

    private bool IsPlaceholderTxMessage(string text) =>
        text.Equals("CQ CALL GRID", StringComparison.OrdinalIgnoreCase)
        || text.Equals(TxMessageWatermark, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void AnswerDecode(Ft4DecodedMessage? decode)
    {
        if (decode is null || decode.IsTransmitted || decode.IsOwnEcho)
            return;
        _modem.Answer(decode);
        CurrentTxMessage = _modem.Sequencer?.CurrentTxMessage ?? "";
        TxEnabled = _modem.Sequencer?.TransmitEnabled == true;
        PreferEvenSlot = _modem.Sequencer?.PreferEvenSlot ?? PreferEvenSlot;
        if (!_settings.Current.Ft4.HoldTxFrequency)
            TxAudioHz = decode.FreqHz;
        else if (_modem.Sequencer is not null)
            TxAudioHz = _modem.Sequencer.TxAudioHz;
        RxAudioHz = Math.Clamp(decode.FreqHz, 200, 3000);
        StatusLine = _l.Get("Ft4.Status.Answering", decode.Text);
        OnPropertyChanged(nameof(CanManualLog));
        ManualLogCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearDecodes()
    {
        _modem.ClearDecodes();
        Decodes.Clear();
        RxAudioHz = TxAudioHz;
        StatusLine = _l.Get("Ft4.Status.DecodesCleared");
    }

    [RelayCommand]
    private async Task SaveActivityAsync()
    {
        if (Decodes.Count == 0)
        {
            StatusLine = _l.Get("Ft4.Status.NoActivity");
            return;
        }

        var owner = App.MainWindow;
        var storage = Avalonia.Controls.TopLevel.GetTopLevel(owner)?.StorageProvider;
        if (storage is null)
        {
            StatusLine = _l.Get("Ft4.Status.SaveActivityFailed", "Storage unavailable.");
            return;
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = _l.Get("Ft4.SaveActivity.Title"),
            SuggestedFileName = $"OscarWatch-FT4-{stamp}.txt",
            DefaultExtension = "txt",
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType(_l.Get("Ft4.SaveActivity.FileType"))
                {
                    Patterns = ["*.txt"],
                    MimeTypes = ["text/plain"]
                }
            ]
        }).ConfigureAwait(true);

        if (file is null)
            return;

        try
        {
            var text = _modem.BuildActivityText();
            await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(text).ConfigureAwait(true);
            StatusLine = _l.Get("Ft4.Status.ActivitySaved", file.Name);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FT4 save activity failed");
            StatusLine = _l.Get("Ft4.Status.SaveActivityFailed", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanManualLog))]
    private async Task ManualLogAsync()
    {
        await _modem.LogQsoAsync(manual: true).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(_modem.Status))
            StatusLine = _modem.Status;
        else if (_modem.Sequencer?.TheirCall is not null)
            StatusLine = _l.Get("Ft4.Logged", _modem.Sequencer.TheirCall);
        ManualLogCommand.NotifyCanExecuteChanged();
    }

    public bool CanManualLog =>
        _modem.Sequencer?.TheirCall is not null
        && _modem.Sequencer.Phase is Ft4QsoPhase.InQso or Ft4QsoPhase.Finished;

    [RelayCommand]
    private void RefreshDevices()
    {
        RefreshAudioDevices();
        RefreshPttPorts();
    }

    /// <summary>Reload echo calibration satellite list and trim for the FT4 settings dialogue.</summary>
    public void RefreshEchoCalibration()
    {
        _loadingEchoCalibration = true;
        try
        {
            var focused = ResolveFocusedSatelliteName();
            var stored = _settings.Current.Ft4.UplinkCalibrationKHzBySatellite
                .Keys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            EchoCalibrationSatelliteOptions.Clear();
            foreach (var name in stored)
                EchoCalibrationSatelliteOptions.Add(name);

            if (!string.IsNullOrEmpty(focused)
                && !EchoCalibrationSatelliteOptions.Contains(focused, StringComparer.OrdinalIgnoreCase))
                EchoCalibrationSatelliteOptions.Insert(0, focused);

            var preferred = focused;
            if (string.IsNullOrEmpty(preferred) && EchoCalibrationSatelliteOptions.Count > 0)
                preferred = EchoCalibrationSatelliteOptions[0];

            SelectedEchoCalibrationSatellite = EchoCalibrationSatelliteOptions
                .FirstOrDefault(n => n.Equals(preferred, StringComparison.OrdinalIgnoreCase));
            LoadEchoCalibrationHzForSelected();
        }
        finally
        {
            _loadingEchoCalibration = false;
        }

        OnPropertyChanged(nameof(HasEchoCalibrationSatellite));
        ClearEchoCalibrationCommand.NotifyCanExecuteChanged();
        ClearAllEchoCalibrationsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanClearEchoCalibration))]
    private void ClearEchoCalibration()
    {
        var sat = SelectedEchoCalibrationSatellite?.Trim();
        if (string.IsNullOrEmpty(sat))
            return;

        _settings.Current.Ft4.SetUplinkCalibrationKHz(sat, 0);
        _settings.RequestSave();
        RefreshEchoCalibration();
        StatusLine = _l.Get("Ft4.Status.EchoCleared", sat);
    }

    private bool CanClearEchoCalibration() =>
        !string.IsNullOrWhiteSpace(SelectedEchoCalibrationSatellite)
        && Math.Abs(EchoCalibrationHz) > 0.01;

    [RelayCommand(CanExecute = nameof(CanClearAllEchoCalibrations))]
    private void ClearAllEchoCalibrations()
    {
        _settings.Current.Ft4.UplinkCalibrationKHzBySatellite.Clear();
        _settings.RequestSave();
        RefreshEchoCalibration();
        StatusLine = _l.Get("Ft4.Status.EchoClearedAll");
    }

    private bool CanClearAllEchoCalibrations() =>
        _settings.Current.Ft4.UplinkCalibrationKHzBySatellite.Count > 0;

    private void LoadEchoCalibrationHzForSelected()
    {
        var wasLoading = _loadingEchoCalibration;
        _loadingEchoCalibration = true;
        try
        {
            var sat = SelectedEchoCalibrationSatellite?.Trim();
            EchoCalibrationHz = string.IsNullOrEmpty(sat)
                ? 0
                : _settings.Current.Ft4.GetUplinkCalibrationKHz(sat) * 1000.0;
        }
        finally
        {
            _loadingEchoCalibration = wasLoading;
        }
    }

    private string? ResolveFocusedSatelliteName()
    {
        var snap = _tracker.GetCurrent();
        if (!string.IsNullOrWhiteSpace(snap.SatelliteName)
            && snap.SatelliteName != "-"
            && snap.SatelliteName != "—")
            return snap.SatelliteName.Trim();

        var overlay = _frequencyOverlay.SatelliteName?.Trim();
        if (!string.IsNullOrWhiteSpace(overlay)
            && overlay != "-"
            && overlay != "—")
            return overlay;

        return null;
    }

    public void SetManualPttPrompt(string phase)
    {
        ManualPttPrompt = phase switch
        {
            "key" => _l.Get("Ft4.ManualKey"),
            "unkey" => _l.Get("Ft4.ManualUnkey"),
            _ => phase
        };
    }

    private void RefreshAudioDevices()
    {
        _loadingDevices = true;
        try
        {
            var defaultLabel = _l.Get("Ft4.Audio.SystemDefault");
            var savedIn = _settings.Current.Ft4.InputDeviceId ?? "";
            var savedInName = _settings.Current.Ft4.InputDeviceDisplayName ?? "";
            var savedOut = _settings.Current.Ft4.OutputDeviceId ?? "";
            var savedOutName = _settings.Current.Ft4.OutputDeviceDisplayName ?? "";

            InputDeviceOptions.Clear();
            InputDeviceOptions.Add(new Ft4AudioDeviceOption("", defaultLabel));
            foreach (var d in _modem.GetInputDevices())
                InputDeviceOptions.Add(new Ft4AudioDeviceOption(d.Id, d.DisplayName));

            OutputDeviceOptions.Clear();
            OutputDeviceOptions.Add(new Ft4AudioDeviceOption("", defaultLabel));
            foreach (var d in _modem.GetOutputDevices())
                OutputDeviceOptions.Add(new Ft4AudioDeviceOption(d.Id, d.DisplayName));

            SelectedInputDevice = FindAudioDeviceOption(InputDeviceOptions, savedIn, savedInName)
                ?? InputDeviceOptions[0];
            SelectedOutputDevice = FindAudioDeviceOption(OutputDeviceOptions, savedOut, savedOutName)
                ?? OutputDeviceOptions[0];
        }
        finally
        {
            _loadingDevices = false;
        }
    }

    private static Ft4AudioDeviceOption? FindAudioDeviceOption(
        IEnumerable<Ft4AudioDeviceOption> options,
        string? deviceId,
        string? deviceDisplayName)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var byId = options.FirstOrDefault(o =>
                string.Equals(o.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(deviceDisplayName))
        {
            var formattedSaved = OscarWatch.Recording.RecordingDeviceNameFormatter.Format(deviceDisplayName);
            var byDisplay = options.FirstOrDefault(o =>
            {
                if (string.IsNullOrWhiteSpace(o.Id))
                    return false;
                if (string.Equals(o.DisplayName, deviceDisplayName, StringComparison.OrdinalIgnoreCase))
                    return true;
                return string.Equals(
                    OscarWatch.Recording.RecordingDeviceNameFormatter.Format(o.DisplayName),
                    formattedSaved,
                    StringComparison.OrdinalIgnoreCase);
            });
            if (byDisplay is not null)
                return byDisplay;

            // Keep the saved choice visible when PortAudio no longer lists it.
            return new Ft4AudioDeviceOption(
                string.IsNullOrWhiteSpace(deviceId) ? deviceDisplayName : deviceId!,
                deviceDisplayName);
        }

        return null;
    }

    private void RefreshPttPorts()
    {
        var saved = _settings.Current.Ft4.SeparatePttPort?.Trim() ?? "";
        PttPortOptions.Clear();
        foreach (var port in SerialPortDiscovery.GetAvailablePorts(forceRefresh: true))
            PttPortOptions.Add(port);
        if (!string.IsNullOrEmpty(saved)
            && !PttPortOptions.Contains(saved, StringComparer.OrdinalIgnoreCase))
            PttPortOptions.Insert(0, saved);
        SeparatePttPort = saved;
        RefreshSeparatePttConflict();
    }

    private void OnModemChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!string.IsNullOrWhiteSpace(_modem.Status))
                StatusLine = _modem.Status;
            CurrentTxMessage = _modem.Sequencer?.CurrentTxMessage ?? CurrentTxMessage;
            TxEnabled = _modem.Sequencer?.TransmitEnabled == true;
            if (!string.IsNullOrEmpty(_modem.ManualPrompt))
                SetManualPttPrompt(_modem.ManualPrompt);
            OnPropertyChanged(nameof(CanManualLog));
            ManualLogCommand.NotifyCanExecuteChanged();
            // Auto echo calibration may have updated the stored trim.
            if (SelectedEchoCalibrationSatellite is not null)
                LoadEchoCalibrationHzForSelected();
            ClearEchoCalibrationCommand.NotifyCanExecuteChanged();
            ClearAllEchoCalibrationsCommand.NotifyCanExecuteChanged();
        });
    }

    private void OnDecodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Decodes.Clear();
            foreach (var d in _modem.Decodes)
                Decodes.Add(d);
            RefreshRxMarker();
        });
    }

    private void RefreshUiTick()
    {
        var utc = DateTime.UtcNow;
        var slotStart = Ft4SlotClock.SlotStartUtc(utc, Ft4SlotClock.Ft4SlotSeconds);
        var into = Ft4SlotClock.SecondsIntoSlot(utc, Ft4SlotClock.Ft4SlotSeconds);
        var even = Ft4SlotClock.IsEvenSlot(slotStart, Ft4SlotClock.Ft4SlotSeconds);
        SlotClockText = $"{slotStart:HH:mm:ss.f} UTC  +{into:0.0}s  {(even ? "even" : "odd")}";

        var progress = Math.Clamp(100.0 * into / Ft4SlotClock.Ft4SlotSeconds, 0, 100);
        SlotProgressPercent = progress;
        SlotProgressText = $"{progress:0}%";
        var preferEven = _modem.Sequencer?.PreferEvenSlot ?? PreferEvenSlot;
        IsTxSlot = TxEnabled && even == preferEven;
        SlotPeriodLabel = IsTxSlot ? _l.Get("Ft4.Slot.Tx") : _l.Get("Ft4.Slot.Rx");

        OnPropertyChanged(nameof(TxMessageWatermark));

        DopplerUplinkText = string.IsNullOrWhiteSpace(_frequencyOverlay.RadioTransmitText)
            ? "-"
            : _frequencyOverlay.RadioTransmitText;
        DopplerDownlinkText = string.IsNullOrWhiteSpace(_frequencyOverlay.RadioReceiveText)
            ? "-"
            : _frequencyOverlay.RadioReceiveText;
        OnPropertyChanged(nameof(HasDoppler));

        if (_modem.IsRunning)
        {
            _modem.RefreshSatelliteEligibility();

            var snap = _tracker.GetCurrent();
            var sat = string.IsNullOrWhiteSpace(snap.SatelliteName) ? null : snap.SatelliteName;
            WaterfallStatusText = sat is null
                ? _l.Get("Ft4.Waterfall.Listening")
                : _l.Get("Ft4.Waterfall.ListeningSat", sat);

            var bins = new float[Ft4WaterfallControl.SpectrumColumns];
            if (_modem.TryBuildSpectrum(bins))
                SpectrumBins = bins;
        }

        TxPlaybackPeakPercent = Math.Clamp(_modem.TxPlaybackPeak * 100.0, 0, 100);
        RefreshClockWarning();
    }

    private void RefreshClockWarning()
    {
        // WSJT-X style: large DT across recent RX decodes usually means the PC clock is off UTC.
        const float thresholdSec = 1.0f;
        var recent = Decodes
            .Where(d => d.IsReceiveActivity)
            .Take(8)
            .Select(d => Math.Abs(d.TimeSec))
            .ToList();
        if (recent.Count < 3)
        {
            ClockWarningText = "";
            return;
        }

        var over = recent.Count(dt => dt >= thresholdSec);
        if (over < 3)
        {
            ClockWarningText = "";
            return;
        }

        var worst = recent.Max();
        ClockWarningText = _l.Get("Ft4.Status.ClockWarning", worst);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _uiTimer.Stop();
        _modem.Changed -= OnModemChanged;
        ((INotifyCollectionChanged)_modem.Decodes).CollectionChanged -= OnDecodesChanged;
        _ = StopSessionAsync();
    }
}

public sealed record Ft4PttMethodOption(Ft4PttMethod Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record Ft4PttLineOption(Ft4PttLine Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record Ft4AudioDeviceOption(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}
