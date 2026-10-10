using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Cloudlog;
using OscarWatch.Core.Display;
using OscarWatch.Core.Ft4;
using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Radio;
using OscarWatch.Core.Services;
using OscarWatch.Localization;

namespace OscarWatch.ViewModels;

public partial class FrequencyOverlayViewModel : ViewModelBase
{
    /// <summary>Maximum magnitude for RX/TX offset spinners and nudge keys (kHz).</summary>
    public const double OffsetMinKHz = -20.0;
    public const double OffsetMaxKHz = 20.0;

    private readonly ISettingsService _settings;
    private readonly ISatelliteDatabaseService _database;
    private readonly ILocalizationService _l;
    private readonly IOrbitPropagator? _propagator;
    private string? _currentSatelliteName;
    private string? _currentStorageKey;
    private string? _currentNoradId;
    private bool _isLoadingSelection;
    private double _lastRangeRateKmPerSec;
    private SatelliteTrackState? _lastTrackState;
    private double _rigPassbandDownlinkAdjustKHz;
    private double _rigPassbandUplinkAdjustKHz;
    private bool _passbandSyncSuspended;
    private bool _dopplerLeadActiveLatched;

    [ObservableProperty]
    private string _satelliteName = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLeadTuningPanelVisible))]
    private bool _hasTransponderData;

    [ObservableProperty]
    private bool _isBeaconOnly;

    [ObservableProperty]
    private bool _showBelowHorizonDim;

    [ObservableProperty]
    private string _radioTransmitText = "—";

    [ObservableProperty]
    private string _radioReceiveText = "—";

    /// <summary>Doppler-corrected receive dial frequency, or null when nothing is tracked.</summary>
    public double? RadioReceiveKHz { get; private set; }

    /// <summary>Downlink Doppler shift in kHz, or null when nothing is tracked.</summary>
    public double? DownlinkDopplerKHz { get; private set; }

    [ObservableProperty]
    private string _satelliteTransmitText = "—";

    [ObservableProperty]
    private string _satelliteReceiveText = "—";

    [ObservableProperty]
    private string _dopplerShiftText = "";

    [ObservableProperty]
    private bool _showCtcssRow;

    [ObservableProperty]
    private bool _showCtcssSelector;

    [ObservableProperty]
    private bool _showCtcssStatic;

    [ObservableProperty]
    private string _selectedCtcssText = "";

    [ObservableProperty]
    private CtcssToneOption? _selectedCtcssTone;

    [ObservableProperty]
    private string _ctcssHintText = "";

    [ObservableProperty]
    private bool _showCtcssHint;

    [ObservableProperty]
    private bool _showOperatingStyleRow;

    [ObservableProperty]
    private bool _isCwUplink;

    [ObservableProperty]
    private DopplerStrategy _dopplerStrategy = DopplerStrategy.Full;

    [ObservableProperty]
    private string _operatingStyleHint = "";

    [ObservableProperty]
    private string _dopplerStrategyHint = "";

    public ObservableCollection<CtcssToneOption> CtcssToneOptions { get; } = [];

    [ObservableProperty]
    private double _receiveOffsetKHz;

    [ObservableProperty]
    private double _transmitOffsetKHz;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveOffsetKHz))]
    [NotifyPropertyChangedFor(nameof(OffsetLabelText))]
    private bool _isTransmitOffsetSelected;

    [ObservableProperty]
    private string _offsetAppliedHint = "";

    [ObservableProperty]
    private double _overlayX = 12;

    [ObservableProperty]
    private double _overlayY = 12;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLeadTuningPanelVisible))]
    private bool _isCollapsed;

    [ObservableProperty]
    private string _collapsedSummaryText = "";

    [ObservableProperty]
    private string _emptyStateMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DopplerLeadToolTip))]
    private bool _showDopplerLeadIndicator;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DopplerLeadToolTip))]
    private bool _isDopplerLeadActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLeadTuningPanelVisible))]
    private bool _showLeadTuningPanel;

    [ObservableProperty]
    private int _liveLeadGainPercent = 100;

    [ObservableProperty]
    private int _liveLeadMs;

    private bool _syncingLeadTuning;

    public bool ShowLeadTuningPanelVisible =>
        ShowLeadTuningPanel && !IsCollapsed && HasTransponderData;

    public string DopplerLeadToolTip =>
        !ShowDopplerLeadIndicator
            ? ""
            : IsDopplerLeadActive
                ? _l.Get("Freq.DopplerLeadActive")
                : _l.Get("Freq.DopplerLeadEnabled");

    public double OverlayMinWidth => IsCollapsed ? 220 : 380;

    public double OverlayMaxWidth => IsCollapsed ? 720 : 520;

    public string CollapseToggleGlyph => IsCollapsed ? "▶" : "▼";

    public string CollapseToggleToolTip => IsCollapsed
        ? _l.Get("Freq.CollapseExpand")
        : _l.Get("Freq.CollapseCompact");

    public bool ShowHeaderOperatingStyle => ShowOperatingStyleRow && !IsCollapsed;

    public bool IsVoiceOperatingStyleSelected => ShowOperatingStyleRow && !IsCwUplink;

    public bool IsCwOperatingStyleSelected => ShowOperatingStyleRow && IsCwUplink;

    public bool ShowDopplerStrategyRow => HasTransponderData && !IsBeaconOnly;

    public bool IsFullDopplerSelected => DopplerStrategy == DopplerStrategy.Full;

    public bool IsTxFixedDopplerSelected => DopplerStrategy == DopplerStrategy.DownlinkOnly;

    public bool IsRxFixedDopplerSelected => DopplerStrategy == DopplerStrategy.UplinkOnly;

    public bool ShowOffsetLegToggle => HasTransponderData && !IsBeaconOnly;

    public string OffsetLabelText => IsTransmitOffsetSelected
        ? _l.Get("Freq.TxOffsetKHz")
        : _l.Get("Freq.RxOffsetKHz");

    public double ActiveOffsetKHz
    {
        get => IsTransmitOffsetSelected ? TransmitOffsetKHz : ReceiveOffsetKHz;
        set
        {
            var clamped = Math.Clamp(value, OffsetMinKHz, OffsetMaxKHz);
            if (IsTransmitOffsetSelected)
                TransmitOffsetKHz = clamped;
            else
                ReceiveOffsetKHz = clamped;
        }
    }

    [ObservableProperty]
    private SatelliteTransponderMode? _selectedMode;

    [ObservableProperty]
    private bool _canReportSatelliteStatus;

    [ObservableProperty]
    private bool _showCommunityStatus;

    [ObservableProperty]
    private string _communityStatusText = "?";

    [ObservableProperty]
    private string _communityStatusToolTip = "";

    [ObservableProperty]
    private bool _isCommunityStatusStale;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommunityStatusIsOn))]
    [NotifyPropertyChangedFor(nameof(CommunityStatusIsOff))]
    [NotifyPropertyChangedFor(nameof(CommunityStatusIsTelem))]
    [NotifyPropertyChangedFor(nameof(CommunityStatusIsUnknown))]
    private SatelliteCommunityStatusKind _communityStatusKind = SatelliteCommunityStatusKind.Unknown;

    public bool CommunityStatusIsOn => CommunityStatusKind == SatelliteCommunityStatusKind.On;
    public bool CommunityStatusIsOff => CommunityStatusKind == SatelliteCommunityStatusKind.Off;
    public bool CommunityStatusIsTelem => CommunityStatusKind == SatelliteCommunityStatusKind.TelemetryOnly;
    public bool CommunityStatusIsUnknown => CommunityStatusKind == SatelliteCommunityStatusKind.Unknown;

    public ObservableCollection<SatelliteTransponderMode> AvailableModes { get; } = [];

    /// <summary>Raised when the operator requests a satellite status report from the frequency overlay.</summary>
    public event EventHandler? ReportSatelliteStatusRequested;

    public FrequencyOverlayViewModel(
        ISettingsService settings,
        ISatelliteDatabaseService database,
        ILocalizationService localization,
        IOrbitPropagator? propagator = null)
    {
        _settings = settings;
        _database = database;
        _l = localization;
        _propagator = propagator;
        OverlayX = settings.Current.FrequencyOverlayX;
        OverlayY = settings.Current.FrequencyOverlayY;
        IsCollapsed = settings.Current.FrequencyOverlayCollapsed;
        EmptyStateMessage = _l.Get("Freq.SelectSatelliteHint");
        CollapsedSummaryText = _l.Get("Freq.SelectSatellite");
    }

    [RelayCommand]
    private void ToggleCollapse()
    {
        IsCollapsed = !IsCollapsed;
    }

    [RelayCommand]
    private void SelectVoiceOperatingStyle() => SetCwUplink(false);

    [RelayCommand]
    private void SelectCwOperatingStyle() => SetCwUplink(true);

    [RelayCommand]
    private void ToggleCwUplink() => SetCwUplink(!IsCwUplink);

    [RelayCommand]
    private void SelectFullDoppler() => SetDopplerStrategy(DopplerStrategy.Full);

    [RelayCommand]
    private void SelectTxFixedDoppler() => SetDopplerStrategy(DopplerStrategy.DownlinkOnly);

    [RelayCommand]
    private void SelectRxFixedDoppler() => SetDopplerStrategy(DopplerStrategy.UplinkOnly);

    [RelayCommand(CanExecute = nameof(CanExecuteReportSatelliteStatus))]
    private void ReportSatelliteStatus() => ReportSatelliteStatusRequested?.Invoke(this, EventArgs.Empty);

    private bool CanExecuteReportSatelliteStatus() => CanReportSatelliteStatus;

    public void RefreshSatelliteStatusReportAvailability() => UpdateCanReportSatelliteStatus();

    /// <summary>Elevation of the track state last applied to the overlay, if known.</summary>
    public double? TryGetFocusedElevationDeg() => _lastTrackState?.LookAngles?.ElevationDeg;

    public void ApplyCommunityStatus(
        bool show,
        SatelliteCommunityStatusKind kind,
        string shortText,
        string toolTip,
        bool stale)
    {
        ShowCommunityStatus = show;
        CommunityStatusKind = kind;
        CommunityStatusText = shortText;
        CommunityStatusToolTip = toolTip;
        IsCommunityStatusStale = stale;
    }

    public void ClearCommunityStatus() =>
        ApplyCommunityStatus(false, SatelliteCommunityStatusKind.Unknown, "?", "", false);

    private void UpdateCanReportSatelliteStatus()
    {
        var cfg = _settings.Current.SatelliteStatus;
        var elevationDeg = _lastTrackState?.LookAngles?.ElevationDeg;
        var can = cfg is { Enabled: true }
                  && !string.IsNullOrWhiteSpace(cfg.ApiToken)
                  && !string.IsNullOrWhiteSpace(_currentSatelliteName)
                  && SelectedMode is not null
                  && !string.IsNullOrWhiteSpace(SelectedMode.Type)
                  && SatelliteStatusReportFormatting.IsElevationReportable(elevationDeg);

        if (CanReportSatelliteStatus == can)
        {
            ReportSatelliteStatusCommand.NotifyCanExecuteChanged();
            return;
        }

        CanReportSatelliteStatus = can;
    }

    partial void OnCanReportSatelliteStatusChanged(bool value) =>
        ReportSatelliteStatusCommand.NotifyCanExecuteChanged();


    public void SetDopplerStrategy(DopplerStrategy strategy)
    {
        if (!ShowDopplerStrategyRow || DopplerStrategy == strategy)
            return;

        DopplerStrategy = strategy;
    }

    public void SetCwUplink(bool cwUplink)
    {
        if (!ShowOperatingStyleRow || IsCwUplink == cwUplink)
            return;

        IsCwUplink = cwUplink;
    }

    partial void OnIsCollapsedChanged(bool value)
    {
        _settings.Current.FrequencyOverlayCollapsed = value;
        _settings.RequestSave();
        OnPropertyChanged(nameof(CollapseToggleGlyph));
        OnPropertyChanged(nameof(CollapseToggleToolTip));
        OnPropertyChanged(nameof(OverlayMinWidth));
        OnPropertyChanged(nameof(OverlayMaxWidth));
        OnPropertyChanged(nameof(ShowHeaderOperatingStyle));
        NotifyOperatingStyleSelectionChanged();
        RequestOverlayReclamp();
    }

    /// <summary>Keep the full panel inside the map area (called after measure / resize).</summary>
    public void EnsureOverlayWithinHost(double hostWidth, double hostHeight, double overlayWidth, double overlayHeight)
    {
        if (hostWidth <= 0 || hostHeight <= 0 || overlayWidth <= 0 || overlayHeight <= 0)
            return;

        const double edge = 8;
        var maxX = Math.Max(edge, hostWidth - overlayWidth - edge);
        var maxY = Math.Max(edge, hostHeight - overlayHeight - edge);
        var x = Math.Clamp(OverlayX, edge, maxX);
        var y = Math.Clamp(OverlayY, edge, maxY);
        if (Math.Abs(x - OverlayX) > 0.5 || Math.Abs(y - OverlayY) > 0.5)
            SetOverlayPosition(x, y);
    }

    public void ReloadLayoutFromSettings()
    {
        OverlayX = _settings.Current.FrequencyOverlayX;
        OverlayY = _settings.Current.FrequencyOverlayY;
        IsCollapsed = _settings.Current.FrequencyOverlayCollapsed;
        OnPropertyChanged(nameof(OverlayMargin));
        RequestOverlayReclamp();
    }

    public void ReloadFromDatabase()
    {
        _database.Reload();
        if (string.IsNullOrEmpty(_currentSatelliteName))
            return;

        LoadModesForSatellite(_currentSatelliteName, _currentNoradId);
        if (_lastTrackState is not null)
            Update(_lastTrackState);
    }

    public void Update(SatelliteTrackState? state)
    {
        _lastTrackState = state;
        if (state is null)
        {
            SatelliteName = "—";
            _currentNoradId = null;
            _currentSatelliteName = null;
            _currentStorageKey = null;
            _passbandSyncSuspended = false;
            HasTransponderData = false;
            EmptyStateMessage = _l.Get("Freq.SelectSatelliteHint");
            ClearFrequencyDisplay();
            UpdateCanReportSatelliteStatus();
            return;
        }

        SatelliteName = state.Name;
        ShowBelowHorizonDim = state.LookAngles is null || state.LookAngles.ElevationDeg < 0;

        if (!string.Equals(_currentNoradId, state.NoradId, StringComparison.Ordinal))
        {
            _currentNoradId = state.NoradId;
            _currentSatelliteName = state.Name;
            _currentStorageKey = ResolveStorageKey(state.Name, state.NoradId);
            _rigPassbandDownlinkAdjustKHz = 0;
            _rigPassbandUplinkAdjustKHz = 0;
            _passbandSyncSuspended = true;
            LoadModesForSatellite(state.Name, state.NoradId);
            RequestOverlayReclamp();
        }

        if (SelectedMode is null)
        {
            HasTransponderData = false;
            EmptyStateMessage = _l.Get("Freq.NoTransponder");
            ClearFrequencyDisplay();
            UpdateCanReportSatelliteStatus();
            return;
        }

        HasTransponderData = true;
        IsBeaconOnly = SelectedMode.IsBeaconOnly;

        _lastRangeRateKmPerSec = state.LookAngles?.RangeRateKmPerSec ?? 0;
        ApplyFrequencyDisplay(state);
        UpdateCanReportSatelliteStatus();
    }

    private CorrectedFrequencies ComputeCorrected(
        double rxRangeRateKmPerSec,
        double? txRangeRateKmPerSec = null) =>
        DopplerFrequencyCalculator.Compute(
            SelectedMode!,
            rxRangeRateKmPerSec,
            ReceiveOffsetKHz,
            GetEffectiveTransmitOffsetKHz(),
            _rigPassbandDownlinkAdjustKHz,
            _rigPassbandUplinkAdjustKHz,
            DopplerStrategy,
            txRangeRateKmPerSec);

    private DopplerLeadRangeRates ResolveRangeRates(SatelliteTrackState state) =>
        DopplerCatLead.ResolveRangeRates(
            _propagator,
            _settings.Current.Rig,
            _settings.Current.GroundStation,
            state,
            DateTime.UtcNow,
            SelectedMode?.DownlinkMode,
            SelectedMode?.UplinkMode);

    private void ApplyFrequencyDisplay(SatelliteTrackState state)
    {
        var snapshotRate = state.LookAngles?.RangeRateKmPerSec ?? 0;
        var leadRates = ResolveRangeRates(state);
        UpdateDopplerLeadIndicator(leadRates.LeadBlend, state.LookAngles?.ElevationDeg);
        var snapshotCorrected = ComputeCorrected(snapshotRate);
        var radioCorrected = ComputeCorrected(leadRates.RxRangeRateKmPerSec, leadRates.TxRangeRateKmPerSec);
        ApplyCorrectedDisplay(snapshotCorrected, radioCorrected);
    }

    private void UpdateDopplerLeadIndicator(double leadBlend, double? elevationDeg)
    {
        var aboveHorizon = elevationDeg is >= 0;
        var dataMode = TransponderCatModes.IsData(SelectedMode?.DownlinkMode)
            || TransponderCatModes.IsData(SelectedMode?.UplinkMode);
        ShowDopplerLeadIndicator = _settings.Current.Rig.DopplerCatLeadEnabled && aboveHorizon && !dataMode;
        if (!ShowDopplerLeadIndicator)
        {
            _dopplerLeadActiveLatched = false;
            IsDopplerLeadActive = false;
            return;
        }

        const double activateBlend = 0.08;
        const double deactivateBlend = 0.04;
        if (leadBlend >= activateBlend)
            _dopplerLeadActiveLatched = true;
        else if (leadBlend < deactivateBlend)
            _dopplerLeadActiveLatched = false;

        IsDopplerLeadActive = _dopplerLeadActiveLatched;
    }

    public CloudlogRadioUpdate? TryBuildCloudlogUpdate(SatelliteTrackState? state)
    {
        if (state is null || SelectedMode is null)
            return null;

        var (rxRate, txRate) = ResolveRangeRates(state);
        var corrected = ComputeCorrected(rxRate, txRate);

        return CloudlogRadioMapper.TryCreate(
            state.Name,
            SelectedMode,
            corrected,
            IsCwUplink,
            CwKeepSidebandDownlink);
    }

    public RigTrackingContext? TryBuildRigTrackingContext(SatelliteTrackState? state)
    {
        if (state is null || SelectedMode is null || state.LookAngles is null)
            return null;

        // Overlay modes reload when NoradId changes; don't drive CAT until synced.
        if (!string.Equals(state.NoradId, _currentNoradId, StringComparison.Ordinal))
            return null;

        var (rxRate, txRate) = ResolveRangeRates(state);
        var corrected = ComputeCorrected(rxRate, txRate);

        return new RigTrackingContext
        {
            TrackState = state,
            Mode = SelectedMode,
            Corrected = corrected,
            TransmitOffsetKHz = GetEffectiveTransmitOffsetKHz(),
            ReceiveOffsetKHz = ReceiveOffsetKHz,
            SelectedCtcssHz = GetActiveCtcssHz(),
            CwUplink = IsCwUplink,
            CwKeepSidebandDownlink = CwKeepSidebandDownlink,
            DopplerStrategy = DopplerStrategy
        };
    }

    /// <summary>
    /// Operator TX offset plus any FT4 echo-calibration for the current satellite mode.
    /// </summary>
    private double GetEffectiveTransmitOffsetKHz()
    {
        var txOffset = TransmitOffsetKHz;
        if (Ft4SlotGate.IsFt4Transponder(SelectedMode)
            && !string.IsNullOrWhiteSpace(_currentSatelliteName))
        {
            txOffset += _settings.Current.Ft4.GetUplinkCalibrationKHz(_currentSatelliteName);
        }

        return txOffset;
    }

    private bool CwKeepSidebandDownlink =>
        _settings.Current.Rig?.CwKeepSidebandDownlink == true;

    /// <summary>Hz sent to the uplink VFO (combo selection, or the mode's only tone).</summary>
    public double? GetActiveCtcssHz()
    {
        if (SelectedCtcssTone is { } selected)
            return selected.Hz;

        if (SelectedMode is null || !SelectedMode.HasAnyCtcss)
            return null;

        if (SelectedMode.HasCtcss)
            return SelectedMode.CtcssHz;

        return SelectedMode.CtcssArmHz;
    }

    public Thickness OverlayMargin => new(OverlayX, OverlayY, 0, 0);

    public void SetOverlayPosition(double x, double y, bool persist = false)
    {
        OverlayX = x;
        OverlayY = y;
        OnPropertyChanged(nameof(OverlayMargin));
        if (!persist)
            return;

        _settings.Current.FrequencyOverlayX = x;
        _settings.Current.FrequencyOverlayY = y;
        _settings.RequestSave();
    }

    partial void OnOverlayXChanged(double value) => OnPropertyChanged(nameof(OverlayMargin));

    partial void OnOverlayYChanged(double value) => OnPropertyChanged(nameof(OverlayMargin));

    public void PersistOverlayPosition()
    {
        _settings.Current.FrequencyOverlayX = OverlayX;
        _settings.Current.FrequencyOverlayY = OverlayY;
        _settings.RequestSave();
    }

    /// <summary>Height may change when modes load; view reclamps on next layout pass.</summary>
    public event EventHandler? OverlayLayoutChanged;

    /// <summary>TX/RX micro-adjust offsets changed — rig should refresh CAT promptly.</summary>
    /// <remarks>Event arg is <c>true</c> when transponder mode changed (full pass re-init); <c>false</c> for RX offset edits only.</remarks>
    public event EventHandler<bool>? OffsetsChanged;

    /// <summary>Raised when the overlay satellite or loaded modes change (community status chip should refresh from cache).</summary>
    public event EventHandler? CommunityStatusContextChanged;

    public event EventHandler? CtcssChanged;

    public event EventHandler? LeadTuningChanged;

    public void ToggleLeadTuningPanel()
    {
        ShowLeadTuningPanel = !ShowLeadTuningPanel;
    }

    partial void OnShowLeadTuningPanelChanged(bool value)
    {
        if (value)
            SyncLeadTuningFromSettings();
    }

    partial void OnLiveLeadGainPercentChanged(int value) => ApplyLiveLeadTuningToRig();

    partial void OnLiveLeadMsChanged(int value) => ApplyLiveLeadTuningToRig();

    private void SyncLeadTuningFromSettings()
    {
        var rig = _settings.Current.Rig;
        _syncingLeadTuning = true;
        LiveLeadGainPercent = rig.DopplerCatLeadGainPercent is > 0 and <= 100
            ? rig.DopplerCatLeadGainPercent
            : RigSettings.DefaultDopplerCatLeadGainPercent;
        LiveLeadMs = Math.Clamp(rig.DopplerCatLeadMs, 0, DopplerCatLead.UserLeadMsMax);
        _syncingLeadTuning = false;
    }

    private void ApplyLiveLeadTuningToRig()
    {
        if (_syncingLeadTuning)
            return;

        _settings.Current.Rig.DopplerCatLeadGainPercent = Math.Clamp(LiveLeadGainPercent, 1, 100);
        _settings.Current.Rig.DopplerCatLeadMs = Math.Clamp(LiveLeadMs, 0, DopplerCatLead.UserLeadMsMax);

        if (_lastTrackState is not null)
            ApplyFrequencyDisplay(_lastTrackState);

        LeadTuningChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RequestOverlayReclamp() => OverlayLayoutChanged?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedModeChanged(SatelliteTransponderMode? value)
    {
        if (_isLoadingSelection || string.IsNullOrEmpty(_currentSatelliteName))
            return;

        _rigPassbandDownlinkAdjustKHz = 0;
        _rigPassbandUplinkAdjustKHz = 0;

        if (_currentSatelliteName is not null)
        {
            ApplyOperatingStyleForSelectedMode();
            ApplyDopplerStrategyForSelectedMode();
            UpdateCtcssDisplay(restoreToneRole: GetOrCreateSelection().CtcssToneRole);
            ApplyOffsetsForSelectedMode();
            UpdateFromCurrentState();
        }

        if (value is not null)
            PersistSelection();

        OffsetsChanged?.Invoke(this, true);
        RequestOverlayReclamp();
        StoreOffsetCommand.NotifyCanExecuteChanged();
        UpdateCanReportSatelliteStatus();
    }

    partial void OnReceiveOffsetKHzChanged(double value)
    {
        if (_isLoadingSelection)
            return;

        var clamped = Math.Clamp(value, OffsetMinKHz, OffsetMaxKHz);
        if (Math.Abs(value - clamped) > 0.0001)
        {
            ReceiveOffsetKHz = clamped;
            return;
        }

        OnPropertyChanged(nameof(ActiveOffsetKHz));
        ApplyOffsetEdit();
    }

    partial void OnTransmitOffsetKHzChanged(double value)
    {
        if (_isLoadingSelection)
            return;

        var clamped = Math.Clamp(value, OffsetMinKHz, OffsetMaxKHz);
        if (Math.Abs(value - clamped) > 0.0001)
        {
            TransmitOffsetKHz = clamped;
            return;
        }

        OnPropertyChanged(nameof(ActiveOffsetKHz));
        ApplyOffsetEdit();
    }

    partial void OnIsTransmitOffsetSelectedChanged(bool value)
    {
        if (_isLoadingSelection)
            return;

        OnPropertyChanged(nameof(ActiveOffsetKHz));
        OnPropertyChanged(nameof(OffsetLabelText));
        if (SelectedMode is not null)
        {
            GetOrCreateSelection().SetTransmitOffsetSelectedForMode(SelectedMode.Type, value);
            _settings.RequestSave();
        }

        RefreshFrequencyDisplay();
        OffsetsChanged?.Invoke(this, false);
    }

    /// <summary>Called when offset changes (spinner or step buttons).</summary>
    public void ApplyOffsetEdit()
    {
        RefreshFrequencyDisplay();
        OffsetsChanged?.Invoke(this, false);
    }

    /// <summary>Active offset nudge in Hz (RX or TX leg per toggle).</summary>
    public void AdjustActiveOffsetHz(int deltaHz)
    {
        ActiveOffsetKHz = Math.Clamp(ActiveOffsetKHz + deltaHz / 1000.0, OffsetMinKHz, OffsetMaxKHz);
    }

    /// <summary>Receive offset nudge in Hz (applied to downlink nominal before doppler).</summary>
    public void AdjustReceiveOffsetHz(int deltaHz) => AdjustActiveOffsetHz(deltaHz);

    [RelayCommand(CanExecute = nameof(CanStoreOffset))]
    private void StoreOffset()
    {
        if (SelectedMode is null)
            return;

        var selection = GetOrCreateSelection();
        selection.RememberOffsets = true;
        var cw = UseCwOffsetStorage();
        if (IsTransmitOffsetSelected)
        {
            selection.SetTransmitOffsetForMode(SelectedMode.Type, TransmitOffsetKHz, cw);
            var hz = (int)Math.Round(TransmitOffsetKHz * 1000.0);
            OffsetAppliedHint = hz == 0
                ? _l.Get(cw ? "Freq.StoredOffsetClearedCwTx" : "Freq.StoredOffsetClearedTx")
                : _l.Get(cw ? "Freq.StoredOffsetCwTx" : "Freq.StoredOffsetTx", hz);
        }
        else
        {
            selection.SetReceiveOffsetForMode(SelectedMode.Type, ReceiveOffsetKHz, cw);
            var hz = (int)Math.Round(ReceiveOffsetKHz * 1000.0);
            OffsetAppliedHint = hz == 0
                ? _l.Get(cw ? "Freq.StoredOffsetClearedCw" : "Freq.StoredOffsetCleared")
                : _l.Get(cw ? "Freq.StoredOffsetCw" : "Freq.StoredOffset", hz);
        }

        _settings.RequestSave();
    }

    private bool CanStoreOffset() => HasTransponderData && SelectedMode is not null;

    partial void OnShowOperatingStyleRowChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowHeaderOperatingStyle));
        NotifyOperatingStyleSelectionChanged();
    }

    partial void OnHasTransponderDataChanged(bool value)
    {
        StoreOffsetCommand.NotifyCanExecuteChanged();
        NotifyDopplerStrategySelectionChanged();
        OnPropertyChanged(nameof(ShowOffsetLegToggle));
        UpdateCollapsedSummaryText();
    }

    /// <summary>Recompute Radio/Sat frequencies from last track state and current offsets.</summary>
    public void RefreshFrequencyDisplay()
    {
        if (SelectedMode is null)
        {
            OffsetAppliedHint = "";
            return;
        }

        if (_lastTrackState is not null)
            ApplyFrequencyDisplay(_lastTrackState);
        else
            ApplyCorrectedDisplay(ComputeCorrected(_lastRangeRateKmPerSec), ComputeCorrected(_lastRangeRateKmPerSec));
        UpdateOffsetAppliedHint();
    }

    /// <summary>Main-dial passband trim from rig while tracking (updates downlink/uplink nominals).</summary>
    public void SyncRigPassbandAdjustments(double downlinkAdjustKHz, double uplinkAdjustKHz)
    {
        if (_passbandSyncSuspended)
        {
            if (Math.Abs(downlinkAdjustKHz) > 0.0001 || Math.Abs(uplinkAdjustKHz) > 0.0001)
                return;

            _passbandSyncSuspended = false;
        }

        if (Math.Abs(_rigPassbandDownlinkAdjustKHz - downlinkAdjustKHz) < 0.0001
            && Math.Abs(_rigPassbandUplinkAdjustKHz - uplinkAdjustKHz) < 0.0001)
            return;

        _rigPassbandDownlinkAdjustKHz = downlinkAdjustKHz;
        _rigPassbandUplinkAdjustKHz = uplinkAdjustKHz;
        RefreshFrequencyDisplay();
    }

    private void ApplyCorrectedDisplay(CorrectedFrequencies snapshotCorrected, CorrectedFrequencies radioCorrected)
    {
        IsBeaconOnly = SelectedMode?.IsBeaconOnly == true;
        RadioTransmitText = IsBeaconOnly ? "—" : FrequencyDisplayFormat.FormatMHz(radioCorrected.RadioTransmitKHz);
        RadioReceiveText = FrequencyDisplayFormat.FormatMHz(radioCorrected.RadioReceiveKHz);
        RadioReceiveKHz = radioCorrected.RadioReceiveKHz;
        DownlinkDopplerKHz = snapshotCorrected.DopplerShiftKHz;
        SatelliteTransmitText = IsBeaconOnly ? "—" : FrequencyDisplayFormat.FormatMHz(snapshotCorrected.SatelliteTransmitKHz);
        SatelliteReceiveText = FrequencyDisplayFormat.FormatMHz(snapshotCorrected.SatelliteReceiveKHz);
        DopplerShiftText = FrequencyDisplayFormat.FormatDopplerKHz(snapshotCorrected.DopplerShiftKHz);
        UpdateCollapsedSummaryText(radioCorrected);
    }

    private void UpdateCollapsedSummaryText(CorrectedFrequencies? corrected = null)
    {
        if (!HasTransponderData || SelectedMode is null)
        {
            CollapsedSummaryText = _currentNoradId is null
                ? _l.Get("Freq.SelectSatellite")
                : _l.Get("Freq.CollapsedNoTransponder", SatelliteName);
            return;
        }

        var mode = SelectedMode.DisplayLabel;
        if (IsCwUplink && ShowOperatingStyleRow)
            mode += _l.Get("Freq.CwSuffix");

        if (corrected is null)
        {
            CollapsedSummaryText = SatelliteName;
            return;
        }

        if (IsBeaconOnly)
        {
            CollapsedSummaryText =
                $"{SatelliteName} · {mode} · {FrequencyDisplayFormat.FormatMHzCompact(corrected.RadioReceiveKHz)}";
            return;
        }

        CollapsedSummaryText =
            $"{SatelliteName} · {mode} · {FrequencyDisplayFormat.FormatMHzCompact(corrected.RadioTransmitKHz)} / {FrequencyDisplayFormat.FormatMHzCompact(corrected.RadioReceiveKHz)}";
    }

    private void UpdateOffsetAppliedHint()
    {
        if (SelectedMode is null)
        {
            OffsetAppliedHint = "";
            return;
        }

        var isRev = SelectedMode.DopplerCorrection == DopplerCorrection.Reverse;
        var hasRx = Math.Abs(ReceiveOffsetKHz) > 0.0001;
        var hasTx = !IsBeaconOnly && Math.Abs(TransmitOffsetKHz) > 0.0001;

        if (hasRx && hasTx)
        {
            var rxOffset = $"{ReceiveOffsetKHz:+0.000;-0.000;0} kHz";
            var txOffset = $"{TransmitOffsetKHz:+0.000;-0.000;0} kHz";
            OffsetAppliedHint = isRev
                ? _l.Get("Freq.OffsetOnBothLegsRev", rxOffset, txOffset)
                : _l.Get("Freq.OffsetOnBothLegs", rxOffset, txOffset);
            return;
        }

        if (hasRx)
        {
            var offset = $"{ReceiveOffsetKHz:+0.000;-0.000;0} kHz";
            OffsetAppliedHint = isRev
                ? _l.Get("Freq.OffsetOnDownlinkTuneUplink", offset)
                : _l.Get("Freq.OffsetOnDownlink", offset);
            return;
        }

        if (hasTx)
        {
            var offset = $"{TransmitOffsetKHz:+0.000;-0.000;0} kHz";
            OffsetAppliedHint = isRev
                ? _l.Get("Freq.OffsetOnUplinkTuneDownlink", offset)
                : _l.Get("Freq.OffsetOnUplink", offset);
            return;
        }

        var offsetHint = isRev
            ? _l.Get("Freq.DownlinkOnlyTuneUplink")
            : _l.Get("Freq.OffsetsDownlinkOnly");

        OffsetAppliedHint = DopplerStrategy switch
        {
            DopplerStrategy.DownlinkOnly => _l.Get("Freq.TxFixedOffsetHint", offsetHint),
            DopplerStrategy.UplinkOnly => _l.Get("Freq.RxFixedOffsetHint", offsetHint),
            _ => offsetHint
        };
    }

    partial void OnSelectedCtcssToneChanged(CtcssToneOption? value)
    {
        if (_isLoadingSelection)
            return;

        UpdateCtcssHint();
        PersistSelection();
        CtcssChanged?.Invoke(this, EventArgs.Empty);
        RequestOverlayReclamp();
    }

    private void LoadModesForSatellite(string name, string? noradId = null)
    {
        _isLoadingSelection = true;
        try
        {
            AvailableModes.Clear();
            SelectedMode = null;
            var entry = _database.TryGetEntry(name, noradId);
            if (entry is null || entry.Modes.Count == 0)
            {
                HasTransponderData = false;
                ClearFrequencyDisplay();
                return;
            }

            foreach (var mode in entry.Modes)
                AvailableModes.Add(mode);

            var selection = GetOrCreateSelection();
            SelectedMode = ResolveSelectedMode(selection);
            ApplyOperatingStyleForSelectedMode();
            ApplyDopplerStrategyForSelectedMode();
            ApplyOffsetsForSelectedMode();
            UpdateCtcssDisplay(restoreToneRole: selection.CtcssToneRole);
        }
        finally
        {
            _isLoadingSelection = false;
            RequestOverlayReclamp();
            CommunityStatusContextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private SatelliteTransponderMode? ResolveSelectedMode(SatelliteFrequencySelection selection)
    {
        if (AvailableModes.Count == 0)
            return null;

        if (selection.ModeIndex >= 0 && selection.ModeIndex < AvailableModes.Count)
            return AvailableModes[selection.ModeIndex];

        var byType = AvailableModes.FirstOrDefault(m =>
            m.Type.Equals(selection.ModeType, StringComparison.OrdinalIgnoreCase));
        return byType ?? AvailableModes[0];
    }

    private SatelliteFrequencySelection GetOrCreateSelection()
    {
        var key = _currentStorageKey ?? _currentSatelliteName ?? "";
        if (string.IsNullOrWhiteSpace(key))
            return new SatelliteFrequencySelection();

        if (_settings.Current.FrequencySelections.TryGetValue(key, out var existing))
            return existing;

        var created = new SatelliteFrequencySelection();
        _settings.Current.FrequencySelections[key] = created;
        return created;
    }

    private string ResolveStorageKey(string tleName, string? noradId = null)
    {
        var entry = _database.TryGetEntry(tleName, noradId);
        return entry?.Name ?? tleName.Trim();
    }

    private void ApplyOffsetsForSelectedMode()
    {
        if (SelectedMode is null)
            return;

        _isLoadingSelection = true;
        try
        {
            var selection = GetOrCreateSelection();
            ReceiveOffsetKHz = selection.GetReceiveOffsetForMode(
                SelectedMode.Type,
                UseCwOffsetStorage());
            TransmitOffsetKHz = selection.GetTransmitOffsetForMode(
                SelectedMode.Type,
                UseCwOffsetStorage());
            IsTransmitOffsetSelected = !IsBeaconOnly
                && selection.GetTransmitOffsetSelectedForMode(SelectedMode.Type);
            OnPropertyChanged(nameof(ActiveOffsetKHz));
            OnPropertyChanged(nameof(OffsetLabelText));
            OnPropertyChanged(nameof(ShowOffsetLegToggle));
        }
        finally
        {
            _isLoadingSelection = false;
        }
    }

    private void PersistSelection()
    {
        if (string.IsNullOrEmpty(_currentStorageKey) && string.IsNullOrEmpty(_currentSatelliteName))
            return;
        if (SelectedMode is null)
            return;

        var selection = GetOrCreateSelection();
        selection.ModeType = SelectedMode.Type;
        selection.ModeIndex = AvailableModes.IndexOf(SelectedMode);
        if (selection.ModeIndex < 0)
            selection.ModeIndex = 0;
        if (SelectedCtcssTone is not null)
            selection.CtcssToneRole = SelectedCtcssTone.Role;
        if (SelectedMode is not null)
        {
            selection.SetCwUplinkForMode(SelectedMode.Type, IsCwUplink);
            selection.SetDopplerStrategyForMode(SelectedMode.Type, DopplerStrategy);
        }

        _settings.RequestSave();
    }

    partial void OnDopplerStrategyChanged(DopplerStrategy value)
    {
        if (_isLoadingSelection)
            return;

        UpdateDopplerStrategyHint();
        NotifyDopplerStrategySelectionChanged();
        PersistSelection();
        RefreshFrequencyDisplay();
        OffsetsChanged?.Invoke(this, false);
        RequestOverlayReclamp();
    }

    partial void OnIsBeaconOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDopplerStrategyRow));
        OnPropertyChanged(nameof(ShowOffsetLegToggle));
        NotifyDopplerStrategySelectionChanged();
        if (value && IsTransmitOffsetSelected)
            IsTransmitOffsetSelected = false;
    }

    partial void OnIsCwUplinkChanged(bool value)
    {
        if (_isLoadingSelection)
            return;

        UpdateOperatingStyleHint();
        NotifyOperatingStyleSelectionChanged();
        ApplyOffsetsForSelectedMode();
        PersistSelection();
        RefreshFrequencyDisplay();
        OffsetsChanged?.Invoke(this, true);
        RequestOverlayReclamp();
    }

    private bool UseCwOffsetStorage() =>
        ShowOperatingStyleRow && IsCwUplink;

    private bool UseCwReceiveOffsetStorage() => UseCwOffsetStorage();

    private void ApplyOperatingStyleForSelectedMode()
    {
        if (SelectedMode is null)
        {
            ShowOperatingStyleRow = false;
            OperatingStyleHint = "";
            _isLoadingSelection = true;
            try
            {
                IsCwUplink = false;
            }
            finally
            {
                _isLoadingSelection = false;
            }

            return;
        }

        var show = TransponderOperatingModes.SupportsCwUplinkToggle(SelectedMode);
        ShowOperatingStyleRow = show;

        _isLoadingSelection = true;
        try
        {
            IsCwUplink = show && GetOrCreateSelection().GetCwUplinkForMode(SelectedMode.Type);
        }
        finally
        {
            _isLoadingSelection = false;
        }

        UpdateOperatingStyleHint();
        NotifyOperatingStyleSelectionChanged();
    }

    private void NotifyOperatingStyleSelectionChanged()
    {
        OnPropertyChanged(nameof(IsVoiceOperatingStyleSelected));
        OnPropertyChanged(nameof(IsCwOperatingStyleSelected));
    }

    private void ApplyDopplerStrategyForSelectedMode()
    {
        if (SelectedMode is null || SelectedMode.IsBeaconOnly)
        {
            _isLoadingSelection = true;
            try
            {
                DopplerStrategy = DopplerStrategy.Full;
            }
            finally
            {
                _isLoadingSelection = false;
            }

            UpdateDopplerStrategyHint();
            NotifyDopplerStrategySelectionChanged();
            return;
        }

        _isLoadingSelection = true;
        try
        {
            DopplerStrategy = GetOrCreateSelection().GetDopplerStrategyForMode(SelectedMode.Type);
        }
        finally
        {
            _isLoadingSelection = false;
        }

        UpdateDopplerStrategyHint();
        NotifyDopplerStrategySelectionChanged();
    }

    private void NotifyDopplerStrategySelectionChanged()
    {
        OnPropertyChanged(nameof(ShowDopplerStrategyRow));
        OnPropertyChanged(nameof(IsFullDopplerSelected));
        OnPropertyChanged(nameof(IsTxFixedDopplerSelected));
        OnPropertyChanged(nameof(IsRxFixedDopplerSelected));
    }

    private void UpdateDopplerStrategyHint() =>
        DopplerStrategyHint = DopplerStrategy switch
        {
            DopplerStrategy.DownlinkOnly => _l.Get("Freq.DopplerTxFixedHint"),
            DopplerStrategy.UplinkOnly => _l.Get("Freq.DopplerRxFixedHint"),
            _ => _l.Get("Freq.DopplerFullHint")
        };

    private void UpdateOperatingStyleHint()
    {
        OperatingStyleHint = IsCwUplink && ShowOperatingStyleRow
            ? CwKeepSidebandDownlink
                ? _l.Get("Freq.OperatingCwSideband")
                : _l.Get("Freq.OperatingCwBoth")
            : ShowOperatingStyleRow
                ? _l.Get("Freq.OperatingVoice")
                : "";
    }

    private void UpdateFromCurrentState() => RefreshFrequencyDisplay();

    private void UpdateCtcssDisplay(string? restoreToneRole = null)
    {
        CtcssToneOptions.Clear();
        SelectedCtcssTone = null;

        if (SelectedMode is null || !SelectedMode.HasAnyCtcss)
        {
            ShowCtcssRow = false;
            ShowCtcssSelector = false;
            ShowCtcssStatic = false;
            SelectedCtcssText = "";
            CtcssHintText = "";
            ShowCtcssHint = false;
            return;
        }

        ShowCtcssRow = true;

        if (SelectedMode.HasCtcss && SelectedMode.HasCtcssArm)
        {
            var access = new CtcssToneOption("access", _l.Get("Freq.CtcssAccess"), SelectedMode.CtcssHz!.Value);
            var arm = new CtcssToneOption("arm", _l.Get("Freq.CtcssArm"), SelectedMode.CtcssArmHz!.Value);
            CtcssToneOptions.Add(access);
            CtcssToneOptions.Add(arm);

            ShowCtcssSelector = true;
            ShowCtcssStatic = false;
            SelectedCtcssText = "";

            var role = restoreToneRole ?? GetOrCreateSelection().CtcssToneRole;
            SelectedCtcssTone = role.Equals("arm", StringComparison.OrdinalIgnoreCase) ? arm : access;
        }
        else
        {
            ShowCtcssSelector = false;
            ShowCtcssStatic = true;
            SelectedCtcssText = SelectedMode.HasCtcss
                ? SelectedMode.CtcssAccessDisplay
                : SelectedMode.CtcssArmDisplay;
        }

        UpdateCtcssHint();
    }

    private void UpdateCtcssHint()
    {
        if (SelectedMode is null || !SelectedMode.HasAnyCtcss)
        {
            CtcssHintText = "";
            ShowCtcssHint = false;
            return;
        }

        var armSelected = SelectedCtcssTone?.Role.Equals("arm", StringComparison.OrdinalIgnoreCase) == true
            || (!ShowCtcssSelector && SelectedMode.HasCtcssArm && !SelectedMode.HasCtcss);

        CtcssHintText = armSelected ? SelectedMode.CtcssHintLine : "";
        ShowCtcssHint = !string.IsNullOrEmpty(CtcssHintText);
    }

    private void ClearFrequencyDisplay()
    {
        RadioTransmitText = "—";
        RadioReceiveText = "—";
        RadioReceiveKHz = null;
        DownlinkDopplerKHz = null;
        SatelliteTransmitText = "—";
        SatelliteReceiveText = "—";
        DopplerShiftText = "";
        ShowDopplerLeadIndicator = false;
        IsDopplerLeadActive = false;
        _dopplerLeadActiveLatched = false;
        IsBeaconOnly = false;
        ShowCtcssRow = false;
        ShowCtcssSelector = false;
        ShowCtcssStatic = false;
        SelectedCtcssText = "";
        CtcssToneOptions.Clear();
        SelectedCtcssTone = null;
        CtcssHintText = "";
        ShowCtcssHint = false;
        ShowOperatingStyleRow = false;
        IsCwUplink = false;
        OperatingStyleHint = "";
        DopplerStrategy = DopplerStrategy.Full;
        DopplerStrategyHint = "";
        ReceiveOffsetKHz = 0;
        TransmitOffsetKHz = 0;
        IsTransmitOffsetSelected = false;
        OffsetAppliedHint = "";
        NotifyDopplerStrategySelectionChanged();
        UpdateCollapsedSummaryText();
    }
}
