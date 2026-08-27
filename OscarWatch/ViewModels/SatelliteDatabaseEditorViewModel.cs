using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using OscarWatch.Views;

namespace OscarWatch.ViewModels;

public partial class SatelliteDatabaseEditorViewModel : ViewModelBase
{
    private readonly ISatelliteDatabaseEditor _editor;
    private readonly ISatelliteDatabaseSyncService _syncService;
    private readonly ITleService _tleService;
    private readonly ILocalizationService _l;
    private bool _syncingModeFields;
    private bool _syncingSatelliteFields;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _databasePathText = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private SatelliteRadioEntry? _selectedSatellite;

    [ObservableProperty]
    private SatelliteTransponderMode? _selectedMode;

    [ObservableProperty]
    private string _satelliteName = "";

    [ObservableProperty]
    private string _satelliteNoradId = "";

    [ObservableProperty]
    private string _satelliteAlternativeNames = "";

    [ObservableProperty]
    private string _modeType = "";

    [ObservableProperty]
    private double _downlinkKHz;

    [ObservableProperty]
    private double _uplinkKHz;

    [ObservableProperty]
    private string _downlinkMode = "USB";

    [ObservableProperty]
    private string _uplinkMode = "LSB";

    [ObservableProperty]
    private string _doppler = "NOR";

    [ObservableProperty]
    private double? _ctcssHz;

    [ObservableProperty]
    private double? _ctcssArmHz;

    public ObservableCollection<SatelliteRadioEntry> Satellites { get; } = [];

    public bool HasSelectedSatellite => SelectedSatellite is not null;

    public bool HasSelectedMode => SelectedMode is not null;

    public IReadOnlyList<string> OperatingModes { get; } = TransponderCatModes.EditorOptions;

    public IReadOnlyList<string> DopplerOptions { get; } = ["NOR", "REV"];

    public SatelliteDatabaseEditorViewModel(
        ISatelliteDatabaseEditor editor,
        ISatelliteDatabaseSyncService syncService,
        ITleService tleService,
        ILocalizationService localization)
    {
        _editor = editor;
        _syncService = syncService;
        _tleService = tleService;
        _l = localization;
        Load();
    }

    private void Load()
    {
        Satellites.Clear();
        foreach (var entry in _editor.LoadForEditing())
            Satellites.Add(entry);

        DatabasePathText = _editor.IsUsingUserDatabase
            ? _l.Get("DbEditor.Path.User", _editor.UserPath)
            : _l.Get("DbEditor.Path.Shipped", _editor.BundledPath);

        SelectedSatellite = Satellites.FirstOrDefault();
        StatusMessage = _l.Get("DbEditor.Status.Loaded", Satellites.Count);
    }

    partial void OnSearchTextChanged(string value) => NotifyFilteredSatellites();

    partial void OnSelectedSatelliteChanged(SatelliteRadioEntry? value)
    {
        _syncingSatelliteFields = true;
        try
        {
            SatelliteName = value?.Name ?? "";
            SatelliteNoradId = value?.NoradId ?? "";
            SatelliteAlternativeNames = FormatAlternativeNames(value);
        }
        finally
        {
            _syncingSatelliteFields = false;
        }

        SelectedMode = value?.Modes.FirstOrDefault();
        OnPropertyChanged(nameof(HasSelectedSatellite));
        RefreshAliasNoradHint();
    }

    partial void OnSelectedModeChanged(SatelliteTransponderMode? value)
    {
        LoadModeFields(value);
        OnPropertyChanged(nameof(HasSelectedMode));
    }

    partial void OnSatelliteNameChanged(string value)
    {
        if (_syncingSatelliteFields || SelectedSatellite is null)
            return;

        var previous = SelectedSatellite.Name.Trim();
        var next = value.Trim();
        SelectedSatellite.Name = next;

        if (!string.IsNullOrEmpty(previous)
            && !string.IsNullOrEmpty(next)
            && !string.Equals(previous, next, StringComparison.OrdinalIgnoreCase))
        {
            SelectedSatellite.AlternativeNames ??= [];
            if (!SelectedSatellite.AlternativeNames.Any(a =>
                    string.Equals(a.Trim(), previous, StringComparison.OrdinalIgnoreCase))
                && !string.Equals(previous, next, StringComparison.OrdinalIgnoreCase))
            {
                SelectedSatellite.AlternativeNames.Add(previous);
            }

            SelectedSatellite.AlternativeNames = SatelliteDatabaseFile.NormalizeAlternativeNames(
                next,
                SelectedSatellite.AlternativeNames);

            _syncingSatelliteFields = true;
            try
            {
                SatelliteAlternativeNames = FormatAlternativeNames(SelectedSatellite);
            }
            finally
            {
                _syncingSatelliteFields = false;
            }
        }

        RefreshAliasNoradHint();
    }

