using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Sstv;
using OscarWatch.Localization;
using Serilog;

namespace OscarWatch.ViewModels;

public enum SstvLibraryPeriod
{
    All,
    Today,
    Week,
    Month,
    Custom,
}

/// <summary>OscarWatch SSTV picture library: every saved picture, grouped by day and filtered.</summary>
public partial class SstvLibraryViewModel : ViewModelBase, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SstvLibraryViewModel>();
    private const int ThumbnailWidth = 220;

    private readonly ILocalizationService _l;
    private readonly Dictionary<string, SstvLibraryItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _reloadTimer;
    private FileSystemWatcher? _watcher;
    private bool _loadingThumbnails;
    private bool _updatingOptions;
    private bool _disposed;

    public SstvLibraryViewModel(ILocalizationService localization, string folder)
    {
        _l = localization;
        Folder = folder;

        PeriodOptions =
        [
            new(SstvLibraryPeriod.All, _l.Get("Sstv.Library.Period.All")),
            new(SstvLibraryPeriod.Today, _l.Get("Sstv.Library.Period.Today")),
            new(SstvLibraryPeriod.Week, _l.Get("Sstv.Library.Period.Week")),
            new(SstvLibraryPeriod.Month, _l.Get("Sstv.Library.Period.Month")),
            new(SstvLibraryPeriod.Custom, _l.Get("Sstv.Library.Period.Custom")),
        ];
        _selectedPeriod = PeriodOptions[0];
        _toDate = DateTime.UtcNow.Date;
        _fromDate = _toDate.Value.AddDays(-7);

        _reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer.Stop();
            Reload();
        };
    }

    public string Folder { get; }

    public IReadOnlyList<SstvLibraryPeriodOption> PeriodOptions { get; }

    public ObservableCollection<string> SatelliteOptions { get; } = [];

    public ObservableCollection<string> ModeOptions { get; } = [];

    public ObservableCollection<SstvLibraryDay> Days { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomPeriod))]
    private SstvLibraryPeriodOption _selectedPeriod;

    [ObservableProperty]
    private DateTime? _fromDate;

    [ObservableProperty]
    private DateTime? _toDate;

    [ObservableProperty]
    private string? _selectedSatellite;

    [ObservableProperty]
    private string? _selectedMode;

    [ObservableProperty]
    private string _summaryText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ReceivedText), nameof(ModeText), nameof(SatelliteText), nameof(FileText))]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedCommand), nameof(ShowSelectedInFolderCommand))]
    private SstvLibraryItem? _selectedItem;

    [ObservableProperty]
    private Bitmap? _previewBitmap;

    public bool IsCustomPeriod => SelectedPeriod.Period == SstvLibraryPeriod.Custom;

    public bool HasSelection => SelectedItem is not null;

    public bool IsEmpty => Days.Count == 0;

    public string ReceivedText => SelectedItem is { } i
        ? i.Picture.ReceivedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC"
        : "";

    public string ModeText => SelectedItem?.Picture.Mode?.Name ?? "";

    public string SatelliteText => SelectedItem?.Picture.Satellite ?? "";

    public string FileText => SelectedItem is { } i
        ? $"{Path.GetFileName(i.Picture.Path)} ({i.Picture.Bytes / 1024.0:0} KB)"
        : "";

    public void OnWindowOpened()
    {
        Reload();
        try
        {
            Directory.CreateDirectory(Folder);
            _watcher = new FileSystemWatcher(Folder, "*.png")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Created += OnFolderChanged;
            _watcher.Deleted += OnFolderChanged;
            _watcher.Renamed += OnFolderChanged;
            _watcher.Changed += OnFolderChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SSTV library could not watch {Folder}", Folder);
        }
    }

    private void OnFolderChanged(object sender, FileSystemEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
                return;
            // Pictures arrive in a burst while being written; wait for it to settle.
            _reloadTimer.Stop();
            _reloadTimer.Start();
        });

    [RelayCommand]
    private void Reload()
    {
        IReadOnlyList<SstvStoredPicture> pictures;
        try
        {
            pictures = SstvPictureCatalog.Scan(Folder);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SSTV library scan failed");
            pictures = [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var picture in pictures)
        {
            seen.Add(picture.Path);
            if (!_items.TryGetValue(picture.Path, out var item) || item.Picture.Bytes != picture.Bytes)
                _items[picture.Path] = new SstvLibraryItem(picture, Caption(picture));
        }

        foreach (var gone in _items.Keys.Where(k => !seen.Contains(k)).ToList())
            _items.Remove(gone);

        UpdateFilterOptions();
        ApplyFilter();
        _ = LoadThumbnailsAsync();
    }

    [RelayCommand]
    private void Select(SstvLibraryItem? item) => SelectedItem = item;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenSelected()
    {
        if (SelectedItem is not { } item)
            return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = item.Picture.Path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Path}", item.Picture.Path);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ShowSelectedInFolder()
    {
        if (SelectedItem is not { } item)
            return;
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start("explorer.exe", $"/select,\"{item.Picture.Path}\"");
            else
                Process.Start(new ProcessStartInfo { FileName = Folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not show {Path}", item.Picture.Path);
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Process.Start(new ProcessStartInfo { FileName = Folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open the SSTV folder");
        }
    }

    /// <summary>Delete the selected picture (the window asks first). Returns false if it failed.</summary>
    public bool DeleteSelected()
    {
        if (SelectedItem is not { } item)
            return true;

        try
        {
            File.Delete(item.Picture.Path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not delete {Path}", item.Picture.Path);
            return false;
        }

        _items.Remove(item.Picture.Path);
        SelectedItem = null;
        ApplyFilter();
        return true;
    }

    partial void OnSelectedPeriodChanged(SstvLibraryPeriodOption value) => ApplyFilter();

    partial void OnFromDateChanged(DateTime? value) => ApplyFilter();

    partial void OnToDateChanged(DateTime? value) => ApplyFilter();

    partial void OnSelectedSatelliteChanged(string? value)
    {
        if (!_updatingOptions)
            ApplyFilter();
    }

    partial void OnSelectedModeChanged(string? value)
    {
        if (!_updatingOptions)
            ApplyFilter();
    }

    partial void OnSelectedItemChanged(SstvLibraryItem? oldValue, SstvLibraryItem? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;
        if (newValue is not null)
            newValue.IsSelected = true;

        PreviewBitmap = newValue is null ? null : LoadBitmap(newValue.Picture.Path, width: null);
    }

    private void UpdateFilterOptions()
    {
        _updatingOptions = true;
        try
        {
            var anySatellite = _l.Get("Sstv.Library.AnySatellite");
            var anyMode = _l.Get("Sstv.Library.AnyMode");
            var satellite = SelectedSatellite;
            var mode = SelectedMode;

            Replace(SatelliteOptions, [anySatellite, .. _items.Values
                .Select(i => i.Picture.Satellite)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)]);
            Replace(ModeOptions, [anyMode, .. _items.Values
                .Select(i => i.Picture.Mode?.Name)
                .OfType<string>()
                .Distinct()
                .Order(StringComparer.OrdinalIgnoreCase)]);

            SelectedSatellite = satellite is not null && SatelliteOptions.Contains(satellite) ? satellite : anySatellite;
            SelectedMode = mode is not null && ModeOptions.Contains(mode) ? mode : anyMode;
        }
        finally
        {
            _updatingOptions = false;
        }
    }

    private static void Replace(ObservableCollection<string> target, List<string> values)
    {
        if (target.SequenceEqual(values))
            return;
        target.Clear();
        foreach (var v in values)
            target.Add(v);
    }

    private void ApplyFilter()
    {
        var now = DateTime.UtcNow;
        var period = SelectedPeriod.Period;
        var from = FromDate?.Date;
        var to = ToDate?.Date;
        var satellite = SelectedSatellite is { } s && s != _l.Get("Sstv.Library.AnySatellite") ? s : null;
        var mode = SelectedMode is { } m && m != _l.Get("Sstv.Library.AnyMode") ? m : null;

        bool Matches(SstvStoredPicture p)
        {
            var ok = period switch
            {
                SstvLibraryPeriod.Today => p.ReceivedUtc.Date == now.Date,
                SstvLibraryPeriod.Week => p.ReceivedUtc >= now.AddDays(-7),
                SstvLibraryPeriod.Month => p.ReceivedUtc >= now.AddDays(-30),
                SstvLibraryPeriod.Custom => (from is null || p.ReceivedUtc.Date >= from) && (to is null || p.ReceivedUtc.Date <= to),
                _ => true,
            };
            return ok
                && (satellite is null || string.Equals(p.Satellite, satellite, StringComparison.OrdinalIgnoreCase))
                && (mode is null || p.Mode?.Name == mode);
        }

        var shown = _items.Values
            .Where(i => Matches(i.Picture))
            .OrderByDescending(i => i.Picture.ReceivedUtc)
            .ToList();

        Days.Clear();
        foreach (var day in shown.GroupBy(i => i.Picture.ReceivedUtc.Date))
        {
            var count = day.Count();
            var header = day.Key.ToString("dddd d MMMM yyyy", CultureInfo.CurrentUICulture);
            var countText = count == 1 ? _l.Get("Sstv.Library.CountOne") : _l.Get("Sstv.Library.Count", count);
            Days.Add(new SstvLibraryDay(header, countText, day.ToList()));
        }

        if (SelectedItem is { } selected && !shown.Contains(selected))
            SelectedItem = null;

        SummaryText = _l.Get("Sstv.Library.Summary", shown.Count, _items.Count);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task LoadThumbnailsAsync()
    {
        if (_loadingThumbnails)
            return;
        _loadingThumbnails = true;
        try
        {
            while (!_disposed)
            {
                // Newest first, so the top of the list fills in before older days.
                var next = _items.Values
                    .Where(i => i.Thumbnail is null && !i.ThumbnailFailed)
                    .OrderByDescending(i => i.Picture.ReceivedUtc)
                    .FirstOrDefault();
                if (next is null)
                    break;

                var bitmap = await Task.Run(() => LoadBitmap(next.Picture.Path, ThumbnailWidth)).ConfigureAwait(true);
                if (bitmap is null)
                    next.ThumbnailFailed = true;
                else
                    next.Thumbnail = bitmap;
            }
        }
        finally
        {
            _loadingThumbnails = false;
        }
    }

    private static Bitmap? LoadBitmap(string path, int? width)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return width is { } w
                ? Bitmap.DecodeToWidth(stream, w, BitmapInterpolationMode.MediumQuality)
                : new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not load SSTV picture {Path}", path);
            return null;
        }
    }

    private string Caption(SstvStoredPicture picture)
    {
        var time = picture.ReceivedUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
        var mode = picture.Mode?.Name;
        return mode is null ? time : $"{time}, {mode}";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _reloadTimer.Stop();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}

public sealed record SstvLibraryPeriodOption(SstvLibraryPeriod Period, string Label)
{
    public override string ToString() => Label;
}

public sealed record SstvLibraryDay(string Header, string CountText, IReadOnlyList<SstvLibraryItem> Items);

public sealed partial class SstvLibraryItem : ObservableObject
{
    public SstvLibraryItem(SstvStoredPicture picture, string caption)
    {
        Picture = picture;
        Caption = caption;
    }

    public SstvStoredPicture Picture { get; }

    public string Caption { get; }

    /// <summary>Satellite or recording name, for the line under the caption.</summary>
    public string Satellite => Picture.Satellite ?? "";

    public bool HasSatellite => Picture.Satellite is not null;

    public bool ThumbnailFailed { get; set; }

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isSelected;
}
