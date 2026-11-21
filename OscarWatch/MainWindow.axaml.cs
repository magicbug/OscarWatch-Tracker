using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Services;
using OscarWatch.ViewModels;
using OscarWatch.Views;

namespace OscarWatch;

public partial class MainWindow : Window
{
    private const double HamsAtRovesMinPanelHeight = 80;
    private const double HamsAtRovesMaxPanelHeight = 400;
    private const double HamsAtRovesResizeGripHeight = 14;
    private const double TimelineResizeGripHeight = 10;
    private const double MapMinHeightForTimelineResize = 200;

    private bool _isResizingHamsAtRoves;
    private double _hamsAtRovesResizeStartY;
    private double _hamsAtRovesResizeStartHeight;
    private IPointer? _hamsAtRovesResizePointer;
    private bool _isResizingTimeline;
    private double _timelineResizeStartY;
    private double _timelineResizeStartHeight;
    private IPointer? _timelineResizePointer;
    private bool _hardwareShutdownComplete;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnResizePointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnResizePointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, OnResizePointerCaptureLost, RoutingStrategies.Tunnel);
        TimelineControl.SatelliteFocusRequested += OnTimelineSatelliteFocusRequested;
        Closing += OnClosing;
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

    private void OnSidebarLayoutSizeChanged(object? sender, SizeChangedEventArgs e) =>
        UpdateSidebarLayout(e.NewSize.Height);

    private void UpdateSidebarLayout(double sidebarHeight)
    {
        UpdateSidebarPassesRowDefinition();
        UpdateSidebarTopMaxHeight(sidebarHeight);
    }

    private void UpdateSidebarPassesRowDefinition()
    {
        if (SidebarLayoutGrid is null)
            return;

        // Default to the star row (docked list) until MainViewModel is available. Treating a
        // missing DataContext as "not docked" made the passes row Auto and let the list grow
        // over the Live / sky plot section.
        var docked = DataContext is not MainViewModel vm || vm.IsPassesDockedVisible;
        var row = SidebarLayoutGrid.RowDefinitions[1];
        row.Height = docked ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        if (PassesExpander is not null)
            PassesExpander.VerticalAlignment = docked
                ? Avalonia.Layout.VerticalAlignment.Stretch
                : Avalonia.Layout.VerticalAlignment.Top;
    }

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
        if (DataContext is MainViewModel vm && vm.IsPassesDockedVisible)
            reserve += minPassesListHeight;
        else if (DataContext is MainViewModel { IsPassesDetached: true, IsPassesExpanded: true })
            reserve += 28;

        return reserve;
    }

    private double GetRovesReserve()
    {
        const double expanderHeaderHeight = 32;
        if (DataContext is not MainViewModel vm || !vm.ShowHamsAtRovesPanel)
            return 0;

        var reserve = expanderHeaderHeight + 8;
        if (vm.IsRovesDockedVisible)
            reserve += vm.HamsAtRovesPanelHeight + HamsAtRovesResizeGripHeight + 16;
        else if (vm.IsRovesDetached && vm.IsHamsAtRovesExpanded)
            reserve += 28;

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
                UpdateSidebarLayout(SidebarLayoutGrid.Bounds.Height);
        }
    }

    private void OnSidebarLayoutInvalidated()
    {
        if (SidebarLayoutGrid is not null)
            UpdateSidebarLayout(SidebarLayoutGrid.Bounds.Height);
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
