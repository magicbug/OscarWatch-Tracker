using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.ViewModels;

namespace OscarWatch.Views;

public partial class UpcomingPassesWindow : Window
{
    private bool _dockingFromClose;

    public UpcomingPassesWindow()
    {
        InitializeComponent();
        var settings = App.Services.GetRequiredService<ISettingsService>();
        DetachedWindowBounds.Apply(
            this,
            settings.Current.PassesDetachedWindowWidth,
            settings.Current.PassesDetachedWindowHeight,
            settings.Current.PassesDetachedWindowX,
            settings.Current.PassesDetachedWindowY,
            PassesDetachedWindowDefaults.MinWidth,
            PassesDetachedWindowDefaults.MinHeight);
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        var settings = App.Services.GetRequiredService<ISettingsService>();
        if (PassesDetachedWindowDefaults.TryGetSavedPosition(settings.Current, out _, out _))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = DetachedWindowBounds.ClampToVisibleArea(
                this,
                Position,
                PassesDetachedWindowDefaults.MinWidth,
                PassesDetachedWindowDefaults.MinHeight);
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        PersistBounds();
        if (_dockingFromClose || DataContext is not MainViewModel vm)
            return;

        _dockingFromClose = true;
        vm.DockUpcomingPassesFromWindowClose();
    }

    private void OnClosed(object? sender, EventArgs e) => PersistBounds();

    private void PersistBounds()
    {
        var settings = App.Services.GetRequiredService<ISettingsService>();
        DetachedWindowBounds.Capture(
            this,
            out var width,
            out var height,
            out var x,
            out var y,
            PassesDetachedWindowDefaults.MinWidth,
            PassesDetachedWindowDefaults.MinHeight);
        settings.Current.PassesDetachedWindowWidth = width;
        settings.Current.PassesDetachedWindowHeight = height;
        settings.Current.PassesDetachedWindowX = x;
        settings.Current.PassesDetachedWindowY = y;
        settings.RequestSave();
    }
}
