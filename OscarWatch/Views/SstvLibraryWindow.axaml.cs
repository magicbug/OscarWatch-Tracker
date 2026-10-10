using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OscarWatch.Localization;
using OscarWatch.ViewModels;

namespace OscarWatch.Views;

public partial class SstvLibraryWindow : Window
{
    public SstvLibraryWindow()
    {
        InitializeComponent();
        Opened += (_, _) => (DataContext as SstvLibraryViewModel)?.OnWindowOpened();
        Closed += (_, _) => (DataContext as SstvLibraryViewModel)?.Dispose();
    }

    private void OnThumbnailDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not SstvLibraryViewModel vm || (sender as Control)?.DataContext is not SstvLibraryItem item)
            return;

        vm.SelectedItem = item;
        vm.OpenSelectedCommand.Execute(null);
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SstvLibraryViewModel { SelectedItem: { } item } vm)
            return;

        var l = LocalizationService.Instance;
        var confirmed = await SimpleConfirmDialog.ShowAsync(
            this,
            l.Get("Sstv.Library.DeleteConfirm.Title"),
            l.Get("Sstv.Library.DeleteConfirm", Path.GetFileName(item.Picture.Path))).ConfigureAwait(true);
        if (confirmed)
            vm.DeleteSelected();
    }
}
