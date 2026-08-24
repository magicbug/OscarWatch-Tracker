using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using OscarWatch.Core.Export;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Display;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using OscarWatch.Services;
using OscarWatch.Views;

namespace OscarWatch.ViewModels;

public partial class PassPlanningViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly ITleService _tleService;
    private readonly TrackingOrchestrator _tracking;
    private readonly IHamsAtRovesService _hamsAtRoves;
    private readonly ILocalizationService _l;
    private bool _isSynchronizing;

    public IReadOnlyList<string> TimeDisplayLabels { get; }

    public ObservableCollection<StationProfile> Stations { get; } = [];
    public ObservableCollection<PassPlanningPassRow> Passes { get; } = [];
    public ObservableCollection<PassPlanningPassRow> DisplayedPasses { get; } = [];
    public ObservableCollection<SatelliteFilterOption> SatelliteFilters { get; } = [];
    public ObservableCollection<HorizonMaskPoint> HorizonMaskPoints { get; } = [];

    [ObservableProperty]
    private SatelliteFilterOption? _selectedSatelliteFilter;

    [ObservableProperty]
    private StationProfile? _selectedStation;

    [ObservableProperty]
    private string _stationDisplayName = "";

    [ObservableProperty]
    private double _stationLatitudeDeg;

    [ObservableProperty]
    private double _stationLongitudeDeg;

    [ObservableProperty]
    private double _stationAltitudeMeters;

    [ObservableProperty]
    private string _stationGridSquare = "";

    [ObservableProperty]
    private double _filterMinElevationDeg = 5;

    [ObservableProperty]
    private int _filterMinDurationMinutes = 2;

    [ObservableProperty]
    private int _filterPredictionHours = 48;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _canDeleteStation;

    [ObservableProperty]
    private bool _useUtcTime;

    public bool HasHamsAtApiKey =>
        !string.IsNullOrWhiteSpace(_settings.Current.HamsAt.ApiKey);

    public PassPlanningViewModel(
        ISettingsService settings,
        ITleService tleService,
        TrackingOrchestrator tracking,
        IHamsAtRovesService hamsAtRoves,
        ILocalizationService localization)
    {
        _settings = settings;
        _tleService = tleService;
        _tracking = tracking;
        _hamsAtRoves = hamsAtRoves;
        _l = localization;
        TimeDisplayLabels =
        [
            _l.Get("Pass.Time.Local"),
            _l.Get("Pass.Time.Utc")
        ];
    }

    public void Initialize()
    {
        _settings.EnsureSavedStations();
        Stations.Clear();
        foreach (var station in _settings.Current.SavedStations)
            Stations.Add(station);

        FilterMinElevationDeg = _settings.Current.MinimumElevationDeg;
        FilterMinDurationMinutes = _settings.Current.PassFilterMinDurationMinutes;
        FilterPredictionHours = _settings.Current.PassPredictionHours;
        UseUtcTime = _settings.Current.PassPlannerUseUtcTime;

        SelectedStation = Stations.FirstOrDefault(s => s.Id == _settings.Current.ActiveStationId)
            ?? Stations.FirstOrDefault();
        UpdateCanDeleteStation();
    }

    partial void OnSelectedStationChanged(StationProfile? value)
    {
        if (value is null)
            return;

        _isSynchronizing = true;
        try
        {
            StationDisplayName = value.DisplayName;
            StationLatitudeDeg = value.LatitudeDeg;
            StationLongitudeDeg = value.LongitudeDeg;
            StationAltitudeMeters = value.AltitudeMetersAsl;
            StationGridSquare = value.GridSquare;
            HorizonMaskPoints.Clear();
            foreach (var p in (value.HorizonMask ?? new HorizonMask()).Points)
                HorizonMaskPoints.Add(new HorizonMaskPoint(p.AzimuthDeg, p.ElevationDeg));
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private void ApplyEditableFieldsToSelectedStation()
    {
        if (SelectedStation is null)
            return;

        SelectedStation.DisplayName = StationDisplayName;
        SelectedStation.LatitudeDeg = StationLatitudeDeg;
        SelectedStation.LongitudeDeg = StationLongitudeDeg;
        SelectedStation.AltitudeMetersAsl = StationAltitudeMeters;
        SelectedStation.GridSquare = StationGridSquare.Trim();
        var mask = new HorizonMask();
        foreach (var p in HorizonMaskPoints)
            mask.Points.Add(new HorizonMaskPoint(p.AzimuthDeg, p.ElevationDeg));
        mask.Normalize();
        SelectedStation.HorizonMask = mask;
    }

    partial void OnStationLatitudeDegChanged(double value)
    {
        if (_isSynchronizing)
            return;

        _isSynchronizing = true;
        try
        {
            var grid = MaidenheadGrid.FromLatLon(StationLatitudeDeg, StationLongitudeDeg);
            if (!string.Equals(StationGridSquare, grid, StringComparison.Ordinal))
                StationGridSquare = grid;
        }
        finally
        {
            _isSynchronizing = false;
        }

        SyncStationFromEditableFields();
    }

    partial void OnStationLongitudeDegChanged(double value)
    {
        if (_isSynchronizing)
            return;

        _isSynchronizing = true;
        try
        {
            var grid = MaidenheadGrid.FromLatLon(StationLatitudeDeg, StationLongitudeDeg);
            if (!string.Equals(StationGridSquare, grid, StringComparison.Ordinal))
                StationGridSquare = grid;
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    partial void OnStationDisplayNameChanged(string value) => SyncStationFromEditableFields();
    partial void OnStationAltitudeMetersChanged(double value) => SyncStationFromEditableFields();

    partial void OnStationGridSquareChanged(string value)
    {
        if (_isSynchronizing || string.IsNullOrWhiteSpace(value) || value.Length < 4)
            return;

        _isSynchronizing = true;
        try
        {
            var (lat, lon) = MaidenheadGrid.ToLatLonCenter(value.Trim());
            if (!StationLatitudeDeg.Equals(lat))
                StationLatitudeDeg = lat;
            if (!StationLongitudeDeg.Equals(lon))
                StationLongitudeDeg = lon;
        }
        catch
        {
            // invalid grid
        }
        finally
        {
            _isSynchronizing = false;
        }

        SyncStationFromEditableFields();
    }

    private void SyncStationFromEditableFields()
    {
        if (_isSynchronizing)
            return;

        ApplyEditableFieldsToSelectedStation();
    }

    [RelayCommand]
    private void AddStation()
    {
        var home = Stations.FirstOrDefault();
        var profile = new StationProfile
        {
            DisplayName = _l.Get("Planner.PortableName", Stations.Count + 1),
            Callsign = home?.Callsign ?? "",
            LatitudeDeg = home?.LatitudeDeg ?? 51.5,
            LongitudeDeg = home?.LongitudeDeg ?? -0.1,
            AltitudeMetersAsl = home?.AltitudeMetersAsl ?? 50,
            GridSquare = home?.GridSquare ?? "IO91wm"
        };
        _settings.Current.SavedStations.Add(profile);
        Stations.Add(profile);
        SelectedStation = profile;
        UpdateCanDeleteStation();
    }

    [RelayCommand]
    private void DeleteStation()
    {
        if (SelectedStation is null || Stations.Count <= 1)
            return;

        var removed = SelectedStation;
        _settings.Current.SavedStations.Remove(removed);
        Stations.Remove(removed);

        if (_settings.Current.ActiveStationId == removed.Id)
            _settings.Current.ActiveStationId = Stations[0].Id;

        SelectedStation = Stations[0];
        UpdateCanDeleteStation();
    }

    private void UpdateCanDeleteStation() => CanDeleteStation = Stations.Count > 1;

    partial void OnUseUtcTimeChanged(bool value)
    {
        OnPropertyChanged(nameof(TimeDisplayIndex));
        _settings.Current.PassPlannerUseUtcTime = value;
        RefreshPassDisplayTimes();
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

    private void RefreshPassDisplayTimes()
    {
        if (Passes.Count == 0)
            return;

        var rows = Passes.ToList();
        Passes.Clear();
        var scheduled = _settings.Current.ScheduledPasses ?? [];
        foreach (var row in rows)
        {
            var updated = PassPlanningPassRow.From(row.Source, UseUtcTime, _settings.Current.Use24HourClock);
            updated.IsScheduled = ScheduledPassReminder.IsScheduled(
                scheduled,
                row.Source.NoradId,
                row.Source.AosUtc);
            Passes.Add(updated);
        }

        RebuildDisplayedPasses();
    }

    partial void OnSelectedSatelliteFilterChanged(SatelliteFilterOption? value) =>
        RebuildDisplayedPasses();

    private void RebuildSatelliteFilters()
    {
        var previousNorad = SelectedSatelliteFilter?.NoradId;
        SatelliteFilters.Clear();
        SatelliteFilters.Add(SatelliteFilterOption.All(_l));

        foreach (var group in Passes
                     .Select(p => p.Source)
                     .GroupBy(p => p.NoradId)
                     .OrderBy(g => g.First().SatelliteName, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            SatelliteFilters.Add(new SatelliteFilterOption(first.SatelliteName, first.NoradId));
        }

        SelectedSatelliteFilter = previousNorad is null
            ? SatelliteFilters.FirstOrDefault()
            : SatelliteFilters.FirstOrDefault(f => f.NoradId == previousNorad)
              ?? SatelliteFilters.FirstOrDefault();
    }

    private void RebuildDisplayedPasses()
    {
        DisplayedPasses.Clear();
        var noradId = SelectedSatelliteFilter?.NoradId;
        foreach (var row in Passes)
        {
            if (noradId is not null && row.Source.NoradId != noradId)
                continue;

            DisplayedPasses.Add(row);
        }

        OnPropertyChanged(nameof(CanOpenPassRadarGallery));
    }

    public bool CanOpenPassRadarGallery =>
        SelectedSatelliteFilter?.NoradId is not null && DisplayedPasses.Count > 0;

    public async Task<PassRadarGalleryViewModel?> CreatePassRadarGalleryViewModelAsync()
    {
        if (!CanOpenPassRadarGallery)
            return null;

        ApplyEditableFieldsToSelectedStation();
        var site = SelectedStation?.ToGroundStation() ?? _settings.Current.GroundStation;
        var passes = DisplayedPasses.Select(p => p.Source).ToList();
        var satelliteName = passes[0].SatelliteName;

        var vm = App.Services.GetRequiredService<PassRadarGalleryViewModel>();
        await vm.InitializeAsync(
            satelliteName,
            site,
            passes,
            UseUtcTime,
            _settings.Current.Use24HourClock,
            FilterMinElevationDeg,
            FilterPredictionHours);
        return vm;
    }

    [RelayCommand]
    private async Task RefreshPassesAsync()
    {
        ApplyEditableFieldsToSelectedStation();
        StatusText = _l.Get("Pass.Computing");

        try
        {
            await _tleService.EnsureLoadedAsync();
            var site = SelectedStation?.ToGroundStation() ?? _settings.Current.GroundStation;
            var passes = await _tracking.GetPassesAsync(
                site,
                FilterMinElevationDeg,
                FilterPredictionHours,
                FilterMinDurationMinutes);

            Passes.Clear();
            var scheduled = _settings.Current.ScheduledPasses ?? [];
            foreach (var pass in passes)
            {
                var row = PassPlanningPassRow.From(pass, UseUtcTime, _settings.Current.Use24HourClock);
                row.IsScheduled = ScheduledPassReminder.IsScheduled(scheduled, pass.NoradId, pass.AosUtc);
                Passes.Add(row);
            }

            RematchScheduledPasses(passes);
            ApplyScheduledFlags();
            RebuildSatelliteFilters();
            RebuildDisplayedPasses();

            StatusText = _l.Get("Pass.CountPasses", Passes.Count, FilterPredictionHours);
        }
        catch (Exception ex)
        {
            StatusText = _l.Get("Pass.Failed", ex.Message);
        }
    }

    public async Task<bool> ExportSatelliteIcsAsync(Window owner, PassPlanningPassRow row)
    {
        var passInfos = Passes
            .Where(p => p.Source.NoradId == row.Source.NoradId)
            .Select(p => p.Source)
            .ToList();

        if (passInfos.Count == 0)
        {
            StatusText = _l.Get("Planner.Export.RefreshFirst");
            return false;
        }

        var storage = TopLevel.GetTopLevel(owner)?.StorageProvider;
        if (storage is null)
            return false;

        var safeName = SanitizeFileName(row.SatelliteName, _l);
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = _l.Get("Planner.Export.Title", row.SatelliteName),
            SuggestedFileName = $"oscarwatch-{safeName}.ics",
            DefaultExtension = "ics",
            FileTypeChoices =
            [
                new FilePickerFileType(_l.Get("Planner.Export.FileType")) { Patterns = ["*.ics"] }
            ]
        });

        if (file is null)
            return false;

        ApplyEditableFieldsToSelectedStation();
        var site = SelectedStation?.ToGroundStation() ?? _settings.Current.GroundStation;
        var ics = IcsPassExporter.BuildCalendar(
            passInfos,
            site,
            _l.Get("Planner.Export.CalendarTitle", row.SatelliteName, site.DisplayName));

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(ics);

        StatusText = _l.Get("Planner.Export.Done", passInfos.Count, row.SatelliteName);
        return true;
    }

    private static string SanitizeFileName(string name, ILocalizationService l)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrEmpty(sanitized) ? l.Get("Planner.Export.SatelliteFile") : sanitized;
    }

    [RelayCommand]
    private async Task CopySatelliteDetailsAsync(PassPlanningPassRow row)
    {
        var pass = row.Source;
        var details = string.Join(
            Environment.NewLine,
            $"Satellite: {row.SatelliteName}",
            $"NORAD ID: {pass.NoradId}",
            $"AOS: {row.AosLocal}",
            $"LOS: {row.LosLocal}",
            $"TCA: {row.TcaLocal}",
            $"Max Elevation: {row.MaxEl}",
            $"Duration: {row.Duration}",
            $"Azimuth: {row.AzimuthSummary}");

        var clipboard = TopLevel.GetTopLevel(App.MainWindow)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(details);
    }

    [RelayCommand]
    private void OpenPassVisualizer(PassPlanningPassRow row)
    {
        if (App.MainWindow is null)
            return;

        ApplyEditableFieldsToSelectedStation();
        var site = SelectedStation?.ToGroundStation() ?? _settings.Current.GroundStation;

        var vm = App.Services.GetRequiredService<PassVisualizerViewModel>();
        vm.Initialize(
            row.Source,
            site,
            UseUtcTime,
            _settings.Current.Use24HourClock,
            FilterMinElevationDeg);

        new PassVisualizerWindow
        {
            DataContext = vm
        }.Show(App.MainWindow);
    }

    [RelayCommand]
    private async Task OpenPassRadarGalleryForSatelliteAsync(PassPlanningPassRow row)
    {
        if (App.MainWindow is null)
            return;

        ApplyEditableFieldsToSelectedStation();
        var site = SelectedStation?.ToGroundStation() ?? _settings.Current.GroundStation;
        
        // Get all passes for this satellite
        var passes = Passes
            .Where(p => p.Source.NoradId == row.Source.NoradId)
            .Select(p => p.Source)
            .ToList();

        if (passes.Count == 0)
            return;

        var vm = App.Services.GetRequiredService<PassRadarGalleryViewModel>();
        await vm.InitializeAsync(
            row.SatelliteName,
            site,
            passes,
            UseUtcTime,
            _settings.Current.Use24HourClock,
            FilterMinElevationDeg,
            FilterPredictionHours);

        new PassRadarGalleryWindow
        {
            DataContext = vm
        }.Show(App.MainWindow);
    }

    public async Task SaveFiltersAndStationsAsync()
    {
        ApplyEditableFieldsToSelectedStation();
        _settings.Current.MinimumElevationDeg = FilterMinElevationDeg;
        _settings.Current.PassFilterMinDurationMinutes = FilterMinDurationMinutes;
        _settings.Current.PassPredictionHours = FilterPredictionHours;
        _settings.Current.PassPlannerUseUtcTime = UseUtcTime;
        await _settings.SaveAsync();
    }

    public async Task ApplyAsActiveStationAsync()
    {
        if (SelectedStation is null)
            return;

        ApplyEditableFieldsToSelectedStation();
        _settings.Current.ActiveStationId = SelectedStation.Id;
        _settings.ApplyActiveStation();
        _settings.Current.MinimumElevationDeg = FilterMinElevationDeg;
        _settings.Current.PassFilterMinDurationMinutes = FilterMinDurationMinutes;
        _settings.Current.PassPredictionHours = FilterPredictionHours;
        _settings.Current.PassPlannerUseUtcTime = UseUtcTime;
        await _settings.SaveAsync();
    }

    [RelayCommand]
    private async Task PostHamsAtActivationAsync(PassPlanningPassRow? row)
    {
        if (row is null || App.MainWindow is null)
            return;

        ApplyEditableFieldsToSelectedStation();
        var observer = SelectedStation?.ToGroundStation() ?? _settings.Current.GroundStation;
        var timeRange = _l.Get("Pass.TimeRange", row.AosLocal, row.LosLocal);
        var details = _l.Get(
            "Pass.Details",
            FormatPlannerPassDuration(row.Source.Duration),
            $"{row.Source.MaxElevationDeg:F0}°");

        await HamsAtActivationCoordinator.PostAsync(
            App.MainWindow,
            row.Source,
            observer,
            _settings.Current.GroundStation.Callsign,
            _settings.Current.HamsAt,
            _hamsAtRoves,
            _l,
            timeRange,
            details,
            frequencies: null,
            status => StatusText = status,
            satelliteDatabase: App.Services.GetRequiredService<ISatelliteDatabaseService>(),
            frequencySelections: _settings.Current.FrequencySelections,
            cwKeepSidebandDownlink: _settings.Current.Rig?.CwKeepSidebandDownlink == true).ConfigureAwait(true);
    }

    private string FormatPlannerPassDuration(TimeSpan duration)
    {
        var minutes = duration.TotalSeconds < 30
            ? 0
            : (int)Math.Round(duration.TotalMinutes, MidpointRounding.AwayFromZero);
        return minutes == 1
            ? _l.Get("Pass.DurationOneMinute")
            : _l.Get("Pass.DurationMinutes", minutes);
    }

    [RelayCommand]
    private void TogglePassScheduled(PassPlanningPassRow? row)
    {
        if (row is null)
            return;

        _settings.Current.ScheduledPasses = ScheduledPassReminder.Toggle(
            _settings.Current.ScheduledPasses ?? [],
            row.Source.NoradId,
            row.Source.AosUtc);
        _settings.RequestSave();
        ApplyScheduledFlags();
    }

    private void ApplyScheduledFlags()
    {
        var scheduled = _settings.Current.ScheduledPasses ?? [];
        foreach (var row in Passes)
            row.IsScheduled = ScheduledPassReminder.IsScheduled(scheduled, row.Source.NoradId, row.Source.AosUtc);
        foreach (var row in DisplayedPasses)
            row.IsScheduled = ScheduledPassReminder.IsScheduled(scheduled, row.Source.NoradId, row.Source.AosUtc);
    }

    private void RematchScheduledPasses(IReadOnlyList<PassInfo> upcoming)
    {
        var rematched = ScheduledPassReminder.RematchAndPrune(
            _settings.Current.ScheduledPasses ?? [],
            upcoming,
            DateTime.UtcNow);
        _settings.Current.ScheduledPasses = rematched;
        _settings.RequestSave();
    }
}

public partial class PassPlanningPassRow : ObservableObject
{
    public PassInfo Source { get; init; } = null!;
    public string SatelliteName { get; init; } = "";
    public string AosLocal { get; init; } = "";
    public string LosLocal { get; init; } = "";
    public string TcaLocal { get; init; } = "";
    public string MaxEl { get; init; } = "";
    public string Duration { get; init; } = "";
    public string AzimuthSummary { get; init; } = "";
    public string AosLosLine { get; init; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleLabel))]
    [NotifyPropertyChangedFor(nameof(ScheduleToolTip))]
    private bool _isScheduled;

    public string ScheduleLabel => IsScheduled
        ? LocalizationService.Instance.Get("Pass.Schedule.RemoveShort")
        : LocalizationService.Instance.Get("Pass.Schedule.AddShort");

    public string ScheduleToolTip => IsScheduled
        ? LocalizationService.Instance.Get("Pass.Schedule.RemoveTooltip")
        : LocalizationService.Instance.Get("Pass.Schedule.AddTooltip");

    public static PassPlanningPassRow From(PassInfo p, bool useUtc, bool use24HourClock)
    {
        var clockFormat = PassDisplayFormat.FromSettings(use24HourClock);
        var aosLosLine = PassDisplayFormat.FormatPlannerAosLosLine(p.AosUtc, p.LosUtc, useUtc: useUtc, clockFormat: clockFormat);
        var (aos, los) = PassDisplayFormat.FormatLocalTimes(p.AosUtc, p.LosUtc, useUtc: useUtc, clockFormat: clockFormat);
        var tca = PassDisplayFormat.FormatPlannerTca(p.MaxElevationUtc, p.AosUtc, useUtc: useUtc, clockFormat: clockFormat);
        var az = $"{p.AosAzimuthDeg:F0}°→{p.LosAzimuthDeg:F0}°";

        return new()
        {
            Source = p,
            SatelliteName = p.SatelliteName,
            AosLocal = aos,
            LosLocal = los,
            TcaLocal = tca,
            MaxEl = $"{p.MaxElevationDeg:F1}°",
            Duration = p.Duration.ToString(@"mm\:ss"),
            AzimuthSummary = az,
            AosLosLine = aosLosLine
        };
    }
}

public sealed class SatelliteFilterOption
{
    public string DisplayName { get; }
    public string? NoradId { get; }

    public SatelliteFilterOption(string displayName, string? noradId)
    {
        DisplayName = displayName;
        NoradId = noradId;
    }

    public static SatelliteFilterOption All(ILocalizationService l) =>
        new(l.Get("Planner.SatelliteFilter.All"), noradId: null);

    public override string ToString() => DisplayName;
}
