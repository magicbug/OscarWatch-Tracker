using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Display;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Localization;

namespace OscarWatch.ViewModels;

public partial class MutualPassViewModel : ViewModelBase
{
    private const int MaxDateRangeDays = 30;

    private readonly ISettingsService _settings;
    private readonly ITleService _tleService;
    private readonly TrackingOrchestrator _tracking;
    private readonly ILocalizationService _l;

    public IReadOnlyList<string> TimeDisplayLabels { get; }
    public IReadOnlyList<string> TimeWindowModeLabels { get; }

    public ObservableCollection<MutualPassRow> Passes { get; } = [];

    [ObservableProperty]
    private string _localStationSummary = "";

    [ObservableProperty]
    private string _remoteOperatorLabel = "";

    [ObservableProperty]
    private string _remoteGridSquare = "";

    [ObservableProperty]
    private double _filterMinElevationDeg = 5;

    [ObservableProperty]
    private int _filterMinPassDurationMinutes = 2;

    [ObservableProperty]
    private int _filterMinMutualDurationMinutes = 1;

    [ObservableProperty]
    private int _filterPredictionHours = 48;

    [ObservableProperty]
    private int _timeWindowModeIndex;

    [ObservableProperty]
    private DateTimeOffset _rangeStartDate;

    [ObservableProperty]
    private int _rangeStartHour;

    [ObservableProperty]
    private int _rangeStartMinute;

    [ObservableProperty]
    private DateTimeOffset _rangeEndDate;

    [ObservableProperty]
    private int _rangeEndHour;

    [ObservableProperty]
    private int _rangeEndMinute;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _resultsContextLine = "";

    [ObservableProperty]
    private bool _hasResultsContext;

    [ObservableProperty]
    private bool _useUtcTime;

    [ObservableProperty]
    private int _wizardStep;

    [ObservableProperty]
    private bool _isSearching;

    private string _lastLocalLabel = "";
    private string _lastRemoteLabel = "";
    private GroundStation? _lastLocalSite;
    private GroundStation? _lastRemoteSite;

    public bool UseHoursAhead => TimeWindowModeIndex == 0;
    public bool UseDateRange => TimeWindowModeIndex == 1;
    public bool IsStationsStep => WizardStep == 0;
    public bool IsCriteriaStep => WizardStep == 1;
    public bool IsWhenStep => WizardStep == 2;
    public bool IsResultsStep => WizardStep == 3;
    public bool IsWizardSetupStep => WizardStep is 0 or 1;

    public string WizardProgressText => WizardStep switch
    {
        0 => _l.Get("Mutual.Wizard.Progress", 1, 3, _l.Get("Mutual.Wizard.Step.Stations")),
        1 => _l.Get("Mutual.Wizard.Progress", 2, 3, _l.Get("Mutual.Wizard.Step.Criteria")),
        2 => _l.Get("Mutual.Wizard.Progress", 3, 3, _l.Get("Mutual.Wizard.Step.When")),
        _ => _l.Get("Mutual.Wizard.Step.Results")
    };

    public MutualPassViewModel(
        ISettingsService settings,
        ITleService tleService,
        TrackingOrchestrator tracking,
        ILocalizationService localization)
    {
        _settings = settings;
        _tleService = tleService;
        _tracking = tracking;
        _l = localization;
        TimeDisplayLabels =
        [
            _l.Get("Pass.Time.Local"),
            _l.Get("Pass.Time.Utc")
        ];
        TimeWindowModeLabels =
        [
            _l.Get("Mutual.TimeWindow.HoursAhead"),
            _l.Get("Mutual.TimeWindow.DateRange")
        ];
        _rangeStartDate = DateTimeOffset.UtcNow.Date;
        _rangeEndDate = DateTimeOffset.UtcNow.Date.AddDays(2);
    }

    public void Initialize()
    {
        var local = _settings.Current.GroundStation;
        LocalStationSummary = $"{local.DisplayName} ({local.GridSquare})";

        FilterMinElevationDeg = _settings.Current.MinimumElevationDeg;
        FilterMinPassDurationMinutes = _settings.Current.PassFilterMinDurationMinutes;
        FilterMinMutualDurationMinutes = Math.Max(1, FilterMinPassDurationMinutes / 2);
        FilterPredictionHours = _settings.Current.PassPlannerPredictionHours;
        UseUtcTime = _settings.Current.PassPlannerUseUtcTime;
        InitializeDateRangeDefaults();
        WizardStep = 0;
    }

