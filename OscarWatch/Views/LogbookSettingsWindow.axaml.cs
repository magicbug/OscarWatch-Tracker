using Avalonia.Controls;
using Avalonia.Interactivity;
using OscarWatch.Core.Models;
using OscarWatch.ViewModels;

namespace OscarWatch.Views;

public partial class LogbookSettingsWindow : Window
{
    /// <summary>Zero-based index of the CW keyer tab (Cloudlog, QRZ, HamQTH, WSJT-X, then CW).</summary>
    public const int CwKeyerTabIndex = 4;

    public LogbookSettingsWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    /// <summary>Select the CW keyer tab when opening from the decoder Edit messages button.</summary>
    public void SelectCwKeyerTab() => SettingsTabs.SelectedIndex = CwKeyerTabIndex;

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        if (DataContext is LogbookSettingsViewModel vm)
            await vm.LoadStationProfilesAsync().ConfigureAwait(true);
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogbookSettingsViewModel vm)
        {
            Close(null);
            return;
        }

        if (!vm.TrySave(out var request))
            return;

        Close(request);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