    partial void OnSatelliteNoradIdChanged(string value)
    {
        if (_syncingSatelliteFields || SelectedSatellite is null)
            return;

        SelectedSatellite.NoradId = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        RefreshAliasNoradHint();
    }

    partial void OnSatelliteAlternativeNamesChanged(string value)
    {
        if (_syncingSatelliteFields || SelectedSatellite is null)
            return;

        SelectedSatellite.AlternativeNames = ParseAlternativeNames(value);
        SelectedSatellite.AlternativeNames = SatelliteDatabaseFile.NormalizeAlternativeNames(
            SelectedSatellite.Name,
            SelectedSatellite.AlternativeNames);
        RefreshAliasNoradHint();
    }

    partial void OnModeTypeChanged(string value) => ApplyModeField(m => m.Type = value.Trim());
    partial void OnDownlinkKHzChanged(double value) => ApplyModeField(m => m.DownlinkKHz = value);
    partial void OnUplinkKHzChanged(double value) => ApplyModeField(m => m.UplinkKHz = value);
    partial void OnDownlinkModeChanged(string value) => ApplyModeField(m => m.DownlinkMode = value);
    partial void OnUplinkModeChanged(string value) => ApplyModeField(m => m.UplinkMode = value);
    partial void OnDopplerChanged(string value) => ApplyModeField(m => m.Doppler = value);
    partial void OnCtcssHzChanged(double? value) => ApplyModeField(m => m.CtcssHz = value is > 0 ? value : null);
    partial void OnCtcssArmHzChanged(double? value) => ApplyModeField(m => m.CtcssArmHz = value is > 0 ? value : null);

    private void LoadModeFields(SatelliteTransponderMode? mode)
    {
        _syncingModeFields = true;
        try
        {
            if (mode is null)
            {
                ModeType = "";
                DownlinkKHz = 0;
                UplinkKHz = 0;
                DownlinkMode = "USB";
                UplinkMode = "LSB";
                Doppler = "NOR";
                CtcssHz = null;
                CtcssArmHz = null;
                return;
            }

            ModeType = mode.Type;
            DownlinkKHz = mode.DownlinkKHz;
            UplinkKHz = mode.UplinkKHz;
            DownlinkMode = mode.DownlinkMode;
            UplinkMode = mode.UplinkMode;
            Doppler = mode.Doppler;
            CtcssHz = mode.CtcssHz;
            CtcssArmHz = mode.CtcssArmHz;
        }
        finally
        {
            _syncingModeFields = false;
        }
    }

    private void ApplyModeField(Action<SatelliteTransponderMode> apply)
    {
        if (_syncingModeFields || SelectedMode is null)
            return;

        apply(SelectedMode);
    }

    private void NotifyFilteredSatellites() => OnPropertyChanged(nameof(FilteredSatellites));