    partial void OnWizardStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsStationsStep));
        OnPropertyChanged(nameof(IsCriteriaStep));
        OnPropertyChanged(nameof(IsWhenStep));
        OnPropertyChanged(nameof(IsResultsStep));
        OnPropertyChanged(nameof(IsWizardSetupStep));
        OnPropertyChanged(nameof(WizardProgressText));
        WizardBackCommand.NotifyCanExecuteChanged();
        WizardNextCommand.NotifyCanExecuteChanged();
        FindPassesCommand.NotifyCanExecuteChanged();
        EditSearchCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSearchingChanged(bool value)
    {
        WizardBackCommand.NotifyCanExecuteChanged();
        WizardNextCommand.NotifyCanExecuteChanged();
        FindPassesCommand.NotifyCanExecuteChanged();
        EditSearchCommand.NotifyCanExecuteChanged();
    }

    partial void OnRemoteGridSquareChanged(string value)
    {
        WizardNextCommand.NotifyCanExecuteChanged();
        FindPassesCommand.NotifyCanExecuteChanged();
    }

    private bool CanWizardBack() => !IsSearching && WizardStep is > 0 and < 3;

    private bool CanWizardNext() => !IsSearching && WizardStep is 0 or 1 && ValidateStationsStep();

    private bool CanFindPasses() => !IsSearching && WizardStep == 2 && ValidateStationsStep();

    private bool CanEditSearch() => !IsSearching && WizardStep == 3;

    [RelayCommand(CanExecute = nameof(CanWizardBack))]
    private void WizardBack()
    {
        if (WizardStep > 0 && WizardStep < 3)
            WizardStep--;
    }

    [RelayCommand(CanExecute = nameof(CanWizardNext))]
    private void WizardNext()
    {
        if (WizardStep == 0 && !ValidateStationsStep(showStatus: true))
            return;

        if (WizardStep < 2)
            WizardStep++;
    }

    [RelayCommand(CanExecute = nameof(CanEditSearch))]
    private void EditSearch() => WizardStep = 0;

    [RelayCommand(CanExecute = nameof(CanFindPasses))]
    private Task FindPassesAsync() => RefreshPassesAsync();

    private bool ValidateStationsStep(bool showStatus = false)
    {
        if (RemoteGridSquare.Trim().Length >= 4)
            return true;

        if (showStatus)
            StatusText = _l.Get("Mutual.Status.EnterGrid");

        return false;
    }

    partial void OnTimeWindowModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(UseHoursAhead));
        OnPropertyChanged(nameof(UseDateRange));
    }

    partial void OnUseUtcTimeChanged(bool value)
    {
        OnPropertyChanged(nameof(TimeDisplayIndex));
        _settings.Current.PassPlannerUseUtcTime = value;
        RefreshPassDisplayTimes();
        InitializeDateRangeDefaults();
        _settings.RequestSave();
    }

    public int TimeDisplayIndex
    {
        get => UseUtcTime ? 1 : 0;
        set
        {
            if (value is not (0 or 1) || UseUtcTime == (value == 1))
                return;

            UseUtcTime = value == 1;
        }
    }

    private void InitializeDateRangeDefaults()
    {
        var now = UseUtcTime ? DateTimeOffset.UtcNow : DateTimeOffset.Now;
        var start = now;
        var end = start.AddHours(FilterPredictionHours);

        RangeStartDate = start.Date;
        RangeStartHour = start.Hour;
        RangeStartMinute = start.Minute;
        RangeEndDate = end.Date;
        RangeEndHour = end.Hour;
        RangeEndMinute = end.Minute;
    }

    private void RefreshPassDisplayTimes()
    {
        if (Passes.Count == 0)
            return;

        var rows = Passes.ToList();
        Passes.Clear();
        foreach (var row in rows)
            Passes.Add(MutualPassRow.From(
                row.Source,
                _lastLocalLabel,
                _lastRemoteLabel,
                UseUtcTime,
                _settings.Current.Use24HourClock));
    }

    [RelayCommand]
    private async Task RefreshPassesAsync()
    {
        if (!ValidateStationsStep(showStatus: true))
            return;

        if (!TryGetSearchWindowUtc(out var utcStart, out var utcEnd, out var rangeErrorKey, out var rangeErrorArg))
        {
            StatusText = rangeErrorArg is null
                ? _l.Get(rangeErrorKey!)
                : _l.Get(rangeErrorKey!, rangeErrorArg.Value);
            return;
        }

        IsSearching = true;
        StatusText = _l.Get("Pass.ComputingMutual");

        try
        {
            await _tleService.EnsureLoadedAsync();

            var grid = RemoteGridSquare.Trim();
            var (lat, lon) = MaidenheadGrid.ToLatLonCenter(grid);
            var remoteSite = new GroundStation
            {
                DisplayName = string.IsNullOrWhiteSpace(RemoteOperatorLabel)
                    ? grid.ToUpperInvariant()
                    : RemoteOperatorLabel.Trim(),
                LatitudeDeg = lat,
                LongitudeDeg = lon,
                AltitudeMetersAsl = 50,
                GridSquare = grid.ToUpperInvariant()
            };

            var localSite = _settings.Current.GroundStation;
            var passes = await _tracking.GetMutualPassesAsync(
                localSite,
                remoteSite,
                FilterMinElevationDeg,
                utcStart,
                utcEnd,
                FilterMinPassDurationMinutes,
                FilterMinMutualDurationMinutes);

            _lastLocalLabel = localSite.DisplayName;
            _lastRemoteLabel = remoteSite.DisplayName;
            _lastLocalSite = localSite;
            _lastRemoteSite = remoteSite;
            ResultsContextLine = _l.Get(
                "Mutual.ResultsContext",
                $"{localSite.DisplayName} ({localSite.GridSquare})",
                $"{remoteSite.DisplayName} ({remoteSite.GridSquare})");
            HasResultsContext = true;

            Passes.Clear();
            foreach (var pass in passes)
                Passes.Add(MutualPassRow.From(
                    pass,
                    _lastLocalLabel,
                    _lastRemoteLabel,
                    UseUtcTime,
                    _settings.Current.Use24HourClock));

            var clockFormat = PassDisplayFormat.FromSettings(_settings.Current.Use24HourClock);
            if (passes.Count == 0)
            {
                StatusText = UseHoursAhead
                    ? _l.Get("Mutual.Status.NoPasses", FilterPredictionHours, localSite.GridSquare, remoteSite.GridSquare)
                    : _l.Get(
                        "Mutual.Status.NoPassesInRange",
                        FormatRangePoint(utcStart, clockFormat),
                        FormatRangePoint(utcEnd, clockFormat),
                        localSite.GridSquare,
                        remoteSite.GridSquare);
            }
            else
            {
                StatusText = UseHoursAhead
                    ? _l.Get("Pass.CountMutual", passes.Count, FilterPredictionHours)
                    : _l.Get(
                        "Pass.CountMutualInRange",
                        passes.Count,
                        FormatRangePoint(utcStart, clockFormat),
                        FormatRangePoint(utcEnd, clockFormat));
            }

            WizardStep = 3;
        }
        catch (ArgumentException)
        {
            StatusText = _l.Get("Mutual.Status.InvalidGrid");
        }
        catch (Exception ex)
        {
            StatusText = _l.Get("Pass.FailedMutual", ex.Message);
        }
        finally
        {
            IsSearching = false;
        }
    }

    private bool TryGetSearchWindowUtc(
        out DateTime utcStart,
        out DateTime utcEnd,
        out string? errorKey,
        out int? errorArg)
    {
        errorKey = null;
        errorArg = null;

        if (UseHoursAhead)
        {
            utcStart = DateTime.UtcNow;
            utcEnd = utcStart.AddHours(FilterPredictionHours);
            return true;
        }

        utcStart = CombineDateAndTime(RangeStartDate, RangeStartHour, RangeStartMinute);
        utcEnd = CombineDateAndTime(RangeEndDate, RangeEndHour, RangeEndMinute);

        if (utcEnd <= utcStart)
        {
            errorKey = "Mutual.Status.InvalidRange";
            return false;
        }

        if (utcEnd - utcStart > TimeSpan.FromDays(MaxDateRangeDays))
        {
            errorKey = "Mutual.Status.RangeTooLong";
            errorArg = MaxDateRangeDays;
            return false;
        }

        return true;
    }

    private DateTime CombineDateAndTime(DateTimeOffset date, int hour, int minute)
    {
        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);

        if (UseUtcTime)
        {
            var d = date.UtcDateTime.Date;
            return new DateTime(d.Year, d.Month, d.Day, hour, minute, 0, DateTimeKind.Utc);
        }

        var localDate = date.LocalDateTime.Date;
        var local = new DateTime(localDate.Year, localDate.Month, localDate.Day, hour, minute, 0, DateTimeKind.Local);
        return local.ToUniversalTime();
    }

    private string FormatRangePoint(DateTime utc, ClockDisplayFormat clockFormat) =>
        PassDisplayFormat.FormatLocal(utc, clockFormat, useUtc: UseUtcTime);

    public bool CanOpenVisualizer(MutualPassRow? row) =>
        row is not null && _lastLocalSite is not null && _lastRemoteSite is not null;

    public MutualPassVisualizerViewModel? CreateVisualizerViewModel(MutualPassRow row)
    {
        if (!CanOpenVisualizer(row))
            return null;

        var vm = App.Services.GetRequiredService<MutualPassVisualizerViewModel>();
        vm.Initialize(
            row.Source,
            _lastLocalSite!,
            _lastRemoteSite!,
            UseUtcTime,
            _settings.Current.Use24HourClock,
            FilterMinElevationDeg);
        return vm;
    }

    public bool CanCopyPass(MutualPassRow? row) =>
        row is not null && _lastLocalSite is not null && _lastRemoteSite is not null;

    public string? FormatCopyText(MutualPassRow? row)
    {
        if (!CanCopyPass(row))
            return null;

        return MutualPassCopyFormatter.Format(
            row!.Source,
            _lastLocalSite!,
            _lastRemoteSite!,
            BuildCopyLabels(),
            UseUtcTime,
            PassDisplayFormat.FromSettings(_settings.Current.Use24HourClock));
    }

    private MutualPassCopyFormatter.Labels BuildCopyLabels() => new()
    {
        Title = _l.Get("Mutual.Copy.Title"),
        Between = _l.Get("Mutual.Copy.Between"),
        TimesIn = _l.Get("Mutual.Copy.TimesIn"),
        MutualWindowHeader = _l.Get("Mutual.Copy.MutualWindow"),
        MutualWindowLine = _l.Get("Mutual.Copy.MutualWindowLine"),
        YourPassHeader = _l.Get("Mutual.Copy.YourPass"),
        RemotePassHeader = _l.Get("Mutual.Copy.RemotePass"),
        PassTimes = _l.Get("Mutual.Copy.PassTimes"),
        MaxElevation = _l.Get("Mutual.Copy.MaxElevation"),
        Azimuth = _l.Get("Mutual.Copy.Azimuth")
    };
}

