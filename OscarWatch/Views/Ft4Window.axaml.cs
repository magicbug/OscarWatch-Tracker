using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OscarWatch;
using OscarWatch.Core.Ft4;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using OscarWatch.ViewModels;

namespace OscarWatch.Views;

public partial class Ft4Window : Window
{
    private const double BasicMinWidth = 720;
    private const double AdvancedMinWidth = 960;
    private const double AdvancedWidth = 1180;
    private const double MinDecodeColumnWidth = 300;

    private bool _closeConfirmed;
    private Ft4DecodeRowViewModel? _contextRow;

    public Ft4Window()
    {
        InitializeComponent();
        ApplySavedBounds();
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
        DecodeColumnsSplitter.DragCompleted += (_, _) => SaveDecodeColumnShare();
    }

    private static ISettingsService? Settings => App.Services.GetService(typeof(ISettingsService)) as ISettingsService;

    private void ApplySavedBounds()
    {
        if (Settings?.Current.Ft4 is not { } ft4)
            return;

        if (ft4.WindowWidth is { } w && ft4.WindowHeight is { } h)
        {
            Width = Math.Max(BasicMinWidth, w);
            Height = Math.Max(MinHeight, h);
        }

        if (ft4.WindowX is { } x && ft4.WindowY is { } y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }

        if (ft4.WindowMaximised)
            WindowState = WindowState.Maximized;
    }

    /// <summary>Keeps the last normal size when closing maximised, so restoring later goes back to it.</summary>
    private void CaptureBounds()
    {
        if (Settings is not { } settings)
            return;

        var ft4 = settings.Current.Ft4;
        ft4.WindowMaximised = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            ft4.WindowWidth = (int)Math.Round(Width);
            ft4.WindowHeight = (int)Math.Round(Height);
            ft4.WindowX = Position.X;
            ft4.WindowY = Position.Y;
        }

        settings.RequestSave();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (WindowStartupLocation == WindowStartupLocation.Manual)
            Position = QsoLogbookWindowBounds.ClampToVisibleArea(this, Position);

        if (DataContext is not Ft4ViewModel vm)
            return;

