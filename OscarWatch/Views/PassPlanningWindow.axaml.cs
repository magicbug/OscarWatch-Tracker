using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OscarWatch.Localization;
using OscarWatch.ViewModels;

namespace OscarWatch.Views;

public partial class PassPlanningWindow : Window
{
    public PassPlanningWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        if (DataContext is PassPlanningViewModel vm)
            await vm.RefreshPassesCommand.ExecuteAsync(null);
    }

    private void OnPassesContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (PassesDataGrid.SelectedItem is not PassPlanningPassRow row)
        {
            e.Cancel = true;
            return;
        }

        if (SchedulePassMenuItem is not null)
        {
            var l = LocalizationService.Instance;
            SchedulePassMenuItem.Header = row.IsScheduled
                ? l.Get("Pass.Schedule.Remove")
                : l.Get("Pass.Schedule.Add");
        }
    }

    private async void OnEditHorizonMaskClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PassPlanningViewModel vm)
            return;

        var window = new HorizonMaskEditorWindow
        {
            DataContext = vm
        };
        await window.ShowDialog(this);
        await vm.RefreshPassesCommand.ExecuteAsync(null);
    }

    private async void OnExportSatelliteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PassPlanningPassRow row }
            || DataContext is not PassPlanningViewModel vm)
            return;

        await vm.ExportSatelliteIcsAsync(this, row);
    }

    private void OnToggleScheduleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PassPlanningPassRow row }
            || DataContext is not PassPlanningViewModel vm)
            return;

        vm.TogglePassScheduledCommand.Execute(row);
    }

    private async void OnOpenPassRadarGalleryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PassPlanningViewModel vm)
            return;

        var galleryVm = await vm.CreatePassRadarGalleryViewModelAsync();
        if (galleryVm is null)
            return;

        new PassRadarGalleryWindow
        {
            DataContext = galleryVm
        }.Show(this);
    }

    private async void OnUseActiveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PassPlanningViewModel vm)
            return;

        await vm.ApplyAsActiveStationAsync();
        Close(true);
    }

    private async void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PassPlanningViewModel vm)
            await vm.SaveFiltersAndStationsAsync();

        Close(false);
    }
}
