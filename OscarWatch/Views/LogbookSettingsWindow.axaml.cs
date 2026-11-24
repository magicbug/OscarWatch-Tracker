using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OscarWatch.Core.Models;
using OscarWatch.ViewModels;

namespace OscarWatch.Views;

public partial class LogbookSettingsWindow : Window
{
    private bool _selectCwKeyerTab;

    public LogbookSettingsWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    /// <summary>Select the CW keyer tab when opening from a macro right-click.</summary>
    public void SelectCwKeyerTab()
    {
        _selectCwKeyerTab = true;
        ApplyCwKeyerTab();
    }

    private void ApplyCwKeyerTab()
    {
        if (!_selectCwKeyerTab || CwKeyerTab is null || SettingsTabs is null)
            return;

        SettingsTabs.SelectedItem = CwKeyerTab;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        ApplyCwKeyerTab();
        // After the first layout pass, in case a bound default resettled the tab.
        Dispatcher.UIThread.Post(ApplyCwKeyerTab, DispatcherPriority.Loaded);
        if (DataContext is LogbookSettingsViewModel vm)
            await vm.LoadStationProfilesAsync().ConfigureAwait(true);
        ApplyCwKeyerTab();
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
