using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.ViewModels;

namespace OscarWatch.Views;

public partial class UpcomingRovesWindow : Window
{
    private bool _dockingFromClose;

    public UpcomingRovesWindow()
    {
        InitializeComponent();
        var settings = App.Services.GetRequiredService<ISettingsService>();
        DetachedWindowBounds.Apply(
            this,
            settings.Current.RovesDetachedWindowWidth,
            settings.Current.RovesDetachedWindowHeight,
            settings.Current.RovesDetachedWindowX,
            settings.Current.RovesDetachedWindowY,
            RovesDetachedWindowDefaults.MinWidth,
            RovesDetachedWindowDefaults.MinHeight);
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        var settings = App.Services.GetRequiredService<ISettingsService>();
        if (RovesDetachedWindowDefaults.TryGetSavedPosition(settings.Current, out _, out _))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = DetachedWindowBounds.ClampToVisibleArea(
                this,
                Position,
                RovesDetachedWindowDefaults.MinWidth,
                RovesDetachedWindowDefaults.MinHeight);
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        PersistBounds();
        if (_dockingFromClose || DataContext is not MainViewModel vm)
            return;

        _dockingFromClose = true;
        vm.DockUpcomingRovesFromWindowClose();
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
            RovesDetachedWindowDefaults.MinWidth,
            RovesDetachedWindowDefaults.MinHeight);
        settings.Current.RovesDetachedWindowWidth = width;
        settings.Current.RovesDetachedWindowHeight = height;
        settings.Current.RovesDetachedWindowX = x;
        settings.Current.RovesDetachedWindowY = y;
        settings.RequestSave();
    }
}
