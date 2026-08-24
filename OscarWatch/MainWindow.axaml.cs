using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OscarWatch.Controls;
using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Services;
using OscarWatch.ViewModels;
using OscarWatch.Views;
using OscarWatch.Localization;

namespace OscarWatch;

public partial class MainWindow : Window
{
    private static readonly TimeSpan PassListScrollResetIdle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PassListScrollResetCheckInterval = TimeSpan.FromSeconds(30);
    private const double PassListTopEpsilon = 1.0;

    private const double HamsAtRovesMinPanelHeight = 80;
    private const double HamsAtRovesMaxPanelHeight = 400;
    private const double HamsAtRovesResizeGripHeight = 10;
    private const double TimelineResizeGripHeight = 10;
    private const double MapMinHeightForTimelineResize = 200;

    private ScrollViewer? _passListScrollViewer;
    private DispatcherTimer? _passListScrollResetTimer;
    private DateTime _passListLastUserScrollUtc = DateTime.MinValue;
    private bool _passListIsScrolledDown;
    private bool _isResizingHamsAtRoves;
    private double _hamsAtRovesResizeStartY;
    private double _hamsAtRovesResizeStartHeight;
    private IPointer? _hamsAtRovesResizePointer;
    private bool _isResizingTimeline;
    private double _timelineResizeStartY;
    private double _timelineResizeStartHeight;
    private IPointer? _timelineResizePointer;
    private PassRowViewModel? _passListContextRow;
    private bool _hardwareShutdownComplete;

    public MainWindow()
    {
        InitializeComponent();
        PassesListBox.AddHandler(PointerPressedEvent, OnPassesListRightClickTunnel, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnResizePointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnResizePointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, OnResizePointerCaptureLost, RoutingStrategies.Tunnel);
        PassesListBox.ContainerPrepared += OnPassListContainerPrepared;
        PassesListBox.ContainerClearing += OnPassListContainerClearing;
        PassesListBox.AttachedToVisualTree += (_, _) => TryAttachPassListScrollViewer();
        TimelineControl.SatelliteFocusRequested += OnTimelineSatelliteFocusRequested;
        Closing += OnClosing;

        _passListScrollResetTimer = new DispatcherTimer { Interval = PassListScrollResetCheckInterval };
        _passListScrollResetTimer.Tick += OnPassListScrollResetTick;
        _passListScrollResetTimer.Start();
    }

    private void OnTimelineSatelliteFocusRequested(object? sender, string noradId)
    {
        if (DataContext is MainViewModel vm)
            vm.FocusedNoradId = noradId;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_hardwareShutdownComplete)
            return;

        // Cancel once so teardown finishes before Avalonia tears down the process.
        e.Cancel = true;
        _passListScrollResetTimer?.Stop();
        DetachPassListScrollViewer();

        var settings = App.Services.GetRequiredService<ISettingsService>();
        MainWindowBounds.Capture(this, settings.Current);
        settings.RequestSave();

        if (DataContext is MainViewModel vm)
        {
            vm.Frequencies.PersistOverlayPosition();
            vm.DxStation.PersistOverlayPosition();
            vm.DisconnectHardwareForShutdown();

            if (vm.PrepareForShutdown())
                await vm.SaveSettingsAsync();
        }

        _hardwareShutdownComplete = true;
        Close();
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

        new PassVisualizerWindow
        {
            DataContext = visualizerVm
        }.Show(this);
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

    private void OnSidebarLayoutSizeChanged(object? sender, SizeChangedEventArgs e) =>
        UpdateSidebarTopMaxHeight(e.NewSize.Height);

    private void UpdateSidebarTopMaxHeight(double sidebarHeight)
    {
        if (SidebarTopScrollViewer is null)
            return;

        var passesReserve = GetPassesReserve();
        var rovesReserve = GetRovesReserve();
        var maxTopHeight = sidebarHeight - passesReserve - rovesReserve;
        SidebarTopScrollViewer.MaxHeight = maxTopHeight > 0 ? maxTopHeight : double.PositiveInfinity;
    }

