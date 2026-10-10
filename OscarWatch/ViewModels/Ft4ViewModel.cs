using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Controls;
using OscarWatch.Core.Display;
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
    private readonly IQsoLogbookRepository _logbook;
    private readonly DispatcherTimer _uiTimer;
    private bool _disposed;
    private bool _loadingDevices;
    private bool _loadingEchoCalibration;
    private bool _windowOpen;
    private IReadOnlySet<string> _workedCalls = new HashSet<string>(StringComparer.Ordinal);
    private IReadOnlySet<string> _workedGridFields = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> _finishedPartners = new(StringComparer.Ordinal);
    private string? _highlightPartner;
    private Ft4PounceTarget? _highlightPounce;

    public Ft4ViewModel(
        ISettingsService settings,
        FrequencyOverlayViewModel frequencyOverlay,
        ILiveTrackerSnapshotProvider tracker,
        ILocalizationService localization,
        Ft4ModemService modem,
        IQsoLogbookRepository logbook)
    {
        _settings = settings;
        _frequencyOverlay = frequencyOverlay;
        _tracker = tracker;
        _l = localization;
        _modem = modem;
        _logbook = logbook;

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
        _apEnabled = ft4.ApEnabled;
        _txAudioHz = Math.Clamp(ft4.TxAudioHz, 200, 3000);
        _rxAudioHz = _txAudioHz;
        _txLevel = Math.Clamp(ft4.TxLevel, 0.05, 1.0);
        _holdTxFrequency = ft4.HoldTxFrequency;
        _autoReply = ft4.AutoReply;
        _autoLowerRfPower = ft4.AutoLowerRfPower;
        _audioDopplerTx = ft4.AudioDopplerTx;
        _audioDopplerRx = ft4.AudioDopplerRx;
        _parallelTxEchoDecode = ft4.ParallelTxEchoDecode;
        _saveSlotAudio = ft4.SaveSlotAudio;
        _txWatchdogMinutes = Ft4TxWatchdog.ClampMinutes(ft4.TxWatchdogMinutes);
        _pskReporterEnabled = ft4.PskReporterEnabled;
        _oscarWatchSpotsTokenAvailable = Ft4OscarWatchSpots.HasApiToken(_settings.Current.SatelliteStatus.ApiToken);
        _oscarWatchSpotsEnabled = ft4.OscarWatchSpotsEnabled && _oscarWatchSpotsTokenAvailable;
        if (ft4.OscarWatchSpotsEnabled && !_oscarWatchSpotsTokenAvailable)
        {
            ft4.OscarWatchSpotsEnabled = false;
            _settings.RequestSave();
        }
        _modem.RxAudioHz = _rxAudioHz;
        _pttLeadMs = Math.Clamp(ft4.PttLeadMs, 0, 2000);
        _pttTailMs = Math.Clamp(ft4.PttTailMs, 0, 2000);
        _decodeFontSize = Math.Clamp(ft4.DecodeFontSize, 10, 28);
        _waterfallRangeDb = Ft4Settings.ClampWaterfallRangeDb(ft4.WaterfallRangeDb);
        _callingMeColour = Ft4DecodeHighlight.NormalizeColour(ft4.CallingMeColour)
            ?? Ft4DecodeHighlight.DefaultCallingMeColour;
        _replyingColour = Ft4DecodeHighlight.NormalizeColour(ft4.ReplyingColour)
            ?? Ft4DecodeHighlight.DefaultReplyingColour;
        _newCallColour = Ft4DecodeHighlight.NormalizeColour(ft4.NewCallColour)
            ?? Ft4DecodeHighlight.DefaultNewCallColour;
        _newGridColour = Ft4DecodeHighlight.NormalizeColour(ft4.NewGridColour)
            ?? Ft4DecodeHighlight.DefaultNewGridColour;
        _cqColour = Ft4DecodeHighlight.NormalizeColour(ft4.CqColour)
            ?? Ft4DecodeHighlight.DefaultCqColour;
        _callingMeTextColour = Ft4DecodeHighlight.NormalizeColour(ft4.CallingMeTextColour) ?? "";
        _replyingTextColour = Ft4DecodeHighlight.NormalizeColour(ft4.ReplyingTextColour) ?? "";
        _newCallTextColour = Ft4DecodeHighlight.NormalizeColour(ft4.NewCallTextColour) ?? "";
        _newGridTextColour = Ft4DecodeHighlight.NormalizeColour(ft4.NewGridTextColour) ?? "";
        _cqTextColour = Ft4DecodeHighlight.NormalizeColour(ft4.CqTextColour) ?? "";
        _txTextColour = Ft4DecodeHighlight.NormalizeColour(ft4.TxTextColour)
            ?? Ft4DecodeHighlight.DefaultTxTextColour;
        _preferEvenSlot = false;
        _pttInvert = ft4.PttInvert;
        _selectedPttMethod = PttMethodOptions.FirstOrDefault(o => o.Value == ft4.PttMethod)
            ?? PttMethodOptions[0];
        _selectedPttLine = PttLineOptions.FirstOrDefault(o => o.Value == ft4.PttLine)
            ?? PttLineOptions[0];
        _separatePttPort = ft4.SeparatePttPort ?? "";

        StatusLine = _l.Get("Ft4.Status.Idle");
        _pounceCheckLabel = _l.Get("Ft4.Pounce");
        WaterfallStatusText = _l.Get("Ft4.Waterfall.Unavailable");
        SlotClockText = "-";
        DopplerUplinkText = "-";
        DopplerDownlinkText = "-";

        RefreshAudioDevices();
        RefreshPttPorts();

        _modem.SetManualPromptHandler(SetManualPttPrompt);
        _modem.Changed += OnModemChanged;
        ((INotifyCollectionChanged)_modem.Decodes).CollectionChanged += OnDecodesChanged;
        _logbook.QsosChanged += OnLogbookQsosChanged;
        _ = RefreshWorkedSetsAsync();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) => RefreshUiTick();
        SlotPeriodLabel = _l.Get("Ft4.Slot.Rx");
        SlotProgressText = "0%";
    }

    public ObservableCollection<Ft4DecodeRowViewModel> Decodes { get; } = [];

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

    [ObservableProperty] private string _waterfallStatusText = "";
    [ObservableProperty] private string _slotClockText = "";
    [ObservableProperty] private string _clockSourceText = "";
    [ObservableProperty] private string _slotPeriodLabel = "";
    [ObservableProperty] private string _slotProgressText = "";
    [ObservableProperty] private double _slotProgressPercent;
    [ObservableProperty] private bool _isTxSlot;
    [ObservableProperty] private string _dopplerUplinkText = "";
    [ObservableProperty] private string _dopplerDownlinkText = "";
    [ObservableProperty] private string _currentTxMessage = "";
    [ObservableProperty] private string _manualPttPrompt = "";
    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private string _separatePttConflictText = "";
    [ObservableProperty] private double _txPlaybackPeakPercent;
    [ObservableProperty] private bool _skipRrr;
    [ObservableProperty] private bool _apEnabled = true;
    [ObservableProperty] private bool _preferEvenSlot;
    [ObservableProperty] private bool _holdTxFrequency = true;
    [ObservableProperty] private bool _autoReply = true;
    [ObservableProperty] private bool _autoLowerRfPower;
    [ObservableProperty] private bool _audioDopplerTx = true;
    [ObservableProperty] private bool _audioDopplerRx = true;
    [ObservableProperty] private bool _parallelTxEchoDecode = true;
    [ObservableProperty] private bool _saveSlotAudio;

    [ObservableProperty] private int _txWatchdogMinutes = Ft4TxWatchdog.DefaultMinutes;
    [ObservableProperty] private bool _pskReporterEnabled;

    /// <summary>True when Settings, OscarWatch has an API token, so spot reporting can be turned on.</summary>
    [ObservableProperty] private bool _oscarWatchSpotsTokenAvailable;

    [ObservableProperty] private bool _oscarWatchSpotsEnabled;
    [ObservableProperty] private int _pttLeadMs = 200;
    [ObservableProperty] private int _pttTailMs = 100;
    [ObservableProperty] private double _decodeFontSize = 12;

    [ObservableProperty] private double _waterfallRangeDb = Ft4Settings.DefaultWaterfallRangeDb;
    [ObservableProperty] private string _callingMeColour = Ft4DecodeHighlight.DefaultCallingMeColour;
    [ObservableProperty] private string _replyingColour = Ft4DecodeHighlight.DefaultReplyingColour;
    [ObservableProperty] private string _newCallColour = Ft4DecodeHighlight.DefaultNewCallColour;
    [ObservableProperty] private string _newGridColour = Ft4DecodeHighlight.DefaultNewGridColour;
    [ObservableProperty] private string _cqColour = Ft4DecodeHighlight.DefaultCqColour;
    [ObservableProperty] private string _callingMeTextColour = "";
    [ObservableProperty] private string _replyingTextColour = "";
    [ObservableProperty] private string _newCallTextColour = "";
    [ObservableProperty] private string _newGridTextColour = "";
    [ObservableProperty] private string _cqTextColour = "";
    [ObservableProperty] private string _txTextColour = Ft4DecodeHighlight.DefaultTxTextColour;
    [ObservableProperty] private string? _qsoPartnerCall;
    [ObservableProperty] private string? _selectedEchoCalibrationSatellite;
    [ObservableProperty] private double _echoCalibrationHz;
    [ObservableProperty] private double _txAudioHz = 1500;
    [ObservableProperty] private double _rxAudioHz = 1500;
    [ObservableProperty] private double _txLevel = 0.35;
    [ObservableProperty] private bool _txEnabled;
    [ObservableProperty] private bool _isTuning;
    [ObservableProperty] private bool _pttInvert;
    [ObservableProperty] private string _separatePttPort = "";
    [ObservableProperty] private Ft4PttMethodOption? _selectedPttMethod;
    [ObservableProperty] private Ft4PttLineOption? _selectedPttLine;
    [ObservableProperty] private Ft4AudioDeviceOption? _selectedInputDevice;
    [ObservableProperty] private Ft4AudioDeviceOption? _selectedOutputDevice;
    [ObservableProperty] private Ft4DecodeRowViewModel? _selectedDecode;
    [ObservableProperty] private float[]? _spectrumBins;

    partial void OnSkipRrrChanged(bool value)
    {
        _settings.Current.Ft4.SkipRrr = value;
        _settings.RequestSave();
    }

    partial void OnApEnabledChanged(bool value)
    {
        _settings.Current.Ft4.ApEnabled = value;
        _settings.RequestSave();
    }

    partial void OnAutoReplyChanged(bool value)
    {
        _settings.Current.Ft4.AutoReply = value;
        _settings.RequestSave();
        // The sequencer must see this tick immediately. A settings read alone
        // left CQ running after the box was cleared and ticked again.
        _modem.SetAutoReply(value);
    }

    partial void OnAutoLowerRfPowerChanged(bool value)
    {
        _settings.Current.Ft4.AutoLowerRfPower = value;
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

    partial void OnParallelTxEchoDecodeChanged(bool value)
    {
        _settings.Current.Ft4.ParallelTxEchoDecode = value;
        _settings.RequestSave();
    }

    partial void OnSaveSlotAudioChanged(bool value)
    {
        _settings.Current.Ft4.SaveSlotAudio = value;
        _settings.RequestSave();
    }

    [RelayCommand]
    private void OpenSlotRecordingFolder()
    {
        try
        {
            DopplerPassLogFileNameFormat.OpenLogDirectory(_modem.SlotRecordingDirectory);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open the FT4 slot recording folder");
        }
    }

    partial void OnTxWatchdogMinutesChanged(int value)
    {
        var clamped = Ft4TxWatchdog.ClampMinutes(value);
        if (clamped != value)
        {
            TxWatchdogMinutes = clamped;
            return;
        }

        _settings.Current.Ft4.TxWatchdogMinutes = clamped;
        _settings.RequestSave();
    }

    partial void OnPskReporterEnabledChanged(bool value)
    {
        _settings.Current.Ft4.PskReporterEnabled = value;
        _settings.RequestSave();
        _modem.ApplyPskReporterSettings();
    }

    partial void OnOscarWatchSpotsEnabledChanged(bool value)
    {
        if (value && !Ft4OscarWatchSpots.HasApiToken(_settings.Current.SatelliteStatus.ApiToken))
        {
            OscarWatchSpotsEnabled = false;
            return;
        }

        _settings.Current.Ft4.OscarWatchSpotsEnabled = value;
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

    partial void OnWaterfallRangeDbChanged(double value)
    {
        var clamped = Ft4Settings.ClampWaterfallRangeDb(value);
        if (Math.Abs(clamped - value) > 0.01)
        {
            WaterfallRangeDb = clamped;
            return;
        }

        _settings.Current.Ft4.WaterfallRangeDb = clamped;
        _settings.RequestSave();
    }

    partial void OnCallingMeColourChanged(string value) =>
        CommitDecodeColour(
            value,
            Ft4DecodeHighlight.DefaultCallingMeColour,
            () => CallingMeColour,
            hex => CallingMeColour = hex,
            hex => _settings.Current.Ft4.CallingMeColour = hex);

    partial void OnReplyingColourChanged(string value) =>
        CommitDecodeColour(
            value,
            Ft4DecodeHighlight.DefaultReplyingColour,
            () => ReplyingColour,
            hex => ReplyingColour = hex,
            hex => _settings.Current.Ft4.ReplyingColour = hex);

    partial void OnNewCallColourChanged(string value) =>
        CommitDecodeColour(
            value,
            Ft4DecodeHighlight.DefaultNewCallColour,
            () => NewCallColour,
            hex => NewCallColour = hex,
            hex => _settings.Current.Ft4.NewCallColour = hex);

    partial void OnNewGridColourChanged(string value) =>
        CommitDecodeColour(
            value,
            Ft4DecodeHighlight.DefaultNewGridColour,
            () => NewGridColour,
            hex => NewGridColour = hex,
            hex => _settings.Current.Ft4.NewGridColour = hex);

    partial void OnCqColourChanged(string value) =>
        CommitDecodeColour(
            value,
            Ft4DecodeHighlight.DefaultCqColour,
            () => CqColour,
            hex => CqColour = hex,
            hex => _settings.Current.Ft4.CqColour = hex);

    partial void OnCallingMeTextColourChanged(string value) =>
        CommitDecodeColour(
            value,
            "",
            () => CallingMeTextColour,
            hex => CallingMeTextColour = hex,
            hex => _settings.Current.Ft4.CallingMeTextColour = hex,
            followTheme: true);

    partial void OnReplyingTextColourChanged(string value) =>
        CommitDecodeColour(
            value,
            "",
            () => ReplyingTextColour,
            hex => ReplyingTextColour = hex,
            hex => _settings.Current.Ft4.ReplyingTextColour = hex,
            followTheme: true);

    partial void OnNewCallTextColourChanged(string value) =>
        CommitDecodeColour(
            value,
            "",
            () => NewCallTextColour,
            hex => NewCallTextColour = hex,
            hex => _settings.Current.Ft4.NewCallTextColour = hex,
            followTheme: true);

    partial void OnNewGridTextColourChanged(string value) =>
        CommitDecodeColour(
            value,
            "",
            () => NewGridTextColour,
            hex => NewGridTextColour = hex,
            hex => _settings.Current.Ft4.NewGridTextColour = hex,
            followTheme: true);

    partial void OnCqTextColourChanged(string value) =>
        CommitDecodeColour(
            value,
            "",
            () => CqTextColour,
            hex => CqTextColour = hex,
            hex => _settings.Current.Ft4.CqTextColour = hex,
            followTheme: true);

    partial void OnTxTextColourChanged(string value) =>
        CommitDecodeColour(
            value,
            Ft4DecodeHighlight.DefaultTxTextColour,
            () => TxTextColour,
            hex => TxTextColour = hex,
            hex => _settings.Current.Ft4.TxTextColour = hex);

    partial void OnQsoPartnerCallChanged(string? value)
    {
        // Their lines were "replying" during the contact. Clearing the partner must
        // not repaint them as "calling me" after the QSO.
        var previous = Ft4MessageCodec.NormalizeCall(_highlightPartner ?? "");
        var next = Ft4MessageCodec.NormalizeCall(value ?? "");
        if (previous.Length > 0 && !previous.Equals(next, StringComparison.Ordinal))
            _finishedPartners.Add(previous);
        _highlightPartner = value;
        RefreshDecodeHighlights();
    }

    private int _colourCommitDepth;

    private void CommitDecodeColour(
        string value,
        string fallback,
        Func<string> current,
        Action<string> setCurrent,
        Action<string> store,
        bool followTheme = false)
    {
        if (_colourCommitDepth > 0)
            return;

        var normalized = Ft4DecodeHighlight.NormalizeColour(value) ?? fallback;
        if (followTheme && Ft4HexColorConverter.IsThemeForeground(normalized))
            normalized = "";
        _colourCommitDepth++;
        try
        {
            if (!string.Equals(current(), normalized, StringComparison.OrdinalIgnoreCase))
                setCurrent(normalized);
            store(normalized);
            _settings.RequestSave();
            RefreshDecodeHighlights();
        }
        finally
        {
            _colourCommitDepth--;
        }
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
        if (!double.IsFinite(value))
        {
            TxAudioHz = 1500;
            return;
        }

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

        if (IsTuning)
            _modem.UpdateTuneFrequency();

        // Without Hold Tx, keep RX locked to TX (WSJT-X behaviour).
        if (!_settings.Current.Ft4.HoldTxFrequency)
            RxAudioHz = clamped;
    }

    partial void OnRxAudioHzChanged(double value)
    {
        if (!double.IsFinite(value))
        {
            RxAudioHz = TxAudioHz;
            return;
        }

        var clamped = Math.Clamp(value, 200, 3000);
        if (Math.Abs(clamped - value) > 0.01)
        {
            RxAudioHz = clamped;
            return;
        }

        _modem.RxAudioHz = clamped;
    }

    private void RefreshRxMarker()
    {
        // Follow the station we are in QSO with. The newest line on the band is often
        // someone else's contact, and chasing it walks the green bracket.
        var partner = _modem.Sequencer?.TheirCall;
        if (!string.IsNullOrWhiteSpace(partner)
            && TryPartnerRxHz(Decodes.Select(r => r.Message), partner, out var partnerHz))
        {
            RxAudioHz = partnerHz;
            return;
        }

        if (!string.IsNullOrWhiteSpace(partner))
            return;

        // No QSO yet. Without Hold Tx, RX stays on TX. With Hold Tx, keep the click.
        if (!_settings.Current.Ft4.HoldTxFrequency)
            RxAudioHz = TxAudioHz;
    }

    /// <summary>Newest receive decode from <paramref name="partner"/>, if the list has one.</summary>
    internal static bool TryPartnerRxHz(
        IEnumerable<Ft4DecodedMessage> decodes,
        string partner,
        out double hz)
    {
        foreach (var d in decodes)
        {
            if (!d.IsReceiveActivity || string.IsNullOrWhiteSpace(d.CallDe))
                continue;
            if (!d.CallDe.Equals(partner, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!double.IsFinite(d.FreqHz))
                continue;

            hz = Math.Clamp(d.FreqHz, 200, 3000);
            return true;
        }

        hz = 0;
        return false;
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
        _modem.OnPttSettingsChanged();
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
        _modem.OnPttSettingsChanged();
        RefreshSeparatePttConflict();
    }

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
        if (_modem.IsRunning)
            _modem.RestartCaptureFromSettings();
        else if (_windowOpen)
            StartSession();
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

    partial void OnSelectedDecodeChanged(Ft4DecodeRowViewModel? value)
    {
        if (value is null)
            return;
        AnswerDecode(value.Message);
    }

    public Task OnWindowOpenedAsync()
    {
        _windowOpen = true;
        _uiTimer.Start();
        try
        {
            RefreshAudioDevices();
            RefreshPttPorts();
            RefreshUiTick();
            _modem.SetWindowOpen(true);
            if (!_modem.IsRunning)
                StartSession();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FT4 window open failed");
            StatusLine = _l.Get("Ft4.Status.InputUnavailable");
            WaterfallStatusText = _l.Get("Ft4.Waterfall.Unavailable");
        }

        return Task.CompletedTask;
    }

    public Task OnWindowClosedAsync()
    {
        _windowOpen = false;
        _uiTimer.Stop();
        _modem.SetWindowOpen(false);
        return Task.CompletedTask;
    }

    public void StartSession()
    {
        if (!_modem.NativeAvailable)
        {
            StatusLine = _l.Get(Ft8Native.UnavailableMessageKey);
            WaterfallStatusText = _l.Get("Ft4.Waterfall.Unavailable");
            Log.Warning("FT4 native library unavailable: {Error}", Ft8Native.LoadError);
            return;
        }

        if (StationCallsign.Length == 0 || StationGrid.Length < 4)
        {
            StatusLine = _l.Get("Ft4.Status.NeedStation");
            return;
        }

        try
        {
            _modem.Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FT4 session start failed");
            StatusLine = _l.Get("Ft4.Status.InputUnavailable");
            WaterfallStatusText = _l.Get("Ft4.Waterfall.Unavailable");
            TuneCommand.NotifyCanExecuteChanged();
            return;
        }

        if (!_modem.IsRunning)
        {
            StatusLine = string.IsNullOrWhiteSpace(_modem.Status)
                ? _l.Get("Ft4.Status.InputUnavailable")
                : _modem.Status;
            WaterfallStatusText = _l.Get("Ft4.Waterfall.Unavailable");
            TuneCommand.NotifyCanExecuteChanged();
            return;
        }

        StatusLine = string.IsNullOrWhiteSpace(_modem.Status)
            ? _l.Get("Ft4.Status.Ready")
            : _modem.Status;
        WaterfallStatusText = _l.Get("Ft4.Waterfall.Listening");
        TuneCommand.NotifyCanExecuteChanged();
    }

    public async Task StopSessionAsync()
    {
        await _modem.StopAsync().ConfigureAwait(true);
        TxEnabled = false;
        IsTuning = false;
        StatusLine = _l.Get("Ft4.Status.Idle");
        WaterfallStatusText = _l.Get("Ft4.Waterfall.Unavailable");
        TuneCommand.NotifyCanExecuteChanged();
        HaltTxCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEnableTx))]
    private void EnableTx()
    {
        PushTxMessageToModem();
        _modem.SetAutoReply(AutoReply);
        _modem.EnableTx();
        TxEnabled = _modem.Sequencer?.TransmitEnabled == true;
        CurrentTxMessage = _modem.Sequencer?.CurrentTxMessage ?? CurrentTxMessage;
        StatusLine = string.IsNullOrWhiteSpace(_modem.Status)
            ? _l.Get("Ft4.Status.TxEnabled")
            : _modem.Status;
    }

    private bool CanEnableTx() => !TxEnabled && !IsTuning;

    [RelayCommand(CanExecute = nameof(CanSendStandardMessage))]
    private void SendReport()
    {
        if (!_modem.QueueReport(LatestPartnerSnr()))
            return;

        TxEnabled = _modem.Sequencer?.TransmitEnabled == true;
        CurrentTxMessage = _modem.Sequencer?.CurrentTxMessage ?? CurrentTxMessage;
        StatusLine = _modem.Status;
    }

    [RelayCommand(CanExecute = nameof(CanSendStandardMessage))]
    private void Send73()
    {
        if (!_modem.Queue73())
            return;

        TxEnabled = _modem.Sequencer?.TransmitEnabled == true;
        CurrentTxMessage = _modem.Sequencer?.CurrentTxMessage ?? CurrentTxMessage;
        StatusLine = _modem.Status;
    }

    private bool CanSendStandardMessage() =>
        !string.IsNullOrWhiteSpace(_modem.Sequencer?.TheirCall);

    private float? LatestPartnerSnr()
    {
        var partner = _modem.Sequencer?.TheirCall;
        if (string.IsNullOrWhiteSpace(partner))
            return null;

        foreach (var decode in Decodes)
        {
            if (!decode.Message.IsReceiveActivity || string.IsNullOrWhiteSpace(decode.Message.CallDe))
                continue;
            if (!decode.Message.CallDe.Equals(partner, StringComparison.OrdinalIgnoreCase))
                continue;
            return decode.Message.SnrDb;
        }

        return null;
    }

    [RelayCommand(CanExecute = nameof(CanHaltTx))]
    private void HaltTx()
    {
        _modem.HaltTx();
        SyncPounceState();
        TxEnabled = false;
        IsTuning = false;
        ManualPttPrompt = "";
        StatusLine = _modem.Status;
        TuneCommand.NotifyCanExecuteChanged();
    }

    /// <summary>CQ, a contact, or Tune is using the transmitter.</summary>
    public bool IsTransmissionActive => TxEnabled || IsTuning || _modem.IsTransmissionActive;

    /// <summary>Unkey and stop the audio immediately. Used when the operator closes the window.</summary>
    public void StopTransmissionNow()
    {
        _modem.StopTransmissionNow();
        TxEnabled = false;
        IsTuning = false;
        ManualPttPrompt = "";
        StatusLine = _modem.Status;
        TuneCommand.NotifyCanExecuteChanged();
        HaltTxCommand.NotifyCanExecuteChanged();
        EnableTxCommand.NotifyCanExecuteChanged();
    }

    private bool CanHaltTx() => TxEnabled || IsTuning;

    [RelayCommand(CanExecute = nameof(CanTune))]
    private void Tune()
    {
        if (IsTuning)
        {
            _modem.StopTune();
            IsTuning = false;
            StatusLine = _modem.Status;
            HaltTxCommand.NotifyCanExecuteChanged();
            TuneCommand.NotifyCanExecuteChanged();
            return;
        }

        if (!_modem.StartTune())
        {
            StatusLine = _modem.Status;
            return;
        }

        TxEnabled = false;
        IsTuning = true;
        StatusLine = _modem.Status;
        HaltTxCommand.NotifyCanExecuteChanged();
        TuneCommand.NotifyCanExecuteChanged();
        EnableTxCommand.NotifyCanExecuteChanged();
    }

    private bool CanTune() => _modem.IsRunning;

    partial void OnTxEnabledChanged(bool value)
    {
        EnableTxCommand.NotifyCanExecuteChanged();
        HaltTxCommand.NotifyCanExecuteChanged();
        TuneCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsTuningChanged(bool value)
    {
        HaltTxCommand.NotifyCanExecuteChanged();
        TuneCommand.NotifyCanExecuteChanged();
        EnableTxCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void StartCq()
    {
        // Rebuild from Settings → Station so portable callsigns (e.g. MM9SQL/M) pack correctly.
        _modem.SetAutoReply(AutoReply);
        _modem.StartCq(PreferEvenSlot);
        // The text box is two-way. Losing focus to this button can push the previous
        // QSO text back after the CQ has been stored. Put the CQ on screen again
        // once that write has landed.
        ShowSequencerTxMessage();
        TxEnabled = _modem.Sequencer?.TransmitEnabled == true;
        StatusLine = string.IsNullOrWhiteSpace(_modem.Status)
            ? _l.Get("Ft4.Status.CallingCq")
            : _modem.Status;
        OnPropertyChanged(nameof(CanManualLog));
        ManualLogCommand.NotifyCanExecuteChanged();
    }

    private int _txMessagePublish;

    private void ShowSequencerTxMessage()
    {
        var publish = ++_txMessagePublish;
        CurrentTxMessage = _modem.Sequencer?.CurrentTxMessage ?? "";
        Dispatcher.UIThread.Post(() =>
        {
            if (publish != _txMessagePublish)
                return;

            var live = _modem.Sequencer?.CurrentTxMessage ?? "";
            if (!string.Equals(CurrentTxMessage, live, StringComparison.Ordinal))
                CurrentTxMessage = live;
        }, DispatcherPriority.Background);
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
        if (decode is null || decode.IsTransmitted || decode.IsOwnEcho || decode.IsRejected)
            return;
        _modem.Answer(decode);
        CurrentTxMessage = _modem.Sequencer?.CurrentTxMessage ?? "";
        TxEnabled = _modem.Sequencer?.TransmitEnabled == true;
        PreferEvenSlot = _modem.Sequencer?.PreferEvenSlot ?? PreferEvenSlot;
        if (!_settings.Current.Ft4.HoldTxFrequency)
            TxAudioHz = decode.FreqHz;
        RxAudioHz = Math.Clamp(decode.FreqHz, 200, 3000);
        StatusLine = _l.Get("Ft4.Status.Answering", decode.Text);
        OnPropertyChanged(nameof(CanManualLog));
        ManualLogCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The armed pounce target as shown on the TX panel, or empty.</summary>
    [ObservableProperty] private string _pounceTargetText = "";

    [ObservableProperty] private bool _isPounceArmed;

    /// <summary>"Pounce" while off, "Pouncing: CALL" while armed.</summary>
    [ObservableProperty] private string _pounceCheckLabel = "";

    /// <summary>What the operator last entered in the pounce dialogue this session.</summary>
    public string LastPounceInput { get; private set; } = "";

    public void ArmPounce(Ft4PounceTarget target)
    {
        LastPounceInput = target.Value;
        _modem.ArmPounce(target);
        SyncPounceState();
        StatusLine = _modem.Status;
    }

    [RelayCommand]
    private void CancelPounce()
    {
        _modem.DisarmPounce();
        SyncPounceState();
        StatusLine = _modem.Status;
    }

    [RelayCommand]
    private void PounceOnDecode(Ft4DecodeRowViewModel? row)
    {
        var call = row?.Message.CallDe;
        if (row is null || !row.Message.IsReceiveActivity || string.IsNullOrWhiteSpace(call))
            return;

        if (!Ft4PounceTarget.TryParse(call, out var target))
        {
            StatusLine = _l.Get("Ft4.Status.PounceInvalid", call);
            return;
        }

        ArmPounce(target);
    }

    private void SyncPounceState()
    {
        var target = _modem.PounceTarget;
        IsPounceArmed = target is not null;
        PounceTargetText = target?.Value ?? "";
        PounceCheckLabel = target is null ? _l.Get("Ft4.Pounce") : _l.Get("Ft4.Pounce.Armed", target.Value);
        if (!ReferenceEquals(_highlightPounce, target))
        {
            _highlightPounce = target;
            RefreshDecodeHighlights();
        }

        if (_modem.TakePouncedDecode() is { } pounced)
        {
            if (!_settings.Current.Ft4.HoldTxFrequency)
                TxAudioHz = pounced.FreqHz;
            RxAudioHz = Math.Clamp(pounced.FreqHz, 200, 3000);
        }
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
            // Answering from a decode (pounce or auto reply) picks the slot opposite the caller.
            PreferEvenSlot = _modem.Sequencer?.PreferEvenSlot ?? PreferEvenSlot;
            IsTuning = _modem.IsTuning;
            // Recolour rows already on screen when the QSO partner changes.
            QsoPartnerCall = _modem.Sequencer?.TheirCall;
            if (!string.IsNullOrEmpty(_modem.ManualPrompt))
                SetManualPttPrompt(_modem.ManualPrompt);
            SyncPounceState();
            OnPropertyChanged(nameof(CanManualLog));
            ManualLogCommand.NotifyCanExecuteChanged();
            SendReportCommand.NotifyCanExecuteChanged();
            Send73Command.NotifyCanExecuteChanged();
            // Auto echo calibration may have updated the stored trim.
            if (SelectedEchoCalibrationSatellite is not null)
                LoadEchoCalibrationHzForSelected();
            ClearEchoCalibrationCommand.NotifyCanExecuteChanged();
            ClearAllEchoCalibrationsCommand.NotifyCanExecuteChanged();
        });
    }

    private void OnDecodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Inserts are already posted to the UI thread. Syncing here paints the new
        // line in that same turn. A second post, or clearing and recreating every row,
        // left the background for the next decode to apply.
        if (Dispatcher.UIThread.CheckAccess())
            ShowDecodeListChange();
        else
            Dispatcher.UIThread.Post(ShowDecodeListChange);
    }

    private void ShowDecodeListChange()
    {
        QsoPartnerCall = _modem.Sequencer?.TheirCall;
        SyncDecodeRows();
        RefreshRxMarker();
    }

    /// <summary>
    /// Keep one row per modem message. New lines are inserted in place with their
    /// background already set, so existing rows are not thrown away and repainted later.
    /// </summary>
    private void SyncDecodeRows()
    {
        var messages = _modem.Decodes;
        var live = new HashSet<Ft4DecodedMessage>(messages, ReferenceEqualityComparer.Instance);
        for (var i = Decodes.Count - 1; i >= 0; i--)
        {
            if (!live.Contains(Decodes[i].Message))
                Decodes.RemoveAt(i);
        }

        var rowIndex = 0;
        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            var message = messages[messageIndex];
            if (rowIndex < Decodes.Count && ReferenceEquals(Decodes[rowIndex].Message, message))
            {
                rowIndex++;
                continue;
            }

            var row = new Ft4DecodeRowViewModel(message);
            ApplyHighlight(row);
            Decodes.Insert(rowIndex, row);
            rowIndex++;
        }

        while (Decodes.Count > messages.Count)
            Decodes.RemoveAt(Decodes.Count - 1);
    }

    private void RefreshDecodeHighlights()
    {
        foreach (var row in Decodes)
            ApplyHighlight(row);
    }

    private void ApplyHighlight(Ft4DecodeRowViewModel row) =>
        row.RefreshHighlight(
            StationCallsign,
            QsoPartnerCall,
            CallingMeColour,
            ReplyingColour,
            NewCallColour,
            NewGridColour,
            CqColour,
            _workedCalls,
            _workedGridFields,
            _finishedPartners,
            CallingMeTextColour,
            ReplyingTextColour,
            NewCallTextColour,
            NewGridTextColour,
            CqTextColour,
            TxTextColour,
            _highlightPounce);

    private void OnLogbookQsosChanged(long logbookId) => _ = RefreshWorkedSetsAsync();

    private async Task RefreshWorkedSetsAsync()
    {
        try
        {
            var (calls, grids) = await _logbook.LoadWorkedCallAndGridFieldsAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _workedCalls = calls;
                _workedGridFields = grids;
                RefreshDecodeHighlights();
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FT4 worked-call / grid refresh failed");
        }
    }

    private void SyncOscarWatchSpotAvailability()
    {
        var available = Ft4OscarWatchSpots.HasApiToken(_settings.Current.SatelliteStatus.ApiToken);
        if (available != OscarWatchSpotsTokenAvailable)
            OscarWatchSpotsTokenAvailable = available;
        if (!available && OscarWatchSpotsEnabled)
            OscarWatchSpotsEnabled = false;
    }

    private void RefreshUiTick()
    {
        SyncOscarWatchSpotAvailability();
        var utc = Ft4Clock.UtcNow;
        var slotStart = Ft4SlotClock.SlotStartUtc(utc, Ft4SlotClock.Ft4SlotSeconds);
        var into = Ft4SlotClock.SecondsIntoSlot(utc, Ft4SlotClock.Ft4SlotSeconds);
        var even = Ft4SlotClock.IsEvenSlot(slotStart, Ft4SlotClock.Ft4SlotSeconds);
        SlotClockText = $"{slotStart:HH:mm:ss.f} UTC  +{into:0.0}s  {(even ? "even" : "odd")}";
        ClockSourceText = Ft4Clock.MeasuredOffset is not { } measured
            ? _l.Get("Ft4.ClockSource.Pc")
            : Ft4Clock.UsingGps
                ? _l.Get("Ft4.ClockSource.Gps", -measured.TotalSeconds)
                : _l.Get("Ft4.ClockSource.PcGpsAgrees");

        var progress = Math.Clamp(100.0 * into / Ft4SlotClock.Ft4SlotSeconds, 0, 100);
        SlotProgressPercent = progress;
        SlotProgressText = $"{progress:0}%";
        IsTxSlot = _modem.IsLiveTransmitSlot(utc);
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
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _uiTimer.Stop();
        _modem.Changed -= OnModemChanged;
        ((INotifyCollectionChanged)_modem.Decodes).CollectionChanged -= OnDecodesChanged;
        _logbook.QsosChanged -= OnLogbookQsosChanged;
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
