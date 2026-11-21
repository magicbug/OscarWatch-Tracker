using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OscarWatch.Core.CwDecode;
using OscarWatch.Core.Models;
using OscarWatch.Cw;
using OscarWatch.Localization;
using OscarWatch.ViewModels;

namespace OscarWatch.Views;

public partial class CwWindow : Window
{
    /// <summary>How close to the bottom (in pixels) still counts as following the copy.</summary>
    private const double FollowSlack = 40;

    public CwWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
        Waterfall.TuneRequested += (_, hz) => (DataContext as CwViewModel)?.TuneTo(hz);
        Waterfall.TuneResetRequested += (_, _) => (DataContext as CwViewModel)?.ResetTuning();
        CopyScroller.AddHandler(PointerWheelChangedEvent, OnCopyWheel, RoutingStrategies.Tunnel);
        // Tunnel so a focused control cannot take the macro keys first (same as the logbook).
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Ctrl and the mouse wheel over the copy changes its text size.</summary>
    private void OnCopyWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || DataContext is not CwViewModel vm || e.Delta.Y == 0)
            return;
        vm.CopyFontSize += Math.Sign(e.Delta.Y);
        e.Handled = true;
    }

    /// <summary>
    /// F1 to F12 and Escape drive the CW keyer. Ctrl with plus, minus or 0 changes the copy size.
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CwViewModel vm)
            return;

        if (e.KeyModifiers == KeyModifiers.None)
        {
            if (e.Key is >= Key.F1 and <= Key.F12)
            {
                if (vm.CwKeyer.TryHandleCwFunctionKey(e.Key - Key.F1))
                    e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && vm.CwKeyer.TryHandleCwEscape())
            {
                e.Handled = true;
                return;
            }
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;

        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                vm.CopyFontSize += 1;
                break;
            case Key.OemMinus or Key.Subtract:
                vm.CopyFontSize -= 1;
                break;
            case Key.D0 or Key.NumPad0:
                vm.CopyFontSize = CwDecodeSettings.DefaultCopyFontSize;
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not CwViewModel vm)
            return;

        if (vm.SavedWindowSize is { } size)
        {
            Width = Math.Max(MinWidth, size.Width);
            Height = Math.Max(MinHeight, size.Height);
        }

        vm.CopyAppended += OnCopyAppended;
        vm.SaveCopyRequested = SaveCopyAsync;
        vm.CwKeyer.EditMessagesRequested += OnEditMessagesRequested;
        vm.OnWindowOpened();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is not CwViewModel vm)
            return;

        vm.CopyAppended -= OnCopyAppended;
        vm.CwKeyer.EditMessagesRequested -= OnEditMessagesRequested;
        vm.SaveCopyRequested = null;
        vm.SavedWindowSize = (Width, Height);
        vm.OnWindowClosed();
    }

    /// <summary>Keep the newest copy in view, unless the operator has scrolled back to read.</summary>
    private void OnCopyAppended(object? sender, EventArgs e)
    {
        var scroller = CopyScroller;
        var following = scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - FollowSlack;
        if (following)
            Dispatcher.UIThread.Post(scroller.ScrollToEnd, DispatcherPriority.Background);
    }

    private void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CwViewModel vm && sender is Button { DataContext: CwCopyLink link })
            vm.SendToLogbook(link);
    }

    private async void OnEditMessagesRequested(object? sender, EventArgs e)
    {
        if (DataContext is not CwViewModel vm)
            return;

        var logbook = App.Services.GetRequiredService<OscarWatch.Services.QsoLogbookEntryBridge>().Attached;
        if (logbook?.SelectedLogbook is null)
        {
            vm.CwKeyer.StatusText = LocalizationService.Instance.Get("Logbook.Cw.Status.OpenLogbookToEdit");
            return;
        }

        var settingsVm = App.Services.GetRequiredService<LogbookSettingsViewModel>();
        settingsVm.LoadForLogbook(logbook.SelectedLogbook);
        var dialog = new LogbookSettingsWindow { DataContext = settingsVm };
        dialog.Opened += (_, _) => dialog.SelectCwKeyerTab();
        var result = await dialog.ShowDialog<QsoLogbookCloudlogSettingsRequest?>(this).ConfigureAwait(true);

        vm.CwKeyer.Reload();
        logbook.ReloadCwKeyer();
        if (result is not null)
            await logbook.ApplyCloudlogSettingsAsync(result).ConfigureAwait(true);
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CwViewModel vm)
            return;

        var dialog = new CwSettingsWindow { DataContext = vm };
        await dialog.ShowDialog(this).ConfigureAwait(true);
    }

    private async Task SaveCopyAsync()
    {
        if (DataContext is not CwViewModel vm)
            return;

        var l = LocalizationService.Instance;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = l.Get("Cw.SaveCopy.Picker"),
            SuggestedFileName = vm.SuggestedCopyFileName,
            DefaultExtension = "txt",
            FileTypeChoices =
            [
                new FilePickerFileType(l.Get("Cw.SaveCopy.TextFiles")) { Patterns = ["*.txt"] },
            ],
        }).ConfigureAwait(true);

        if (file is null)
            return;

        await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
        await vm.SaveCopyTo(stream, file.TryGetLocalPath() ?? file.Name).ConfigureAwait(true);
    }
}
