using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OscarWatch.Localization;
using OscarWatch.ViewModels;
using OscarWatch.Views;

namespace OscarWatch.Controls;

public partial class UpcomingPassesListControl : UserControl
{
    private static readonly TimeSpan PassListScrollResetIdle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PassListScrollResetCheckInterval = TimeSpan.FromSeconds(30);
    private const double PassListTopEpsilon = 1.0;

    private ScrollViewer? _passListScrollViewer;
    private DispatcherTimer? _passListScrollResetTimer;
    private DateTime _passListLastUserScrollUtc = DateTime.MinValue;
    private bool _passListIsScrolledDown;
    private PassRowViewModel? _passListContextRow;

    public UpcomingPassesListControl()
    {
        InitializeComponent();
        PassesListBox.AddHandler(PointerPressedEvent, OnPassesListRightClickTunnel, RoutingStrategies.Tunnel);
        PassesListBox.ContainerPrepared += OnPassListContainerPrepared;
        PassesListBox.ContainerClearing += OnPassListContainerClearing;
        PassesListBox.AttachedToVisualTree += (_, _) => TryAttachPassListScrollViewer();
        DetachedFromVisualTree += OnDetachedFromVisualTree;

        _passListScrollResetTimer = new DispatcherTimer { Interval = PassListScrollResetCheckInterval };
        _passListScrollResetTimer.Tick += OnPassListScrollResetTick;
        _passListScrollResetTimer.Start();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _passListScrollResetTimer?.Stop();
        DetachPassListScrollViewer();
    }

    private void TryAttachPassListScrollViewer()
    {
        if (_passListScrollViewer is not null)
            return;

        _passListScrollViewer = PassesListBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_passListScrollViewer is null)
            return;

        _passListScrollViewer.ScrollChanged += OnPassListScrollChanged;
        UpdatePassListScrollState(_passListScrollViewer.Offset.Y);
    }

    private void DetachPassListScrollViewer()
    {
        if (_passListScrollViewer is null)
            return;

        _passListScrollViewer.ScrollChanged -= OnPassListScrollChanged;
        _passListScrollViewer = null;
    }

    private void OnPassListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer viewer)
            UpdatePassListScrollState(viewer.Offset.Y);
    }

    private void UpdatePassListScrollState(double offsetY)
    {
        _passListIsScrolledDown = offsetY > PassListTopEpsilon;
        if (_passListIsScrolledDown)
            _passListLastUserScrollUtc = DateTime.UtcNow;
        else
            _passListLastUserScrollUtc = DateTime.MinValue;
    }

    private void OnPassListScrollResetTick(object? sender, EventArgs e)
    {
        if (!_passListIsScrolledDown || _passListScrollViewer is null)
            return;

        if (_passListLastUserScrollUtc == DateTime.MinValue)
            return;

        if (DateTime.UtcNow - _passListLastUserScrollUtc < PassListScrollResetIdle)
            return;

        var offset = _passListScrollViewer.Offset;
        _passListScrollViewer.Offset = new Vector(offset.X, 0);
        _passListIsScrolledDown = false;
        _passListLastUserScrollUtc = DateTime.MinValue;
    }

    private static void OnPassListContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container is ListBoxItem item)
            item.Classes.Remove("pass-day-header");
    }

    private void OnPassesListRightClickTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PassesListBox).Properties.IsRightButtonPressed)
            return;

        _passListContextRow = TryGetPassRowFromPointerSource(e.Source as Visual);
        e.Handled = _passListContextRow is not null;
    }

    private void OnPassesContextMenuOpening(object? sender, CancelEventArgs e)
    {
        _passListContextRow ??= PassesListBox.SelectedItem as PassRowViewModel;
        if (_passListContextRow is null)
        {
            e.Cancel = true;
            return;
        }

        if (SchedulePassMenuItem is not null)
        {
            var l = LocalizationService.Instance;
            SchedulePassMenuItem.Header = _passListContextRow.IsScheduled
                ? l.Get("Pass.Schedule.Remove")
                : l.Get("Pass.Schedule.Add");
        }

        if (PostHamsAtActivationMenuItem is not null && DataContext is MainViewModel vm)
            PostHamsAtActivationMenuItem.IsVisible = vm.HasHamsAtApiKey;
    }

    private async void OnPostHamsAtActivationClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var row = _passListContextRow ?? PassesListBox.SelectedItem as PassRowViewModel;
        if (row is null)
            return;

        await vm.PostHamsAtActivationAsync(row).ConfigureAwait(true);
        _passListContextRow = null;
    }

    private void OnOpenPassVisualizerClick(object? sender, RoutedEventArgs e) =>
        OpenPassVisualizerForContextPass();

    private void OnToggleSchedulePassClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var row = _passListContextRow ?? PassesListBox.SelectedItem as PassRowViewModel;
        if (row is null)
            return;

        vm.TogglePassScheduled(row);
        _passListContextRow = null;
    }

    private void OnPassScheduleToggleClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (sender is not Button { DataContext: PassRowViewModel row })
            return;

        e.Handled = true;
        vm.TogglePassScheduled(row);
    }

    private static PassRowViewModel? TryGetPassRowFromPointerSource(Visual? source)
    {
        for (var node = source; node is not null; node = node.GetVisualParent() as Visual)
        {
            if (node is ListBoxItem item && item.DataContext is PassRowViewModel passRow)
                return passRow;
        }

        return null;
    }

    private void OpenPassVisualizerForContextPass()
    {
        if (DataContext is not MainViewModel vm)
            return;

        var row = _passListContextRow ?? PassesListBox.SelectedItem as PassRowViewModel;
        if (row is null)
            return;

        var visualizerVm = vm.CreatePassVisualizerViewModel(row);
        _passListContextRow = null;
        if (visualizerVm is null)
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var window = new PassVisualizerWindow { DataContext = visualizerVm };
        if (owner is null)
            window.Show();
        else
            window.Show(owner);
    }

    private static void OnPassListContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is not ListBoxItem item)
            return;

        var isHeader = item.DataContext is PassDayHeaderViewModel;
        item.IsEnabled = !isHeader;
        item.Focusable = !isHeader;

        if (isHeader)
            item.Classes.Add("pass-day-header");
        else
            item.Classes.Remove("pass-day-header");
    }
}
