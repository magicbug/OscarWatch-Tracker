using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Dxcc;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Logbook;
using OscarWatch.Core.Models;
using OscarWatch.Core.Qrz;
using OscarWatch.Core.Radio;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using Serilog;

namespace OscarWatch.ViewModels;

public partial class QsoLogbookViewModel : ViewModelBase, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<QsoLogbookViewModel>();
    private readonly IQsoLogbookRepository _repository;
    private readonly ILiveTrackerSnapshotProvider _tracker;
    private readonly ISettingsService _settings;
    private readonly ICloudlogQsoUploadService _cloudlogUpload;
    private readonly ISatelliteLinkBroadcastService _satelliteLink;
    private readonly ISatelliteStatusReportService _satelliteStatus;
    private readonly IDxccLookupService _dxccLookup;
    private readonly IQrzCallbookService _qrz;
    private readonly IHamQthCallbookService _hamQth;
    private readonly ILocalizationService _l;
    private readonly DispatcherTimer _liveTimer;
    private string _lastRstModeKey = "";
    private bool _snrRst;
    private bool _suppressCallLookup;
    private bool _suppressFieldCoercion;
    private QsoRecord? _editingSource;
    private int _callLookupGeneration;
    private bool _dxccBackfillStarted;
    private CancellationTokenSource? _qrzLookupCts;
    private readonly OscarWatch.Services.QsoLogbookEntryBridge? _entryBridge;
    /// <summary>Locator sent from a decoder; call lookups keep it instead of clearing or replacing it.</summary>
    private string? _decodedGrid;
    /// <summary>Call the decoded locator belongs to, or null when it arrived before any call.</summary>
    private string? _decodedGridCall;

    public QsoLogbookViewModel(
        IQsoLogbookRepository repository,
        ILiveTrackerSnapshotProvider tracker,
        ISettingsService settings,
        ICloudlogQsoUploadService cloudlogUpload,
        ISatelliteLinkBroadcastService satelliteLink,
        ISatelliteStatusReportService satelliteStatus,
        IDxccLookupService dxccLookup,
        IQrzCallbookService qrz,
        IHamQthCallbookService hamQth,
        ILocalizationService localization,
        OscarWatch.Hardware.CwKeyerService? cwKeyer = null,
        OscarWatch.Services.QsoLogbookEntryBridge? entryBridge = null)
    {
        _entryBridge = entryBridge;
        _repository = repository;
        _tracker = tracker;
        _settings = settings;
        _cloudlogUpload = cloudlogUpload;
        _satelliteLink = satelliteLink;
        _satelliteStatus = satelliteStatus;
        _dxccLookup = dxccLookup;
        _qrz = qrz;
        _hamQth = hamQth;
        _l = localization;
        EditClockIdentifier = settings.Current.Use24HourClock ? "24HourClock" : "12HourClock";
        StatusText = _l.Get("Logbook.Status.Ready");
        StationStatusText = _l.Get("Logbook.Station.Unavailable");

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _liveTimer.Tick += (_, _) => RefreshStationPanel();
        _cloudlogUpload.UploadStateChanged += OnCloudlogUploadStateChanged;
        _repository.QsosChanged += OnRepositoryQsosChanged;
        InitializeCwKeyer(cwKeyer);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QsoCountText))]
    [NotifyPropertyChangedFor(nameof(ShowQsoCountText))]
    private int _qsoCount;

    public string QsoCountText =>
        SelectedLogbook is null
            ? ""
            : QsoCount == 1
                ? _l.Get("Logbook.Status.QsoCountOne", SelectedLogbook.Name)
                : _l.Get("Logbook.Status.QsoCountMany", SelectedLogbook.Name, QsoCount);

    public bool ShowQsoCountText => SelectedLogbook is not null;

    public ObservableCollection<string> RstOptions { get; } = new(QsoRstSuggestions.VoiceOptions);

    public ObservableCollection<QsoLogbook> Logbooks { get; } = [];

    public ObservableCollection<LogbookSwitchMenuItemViewModel> LogbookSwitchItems { get; } = [];

    public ObservableCollection<QsoRowViewModel> QsoRows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedQsoCommand))]
    [NotifyCanExecuteChangedFor(nameof(BeginEditSelectedQsoCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReuploadSelectedQsoCommand))]
    private QsoRowViewModel? _selectedQso;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EntryCommitLabel))]
    [NotifyPropertyChangedFor(nameof(ShowCancelEdit))]
    [NotifyPropertyChangedFor(nameof(EditingStatusText))]
    private bool _isEditingQso;

    [ObservableProperty]
    private QsoLogbook? _selectedLogbook;

    [ObservableProperty]
    private string _call = "";

    [ObservableProperty]
    private string _rstSent = "59";

    [ObservableProperty]
    private string _rstRcvd = "59";

    [ObservableProperty]
    private string _grid = "";

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _comment = "";

    [ObservableProperty]
    private DateTimeOffset? _editQsoDate;

    [ObservableProperty]
    private TimeSpan? _editQsoTime;

    [ObservableProperty]
    private string _editMode = "";

    [ObservableProperty]
    private string _editClockIdentifier = "12HourClock";

    public IReadOnlyList<string> ModeOptions { get; } =
    [
        .. TransponderCatModes.EditorOptions,
        "FT4",
        "FT8",
        "PKT",
        "SSTV"
    ];

    private string _loadedModeText = "";

    [ObservableProperty]
    private string _callHint = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDxccBadge))]
    [NotifyPropertyChangedFor(nameof(DxccBadgeText))]
    [NotifyPropertyChangedFor(nameof(DxccEntityIsWorked))]
    [NotifyPropertyChangedFor(nameof(DxccEntityIsNew))]
    private string _resolvedCountry = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDxccBadge))]
    [NotifyPropertyChangedFor(nameof(DxccBadgeText))]
    [NotifyPropertyChangedFor(nameof(DxccEntityIsWorked))]
    [NotifyPropertyChangedFor(nameof(DxccEntityIsNew))]
    private int? _resolvedDxcc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDxccBadge))]
    [NotifyPropertyChangedFor(nameof(DxccBadgeText))]
    [NotifyPropertyChangedFor(nameof(DxccEntityIsWorked))]
    [NotifyPropertyChangedFor(nameof(DxccEntityIsNew))]
    private bool? _dxccPreviouslyWorked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridValidationIsValid))]
    [NotifyPropertyChangedFor(nameof(GridValidationIsInvalid))]
    [NotifyCanExecuteChangedFor(nameof(CommitQsoCommand))]
    private bool? _gridIsValid;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _utcClock = "";

    [ObservableProperty]
    private string _stationSatellite = "—";

    [ObservableProperty]
    private string _stationMode = "—";

    [ObservableProperty]
    private string _stationFrequencies = "—";

    [ObservableProperty]
    private string _stationStatusText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitQsoCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddQsoCommand))]
    private bool _stationAvailable;

    public bool CanAddQso =>
        SelectedLogbook is not null
        && !string.IsNullOrWhiteSpace(Call)
        && StationAvailable;

    public bool CanCommitQso =>
        SelectedLogbook is not null
        && !string.IsNullOrWhiteSpace(Call)
        && GridIsValid != false
        && (IsEditingQso || StationAvailable);

    public bool CanDeleteLogbook => SelectedLogbook is not null;

    public bool CanEditLogbook => SelectedLogbook is not null;

    public bool CanExport => SelectedLogbook is not null && QsoRows.Count > 0;

    public bool CanDeleteQso => SelectedQso is not null && !IsEditingQso;

    public bool CanEditQso => SelectedQso is not null && !IsEditingQso;

    public bool ShowCancelEdit => IsEditingQso;

    public string EntryCommitLabel =>
        IsEditingQso ? _l.Get("Logbook.SaveQso") : _l.Get("Logbook.AddQso");

    public string EditingStatusText =>
        IsEditingQso && _editingSource is not null
            ? _l.Get("Logbook.EditingStatus", FormatQsoUtcTime(_editingSource.QsoUtc))
            : "";

    public string CurrentLogbookDisplayName =>
        SelectedLogbook?.Name ?? _l.Get("Logbook.Menu.NoLogbook");

    public bool ShowDxccBadge =>
        ResolvedDxcc.HasValue
        && !string.IsNullOrWhiteSpace(ResolvedCountry)
        && DxccPreviouslyWorked.HasValue;

    public bool DxccEntityIsWorked => DxccPreviouslyWorked == true;

    public bool DxccEntityIsNew => DxccPreviouslyWorked == false;

    public string DxccBadgeText
    {
        get
        {
            if (!ShowDxccBadge)
                return "";

            var status = DxccPreviouslyWorked == true
                ? _l.Get("Logbook.DxccStatus.Worked")
                : _l.Get("Logbook.DxccStatus.New");
            return _l.Get("Logbook.DxccBadge", ResolvedCountry, status);
        }
    }

    public bool GridValidationIsValid => GridIsValid == true;

    public bool GridValidationIsInvalid => GridIsValid == false;

    public async Task InitializeAsync()
    {
        await _repository.InitializeAsync().ConfigureAwait(true);
        await ReloadLogbooksAsync().ConfigureAwait(true);

        if (Logbooks.Count == 0)
        {
            var gs = _settings.Current.GroundStation;
            var created = await _repository.GetOrCreateLogbookAsync(new QsoLogbookCreateRequest
            {
                Name = _l.Get("Logbook.DefaultName"),
                MyGridSquare = gs.GridSquare
            }).ConfigureAwait(true);
            await ReloadLogbooksAsync().ConfigureAwait(true);
            SelectedLogbook = Logbooks.FirstOrDefault(l => l.Id == created.Id);
        }

        _liveTimer.Start();
        RefreshStationPanel();
        _dxccLookup.EnsureLoaded();
        _ = _cloudlogUpload.ProcessRetryQueueAsync();
        _ = BackfillMissingDxccAsync();
        _entryBridge?.Attach(this);
    }

    /// <summary>
    /// Put a decoded callsign and/or locator into the entry form, as if typed. Nothing is logged.
    /// Refused while a saved QSO is being edited. A decoded locator survives the call lookup, so
    /// clicking the locator before or after the call gives both.
    /// </summary>
    public bool FillFromDecode(string? call, string? grid)
    {
        if (IsEditingQso)
            return false;

        var newCall = string.IsNullOrWhiteSpace(call) ? null : call.Trim().ToUpperInvariant();
        var newGrid = string.IsNullOrWhiteSpace(grid) ? null : MaidenheadLocator.UppercaseGridEntry(grid.Trim());
        var currentCall = string.IsNullOrWhiteSpace(Call) ? null : Call.Trim().ToUpperInvariant();

        if (newGrid is not null)
        {
            _decodedGrid = newGrid;
            _decodedGridCall = newCall ?? currentCall;
        }
        else if (newCall is not null && _decodedGrid is not null)
        {
            // A locator pinned for another contact does not carry over to this one.
            if (_decodedGridCall is not null && !string.Equals(_decodedGridCall, newCall, StringComparison.Ordinal))
                _decodedGrid = null;
            else
                _decodedGridCall = newCall;
        }

        if (newCall is not null && !string.Equals(newCall, currentCall, StringComparison.Ordinal))
            Call = newCall;
        if (_decodedGrid is { } pinned)
            Grid = pinned;
        return true;
    }

    public async Task ApplyCloudlogSettingsAsync(QsoLogbookCloudlogSettingsRequest request)
    {
        var updated = await _repository.UpdateLogbookCloudlogSettingsAsync(request).ConfigureAwait(true);
        await ReloadLogbooksAsync().ConfigureAwait(true);
        SelectedLogbook = Logbooks.FirstOrDefault(l => l.Id == updated.Id);
        StatusText = _l.Get("Logbook.Settings.Saved");
        _ = _cloudlogUpload.ProcessRetryQueueAsync();
    }

    public async Task CreateLogbookAsync(LogbookDetailsDialogResult result)
    {
        await CreateLogbookInternalAsync(new QsoLogbookCreateRequest
        {
            Name = result.Name,
            MyCallsign = result.MyCallsign,
            MyGridSquare = result.MyGridSquare
        }).ConfigureAwait(true);
        StatusText = _l.Get("Logbook.Status.Created", result.Name);
    }

    public async Task UpdateLogbookAsync(LogbookDetailsDialogResult result)
    {
        if (!result.UpdateLogbookId.HasValue)
            return;

        var updated = await _repository.UpdateLogbookAsync(new QsoLogbookUpdateRequest
        {
            Id = result.UpdateLogbookId.Value,
            Name = result.Name,
            MyCallsign = result.MyCallsign,
            MyGridSquare = result.MyGridSquare
        }).ConfigureAwait(true);

        await ReloadLogbooksAsync().ConfigureAwait(true);
        SelectedLogbook = Logbooks.FirstOrDefault(l => l.Id == updated.Id);
        StatusText = _l.Get("Logbook.Status.LogbookUpdated", updated.Name);
    }

    public async Task DeleteSelectedLogbookAsync()
    {
        if (SelectedLogbook is null)
            return;

        var name = SelectedLogbook.Name;
        await _repository.DeleteLogbookAsync(SelectedLogbook.Id).ConfigureAwait(true);
        SelectedLogbook = null;
        await ReloadLogbooksAsync().ConfigureAwait(true);
        StatusText = _l.Get("Logbook.Status.Deleted", name);
    }

    [RelayCommand(CanExecute = nameof(CanCommitQso))]
    private async Task CommitQsoAsync()
    {
        if (IsEditingQso)
            await SaveEditedQsoAsync().ConfigureAwait(true);
        else
            await AddQsoAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanEditQso))]
    private void BeginEditSelectedQso()
    {
        if (SelectedQso is null)
            return;

        _editingSource = SelectedQso.Record;
        IsEditingQso = true;
        LoadEntryFromRecord(_editingSource);
        StatusText = EditingStatusText;
        _ = LookupCallAsync(Call);
    }

    [RelayCommand]
    private async Task CancelEditQso()
    {
        ++_callLookupGeneration;
        ClearEntryForm();
        IsEditingQso = false;
        _editingSource = null;
        StatusText = _l.Get("Logbook.Status.Ready");
        await ReloadQsosAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ClearQsoFields()
    {
        if (IsEditingQso)
            await CancelEditQso().ConfigureAwait(true);

        ClearEntryForm();
        var report = CurrentDefaultReport();
        RstSent = report;
        RstRcvd = report;
        StatusText = _l.Get("Logbook.Status.Ready");
        await ReloadQsosAsync().ConfigureAwait(true);
        CommitQsoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAddQso))]
    private async Task AddQsoAsync()
    {
        if (SelectedLogbook is null || string.IsNullOrWhiteSpace(Call))
            return;

        if (!TryNormalizeWorkedGrid(Grid, out var workedGrid))
            return;

        var snapshot = _tracker.GetCurrent();
        if (!snapshot.IsAvailable)
        {
            StatusText = _l.Get("Logbook.Status.StationUnavailable");
            StationAvailable = false;
            return;
        }

        var qsoUtc = DateTime.UtcNow;
        var cloudlogUpload = SelectedLogbook.CloudlogAutoUpload && SelectedLogbook.CloudlogStationProfileId.HasValue
            ? CloudlogUploadStatus.Pending
            : CloudlogUploadStatus.None;
        ResolveDxccFields(Call, out var dxcc, out var country);
        var record = await _repository.AddQsoAsync(new QsoRecordCreateRequest
        {
            LogbookId = SelectedLogbook.Id,
            QsoUtc = qsoUtc,
            Call = Call,
            RstSent = FormatRstForLog(RstSent),
            RstRcvd = FormatRstForLog(RstRcvd),
            GridSquare = workedGrid,
            Name = Name,
            Comment = Comment,
            SatName = snapshot.SatelliteName,
            Mode = snapshot.Mode,
            ModeRx = snapshot.ModeRx,
            FreqHz = snapshot.UplinkHz,
            FreqRxHz = snapshot.DownlinkHz,
            Band = snapshot.Band,
            BandRx = snapshot.BandRx,
            Dxcc = dxcc,
            Country = country,
            CloudlogUploadStatus = cloudlogUpload
        }).ConfigureAwait(true);

        if (cloudlogUpload == CloudlogUploadStatus.Pending)
            await _cloudlogUpload.QueueUploadIfEnabledAsync(record.Id).ConfigureAwait(true);

        PublishQsoEvent(record, SatelliteLinkQsoEventKind.Logged);
        _ = TryAutoReportSatelliteStatusAsync(record, snapshot);

        await ReloadQsosAsync().ConfigureAwait(true);
        StatusText = _l.Get("Logbook.Status.Added", record.Call, FormatQsoUtcTime(record.QsoUtc));
        ClearEntryForm();
        CommitQsoCommand.NotifyCanExecuteChanged();
    }

    private async Task TryAutoReportSatelliteStatusAsync(QsoRecord record, LiveTrackerSnapshot snapshot)
    {
        var cfg = _settings.Current.SatelliteStatus;
        if (cfg is not { Enabled: true, AutoReportOnQso: true }
            || string.IsNullOrWhiteSpace(cfg.ApiToken))
            return;

        var satellite = snapshot.SatelliteName?.Trim() ?? "";
        var modeType = snapshot.ModeType?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(satellite)
            || string.IsNullOrWhiteSpace(modeType)
            || !SatelliteStatusReportFormatting.IsElevationReportable(snapshot.ElevationDeg))
            return;

        var request = new SatelliteStatusReportRequest(
            satellite,
            modeType,
            SatelliteStatusValue.On,
            record.QsoUtc.Kind == DateTimeKind.Utc
                ? record.QsoUtc
                : DateTime.SpecifyKind(record.QsoUtc, DateTimeKind.Utc),
            SatelliteStatusReportFormatting.NormalizeGridsquare(_settings.Current.GroundStation.GridSquare),
            $"OscarWatch-Tracker/{AppVersionHelper.GetDisplayVersionText()}");

        try
        {
            var result = await _satelliteStatus.SubmitReportAsync(cfg, request).ConfigureAwait(false);
            if (!result.Ok)
                Log.Warning("Auto satellite status report failed for {Satellite} {Mode}: {Message}",
                    satellite, modeType, result.Message);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto satellite status report failed for {Satellite} {Mode}",
                satellite, modeType);
        }
    }

    private async Task SaveEditedQsoAsync()
    {
        if (_editingSource is null || string.IsNullOrWhiteSpace(Call))
            return;

        if (!TryNormalizeWorkedGrid(Grid, out var workedGrid))
            return;

        ++_callLookupGeneration;

        if (EditQsoDate is not { } qsoDate
            || EditQsoTime is not { } qsoTime
            || !QsoLogbookTime.TryCombineUtcCalendar(qsoDate, qsoTime, out var editedUtc))
        {
            StatusText = _l.Get("Logbook.Status.InvalidQsoTime");
            return;
        }

        var originalUtc = QsoLogbookTime.NormalizeToUtc(_editingSource.QsoUtc);
        var qsoUtc = SameUtcToTheSecond(originalUtc, editedUtc) ? originalUtc : editedUtc;
        var (mode, modeRx) = string.Equals(EditMode.Trim(), _loadedModeText, StringComparison.OrdinalIgnoreCase)
            ? (_editingSource.Mode, _editingSource.ModeRx)
            : QsoModeText.Split(EditMode);

        ResolveDxccFields(Call, out var dxcc, out var country);
        var record = await _repository.UpdateQsoAsync(new QsoRecordUpdateRequest
        {
            Id = _editingSource.Id,
            QsoUtc = qsoUtc,
            Call = Call,
            RstSent = FormatRstForLog(RstSent),
            RstRcvd = FormatRstForLog(RstRcvd),
            GridSquare = workedGrid,
            Name = Name,
            Comment = Comment,
            SatName = _editingSource.SatName,
            Mode = mode,
            ModeRx = modeRx,
            FreqHz = _editingSource.FreqHz,
            FreqRxHz = _editingSource.FreqRxHz,
            Band = _editingSource.Band,
            BandRx = _editingSource.BandRx,
            PropMode = _editingSource.PropMode,
            Dxcc = dxcc,
            Country = country
        }).ConfigureAwait(true);

        var editedId = record.Id;
        PublishQsoEvent(record, SatelliteLinkQsoEventKind.Updated);
        await ReloadQsosAsync().ConfigureAwait(true);
        SelectedQso = QsoRows.FirstOrDefault(r => r.Id == editedId);
        IsEditingQso = false;
        _editingSource = null;
        ClearEntryForm();
        StatusText = _l.Get("Logbook.Status.Updated", record.Call, FormatQsoUtcTime(record.QsoUtc));
        CommitQsoCommand.NotifyCanExecuteChanged();
    }

    private void ClearEntryForm()
    {
        _decodedGrid = null;
        _decodedGridCall = null;
        _suppressCallLookup = true;
        ++_callLookupGeneration;
        Call = "";
        Grid = "";
        Name = "";
        Comment = "";
        EditQsoDate = null;
        EditQsoTime = null;
        EditMode = "";
        _loadedModeText = "";
        CallHint = "";
        ResolvedCountry = "";
        ResolvedDxcc = null;
        DxccPreviouslyWorked = null;
        GridIsValid = null;
        _suppressCallLookup = false;
    }

    private void LoadEntryFromRecord(QsoRecord record)
    {
        _suppressCallLookup = true;
        _suppressFieldCoercion = true;
        var qsoUtc = QsoLogbookTime.NormalizeToUtc(record.QsoUtc);
        Call = record.Call;
        RstSent = record.RstSent;
        RstRcvd = record.RstRcvd;
        Grid = record.GridSquare;
        Name = record.Name;
        Comment = record.Comment;
        EditQsoDate = new DateTimeOffset(qsoUtc.Year, qsoUtc.Month, qsoUtc.Day, 0, 0, 0, TimeSpan.Zero);
        EditQsoTime = new TimeSpan(qsoUtc.Hour, qsoUtc.Minute, qsoUtc.Second);
        _loadedModeText = QsoModeText.Format(record.Mode, record.ModeRx);
        EditMode = _loadedModeText;
        CallHint = "";
        ResolvedCountry = record.Country;
        ResolvedDxcc = record.Dxcc;
        DxccPreviouslyWorked = null;
        GridIsValid = MaidenheadLocator.GetLiveValidationState(record.GridSquare);
        _suppressFieldCoercion = false;
        _suppressCallLookup = false;
        ApplyEditedModeRstOptions();
        CommitQsoCommand.NotifyCanExecuteChanged();
    }

    private static bool SameUtcToTheSecond(DateTime left, DateTime right) =>
        left.Year == right.Year
        && left.Month == right.Month
        && left.Day == right.Day
        && left.Hour == right.Hour
        && left.Minute == right.Minute
        && left.Second == right.Second;

    [RelayCommand(CanExecute = nameof(CanExport))]
    public async Task<int> CountQsosForExportAsync(QsoAdifExportOptions options)
    {
        if (SelectedLogbook is null)
            return 0;

        var (fromInclusive, toExclusive) = QsoLogbookExportRange.ToQueryBounds(options);
        return await _repository.CountQsosAsync(
            SelectedLogbook.Id,
            fromInclusive,
            toExclusive).ConfigureAwait(true);
    }

    public QsoAdifExportDialogDefaults GetExportDialogDefaults()
    {
        var utcDates = QsoRows.Select(row => row.Record.QsoUtc).ToList();
        var (from, to) = QsoLogbookExportRange.DefaultUtcDates(utcDates);
        return new QsoAdifExportDialogDefaults(
            new DateTimeOffset(from, TimeSpan.Zero),
            new DateTimeOffset(to, TimeSpan.Zero));
    }

    public async Task<string?> ExportAdifAsync(QsoAdifExportOptions options)
    {
        if (SelectedLogbook is null)
            return null;

        var (fromInclusive, toExclusive) = QsoLogbookExportRange.ToQueryBounds(options);
        var qsos = await _repository.ListQsosAsync(
            SelectedLogbook.Id,
            fromInclusive,
            toExclusive).ConfigureAwait(true);
        if (qsos.Count == 0)
            return null;

        return AdifExporter.ExportLogbook(SelectedLogbook, qsos, options.ForLotw);
    }

    public string FormatExportStatusText(QsoAdifExportOptions options, int exportedCount)
    {
        if (options.Scope == QsoAdifExportScope.DateRange)
        {
            var (from, to) = QsoLogbookExportRange.NormalizeUtcDates(options.FromUtcDate, options.ToUtcDate);
            var rangeLabel = from == to
                ? from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                : $"{from:yyyy-MM-dd}–{to:yyyy-MM-dd}";
            return exportedCount == 1
                ? _l.Get("Logbook.Status.ExportedOneRange", rangeLabel)
                : _l.Get("Logbook.Status.ExportedManyRange", exportedCount, rangeLabel);
        }

        return exportedCount == 1
            ? _l.Get("Logbook.Status.ExportedOne")
            : _l.Get("Logbook.Status.ExportedMany", exportedCount);
    }

    public static string BuildSuggestedExportFileName(string logbookName, QsoAdifExportOptions options)
    {
        var safeName = SanitizeExportFileName(logbookName);
        if (options.Scope != QsoAdifExportScope.DateRange)
            return $"{safeName}.adi";

        var (from, to) = QsoLogbookExportRange.NormalizeUtcDates(options.FromUtcDate, options.ToUtcDate);
        var fromToken = from.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var toToken = to.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        return from == to
            ? $"{safeName}_{fromToken}.adi"
            : $"{safeName}_{fromToken}-{toToken}.adi";
    }

    private static string SanitizeExportFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var result = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(result) ? "logbook" : result;
    }

    [RelayCommand(CanExecute = nameof(CanEditQso))]
    private async Task ReuploadSelectedQsoAsync()
    {
        if (SelectedQso is null)
            return;

        var id = SelectedQso.Id;
        var call = SelectedQso.Call;
        StatusText = _l.Get("Logbook.Status.CloudlogReupload", call);
        await _cloudlogUpload.ReuploadQsoAsync(id).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteQso))]
    private async Task DeleteSelectedQsoAsync()
    {
        if (SelectedQso is null)
            return;

        var id = SelectedQso.Id;
        var call = SelectedQso.Call;
        var logbook = SelectedLogbook;
        var wasEditing = _editingSource?.Id == id;
        await _repository.DeleteQsoAsync(id).ConfigureAwait(true);
        if (logbook is not null)
        {
            PublishQsoEvent(
                new QsoRecord { Id = id, LogbookId = logbook.Id, Call = call },
                SatelliteLinkQsoEventKind.Deleted,
                logbook);
        }

        SelectedQso = null;
        if (wasEditing)
            await CancelEditQso().ConfigureAwait(true);
        else
            await ReloadQsosAsync().ConfigureAwait(true);
        StatusText = _l.Get("Logbook.Status.Removed", call);
    }

    partial void OnSelectedLogbookChanged(QsoLogbook? value)
    {
        _ = ApplySelectedLogbookChangeAsync();
        CommitQsoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditLogbook));
        OnPropertyChanged(nameof(CanDeleteLogbook));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CurrentLogbookDisplayName));
        OnPropertyChanged(nameof(QsoCountText));
        OnPropertyChanged(nameof(ShowQsoCountText));
        RefreshLogbookSwitchItems();
    }

    private async Task ApplySelectedLogbookChangeAsync()
    {
        await CancelEditQso().ConfigureAwait(true);
        _dxccBackfillStarted = false;
        _ = BackfillMissingDxccAsync();
    }

    partial void OnIsEditingQsoChanged(bool value)
    {
        CommitQsoCommand.NotifyCanExecuteChanged();
        BeginEditSelectedQsoCommand.NotifyCanExecuteChanged();
        DeleteSelectedQsoCommand.NotifyCanExecuteChanged();
        ReuploadSelectedQsoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(EntryCommitLabel));
        OnPropertyChanged(nameof(ShowCancelEdit));
        OnPropertyChanged(nameof(EditingStatusText));
        if (!value)
            RestoreStationRstOptions();
    }

    partial void OnSelectedQsoChanged(QsoRowViewModel? value)
    {
        BeginEditSelectedQsoCommand.NotifyCanExecuteChanged();
        DeleteSelectedQsoCommand.NotifyCanExecuteChanged();
    }

    partial void OnCallChanged(string value)
    {
        CoerceCallsign(value);
        CommitQsoCommand.NotifyCanExecuteChanged();
        if (_suppressCallLookup)
            return;

        _ = LookupCallAsync(Call);
    }

    partial void OnGridChanged(string value)
    {
        // Typing over a decoded locator hands it back to the operator.
        if (_decodedGrid is not null && !string.Equals(value.Trim(), _decodedGrid, StringComparison.OrdinalIgnoreCase))
        {
            _decodedGrid = null;
            _decodedGridCall = null;
        }

        CoerceWorkedGrid(value);
        UpdateGridValidationState();
    }

    private void UpdateGridValidationState() =>
        GridIsValid = MaidenheadLocator.GetLiveValidationState(Grid);

    partial void OnEditModeChanged(string value)
    {
        if (_suppressFieldCoercion)
            return;

        var upper = value.ToUpperInvariant();
        if (!string.Equals(upper, value, StringComparison.Ordinal))
        {
            _suppressFieldCoercion = true;
            EditMode = upper;
            _suppressFieldCoercion = false;
        }

        if (IsEditingQso)
            ApplyEditedModeRstOptions();
    }

    partial void OnRstSentChanged(string value) => CommitQsoCommand.NotifyCanExecuteChanged();

    partial void OnRstRcvdChanged(string value) => CommitQsoCommand.NotifyCanExecuteChanged();

    private void CoerceCallsign(string value)
    {
        if (_suppressFieldCoercion)
            return;

        var normalized = MaidenheadLocator.NormalizeCallsign(value);
        if (string.Equals(normalized, value, StringComparison.Ordinal))
            return;

        _suppressFieldCoercion = true;
        Call = normalized;
        _suppressFieldCoercion = false;
    }

    private void CoerceWorkedGrid(string value)
    {
        if (_suppressFieldCoercion)
            return;

        var normalized = MaidenheadLocator.UppercaseGridEntry(value);
        if (string.Equals(normalized, value, StringComparison.Ordinal))
            return;

        _suppressFieldCoercion = true;
        Grid = normalized;
        _suppressFieldCoercion = false;
    }

    private bool TryNormalizeWorkedGrid(string value, out string normalized)
    {
        if (MaidenheadLocator.TryValidateGrids(value, out normalized, out var error, out var invalidSegment))
            return true;

        StatusText = FormatGridValidationError(error, invalidSegment);
        return false;
    }

    private string FormatGridValidationError(GridValidationError error, string? invalidSegment) =>
        error switch
        {
            GridValidationError.TooManyGrids =>
                _l.Get("Logbook.Error.GridTooMany", MaidenheadLocator.MaxGridCount),
            GridValidationError.InvalidSegment =>
                _l.Get("Logbook.Error.GridInvalidSegment", invalidSegment ?? ""),
            _ => ""
        };

    private async Task LookupCallAsync(string value)
    {
        if (SelectedLogbook is null)
            return;

        CancelQrzLookup();
        var generation = ++_callLookupGeneration;
        var trimmed = value.Trim();
        if (trimmed.Length < 3)
        {
            CallHint = "";
            ClearDxccBadge();
            if (!IsEditingQso)
            {
                ClearCorrespondentFields();
                await ReloadQsosAsync().ConfigureAwait(true);
                if (generation != _callLookupGeneration)
                    return;
            }

            return;
        }

        if (!IsEditingQso)
        {
            await ReloadQsosAsync(trimmed).ConfigureAwait(true);
            if (generation != _callLookupGeneration)
                return;
        }

        var previous = await _repository.FindLatestQsoForCallAsync(SelectedLogbook.Id, trimmed).ConfigureAwait(true);
        if (generation != _callLookupGeneration)
            return;

        if (!string.Equals(trimmed, Call.Trim(), StringComparison.OrdinalIgnoreCase))
            return;

        ResolveDxccFields(trimmed, out var dxcc, out var country);
        ResolvedDxcc = dxcc;
        ResolvedCountry = country;

        if (dxcc is int entityId)
        {
            var previousEntity = await _repository.FindLatestQsoForDxccAsync(SelectedLogbook.Id, entityId)
                .ConfigureAwait(true);
            if (generation != _callLookupGeneration)
                return;
            if (!string.Equals(trimmed, Call.Trim(), StringComparison.OrdinalIgnoreCase))
                return;

            // While editing the current QSO, treat the entity as worked only if another QSO exists.
            DxccPreviouslyWorked = previousEntity is not null
                && (!IsEditingQso || previousEntity.Id != _editingSource?.Id);
        }
        else
        {
            DxccPreviouslyWorked = null;
        }

        if (previous is null)
        {
            CallHint = "";
            if (!IsEditingQso)
                ClearCorrespondentFields();
            await LookupCallbookIfNeededAsync(trimmed, generation).ConfigureAwait(true);
            return;
        }

        ApplyCorrespondentFromPrevious(previous);

        CallHint = string.IsNullOrWhiteSpace(previous.GridSquare)
            ? _l.Get("Logbook.CallHint.Previous")
            : _l.Get("Logbook.CallHint.PreviousWithGrid", previous.GridSquare);

        await LookupCallbookIfNeededAsync(trimmed, generation).ConfigureAwait(true);
    }

    /// <summary>
    /// A new contact takes name and locator from the latest QSO for this call.
    /// An edit keeps values already on the contact and only fills blanks.
    /// </summary>
    private void ApplyCorrespondentFromPrevious(QsoRecord previous)
    {
        if (IsEditingQso)
        {
            if (string.IsNullOrWhiteSpace(Grid) && !string.IsNullOrWhiteSpace(previous.GridSquare))
                Grid = previous.GridSquare;
            if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(previous.Name))
                Name = previous.Name;
            return;
        }

        Grid = _decodedGrid ?? previous.GridSquare.Trim();
        Name = previous.Name.Trim();
    }

    private void ClearCorrespondentFields()
    {
        Name = "";
        Grid = _decodedGrid ?? "";
    }

    private async Task LookupCallbookIfNeededAsync(string call, int generation)
    {
        CancelQrzLookup();
        var qrzEnabled = _qrz.CanLookup(_settings.Current.Qrz);
        var hamQthEnabled = _hamQth.CanLookup(_settings.Current.HamQth);
        if (!qrzEnabled && !hamQthEnabled)
            return;
        if (!QrzCallsignHelper.IsPlausible(call))
            return;
        if (!string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Grid))
            return;

        _qrzLookupCts = new CancellationTokenSource();
        var token = _qrzLookupCts.Token;
        try
        {
            await Task.Delay(450, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (generation != _callLookupGeneration)
            return;
        if (!string.Equals(call, Call.Trim(), StringComparison.OrdinalIgnoreCase))
            return;
        if (!string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Grid))
            return;

        QrzCallbookEntry? qrzEntry = null;
        if (qrzEnabled)
        {
            try
            {
                qrzEntry = await _qrz.LookupAsync(_settings.Current.Qrz, call, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "QRZ callbook lookup failed for {Call}", call);
            }
        }

        var needName = string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(qrzEntry?.Name);
        var needGrid = string.IsNullOrWhiteSpace(Grid) && string.IsNullOrWhiteSpace(qrzEntry?.Grid);
        QrzCallbookEntry? hamEntry = null;
        if (hamQthEnabled && (needName || needGrid))
        {
            try
            {
                hamEntry = await _hamQth.LookupAsync(_settings.Current.HamQth, call, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "HamQTH callbook lookup failed for {Call}", call);
            }
        }

        if (generation != _callLookupGeneration)
            return;
        if (!string.Equals(call, Call.Trim(), StringComparison.OrdinalIgnoreCase))
            return;

        var name = FirstNonEmpty(Name, qrzEntry?.Name, hamEntry?.Name);
        var grid = FirstNonEmpty(Grid, qrzEntry?.Grid, hamEntry?.Grid);
        var nameSource = string.IsNullOrWhiteSpace(Name)
            ? !string.IsNullOrWhiteSpace(qrzEntry?.Name) ? "qrz"
            : !string.IsNullOrWhiteSpace(hamEntry?.Name) ? "hamqth"
            : null
            : null;

        if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(name))
        {
            Name = name;
            if (string.IsNullOrWhiteSpace(CallHint))
            {
                CallHint = nameSource == "hamqth"
                    ? _l.Get("Logbook.CallHint.HamQth")
                    : _l.Get("Logbook.CallHint.Qrz");
            }
        }

        if (string.IsNullOrWhiteSpace(Grid) && !string.IsNullOrWhiteSpace(grid))
            Grid = grid;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private void CancelQrzLookup()
    {
        try
        {
            _qrzLookupCts?.Cancel();
            _qrzLookupCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _qrzLookupCts = null;
    }

    private void ClearDxccBadge()
    {
        ResolvedCountry = "";
        ResolvedDxcc = null;
        DxccPreviouslyWorked = null;
    }

    private void ResolveDxccFields(string call, out int? dxcc, out string country)
    {
        if (_dxccLookup.TryResolve(call, out var match))
        {
            dxcc = match.Dxcc;
            country = match.Country;
            return;
        }

        dxcc = null;
        country = "";
    }

    [RelayCommand]
    private async Task UpdateCountryFileAsync()
    {
        StatusText = _l.Get("Logbook.Status.CountryFileUpdating");
        var result = await _dxccLookup.UpdateCountryFileAsync().ConfigureAwait(true);
        if (result.Success)
        {
            StatusText = _l.Get("Logbook.Status.CountryFileUpdated");
            if (!string.IsNullOrWhiteSpace(Call))
                _ = LookupCallAsync(Call);
            _ = BackfillMissingDxccAsync(force: true);
        }
        else
        {
            StatusText = _l.Get("Logbook.Status.CountryFileUpdateFailed", result.Message);
        }
    }

    private async Task BackfillMissingDxccAsync(bool force = false)
    {
        if (SelectedLogbook is null)
            return;
        if (_dxccBackfillStarted && !force)
            return;

        _dxccBackfillStarted = true;
        try
        {
            var missing = await _repository.ListQsosMissingDxccAsync(SelectedLogbook.Id).ConfigureAwait(true);
            foreach (var qso in missing)
            {
                ResolveDxccFields(qso.Call, out var dxcc, out var country);
                if (dxcc is null)
                    continue;

                await _repository.UpdateQsoAsync(new QsoRecordUpdateRequest
                {
                    Id = qso.Id,
                    QsoUtc = qso.QsoUtc,
                    Call = qso.Call,
                    RstSent = qso.RstSent,
                    RstRcvd = qso.RstRcvd,
                    GridSquare = qso.GridSquare,
                    Name = qso.Name,
                    Comment = qso.Comment,
                    SatName = qso.SatName,
                    Mode = qso.Mode,
                    ModeRx = qso.ModeRx,
                    FreqHz = qso.FreqHz,
                    FreqRxHz = qso.FreqRxHz,
                    Band = qso.Band,
                    BandRx = qso.BandRx,
                    PropMode = qso.PropMode,
                    Dxcc = dxcc,
                    Country = country
                }).ConfigureAwait(true);
            }

            if (missing.Count > 0 && string.IsNullOrWhiteSpace(Call))
                await ReloadQsosAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DXCC backfill failed");
        }
    }

    private async Task CreateLogbookInternalAsync(QsoLogbookCreateRequest request)
    {
        var created = await _repository.CreateLogbookAsync(request).ConfigureAwait(true);
        await ReloadLogbooksAsync().ConfigureAwait(true);
        SelectedLogbook = Logbooks.FirstOrDefault(l => l.Id == created.Id);
    }

    private async Task ReloadLogbooksAsync()
    {
        var items = await _repository.ListLogbooksAsync().ConfigureAwait(true);
        Logbooks.Clear();
        foreach (var item in items)
            Logbooks.Add(item);

        if (SelectedLogbook is null && Logbooks.Count > 0)
            SelectedLogbook = Logbooks[0];
        else if (SelectedLogbook is not null)
            SelectedLogbook = Logbooks.FirstOrDefault(l => l.Id == SelectedLogbook.Id);

        RefreshLogbookSwitchItems();
        OnPropertyChanged(nameof(CurrentLogbookDisplayName));
    }

    private void RefreshLogbookSwitchItems()
    {
        LogbookSwitchItems.Clear();
        foreach (var logbook in Logbooks)
        {
            LogbookSwitchItems.Add(new LogbookSwitchMenuItemViewModel(
                logbook,
                SelectedLogbook?.Id == logbook.Id,
                lb => SelectedLogbook = lb));
        }
    }

    private async Task ReloadQsosAsync(string? callFilter = null)
    {
        QsoRows.Clear();
        if (SelectedLogbook is null)
        {
            QsoCount = 0;
            OnPropertyChanged(nameof(CanExport));
            return;
        }

        var qsos = string.IsNullOrWhiteSpace(callFilter)
            ? await _repository.ListQsosAsync(SelectedLogbook.Id).ConfigureAwait(true)
            : await _repository.SearchQsosByCallAsync(SelectedLogbook.Id, callFilter).ConfigureAwait(true);

        foreach (var qso in qsos)
            QsoRows.Add(QsoRowViewModel.From(qso, _settings.Current.Use24HourClock, _l));

        if (string.IsNullOrWhiteSpace(callFilter))
            QsoCount = qsos.Count;

        OnPropertyChanged(nameof(CanExport));
    }

    private void OnCloudlogUploadStateChanged(long qsoId) =>
        Dispatcher.UIThread.Post(() => _ = RefreshQsoRowAsync(qsoId));

    private void OnRepositoryQsosChanged(long logbookId) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (SelectedLogbook?.Id != logbookId || IsEditingQso)
                return;

            var filter = Call?.Trim();
            _ = ReloadQsosAsync(filter is { Length: >= 3 } ? filter : null);
        });

    private async Task RefreshQsoRowAsync(long qsoId)
    {
        if (SelectedLogbook is null)
            return;

        var updated = await _repository.GetQsoByIdAsync(qsoId).ConfigureAwait(true);
        if (updated is null || updated.LogbookId != SelectedLogbook.Id)
            return;

        for (var i = 0; i < QsoRows.Count; i++)
        {
            if (QsoRows[i].Id != qsoId)
                continue;

            QsoRows[i] = QsoRowViewModel.From(updated, _settings.Current.Use24HourClock, _l);
            break;
        }
    }

    private void RefreshStationPanel()
    {
        var clockIdentifier = _settings.Current.Use24HourClock ? "24HourClock" : "12HourClock";
        if (!string.Equals(EditClockIdentifier, clockIdentifier, StringComparison.Ordinal))
            EditClockIdentifier = clockIdentifier;

        UtcClock = QsoLogbookTime.FormatLiveUtcClock(
            DateTime.UtcNow,
            _settings.Current.Use24HourClock,
            System.Globalization.CultureInfo.CurrentCulture);
        var snapshot = _tracker.GetCurrent();
        StationAvailable = snapshot.IsAvailable;
        StationSatellite = snapshot.IsAvailable ? snapshot.SatelliteName : "—";
        StationMode = snapshot.IsAvailable && !string.IsNullOrWhiteSpace(snapshot.Mode)
            ? FormatMode(snapshot)
            : "—";
        StationFrequencies = snapshot.IsAvailable ? snapshot.FrequencySummary : "—";
        StationStatusText = snapshot.IsAvailable
            ? _l.Get("Logbook.Station.Tracking", snapshot.SatelliteName)
            : _l.Get("Logbook.Station.Unavailable");

        ApplyDefaultRstForMode(snapshot);
        RefreshCwKeyer(snapshot);
    }

    private void ApplyDefaultRstForMode(LiveTrackerSnapshot snapshot)
    {
        if (IsEditingQso)
            return;

        var key = QsoRstSuggestions.ModeKey(snapshot.Mode, snapshot.ModeType);
        if (string.Equals(key, _lastRstModeKey, StringComparison.Ordinal))
            return;

        _lastRstModeKey = key;
        _snrRst = QsoRstSuggestions.UsesSnr(snapshot.Mode, snapshot.ModeType);
        SetRstOptions(_snrRst ? QsoRstSuggestions.SnrOptions : QsoRstSuggestions.VoiceOptions);

        var report = QsoRstSuggestions.DefaultReport(snapshot.Mode, snapshot.ModeType);
        if (report is null)
            return;

        RstSent = report;
        RstRcvd = report;
    }

    private void ApplyEditedModeRstOptions()
    {
        var (uplink, downlink) = QsoModeText.Split(EditMode);
        _snrRst = QsoRstSuggestions.UsesSnr(uplink, downlink);
        SetRstOptions(_snrRst ? QsoRstSuggestions.SnrOptions : QsoRstSuggestions.VoiceOptions);
    }

    private void RestoreStationRstOptions()
    {
        var snapshot = _tracker.GetCurrent();
        _lastRstModeKey = QsoRstSuggestions.ModeKey(snapshot.Mode, snapshot.ModeType);
        _snrRst = QsoRstSuggestions.UsesSnr(snapshot.Mode, snapshot.ModeType);
        SetRstOptions(_snrRst ? QsoRstSuggestions.SnrOptions : QsoRstSuggestions.VoiceOptions);
    }

    private string CurrentDefaultReport()
    {
        var snapshot = _tracker.GetCurrent();
        return QsoRstSuggestions.DefaultReport(snapshot.Mode, snapshot.ModeType) ?? "59";
    }

    private string FormatRstForLog(string? value) =>
        QsoRstSuggestions.NormalizeForLog(value, _snrRst);

    private void SetRstOptions(IReadOnlyList<string> options)
    {
        if (RstOptions.Count == options.Count)
        {
            var same = true;
            for (var i = 0; i < options.Count; i++)
            {
                if (!string.Equals(RstOptions[i], options[i], StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }

            if (same)
                return;
        }

        RstOptions.Clear();
        foreach (var option in options)
            RstOptions.Add(option);
    }

    private static string FormatMode(LiveTrackerSnapshot snapshot)
    {
        if (string.Equals(snapshot.Mode, snapshot.ModeRx, StringComparison.OrdinalIgnoreCase))
            return snapshot.Mode;

        return $"{snapshot.Mode} / {snapshot.ModeRx}";
    }

    private string FormatQsoUtcTime(DateTime utc) =>
        QsoLogbookTime.FormatQsoUtc(utc, _settings.Current.Use24HourClock);

    private void PublishQsoEvent(
        QsoRecord record,
        SatelliteLinkQsoEventKind kind,
        QsoLogbook? logbook = null)
    {
        logbook ??= SelectedLogbook;
        if (logbook is null)
            return;

        _satelliteLink.PublishQso(
            record,
            logbook,
            kind,
            kind == SatelliteLinkQsoEventKind.Deleted ? null : _tracker.FocusedNoradId);
    }

    public void Dispose()
    {
        _entryBridge?.Detach(this);
        CancelQrzLookup();
        _cloudlogUpload.UploadStateChanged -= OnCloudlogUploadStateChanged;
        _repository.QsosChanged -= OnRepositoryQsosChanged;
        _liveTimer.Stop();
        DisposeCwKeyer();
    }
}

public sealed partial class LogbookSwitchMenuItemViewModel : ObservableObject
{
    private readonly Action<QsoLogbook> _select;

    public LogbookSwitchMenuItemViewModel(QsoLogbook logbook, bool isChecked, Action<QsoLogbook> select)
    {
        Logbook = logbook;
        IsChecked = isChecked;
        _select = select;
    }

    public QsoLogbook Logbook { get; }

    public string Name => Logbook.Name;

    [ObservableProperty]
    private bool _isChecked;

    [RelayCommand]
    private void Select() => _select(Logbook);
}

public sealed class QsoRowViewModel
{
    public required QsoRecord Record { get; init; }

    public long Id => Record.Id;
    public string DateTimeText { get; init; } = "";
    public string Call { get; init; } = "";
    public string Country { get; init; } = "";
    public string Grid { get; init; } = "";
    public string Satellite { get; init; } = "";
    public string Mode { get; init; } = "";
    public string RstSent { get; init; } = "";
    public string RstRcvd { get; init; } = "";
    public string Comment { get; init; } = "";
    public CloudlogUploadStatus CloudlogUploadStatus { get; init; }
    public string CloudlogStatusGlyph { get; init; } = "";
    public string CloudlogStatusToolTip { get; init; } = "";
    public bool CloudlogStatusIsSent => CloudlogUploadStatus == CloudlogUploadStatus.Sent;
    public bool CloudlogStatusIsPending => CloudlogUploadStatus == CloudlogUploadStatus.Pending;
    public bool CloudlogStatusIsFailed => CloudlogUploadStatus == CloudlogUploadStatus.Failed;

    public static QsoRowViewModel From(QsoRecord record, bool use24Hour, ILocalizationService localization)
    {
        var dateTimeText = QsoLogbookTime.FormatQsoUtc(record.QsoUtc, use24Hour);
        var mode = QsoModeText.Format(record.Mode, record.ModeRx);

        return new QsoRowViewModel
        {
            Record = record,
            DateTimeText = dateTimeText,
            Call = record.Call,
            Country = record.Country,
            Grid = record.GridSquare,
            Satellite = record.SatName,
            Mode = mode,
            RstSent = record.RstSent,
            RstRcvd = record.RstRcvd,
            Comment = record.Comment,
            CloudlogUploadStatus = record.CloudlogUploadStatus,
            CloudlogStatusGlyph = FormatCloudlogGlyph(record.CloudlogUploadStatus),
            CloudlogStatusToolTip = FormatCloudlogStatus(
                record.CloudlogUploadStatus,
                record.CloudlogUploadLastError,
                localization)
        };
    }

    private static string FormatCloudlogGlyph(CloudlogUploadStatus status) =>
        status switch
        {
            CloudlogUploadStatus.Sent => "✓",
            CloudlogUploadStatus.Failed => "✗",
            _ => ""
        };

    private static string FormatCloudlogStatus(
        CloudlogUploadStatus status,
        string? lastError,
        ILocalizationService localization)
    {
        var label = status switch
        {
            CloudlogUploadStatus.Pending => localization.Get("Logbook.CloudlogStatus.Pending"),
            CloudlogUploadStatus.Sent => localization.Get("Logbook.CloudlogStatus.Sent"),
            CloudlogUploadStatus.Failed => localization.Get("Logbook.CloudlogStatus.Failed"),
            _ => ""
        };
        if (status == CloudlogUploadStatus.Failed && !string.IsNullOrWhiteSpace(lastError))
            return $"{label}: {lastError.Trim()}";

        return label;
    }
}
