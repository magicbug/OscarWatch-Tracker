using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Logbook;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Localization;

namespace OscarWatch.ViewModels;

public partial class CreateLogbookViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly ILocalizationService _l;
    private readonly ActivationDirectoryService _directories;
    private readonly ActivationReferenceSuggestions _potaSuggestions;
    private readonly ActivationReferenceSuggestions _sotaSuggestions;
    private readonly ActivationReferenceSuggestions _wwffSuggestions;
    private readonly HashSet<ActivationProgramme> _loadingDirectories = [];
    private bool _suppressFieldCoercion;
    private long? _editingLogbookId;

    public CreateLogbookViewModel(
        ISettingsService settings,
        ILocalizationService localization,
        ActivationDirectoryService directories)
    {
        _settings = settings;
        _l = localization;
        _directories = directories;
        _potaSuggestions = new ActivationReferenceSuggestions(directories, ActivationProgramme.Pota);
        _sotaSuggestions = new ActivationReferenceSuggestions(directories, ActivationProgramme.Sota);
        _wwffSuggestions = new ActivationReferenceSuggestions(directories, ActivationProgramme.Wwff);
        ResetForCreate();
    }

    public bool IsEditMode => _editingLogbookId.HasValue;

    public string WindowTitle =>
        IsEditMode ? _l.Get("Window.EditLogbook.Title") : _l.Get("Window.CreateLogbook.Title");

    public string IntroText =>
        IsEditMode ? _l.Get("Logbook.EditLogbook.Intro") : _l.Get("Logbook.CreateLogbook.Intro");

    public string ConfirmButtonText =>
        IsEditMode ? _l.Get("Common.Save") : _l.Get("Common.Add");

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _myCallsign = "";

    [ObservableProperty]
    private string _myGridSquare = "";

    [ObservableProperty]
    private string _potaRef = "";

    [ObservableProperty]
    private string _sotaRef = "";

    [ObservableProperty]
    private string _wwffRef = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = "";

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridValidationIsValid))]
    [NotifyPropertyChangedFor(nameof(GridValidationIsInvalid))]
    private bool? _gridIsValid;

    public bool GridValidationIsValid => GridIsValid == true;

    public bool GridValidationIsInvalid => GridIsValid == false;

    public ObservableCollection<ActivationDirectoryEntry> PotaSuggestions => _potaSuggestions.Items;

    public ObservableCollection<ActivationDirectoryEntry> SotaSuggestions => _sotaSuggestions.Items;

    public ObservableCollection<ActivationDirectoryEntry> WwffSuggestions => _wwffSuggestions.Items;

    public string PotaListStatus => DirectoryStatusText(ActivationProgramme.Pota);

    public string SotaListStatus => DirectoryStatusText(ActivationProgramme.Sota);

    public string WwffListStatus => DirectoryStatusText(ActivationProgramme.Wwff);

    public void ResetForCreate(QsoLogbook? template = null)
    {
        _editingLogbookId = null;
        NotifyDialogModeChanged();
        _suppressFieldCoercion = true;
        Name = "";
        var gs = _settings.Current.GroundStation;
        MyCallsign = template?.MyCallsign ?? "";
        MyGridSquare = !string.IsNullOrWhiteSpace(template?.MyGridSquare)
            ? template.MyGridSquare
            : gs.GridSquare;
        PotaRef = template?.PotaRef ?? "";
        SotaRef = template?.SotaRef ?? "";
        WwffRef = template?.WwffRef ?? "";
        ErrorText = "";
        GridIsValid = MaidenheadLocator.GetLiveValidationState(MyGridSquare);
        _suppressFieldCoercion = false;
        RefreshAllSuggestions();
    }

    public void LoadForEdit(QsoLogbook logbook)
    {
        _editingLogbookId = logbook.Id;
        NotifyDialogModeChanged();
        _suppressFieldCoercion = true;
        Name = logbook.Name;
        MyCallsign = logbook.MyCallsign;
        MyGridSquare = logbook.MyGridSquare;
        PotaRef = logbook.PotaRef;
        SotaRef = logbook.SotaRef;
        WwffRef = logbook.WwffRef;
        ErrorText = "";
        GridIsValid = MaidenheadLocator.GetLiveValidationState(MyGridSquare);
        _suppressFieldCoercion = false;
        RefreshAllSuggestions();
    }

    /// <summary>
    /// Downloads or reads the three directories so the reference boxes can suggest entries.
    /// Safe to call every time the dialogue opens; a failure leaves the boxes free-typed.
    /// </summary>
    public async Task LoadDirectoriesAsync()
    {
        foreach (var programme in Enum.GetValues<ActivationProgramme>())
        {
            _loadingDirectories.Add(programme);
            OnPropertyChanged(DirectoryStatusProperty(programme));
            try
            {
                await Task.Run(() => _directories.EnsureLoadedAsync(programme)).ConfigureAwait(true);
            }
            finally
            {
                _loadingDirectories.Remove(programme);
            }

            OnPropertyChanged(DirectoryStatusProperty(programme));
            RefreshAllSuggestions();
        }
    }

    partial void OnPotaRefChanged(string value)
    {
        CoerceReference(value, upper => PotaRef = upper);
        _potaSuggestions.Update(PotaRef);
    }

    partial void OnSotaRefChanged(string value)
    {
        CoerceReference(value, upper => SotaRef = upper);
        _sotaSuggestions.Update(SotaRef);
    }

    partial void OnWwffRefChanged(string value)
    {
        CoerceReference(value, upper => WwffRef = upper);
        _wwffSuggestions.Update(WwffRef);
    }

    partial void OnMyGridSquareChanged(string value)
    {
        CoerceGrid(value);
        GridIsValid = MaidenheadLocator.GetLiveValidationState(MyGridSquare);
    }

    partial void OnMyCallsignChanged(string value) => CoerceCallsign(value);

    public bool TryConfirm([NotNullWhen(true)] out LogbookDetailsDialogResult? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorText = _l.Get("Logbook.Error.NameRequired");
            return false;
        }

        if (!MaidenheadLocator.TryValidateGrids(MyGridSquare, out var grid, out var error, out var invalidSegment))
        {
            ErrorText = error switch
            {
                GridValidationError.TooManyGrids =>
                    _l.Get("Logbook.Error.GridTooMany", MaidenheadLocator.MaxGridCount),
                GridValidationError.InvalidSegment =>
                    _l.Get("Logbook.Error.GridInvalidSegment", invalidSegment ?? ""),
                _ => ""
            };
            return false;
        }

        if (!ActivationReferences.IsValid(ActivationProgramme.Pota, PotaRef))
        {
            ErrorText = _l.Get("Logbook.Error.PotaRef");
            return false;
        }

        if (!ActivationReferences.IsValid(ActivationProgramme.Sota, SotaRef))
        {
            ErrorText = _l.Get("Logbook.Error.SotaRef");
            return false;
        }

        if (!ActivationReferences.IsValid(ActivationProgramme.Wwff, WwffRef))
        {
            ErrorText = _l.Get("Logbook.Error.WwffRef");
            return false;
        }

        result = new LogbookDetailsDialogResult
        {
            UpdateLogbookId = _editingLogbookId,
            Name = Name.Trim(),
            MyCallsign = MyCallsign,
            MyGridSquare = grid,
            PotaRef = ActivationReferences.Normalize(PotaRef),
            SotaRef = ActivationReferences.Normalize(SotaRef),
            WwffRef = ActivationReferences.Normalize(WwffRef)
        };
        return true;
    }

    private void RefreshAllSuggestions()
    {
        _potaSuggestions.Update(PotaRef);
        _sotaSuggestions.Update(SotaRef);
        _wwffSuggestions.Update(WwffRef);
    }

    private string DirectoryStatusText(ActivationProgramme programme)
    {
        var name = ProgrammeName(programme);
        var count = _directories.Count(programme);
        if (count == 0)
        {
            return _loadingDirectories.Contains(programme)
                ? _l.Get("Logbook.Refs.Loading", name)
                : _l.Get("Logbook.Refs.Unavailable", name);
        }

        var updated = _directories.LastUpdatedUtc(programme)?.ToLocalTime().ToString("d") ?? "";
        return _directories.LastError(programme).Length > 0
            ? _l.Get("Logbook.Refs.SavedCopy", name, count, updated)
            : _l.Get("Logbook.Refs.Ready", name, count, updated);
    }

    private string ProgrammeName(ActivationProgramme programme) =>
        programme switch
        {
            ActivationProgramme.Pota => _l.Get("Logbook.Refs.Pota.Short"),
            ActivationProgramme.Sota => _l.Get("Logbook.Refs.Sota.Short"),
            _ => _l.Get("Logbook.Refs.Wwff.Short")
        };

    private static string DirectoryStatusProperty(ActivationProgramme programme) =>
        programme switch
        {
            ActivationProgramme.Pota => nameof(PotaListStatus),
            ActivationProgramme.Sota => nameof(SotaListStatus),
            _ => nameof(WwffListStatus)
        };

    private void NotifyDialogModeChanged()
    {
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(IntroText));
        OnPropertyChanged(nameof(ConfirmButtonText));
    }

    private void CoerceCallsign(string value)
    {
        if (_suppressFieldCoercion)
            return;

        var normalized = MaidenheadLocator.NormalizeCallsign(value);
        if (string.Equals(normalized, value, StringComparison.Ordinal))
            return;

        _suppressFieldCoercion = true;
        MyCallsign = normalized;
        _suppressFieldCoercion = false;
    }

    private void CoerceReference(string value, Action<string> assign)
    {
        if (_suppressFieldCoercion)
            return;

        var upper = value.ToUpperInvariant();
        if (string.Equals(upper, value, StringComparison.Ordinal))
            return;

        _suppressFieldCoercion = true;
        assign(upper);
        _suppressFieldCoercion = false;
    }

    private void CoerceGrid(string value)
    {
        if (_suppressFieldCoercion)
            return;

        var normalized = MaidenheadLocator.UppercaseGridEntry(value);
        if (string.Equals(normalized, value, StringComparison.Ordinal))
            return;

        _suppressFieldCoercion = true;
        MyGridSquare = normalized;
        _suppressFieldCoercion = false;
    }
}
