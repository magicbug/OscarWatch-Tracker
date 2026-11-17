using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Localization;

namespace OscarWatch.ViewModels;

public enum SatellitePickerSelectionFilter
{
    All,
    Selected,
    NotSelected
}

public partial class SatellitePickerViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly ITleService _tleService;
    private readonly ILocalizationService _l;
    private List<SatelliteCatalogEntry> _loadedCatalog = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private SatellitePickerFilterOption? _selectedSelectionFilterChoice;

    public IReadOnlyList<SatellitePickerFilterOption> SelectionFilterChoices { get; }

    public ObservableCollection<SatelliteItemViewModel> Satellites { get; } = [];

    public ObservableCollection<SatelliteItemViewModel> FilteredSatellites { get; } = [];

    public SatellitePickerViewModel(
        ISettingsService settings,
        ITleService tleService,
        ILocalizationService localization)
    {
        _settings = settings;
        _tleService = tleService;
        _l = localization;
        SelectionFilterChoices =
        [
            new(SatellitePickerSelectionFilter.All, _l.Get("Picker.Filter.All")),
            new(SatellitePickerSelectionFilter.Selected, _l.Get("Picker.Filter.Selected")),
            new(SatellitePickerSelectionFilter.NotSelected, _l.Get("Picker.Filter.NotSelected"))
        ];
        SelectedSelectionFilterChoice = SelectionFilterChoices[0];
        Load();
    }

    private void Load()
    {
        Satellites.Clear();
        _loadedCatalog = _tleService.Catalog.ToList();
        var enabledNames = new HashSet<string>(
            _settings.Current.EnabledSatelliteNames ?? [],
            StringComparer.OrdinalIgnoreCase);
        var enabledIds = SatelliteCatalogMatching.CreateNoradIdSet(_settings.Current.EnabledSatelliteNoradIds);
        var utcNow = DateTime.UtcNow;
        var warningDays = _settings.Current.ElementAgeWarningDays;
        foreach (var sat in _loadedCatalog.OrderBy(s => s.Name))
        {
            var item = new SatelliteItemViewModel
            {
                Name = sat.Name,
                NoradId = sat.NoradId,
                // Same rules as TleService.GetEnabledSatellites so checkboxes match the map.
                IsEnabled = SatelliteCatalogMatching.IsEnabled(sat, enabledIds, enabledNames),
                IsStale = ElementAgeWarning.IsOldEnough(sat.EpochUtc, utcNow, warningDays),
                EpochText = sat.EpochUtc?.ToString("yyyy-MM-dd") ?? "?"
            };
            item.EnabledChanged += ApplyFilters;
            Satellites.Add(item);
        }

        ApplyFilters();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();

    partial void OnSelectedSelectionFilterChoiceChanged(SatellitePickerFilterOption? value) => ApplyFilters();

    private void ApplyFilters()
    {
        var search = SearchText.Trim();
        var selectionFilter = SelectedSelectionFilterChoice?.Value ?? SatellitePickerSelectionFilter.All;

        foreach (var s in Satellites)
        {
            var matchesSearch = string.IsNullOrEmpty(search) ||
                                s.Name.Contains(search, StringComparison.OrdinalIgnoreCase);
            var matchesSelection = selectionFilter switch
            {
                SatellitePickerSelectionFilter.Selected => s.IsEnabled,
                SatellitePickerSelectionFilter.NotSelected => !s.IsEnabled,
                _ => true
            };
            s.IsVisible = matchesSearch && matchesSelection;
        }

        FilteredSatellites.Clear();
        foreach (var s in Satellites.Where(x => x.IsVisible))
            FilteredSatellites.Add(s);
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var s in FilteredSatellites.ToList())
            s.IsEnabled = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var s in Satellites)
            s.IsEnabled = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var enabled = Satellites.Where(s => s.IsEnabled).ToList();

        // Selections for satellites missing from the loaded catalogue (for example while TLE
        // downloads are failing) cannot be shown here, so they must survive the save.
        var catalogIds = SatelliteCatalogMatching.CreateNoradIdSet(_loadedCatalog.Select(s => s.NoradId));
        var keptIds = (_settings.Current.EnabledSatelliteNoradIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id) && !SatelliteCatalogMatching.MatchesNoradId(id, catalogIds));
        var keptNames = (_settings.Current.EnabledSatelliteNames ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name) && !MatchesAnyLoadedSatellite(name));

        _settings.Current.EnabledSatelliteNames = enabled
            .Select(s => s.Name)
            .Concat(keptNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings.Current.EnabledSatelliteNoradIds = enabled
            .Select(s => s.NoradId)
            .Concat(keptIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(SatelliteCatalogMatching.NormalizeNoradId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        await _settings.SaveAsync();
    }

    private bool MatchesAnyLoadedSatellite(string enabledName)
    {
        var names = new HashSet<string>([enabledName], StringComparer.OrdinalIgnoreCase);
        return _loadedCatalog.Any(s => SatelliteCatalogMatching.IsEnabled(s, names));
    }
}

public partial class SatelliteItemViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isVisible = true;

    public string Name { get; init; } = "";
    public string NoradId { get; init; } = "";
    public bool IsStale { get; init; }
    public string EpochText { get; init; } = "";

    public event Action? EnabledChanged;

    partial void OnIsEnabledChanged(bool value) => EnabledChanged?.Invoke();
}

public sealed record SatellitePickerFilterOption(SatellitePickerSelectionFilter Value, string Label);