public sealed class MutualPassRow
{
    public MutualPassInfo Source { get; init; } = null!;
    public string SatelliteName { get; init; } = "";
    public string MutualWindowLine { get; init; } = "";
    public string OverlapDuration { get; init; } = "";
    public string MaxElPair { get; init; } = "";
    public string LocalPassLine { get; init; } = "";
    public string RemotePassLine { get; init; } = "";

    public static MutualPassRow From(
        MutualPassInfo pass,
        string localLabel,
        string remoteLabel,
        bool useUtc,
        bool use24HourClock)
    {
        var clockFormat = PassDisplayFormat.FromSettings(use24HourClock);
        return new()
        {
            Source = pass,
            SatelliteName = pass.SatelliteName,
            MutualWindowLine = PassDisplayFormat.FormatMutualWindowLine(
                pass.MutualStartUtc, pass.MutualEndUtc, useUtc: useUtc, clockFormat: clockFormat),
            OverlapDuration = PassDisplayFormat.FormatDurationMinutes(pass.Duration),
            MaxElPair = $"{pass.LocalPass.MaxElevationDeg:F1} / {pass.RemotePass.MaxElevationDeg:F1}",
            LocalPassLine = PassDisplayFormat.FormatPlannerAosLosLine(
                pass.LocalPass.AosUtc, pass.LocalPass.LosUtc, useUtc: useUtc, clockFormat: clockFormat),
            RemotePassLine = PassDisplayFormat.FormatPlannerAosLosLine(
                pass.RemotePass.AosUtc, pass.RemotePass.LosUtc, useUtc: useUtc, clockFormat: clockFormat)
        };
    }
}
