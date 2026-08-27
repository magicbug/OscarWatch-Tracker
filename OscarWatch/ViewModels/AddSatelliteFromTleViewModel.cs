using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Localization;

namespace OscarWatch.ViewModels;

public partial class AddSatelliteFromTleViewModel : ViewModelBase
{
    private readonly ILocalizationService _l;
    private readonly List<TleSatellitePickItem> _allCandidates = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _customName = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private TleSatellitePickItem? _selectedCandidate;

    public ObservableCollection<TleSatellitePickItem> FilteredCandidates { get; } = [];

    public bool HasCatalogCandidates => _allCandidates.Count > 0;

    public string IntroText => HasCatalogCandidates
        ? _l.Get("DbAdd.Intro.WithCatalog")
        : _l.Get("DbAdd.Intro.NoCatalog");

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public AddSatelliteFromTleViewModel(
        IReadOnlyList<SatelliteCatalogEntry> catalog,
        IEnumerable<SatelliteRadioEntry> existingEntries,
        ILocalizationService localization)
    {
        _l = localization;
        foreach (var entry in TransponderDatabaseTlePicker.ListAvailable(catalog, existingEntries))
        {
            _allCandidates.Add(new TleSatellitePickItem(entry.Name, entry.NoradId));
        }

        ApplyFilter();
        OnPropertyChanged(nameof(HasCatalogCandidates));
        OnPropertyChanged(nameof(IntroText));
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));

    partial void OnCustomNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            SelectedCandidate = null;
    }

    partial void OnSelectedCandidateChanged(TleSatellitePickItem? value)
    {
        if (value is not null)
            CustomName = "";
    }

    public bool TryConfirm(out string? name, out string? noradId, out string? error)
    {
        name = TransponderDatabaseTlePicker.ResolveChosenName(SelectedCandidate?.Name, CustomName);
        if (name is null)
        {
            noradId = null;
            error = _l.Get("DbAdd.Error.PickOne");
            StatusMessage = error;
            return false;
        }

        noradId = SelectedCandidate?.NoradId;
        error = null;
        StatusMessage = "";
        return true;
    }

    private void ApplyFilter()
    {
        var filter = SearchText.Trim();
        FilteredCandidates.Clear();
        IEnumerable<TleSatellitePickItem> query = _allCandidates;
        if (!string.IsNullOrEmpty(filter))
        {
            query = query.Where(c =>
                c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || c.NoradId.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in query)
            FilteredCandidates.Add(item);

        if (SelectedCandidate is not null
            && !FilteredCandidates.Any(c => string.Equals(c.Name, SelectedCandidate.Name, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedCandidate = null;
        }
    }
}

public sealed class TleSatellitePickItem(string name, string noradId)
{
    public string Name { get; } = name;
    public string NoradId { get; } = noradId;
    public string Display => string.IsNullOrWhiteSpace(NoradId) ? Name : $"{Name}  ({NoradId})";
}
