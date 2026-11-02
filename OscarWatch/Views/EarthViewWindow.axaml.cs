using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OscarWatch.ViewModels;

namespace OscarWatch.Views;

/// <summary>
/// The Earth View window. The view model's timer runs only while the window is open and not minimised, so a
/// closed or hidden window costs nothing.
/// </summary>
public partial class EarthViewWindow : Window
{
    public EarthViewWindow()
    {
        InitializeComponent();
        Globe.ViewChanged += (_, _) => UpdateZoomButtons();
        Opened += (_, _) => Viewmodel?.Start();
        Closed += (_, _) =>
        {
            if (Viewmodel is { } vm)
            {
                vm.Stop();
                vm.Dispose();
            }
        };
    }

    private EarthViewViewModel? Viewmodel => DataContext as EarthViewViewModel;

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => Globe.ZoomIn();

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => Globe.ZoomOut();

    private void OnResetViewClick(object? sender, RoutedEventArgs e) => Globe.ResetView();

    private void UpdateZoomButtons()
    {
        ZoomInButton.IsEnabled = Globe.CanZoomIn;
        ZoomOutButton.IsEnabled = Globe.CanZoomOut;
        ResetViewButton.IsEnabled = Globe.IsViewChanged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty || !IsVisible)
            return;

        if (WindowState == WindowState.Minimized)
            Viewmodel?.Stop();
        else
            Viewmodel?.Start();
    }
}