    private double GetPassesReserve()
    {
        const double minPassesListHeight = 140;
        const double expanderHeaderHeight = 32;
        var reserve = expanderHeaderHeight + 8;
        if (DataContext is MainViewModel vm && vm.IsPassesExpanded)
            reserve += minPassesListHeight;

        return reserve;
    }

    private double GetRovesReserve()
    {
        const double expanderHeaderHeight = 32;
        if (DataContext is not MainViewModel vm || !vm.ShowHamsAtRovesPanel)
            return 0;

        var reserve = expanderHeaderHeight + 8;
        if (vm.IsHamsAtRovesExpanded)
            reserve += vm.HamsAtRovesPanelHeight + HamsAtRovesResizeGripHeight + 16;

        return reserve;
    }

    private void OnHamsAtRovesResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsHamsAtRovesExpanded)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _isResizingHamsAtRoves = true;
        _hamsAtRovesResizePointer = e.Pointer;
        _hamsAtRovesResizeStartY = e.GetPosition(this).Y;
        _hamsAtRovesResizeStartHeight = vm.HamsAtRovesPanelHeight;
        e.Pointer.Capture((IInputElement)sender!);
        e.Handled = true;
    }

    private void OnResizePointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (_isResizingHamsAtRoves)
        {
            if (_hamsAtRovesResizePointer is not null && !ReferenceEquals(e.Pointer, _hamsAtRovesResizePointer))
                return;

            var deltaY = e.GetPosition(this).Y - _hamsAtRovesResizeStartY;
            var nextHeight = _hamsAtRovesResizeStartHeight - deltaY;
            vm.SetHamsAtRovesPanelHeight(nextHeight, GetMaxHamsAtRovesPanelHeight());
            e.Handled = true;
            return;
        }

        if (!_isResizingTimeline)
            return;

        if (_timelineResizePointer is not null && !ReferenceEquals(e.Pointer, _timelineResizePointer))
            return;

        var timelineDeltaY = e.GetPosition(this).Y - _timelineResizeStartY;
        var nextTimelineHeight = _timelineResizeStartHeight - timelineDeltaY;
        vm.SetTimelinePanelHeight(nextTimelineHeight, GetMaxTimelinePanelHeight());
        e.Handled = true;
    }

    private void OnResizePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isResizingHamsAtRoves)
        {
            if (_hamsAtRovesResizePointer is not null && !ReferenceEquals(e.Pointer, _hamsAtRovesResizePointer))
                return;

            EndHamsAtRovesResize(persist: true);
            e.Handled = true;
            return;
        }

        if (!_isResizingTimeline)
            return;

        if (_timelineResizePointer is not null && !ReferenceEquals(e.Pointer, _timelineResizePointer))
            return;

        EndTimelineResize(persist: true);
        e.Handled = true;
    }

    private void OnResizePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_isResizingHamsAtRoves)
        {
            if (_hamsAtRovesResizePointer is not null && !ReferenceEquals(e.Pointer, _hamsAtRovesResizePointer))
                return;

            EndHamsAtRovesResize(persist: true);
            return;
        }

        if (!_isResizingTimeline)
            return;

        if (_timelineResizePointer is not null && !ReferenceEquals(e.Pointer, _timelineResizePointer))
            return;

        EndTimelineResize(persist: true);
    }

    private void EndHamsAtRovesResize(bool persist)
    {
        _isResizingHamsAtRoves = false;
        _hamsAtRovesResizePointer = null;

        if (persist && DataContext is MainViewModel vm)
            vm.PersistHamsAtRovesPanelHeight();
    }

    private void OnTimelineResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsTimelineDockedVisible)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _isResizingTimeline = true;
        _timelineResizePointer = e.Pointer;
        _timelineResizeStartY = e.GetPosition(this).Y;
        _timelineResizeStartHeight = vm.TimelinePanelHeight;
        e.Pointer.Capture((IInputElement)sender!);
        e.Handled = true;
    }

    private void EndTimelineResize(bool persist)
    {
        _isResizingTimeline = false;
        _timelineResizePointer = null;

        if (persist && DataContext is MainViewModel vm)
            vm.PersistTimelinePanelHeight();
    }

    private double GetMaxTimelinePanelHeight()
    {
        if (MapColumn is null)
            return MainViewModel.TimelineMaxPanelHeight;

        var computed = MapColumn.Bounds.Height - MapMinHeightForTimelineResize - TimelineResizeGripHeight;
        return Math.Clamp(computed, MainViewModel.TimelineMinPanelHeight, MainViewModel.TimelineMaxPanelHeight);
    }

    private double GetMaxHamsAtRovesPanelHeight()
    {
        if (SidebarLayoutGrid is null)
            return HamsAtRovesMaxPanelHeight;

        const double expanderHeaderHeight = 32;
        const double topSectionFloor = 120;
        var sidebarHeight = SidebarLayoutGrid.Bounds.Height;
        var computed = sidebarHeight - GetPassesReserve() - expanderHeaderHeight - HamsAtRovesResizeGripHeight - topSectionFloor - 16;
        return Math.Clamp(computed, HamsAtRovesMinPanelHeight, HamsAtRovesMaxPanelHeight);
    }

    private void OnHamsAtRovesListTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        for (var node = e.Source as Visual; node is not null; node = node.GetVisualParent() as Visual)
        {
            if (node is ListBoxItem { DataContext: HamsAtRoveRowViewModel row })
            {
                vm.OpenHamsAtRove(row);
                break;
            }
        }
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var settings = App.Services.GetRequiredService<ISettingsService>();
        if (MainWindowLayoutDefaults.TryGetSavedPosition(settings.Current, out _, out _))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = QsoLogbookWindowBounds.ClampToVisibleArea(this, Position);
        }

        if (settings.Current.MainWindowMaximized)
            WindowState = WindowState.Maximized;

        if (DataContext is MainViewModel vm)
        {
            var initStopwatch = Stopwatch.StartNew();
            vm.SidebarLayoutInvalidated += OnSidebarLayoutInvalidated;
            await vm.InitializeAsync();
            TimelineControl.SetPropagator(
                App.Services.GetRequiredService<IOrbitPropagator>(),
                vm.GroundStation);
            Serilog.Log.Information("MainWindow.OnOpened initialization completed in {ElapsedMs} ms", initStopwatch.ElapsedMilliseconds);
            if (SidebarLayoutGrid is not null)
                UpdateSidebarTopMaxHeight(SidebarLayoutGrid.Bounds.Height);
        }
    }

    private void OnSidebarLayoutInvalidated()
    {
        if (SidebarLayoutGrid is not null)
            UpdateSidebarTopMaxHeight(SidebarLayoutGrid.Bounds.Height);
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (DataContext is MainViewModel vm && vm.Frequencies.ShowOperatingStyleRow)
            {
                vm.Frequencies.ToggleCwUplinkCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.None)
        {
            if (IsTextEntryFocused(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()))
                return;

            if (DataContext is MainViewModel vm)
            {
                vm.ToggleSoloFocusedSatelliteCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.L && e.KeyModifiers == KeyModifiers.None)
        {
            if (IsTextEntryFocused(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()))
                return;

            if (DataContext is MainViewModel vm)
            {
                vm.Frequencies.ToggleLeadTuningPanel();
                e.Handled = true;
            }

            return;
        }

        if (e.Key is not (Key.Add or Key.Subtract))
            return;

        if (IsTextEntryFocused(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()))
            return;

        if (DataContext is not MainViewModel main || !main.Frequencies.HasTransponderData)
            return;

        const int stepHz = 10;
        main.Frequencies.AdjustActiveOffsetHz(e.Key == Key.Add ? stepHz : -stepHz);
        e.Handled = true;
    }

    private static bool IsTextEntryFocused(IInputElement? focused)
    {
        for (var current = focused as Control; current is not null; current = current.Parent as Control)
        {
            if (current is TextBox or NumericUpDown)
                return true;
        }

        return false;
    }
}