    public IEnumerable<SatelliteRadioEntry> FilteredSatellites
    {
        get
        {
            var filter = SearchText.Trim();
            return string.IsNullOrEmpty(filter)
                ? Satellites
                : Satellites.Where(s =>
                    s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || (s.AlternativeNames?.Any(a =>
                        a.Contains(filter, StringComparison.OrdinalIgnoreCase)) ?? false)
                    || (s.NoradId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }
    }

    [RelayCommand]
    private async Task AddSatelliteAsync()
    {
        if (App.MainWindow is null)
            return;

        var existing = Satellites.ToList();
        var pick = await AddSatelliteFromTleDialog.TryPickAsync(App.MainWindow, _tleService, existing)
            .ConfigureAwait(true);

        if (pick is null)
        {
            StatusMessage = _l.Get("DbEditor.Status.AddCancelled");
            return;
        }

        var trimmed = pick.Name.Trim();
        if (Satellites.Any(s => s.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = _l.Get("DbEditor.Status.AlreadyExists", trimmed);
            return;
        }

        var entry = new SatelliteRadioEntry
        {
            Name = trimmed,
            NoradId = string.IsNullOrWhiteSpace(pick.NoradId) ? null : pick.NoradId.Trim(),
            Modes =
            [
                new SatelliteTransponderMode
                {
                    Type = "FM",
                    DownlinkKHz = 145_825,
                    UplinkKHz = 145_825,
                    DownlinkMode = "FMN",
                    UplinkMode = "FMN",
                    Doppler = "NOR"
                }
            ]
        };
        Satellites.Add(entry);
        SelectedSatellite = entry;
        NotifyFilteredSatellites();
        StatusMessage = _l.Get("DbEditor.Status.Added", trimmed);
    }

    [RelayCommand]
    private void RemoveSatellite()
    {
        if (SelectedSatellite is null)
            return;

        var index = Satellites.IndexOf(SelectedSatellite);
        Satellites.Remove(SelectedSatellite);
        SelectedSatellite = Satellites.Count == 0
            ? null
            : Satellites[Math.Min(index, Satellites.Count - 1)];
        NotifyFilteredSatellites();
        StatusMessage = _l.Get("DbEditor.Status.RemovedSatellite");
    }

    [RelayCommand]
    private void AddMode()
    {
        if (SelectedSatellite is null)
            return;

        var mode = new SatelliteTransponderMode
        {
            Type = _l.Get("DbEditor.Mode.NewMode"),
            DownlinkKHz = 435_000,
            UplinkKHz = 145_000,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "NOR"
        };
        SelectedSatellite.Modes.Add(mode);
        SelectedMode = mode;
        StatusMessage = _l.Get("DbEditor.Status.AddedMode");
    }

    [RelayCommand]
    private void RemoveMode()
    {
        if (SelectedSatellite is null || SelectedMode is null)
            return;

        var index = SelectedSatellite.Modes.IndexOf(SelectedMode);
        SelectedSatellite.Modes.Remove(SelectedMode);
        SelectedMode = SelectedSatellite.Modes.Count == 0
            ? null
            : SelectedSatellite.Modes[Math.Min(index, SelectedSatellite.Modes.Count - 1)];
        StatusMessage = _l.Get("DbEditor.Status.RemovedMode");
    }

    [RelayCommand]
    private void DuplicateMode()
    {
        if (SelectedSatellite is null || SelectedMode is null)
            return;

        var clone = new SatelliteTransponderMode
        {
            Type = SelectedMode.Type + _l.Get("DbEditor.Mode.CopySuffix"),
            DownlinkKHz = SelectedMode.DownlinkKHz,
            UplinkKHz = SelectedMode.UplinkKHz,
            DownlinkMode = SelectedMode.DownlinkMode,
            UplinkMode = SelectedMode.UplinkMode,
            Doppler = SelectedMode.Doppler,
            CtcssHz = SelectedMode.CtcssHz,
            CtcssArmHz = SelectedMode.CtcssArmHz
        };
        SelectedSatellite.Modes.Add(clone);
        SelectedMode = clone;
        StatusMessage = _l.Get("DbEditor.Status.DuplicatedMode");
    }

    public bool TrySave(out string? errorMessage)
    {
        try
        {
            foreach (var entry in Satellites)
                SatelliteDatabaseFile.NormalizeEntry(entry);

            if (SelectedSatellite is not null)
            {
                _syncingSatelliteFields = true;
                try
                {
                    SatelliteName = SelectedSatellite.Name;
                    SatelliteNoradId = SelectedSatellite.NoradId ?? "";
                    SatelliteAlternativeNames = FormatAlternativeNames(SelectedSatellite);
                }
                finally
                {
                    _syncingSatelliteFields = false;
                }
            }

            _editor.Save(Satellites.ToList());
            DatabasePathText = _l.Get("DbEditor.Path.User", _editor.UserPath);
            StatusMessage = _l.Get("DbEditor.Status.Saved");
            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            StatusMessage = ex.Message;
            return false;
        }
    }

    [RelayCommand]
    private void ResetToBundled()
    {
        _editor.ResetToBundled();
        Load();
        StatusMessage = _l.Get("DbEditor.Status.Restored");
    }

    private FilePickerFileType JsonFileType => new(_l.Get("DbEditor.FileType.Json"))
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"]
    };

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        if (App.MainWindow is null)
            return;

        var storage = TopLevel.GetTopLevel(App.MainWindow)?.StorageProvider;
        if (storage is null)
            return;

        var validationError = SatelliteDatabaseFile.ValidateEntries(Satellites);
        if (validationError is not null)
        {
            StatusMessage = validationError;
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = _l.Get("DbEditor.Export.Title"),
            SuggestedFileName = "satellite_database.json",
            DefaultExtension = "json",
            FileTypeChoices = [JsonFileType]
        }).ConfigureAwait(true);

        if (file is null)
            return;

        try
        {
            var json = SatelliteDatabaseFile.SerializeEntries(Satellites);
            await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json).ConfigureAwait(true);
            StatusMessage = _l.Get("DbEditor.Status.Exported", Satellites.Count);
        }
        catch (Exception ex)
        {
            StatusMessage = _l.Get("DbEditor.Status.ExportFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportJsonAsync()
    {
        if (App.MainWindow is null)
            return;

        var storage = TopLevel.GetTopLevel(App.MainWindow)?.StorageProvider;
        if (storage is null)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _l.Get("DbEditor.Import.Title"),
            AllowMultiple = false,
            FileTypeFilter = [JsonFileType]
        }).ConfigureAwait(true);

        var file = files.FirstOrDefault();
        if (file is null)
            return;

        try
        {
            StatusMessage = _l.Get("DbEditor.Status.ReadingImport");
            await using var stream = await file.OpenReadAsync().ConfigureAwait(true);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync().ConfigureAwait(true);
            var imported = SatelliteDatabaseFile.ParseJson(json);
            var validationError = SatelliteDatabaseFile.ValidateEntries(imported);
            if (validationError is not null)
            {
                StatusMessage = _l.Get("DbEditor.Status.InvalidDatabase", validationError);
                return;
            }

            var local = SnapshotSatellites();
            var plan = SatelliteDatabaseMerger.BuildPlan(local, imported);
            if (!plan.HasChanges)
            {
                StatusMessage = _l.Get("DbEditor.Status.ImportNoChanges");
                return;
            }

            var result = await TransponderDatabaseMergeDialog.TryMergeApplyAsync(
                App.MainWindow,
                plan,
                local,
                SatelliteDatabaseMergePresentation.FileImport).ConfigureAwait(true);

            if (result is null)
            {
                StatusMessage = _l.Get("DbEditor.Status.ImportCancelled");
                return;
            }

            ReloadFromList(result.Merged);
            StatusMessage = _l.Get("DbEditor.Status.ImportApplied");
        }
        catch (Exception ex)
        {
            StatusMessage = _l.Get("DbEditor.Status.ImportFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (App.MainWindow is null)
            return;

        try
        {
            StatusMessage = _l.Get("DbEditor.Status.CheckingUpdates");
            var plan = await _syncService.FetchMergePlanAsync().ConfigureAwait(true);
            if (!plan.HasChanges)
            {
                StatusMessage = _l.Get("DbEditor.Status.UpToDate");
                return;
            }

            if (await TransponderDatabaseMergeDialog.TryShowAsync(App.MainWindow, plan, _syncService))
            {
                Load();
                StatusMessage = _l.Get("DbEditor.Status.UpdatesApplied");
            }
            else
            {
                StatusMessage = _l.Get("DbEditor.Status.NoChangesApplied");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = _l.Get("DbEditor.Status.UpdateCheckFailed", ex.Message);
        }
    }

    private List<SatelliteRadioEntry> SnapshotSatellites() =>
        Satellites.Select(CloneEntry).ToList();

    private void ReloadFromList(IReadOnlyList<SatelliteRadioEntry> entries)
    {
        Satellites.Clear();
        foreach (var entry in entries)
            Satellites.Add(CloneEntry(entry));

        NotifyFilteredSatellites();
        SelectedSatellite = Satellites.FirstOrDefault();
        StatusMessage = _l.Get("DbEditor.Status.InEditor", Satellites.Count);
    }

    private void RefreshAliasNoradHint()
    {
        if (SelectedSatellite is null)
            return;

        var hasAliases = SelectedSatellite.AlternativeNames is { Count: > 0 };
        var missingNorad = string.IsNullOrWhiteSpace(SelectedSatellite.NoradId);
        if (hasAliases && missingNorad)
            StatusMessage = _l.Get("DbEditor.Status.AliasesWithoutNorad");
    }

    private static string FormatAlternativeNames(SatelliteRadioEntry? entry)
    {
        if (entry?.AlternativeNames is not { Count: > 0 })
            return "";

        return string.Join(", ", entry.AlternativeNames);
    }

    private static List<string> ParseAlternativeNames(string raw) =>
        raw.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();

    private static SatelliteRadioEntry CloneEntry(SatelliteRadioEntry source) =>
        new()
        {
            Name = source.Name,
            NoradId = source.NoradId,
            AlternativeNames = source.AlternativeNames?.ToList() ?? [],
            Modes = source.Modes.Select(CloneMode).ToList()
        };

    private static SatelliteTransponderMode CloneMode(SatelliteTransponderMode source) =>
        new()
        {
            Type = source.Type,
            DownlinkKHz = source.DownlinkKHz,
            UplinkKHz = source.UplinkKHz,
            DownlinkMode = source.DownlinkMode,
            UplinkMode = source.UplinkMode,
            Doppler = source.Doppler,
            CtcssHz = source.CtcssHz,
            CtcssArmHz = source.CtcssArmHz
        };
}
