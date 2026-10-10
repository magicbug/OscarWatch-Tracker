using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OscarWatch.Localization;
using OscarWatch.ViewModels;

namespace OscarWatch.Views;

public partial class SstvWindow : Window
{
    public SstvWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not SstvViewModel vm)
            return;

        if (vm.SavedWindowSize is { } size)
        {
            Width = Math.Max(MinWidth, size.Width);
            Height = Math.Max(MinHeight, size.Height);
        }

        vm.OnWindowOpened();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is not SstvViewModel vm)
            return;

        vm.SavedWindowSize = (Width, Height);
        vm.OnWindowClosed();
    }

    private void OnShowLiveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SstvViewModel vm)
            vm.SelectedGalleryItem = null;
    }

    private SstvLibraryWindow? _library;

    private void OnOpenLibraryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SstvViewModel vm)
            return;

        if (_library is { IsVisible: true })
        {
            _library.Activate();
            return;
        }

        _library = new SstvLibraryWindow
        {
            DataContext = new SstvLibraryViewModel(LocalizationService.Instance, vm.PictureFolder),
        };
        _library.Closed += (_, _) => _library = null;
        _library.Show(this);
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SstvViewModel vm)
            return;

        var dialog = new SstvSettingsWindow { DataContext = vm };
        await dialog.ShowDialog(this).ConfigureAwait(true);
    }

    private async void OnDecodeRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SstvViewModel vm)
            return;

        var l = LocalizationService.Instance;
        IStorageFolder? startFolder = null;
        try
        {
            Directory.CreateDirectory(vm.PictureFolder);
            startFolder = await StorageProvider.TryGetFolderFromPathAsync(vm.PictureFolder).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // The picker opens in its default folder instead.
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = l.Get("Sstv.Picker.Title"),
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter =
            [
                new FilePickerFileType(l.Get("Sstv.Picker.Audio")) { Patterns = ["*.wav", "*.mp3", "*.flac", "*.ogg", "*.m4a"] },
            ],
        }).ConfigureAwait(true);

        if (files.Count == 0)
            return;

        await vm.DecodeRecordingAsync(files[0].Path.LocalPath).ConfigureAwait(true);
    }
}