        vm.Decodes.CollectionChanged += OnDecodeRowsChanged;
        vm.CallsToMe.CollectionChanged += OnCallsToMeRowsChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        ApplyDecodeLayout(vm, widen: false);
        await vm.OnWindowOpenedAsync().ConfigureAwait(true);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Ft4ViewModel.IsAdvancedDecodeLayout) && sender is Ft4ViewModel vm)
            ApplyDecodeLayout(vm, widen: true);
    }

    /// <summary>
    /// Show or hide the calling-me column at its saved share. <paramref name="widen"/> makes room
    /// when the operator switches to the advanced layout; a saved window size is left alone on open.
    /// </summary>
    private void ApplyDecodeLayout(Ft4ViewModel vm, bool widen)
    {
        var advanced = vm.IsAdvancedDecodeLayout;
        var columns = DecodeColumns.ColumnDefinitions;
        var share = vm.DecodeColumnShare;
        columns[0].Width = new GridLength(advanced ? share : 1, GridUnitType.Star);
        columns[0].MinWidth = advanced ? MinDecodeColumnWidth : 0;
        columns[2].Width = advanced ? new GridLength(1 - share, GridUnitType.Star) : new GridLength(0);
        columns[2].MinWidth = advanced ? MinDecodeColumnWidth : 0;
        MinWidth = advanced ? AdvancedMinWidth : BasicMinWidth;
        if (widen && advanced && WindowState == WindowState.Normal && Width < AdvancedWidth)
            Width = AdvancedWidth;
    }

    private void SaveDecodeColumnShare()
    {
        if (DataContext is not Ft4ViewModel { IsAdvancedDecodeLayout: true } vm)
            return;

        var left = DecodeColumns.ColumnDefinitions[0].ActualWidth;
        var right = DecodeColumns.ColumnDefinitions[2].ActualWidth;
        if (left + right > 0)
            vm.SaveDecodeColumnShare(left / (left + right));
    }

    private void OnDecodeRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ScrollToNewest(DecodeList, e);

    private void OnCallsToMeRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ScrollToNewest(CallsToMeList, e);

    private static void ScrollToNewest(ListBox list, NotifyCollectionChangedEventArgs e)
    {
        // Newest lines are inserted at the top. The list keeps its scroll offset,
        // so each cycle appears above the viewport until the operator scrolls up.
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewStartingIndex != 0)
            return;

        Dispatcher.UIThread.Post(() => ScrollToTop(list), DispatcherPriority.Background);
    }

    private static void ScrollToTop(ListBox list)
    {
        if (list.ItemCount == 0)
            return;

        list.ScrollIntoView(0);
        if (list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
            scroll.Offset = new Vector(scroll.Offset.X, 0);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        CaptureBounds();
        if (_closeConfirmed)
            return;
        if (DataContext is not Ft4ViewModel vm || !vm.IsTransmissionActive)
            return;

        e.Cancel = true;
        var l = LocalizationService.Instance;
        var stop = await SimpleConfirmDialog.ShowAsync(
            this,
            l.Get("Ft4.CloseWhileTx.Title"),
            l.Get("Ft4.CloseWhileTx.Message")).ConfigureAwait(true);
        if (!stop)
            return;

        vm.StopTransmissionNow();
        _closeConfirmed = true;
        Close();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is not Ft4ViewModel vm)
            return;

        vm.Decodes.CollectionChanged -= OnDecodeRowsChanged;
        vm.CallsToMe.CollectionChanged -= OnCallsToMeRowsChanged;
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        await vm.OnWindowClosedAsync().ConfigureAwait(true);
    }

    private async void OnEnterStationDetailsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not Ft4ViewModel vm)
            return;

        var dialog = new Ft4StationSetupWindow { DataContext = vm.CreateStationSetup() };
        var saved = await dialog.ShowDialog<bool>(this).ConfigureAwait(true);
        if (!saved)
            return;

        if (App.MainWindow?.DataContext is MainViewModel main)
            main.RefreshStationFromSettings();
        vm.OnStationDetailsSaved();
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not Ft4ViewModel vm)
            return;

        var dialog = new Ft4SettingsWindow { DataContext = vm };
        await dialog.ShowDialog(this).ConfigureAwait(true);
    }

    private void OnDecodeRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            return;

        _contextRow = (sender as Control)?.DataContext as Ft4DecodeRowViewModel;
        // Selecting a row answers that station, so a right-click must not select it.
        e.Handled = true;
    }

    private void OnDecodeContextMenuOpening(object? sender, CancelEventArgs e)
    {
        var list = ReferenceEquals(sender, CallsToMeList.ContextMenu) ? CallsToMeList : DecodeList;
        _contextRow ??= list.SelectedItem as Ft4DecodeRowViewModel;
        if (_contextRow is null
            || !_contextRow.Message.IsReceiveActivity
            || string.IsNullOrWhiteSpace(_contextRow.Message.CallDe))
        {
            _contextRow = null;
            e.Cancel = true;
        }
    }

    private void OnDecodeContextMenuClosed(object? sender, RoutedEventArgs e)
    {
        // Click runs before Closed, so the row is still set when the item is chosen.
        Dispatcher.UIThread.Post(() => _contextRow = null);
    }

    private void OnPounceOnStationClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is Ft4ViewModel vm && _contextRow is { } row)
            vm.PounceOnDecodeCommand.Execute(row);
        _contextRow = null;
    }

    private async void OnPounceCheckClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not Ft4ViewModel vm)
            return;

        if (vm.IsPounceArmed)
        {
            vm.CancelPounceCommand.Execute(null);
        }
        else
        {
            var dialog = new Ft4PounceWindow(vm.LastPounceInput);
            var target = await dialog.ShowDialog<Ft4PounceTarget?>(this).ConfigureAwait(true);
            if (target is not null)
                vm.ArmPounce(target);
        }

        // The tick always shows whether pounce is armed, not what the click toggled it to.
        PounceCheckBox.SetCurrentValue(ToggleButton.IsCheckedProperty, vm.IsPounceArmed);
    }
}
