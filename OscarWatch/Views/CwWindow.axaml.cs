using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OscarWatch.Core.CwDecode;
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
        WaterfallSplitter.DragCompleted += (_, _) => PersistWaterfallHeight();
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
    /// F1 to F12 and Escape drive the CW keyer. Ctrl+K opens the keyboard.
    /// Ctrl with plus, minus or 0 changes the copy size.
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CwViewModel vm)
            return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.K)
        {
            if (vm.CwKeyer.TryHandleCtrlK(this))
                e.Handled = true;
            return;
        }

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

        CopyLayout.RowDefinitions[0].Height = new GridLength(vm.SavedWaterfallHeight);
        vm.CopyAppended += OnCopyAppended;
        vm.SaveCopyRequested = SaveCopyAsync;
        vm.OnWindowOpened();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is not CwViewModel vm)
            return;

        vm.CopyAppended -= OnCopyAppended;
        vm.SaveCopyRequested = null;
        PersistWaterfallHeight();
        vm.SavedWindowSize = (Width, Height);
        vm.OnWindowClosed();
    }

    private void PersistWaterfallHeight()
    {
        if (DataContext is not CwViewModel vm)
            return;

        var row = CopyLayout.RowDefinitions[0];
        vm.SavedWaterfallHeight = row.ActualHeight > 0 ? row.ActualHeight : row.Height.Value;
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
