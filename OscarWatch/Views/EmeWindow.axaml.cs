using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using OscarWatch.Core.Services;
using OscarWatch.ViewModels;

namespace OscarWatch.Views;

/// <summary>
/// The Moon (EME) window. The view model's timer runs only while the window is open and not minimised, so a
/// closed or hidden window costs nothing.
/// </summary>
public partial class EmeWindow : Window
{
    public EmeWindow()
    {
        InitializeComponent();
        var settings = App.Services.GetService<ISettingsService>();
        if (settings is not null)
            EmeWindowBounds.Apply(this, settings.Current.Eme);

        Opened += (_, _) => Viewmodel?.Start();
        Closed += (_, _) =>
        {
            if (settings is not null)
            {
                EmeWindowBounds.Capture(this, settings.Current.Eme);
                settings.RequestSave();
            }
            if (Viewmodel is { } vm)
            {
                vm.Stop();
                vm.Dispose();
            }
        };
    }

    private EmeViewModel? Viewmodel => DataContext as EmeViewModel;

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        var text = Viewmodel?.PlainText;
        if (string.IsNullOrEmpty(text))
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
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
