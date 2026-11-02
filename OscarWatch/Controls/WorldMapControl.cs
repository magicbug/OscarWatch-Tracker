using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;
using OscarWatch.Core.Terminal;
using OscarWatch.Localization;
using OscarWatch.Terminal;

namespace OscarWatch.Controls;

public class WorldMapControl : ThemeAwareControl
{
    private const double WrapEdgeMarginPx = 60;
    /// <summary>Cap decoded map size — full-res Blue Marble can exceed 100 MB in memory.</summary>
    private const int MapDecodeMaxWidth = 2048;

    public static readonly StyledProperty<IReadOnlyList<SatelliteTrackState>?> TrackStatesProperty =
        AvaloniaProperty.Register<WorldMapControl, IReadOnlyList<SatelliteTrackState>?>(
            nameof(TrackStates));

    public static readonly StyledProperty<GroundStation?> GroundStationProperty =
        AvaloniaProperty.Register<WorldMapControl, GroundStation?>(nameof(GroundStation));

    public static readonly StyledProperty<string?> FocusedNoradIdProperty =
        AvaloniaProperty.Register<WorldMapControl, string?>(nameof(FocusedNoradId));

    public static readonly StyledProperty<bool> ShowFootprintMotionArrowsProperty =
        AvaloniaProperty.Register<WorldMapControl, bool>(nameof(ShowFootprintMotionArrows), true);

    public static readonly StyledProperty<GeoCoordinate?> RemoteStationProperty =
        AvaloniaProperty.Register<WorldMapControl, GeoCoordinate?>(nameof(RemoteStation));

    public static readonly StyledProperty<bool> SoloFocusedSatelliteProperty =
        AvaloniaProperty.Register<WorldMapControl, bool>(nameof(SoloFocusedSatellite));

    public static readonly StyledProperty<bool> ShowGreylineOverlayProperty =
        AvaloniaProperty.Register<WorldMapControl, bool>(nameof(ShowGreylineOverlay));

    public static readonly StyledProperty<bool> ShowMultiTrackOverlayProperty =
        AvaloniaProperty.Register<WorldMapControl, bool>(nameof(ShowMultiTrackOverlay), true);

    public static readonly StyledProperty<DateTime> MapDisplayUtcProperty =
        AvaloniaProperty.Register<WorldMapControl, DateTime>(
            nameof(MapDisplayUtc),
            defaultValue: DateTime.UtcNow);

    public static readonly StyledProperty<double> MapCentreLongitudeProperty =
        AvaloniaProperty.Register<WorldMapControl, double>(nameof(MapCentreLongitude));

    public static readonly StyledProperty<IReadOnlyList<MapStationMarker>?> StationMarkersProperty =
        AvaloniaProperty.Register<WorldMapControl, IReadOnlyList<MapStationMarker>?>(nameof(StationMarkers));

    /// <summary>Mouse wheel zoom, drag to pan and the zoom keys. Off for the main map.</summary>
    public static readonly StyledProperty<bool> IsZoomEnabledProperty =
        AvaloniaProperty.Register<WorldMapControl, bool>(nameof(IsZoomEnabled));

    public const double MaxZoom = 16;
    private const double WheelZoomStep = 1.25;
    private const double ButtonZoomStep = 2;
    private const double DragThresholdPx = 4;
    private const double KeyPanFraction = 0.2;

    private double _zoom = 1;
    private double? _viewCentreLon;
    private double _viewCentreLat;
    private Point? _dragStart;
    private Point _dragLast;
    private bool _dragging;

    private const double StationLabelFontSize = 11;
    private const double StationHitRadiusPx = 16;
    private const double StationSymbolSize = 26;
    private readonly FormattedTextCache _stationLabelCache = new();

    /// <summary>Raised with the callsign when a heard-station marker is clicked.</summary>
    public event EventHandler<string>? StationMarkerClicked;

    /// <summary>Raised after the zoom level or the visible area changes.</summary>
    public event EventHandler? ViewChanged;

    public bool IsZoomEnabled
    {
        get => GetValue(IsZoomEnabledProperty);
        set => SetValue(IsZoomEnabledProperty, value);
    }

    public double ZoomLevel => _zoom;

    public bool CanZoomIn => IsZoomEnabled && _zoom < MaxZoom;

    public bool CanZoomOut => IsZoomEnabled && _zoom > 1;

    public bool IsViewChanged => _zoom > 1 || _viewCentreLon is not null;

    private double EffectiveCentreLongitude => _viewCentreLon ?? MapCentreLongitude;

    /// <summary>
    /// The world is drawn at <c>zoom</c> times the control size; the offsets say which part of it is visible.
    /// Horizontal panning moves the centre longitude instead, so the map still wraps.
    /// </summary>
    private (double W, double H, double OffsetX, double OffsetY) Viewport(double w, double h)
    {
        var z = IsZoomEnabled ? _zoom : 1;
        var vw = w * z;
        var vh = h * z;
        var offsetY = Math.Clamp(vh * (90 - _viewCentreLat) / 180 - h / 2, 0, Math.Max(0, vh - h));
        return (vw, vh, (vw - w) / 2, offsetY);
    }

    private (Point Pos, double W, double H) ToVirtual(Point pos)
    {
        var (vw, vh, ox, oy) = Viewport(Bounds.Width, Bounds.Height);
        return (new Point(pos.X + ox, pos.Y + oy), vw, vh);
    }

    public void ZoomIn() => ZoomAroundCentre(ButtonZoomStep);

    public void ZoomOut() => ZoomAroundCentre(1 / ButtonZoomStep);

    /// <summary>Back to the whole world, centred on <see cref="MapCentreLongitude"/>.</summary>
    public void ResetView()
    {
        _zoom = 1;
        _viewCentreLon = null;
        _viewCentreLat = 0;
        OnViewChanged();
    }

    private void ZoomAroundCentre(double factor)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (!IsZoomEnabled || w <= 0 || h <= 0)
            return;

        // The first zoom in from the whole world goes to the home station rather than the equator.
        if (_zoom <= 1 && factor > 1 && GroundStation is { } gs)
        {
            _zoom = Math.Min(MaxZoom, factor);
            _viewCentreLon = NormaliseLongitude(gs.LongitudeDeg);
            _viewCentreLat = ClampViewLatitude(gs.LatitudeDeg);
            OnViewChanged();
            return;
        }

        ZoomAt(new Point(w / 2, h / 2), factor);
    }

    /// <summary>Zooms by <paramref name="factor"/> keeping the place under <paramref name="pos"/> still.</summary>
    private void ZoomAt(Point pos, double factor)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (!IsZoomEnabled || w <= 0 || h <= 0)
            return;

        var newZoom = Math.Clamp(_zoom * factor, 1, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 1e-6)
            return;
        if (newZoom <= 1.0001)
        {
            ResetView();
            return;
        }

        var (vw, vh, ox, oy) = Viewport(w, h);
        var (lat, lon) = PixelToGeo(pos.X + ox, pos.Y + oy, vw, vh, EffectiveCentreLongitude);
        _zoom = newZoom;
        _viewCentreLon = NormaliseLongitude(lon - (pos.X - w / 2) / (w * _zoom) * 360);
        _viewCentreLat = ClampViewLatitude(lat + (pos.Y - h / 2) / (h * _zoom) * 180);
        OnViewChanged();
    }

    private void PanByPixels(double dx, double dy)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (!IsZoomEnabled || w <= 0 || h <= 0)
            return;

        _viewCentreLon = NormaliseLongitude(EffectiveCentreLongitude - dx / (w * _zoom) * 360);
        _viewCentreLat = ClampViewLatitude(_viewCentreLat + dy / (h * _zoom) * 180);
        OnViewChanged();
    }

    /// <summary>Keeps the visible area inside the map top and bottom.</summary>
    private double ClampViewLatitude(double lat)
    {
        var half = 90 / _zoom;
        return Math.Clamp(lat, -90 + half, 90 - half);
    }

    private static double NormaliseLongitude(double lon)
    {
        lon %= 360;
        if (lon >= 180)
            lon -= 360;
        else if (lon < -180)
            lon += 360;
        return lon;
    }

    private void OnViewChanged()
    {
        _lastRenderedSubpoints.Clear();
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private Bitmap? _mapBitmap;
    private INotifyCollectionChanged? _trackStatesSource;
    private Size _lastLayoutInvalidationSize;
    private readonly Dictionary<string, (double X, double Y)> _lastRenderedSubpoints = new();
    private long _lastGreylineUtcMinuteTicks = long.MinValue;

    private Color _cachedTwilightBaseColor;
    private int _cachedTwilightBandCount;
    private ImmutableArray<SolidColorBrush> _twilightBrushes = ImmutableArray<SolidColorBrush>.Empty;

    private LabelOrderBuffer _labelOrderBuffer = new(64);
    private readonly RenderResourceCache _renderCache = new();
    private readonly FormattedTextCache _labelCache = new();
    private readonly Dictionary<string, FootprintGeometryEntry> _footprintGeometryCache = new();
    private readonly Dictionary<string, GroundTrackSplitEntry> _groundTrackSplitCache = new();
    
    // Reusable buffers for cache key iteration to avoid ToArray() allocations every render frame
    private string[] _footprintCacheKeysBuffer = new string[32];
    private string[] _groundTrackCacheKeysBuffer = new string[32];

    public WorldMapControl()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    static WorldMapControl()
    {
        // TrackStates and MapDisplayUtc are handled in OnPropertyChanged with movement /
        // greyline-minute throttling so the live 4 Hz / 1 Hz path does not force full paints.
        AffectsRender<WorldMapControl>(
            GroundStationProperty,
            FocusedNoradIdProperty,
            ShowFootprintMotionArrowsProperty,
            RemoteStationProperty,
            SoloFocusedSatelliteProperty,
            ShowGreylineOverlayProperty,
            ShowMultiTrackOverlayProperty,
            MapCentreLongitudeProperty,
            StationMarkersProperty);
    }

    /// <summary>Heard Terminal stations to plot (pass map). Null or empty draws nothing.</summary>
    public IReadOnlyList<MapStationMarker>? StationMarkers
    {
        get => GetValue(StationMarkersProperty);
        set => SetValue(StationMarkersProperty, value);
    }

    public bool ShowFootprintMotionArrows
    {
        get => GetValue(ShowFootprintMotionArrowsProperty);
        set => SetValue(ShowFootprintMotionArrowsProperty, value);
    }

    private const double HitRadiusPx = 16;

    public IReadOnlyList<SatelliteTrackState>? TrackStates
    {
        get => GetValue(TrackStatesProperty);
        set => SetValue(TrackStatesProperty, value);
    }

    public GroundStation? GroundStation
    {
        get => GetValue(GroundStationProperty);
        set => SetValue(GroundStationProperty, value);
    }

    public string? FocusedNoradId
    {
        get => GetValue(FocusedNoradIdProperty);
        set => SetValue(FocusedNoradIdProperty, value);
    }

    public GeoCoordinate? RemoteStation
    {
        get => GetValue(RemoteStationProperty);
        set => SetValue(RemoteStationProperty, value);
    }

    public bool SoloFocusedSatellite
    {
        get => GetValue(SoloFocusedSatelliteProperty);
        set => SetValue(SoloFocusedSatelliteProperty, value);
    }

    public bool ShowGreylineOverlay
    {
        get => GetValue(ShowGreylineOverlayProperty);
        set => SetValue(ShowGreylineOverlayProperty, value);
    }

    public bool ShowMultiTrackOverlay
    {
        get => GetValue(ShowMultiTrackOverlayProperty);
        set => SetValue(ShowMultiTrackOverlayProperty, value);
    }

    public DateTime MapDisplayUtc
    {
        get => GetValue(MapDisplayUtcProperty);
        set => SetValue(MapDisplayUtcProperty, value);
    }

    /// <summary>Longitude at mid-map (0 = Greenwich). Seam stays at the viewport edges.</summary>
    public double MapCentreLongitude
    {
        get => GetValue(MapCentreLongitudeProperty);
        set => SetValue(MapCentreLongitudeProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LayoutUpdated += OnLayoutUpdatedForRender;
        if (Application.Current is not null)
            Application.Current.ActualThemeVariantChanged += OnThemeChangedClearCache;
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdatedForRender;
        if (Application.Current is not null)
            Application.Current.ActualThemeVariantChanged -= OnThemeChangedClearCache;
        UnsubscribeTrackStatesSource();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TrackStatesProperty)
        {
            BindTrackStatesSource(change.NewValue);
            if (ShouldRenderForTrackStates())
                InvalidateVisual();
        }
        else if (change.Property == MapDisplayUtcProperty)
        {
            if (ShouldRenderForMapDisplayUtc())
                InvalidateVisual();
        }
        else if (change.Property == MapCentreLongitudeProperty)
        {
            _footprintGeometryCache.Clear();
            _groundTrackSplitCache.Clear();
            _lastRenderedSubpoints.Clear();
        }
        else if (change.Property == IsZoomEnabledProperty && !change.GetNewValue<bool>())
        {
            ResetView();
        }

        if (change.Property == TrackStatesProperty || change.Property == FocusedNoradIdProperty)
            TrackingPlotAccessibility.UpdateName(
                this,
                LocalizationService.Instance.Get("Main.WorldMap"),
                TrackStates,
                FocusedNoradId);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            _lastRenderedSubpoints.Clear();
            InvalidateVisual();
        }
    }

    private void BindTrackStatesSource(object? value)
    {
        UnsubscribeTrackStatesSource();
        _trackStatesSource = value as INotifyCollectionChanged;
        if (_trackStatesSource is not null)
            _trackStatesSource.CollectionChanged += OnTrackStatesSourceChanged;
        // Do not clear render/label caches here: LiveStates is a new array every 250 ms and
        // brushes/FormattedText remain valid across snapshot identity changes.
    }

    private void UnsubscribeTrackStatesSource()
    {
        if (_trackStatesSource is null)
            return;

        _trackStatesSource.CollectionChanged -= OnTrackStatesSourceChanged;
        _trackStatesSource = null;
    }

    private void OnTrackStatesSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ShouldRenderForTrackStates())
            InvalidateVisual();
    }

    private void OnThemeChangedClearCache(object? sender, EventArgs e)
    {
        _renderCache.Clear();
        _labelCache.Clear();
        _stationLabelCache.Clear();
        _lastRenderedSubpoints.Clear();
        _lastGreylineUtcMinuteTicks = long.MinValue;
    }

    private void OnLayoutUpdatedForRender(object? sender, EventArgs e)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;

        if (Math.Abs(w - _lastLayoutInvalidationSize.Width) < 0.5
            && Math.Abs(h - _lastLayoutInvalidationSize.Height) < 0.5)
            return;

        _lastLayoutInvalidationSize = new Size(w, h);
        _lastRenderedSubpoints.Clear();
        InvalidateVisual();
    }

    private bool ShouldRenderForTrackStates()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return true;

        var (w, h, _, _) = Viewport(Bounds.Width, Bounds.Height);
        return HasMovedBeyondThreshold(
            TrackStates,
            _lastRenderedSubpoints,
            w,
            h,
            EffectiveCentreLongitude);
    }

    private bool ShouldRenderForMapDisplayUtc()
    {
        if (!ShowGreylineOverlay)
            return false;

        var minuteTicks = FloorToUtcMinuteTicks(MapDisplayUtc);
        return minuteTicks != _lastGreylineUtcMinuteTicks;
    }

    /// <summary>
    /// Returns true when any satellite subpoint moved ≥ 1 px, a satellite appeared/disappeared,
    /// or the previous cache was empty and states are non-empty.
    /// </summary>
    internal static bool HasMovedBeyondThreshold(
        IReadOnlyList<SatelliteTrackState>? states,
        Dictionary<string, (double X, double Y)> previousPositions,
        double width,
        double height,
        double centreLongitudeDeg,
        double thresholdPxSquared = 1.0)
    {
        if (states is null || states.Count == 0)
            return previousPositions.Count > 0;

        if (width <= 0 || height <= 0)
            return true;

        var count = 0;
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            var (x, y) = EquirectangularProjection.GeoToPixel(
                state.Subpoint.LatitudeDeg,
                state.Subpoint.LongitudeDeg,
                width,
                height,
                centreLongitudeDeg);
            count++;

            if (previousPositions.TryGetValue(state.NoradId, out var prev))
            {
                var dx = x - prev.X;
                var dy = y - prev.Y;
                if (dx * dx + dy * dy >= thresholdPxSquared)
                    return true;
            }
            else
            {
                return true;
            }
        }

        return previousPositions.Count != count;
    }

    internal static long FloorToUtcMinuteTicks(DateTime utc)
    {
        var floored = new DateTime(
            utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);
        return floored.Ticks;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pos = e.GetPosition(this);
        var (vp, vw, vh) = ToVirtual(pos);
        if (HitTestStation(vp, vw, vh) is { } station)
        {
            StationMarkerClicked?.Invoke(this, station.Call);
            return;
        }

        var hit = HitTestSatellite(vp, vw, vh);
        if (hit is not null)
            FocusedNoradId = hit;

        if (IsZoomEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragStart = pos;
            _dragLast = pos;
            _dragging = false;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (_dragStart is { } start)
        {
            if (!_dragging && (Math.Abs(pos.X - start.X) > DragThresholdPx || Math.Abs(pos.Y - start.Y) > DragThresholdPx))
            {
                _dragging = true;
                e.Pointer.Capture(this);
                Cursor = new Cursor(StandardCursorType.SizeAll);
                ToolTip.SetTip(this, null);
            }

            if (_dragging)
            {
                PanByPixels(pos.X - _dragLast.X, pos.Y - _dragLast.Y);
                _dragLast = pos;
                return;
            }
        }

        if (StationMarkers is not { Count: > 0 })
            return;
        var (vp, vw, vh) = ToVirtual(pos);
        var station = HitTestStation(vp, vw, vh);
        var tip = station?.ToolTipText;
        if (!Equals(ToolTip.GetTip(this), tip))
            ToolTip.SetTip(this, tip);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
            e.Pointer.Capture(null);
        EndDrag();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!IsZoomEnabled || e.Delta.Y == 0)
            return;
        ZoomAt(e.GetPosition(this), Math.Pow(WheelZoomStep, e.Delta.Y));
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (_dragging)
            Cursor = new Cursor(StandardCursorType.Hand);
        _dragStart = null;
        _dragging = false;
    }

    /// <summary>+ and − zoom, 0 shows the whole world, Shift with the arrow keys pans.</summary>
    private bool HandleZoomKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                ZoomIn();
                return true;
            case Key.OemMinus or Key.Subtract:
                ZoomOut();
                return true;
            case Key.D0 or Key.NumPad0:
                ResetView();
                return true;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return false;

        var stepX = Bounds.Width * KeyPanFraction;
        var stepY = Bounds.Height * KeyPanFraction;
        switch (e.Key)
        {
            case Key.Left:
                PanByPixels(stepX, 0);
                return true;
            case Key.Right:
                PanByPixels(-stepX, 0);
                return true;
            case Key.Up:
                PanByPixels(0, stepY);
                return true;
            case Key.Down:
                PanByPixels(0, -stepY);
                return true;
            default:
                return false;
        }
    }

    private MapStationMarker? HitTestStation(Point pos, double w, double h)
    {
        var markers = StationMarkers;
        if (markers is not { Count: > 0 } || w <= 0 || h <= 0)
            return null;

        MapStationMarker? best = null;
        var bestDist = double.MaxValue;
        foreach (var m in markers)
        {
            var (mx, my) = EquirectangularProjection.GeoToPixel(m.Latitude, m.Longitude, w, h, EffectiveCentreLongitude);
            foreach (var xOffset in GetSubpointWrapOffsets(mx, w))
            {
                var dx = pos.X - (mx + xOffset);
                var dy = pos.Y - my;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist <= StationHitRadiusPx && dist < bestDist)
                {
                    bestDist = dist;
                    best = m;
                }
            }
        }

        return best;
    }

    private void DrawStationMarkers(DrawingContext context, double w, double h, double centreLon, UiPalette palette)
    {
        var markers = StationMarkers;
        if (markers is not { Count: > 0 })
            return;

        // Oldest first so the most recently heard stations end up on top.
        for (var i = markers.Count - 1; i >= 0; i--)
        {
            var m = markers[i];
            var (mx, my) = EquirectangularProjection.GeoToPixel(m.Latitude, m.Longitude, w, h, centreLon);
            foreach (var xOffset in GetSubpointWrapOffsets(mx, w))
            {
                var x = mx + xOffset;
                var drewSymbol = m.SymbolTable is { } table && m.SymbolCode is { } code
                    && AprsSymbolAtlas.Draw(context, table, code, x, my, StationSymbolSize, m.IsFaded);
                if (!drewSymbol)
                    PlotMarkerDrawing.DrawHeardStationMarker(context, x, my, m.IsFaded, _renderCache);
                else if (m.IsFaded)
                {
                    context.DrawEllipse(
                        null,
                        _renderCache.GetDashedPen(Colors.White, 1.5),
                        new Rect(x - StationSymbolSize / 2, my - StationSymbolSize / 2, StationSymbolSize, StationSymbolSize));
                }
            }
        }

        _stationLabelCache.GetLabelBrush(palette);
        var background = _stationLabelCache.GetBackgroundBrush(palette);
        for (var i = markers.Count - 1; i >= 0; i--)
        {
            var m = markers[i];
            var text = _stationLabelCache.Get(m.Call, StationLabelFontSize, palette);
            var (mx, my) = EquirectangularProjection.GeoToPixel(m.Latitude, m.Longitude, w, h, centreLon);
            foreach (var xOffset in GetSubpointWrapOffsets(mx, w))
            {
                var labelGap = m.SymbolTable is not null && m.SymbolCode is not null ? 16 : 8;
                var bg = new Rect(mx + xOffset + labelGap, my - text.Height / 2 - 2, text.Width + 8, text.Height + 4);
                context.FillRectangle(background, bg);
                context.DrawText(text, new Point(bg.X + 4, bg.Y + 2));
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (IsZoomEnabled && HandleZoomKey(e))
        {
            e.Handled = true;
            return;
        }

        if (TrackStates is not { Count: > 0 })
            return;

        if (e.Key is Key.Enter or Key.Space)
        {
            FocusedNoradId ??= TrackStates[0].NoradId;
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Left or Key.Up)
        {
            FocusedNoradId = TrackingPlotAccessibility.CycleFocusedNoradId(
                TrackStates, FocusedNoradId, -1);
            e.Handled = true;
        }
        else if (e.Key is Key.Right or Key.Down)
        {
            FocusedNoradId = TrackingPlotAccessibility.CycleFocusedNoradId(
                TrackStates, FocusedNoradId, 1);
            e.Handled = true;
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var (vw, vh, ox, oy) = Viewport(bounds.Width, bounds.Height);
        using (context.PushClip(new Rect(bounds.Size)))
        using (context.PushTransform(Matrix.CreateTranslation(-ox, -oy)))
        {
            RenderMapContent(context, vw, vh, EffectiveCentreLongitude);
        }
    }

    private void RenderMapContent(DrawingContext context, double w, double h, double centreLon)
    {
        var palette = UiPaletteResolver.Current;
        EnsureMapLoaded();

        if (_mapBitmap is not null)
        {
            DrawBasemap(context, _mapBitmap, w, h, centreLon);
        }
        else
        {
            context.FillRectangle(new SolidColorBrush(palette.MapFallbackBackground), new Rect(0, 0, w, h));
            var noMap = new FormattedText(
                "world_map.jpg not found in Assets/Maps",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Inter"),
                14,
                new SolidColorBrush(palette.MapLabelForeground));
            context.DrawText(noMap, new Point(12, 12));
        }

        // Layer order: base map → greyline → tracks/footprints/markers → labels (footprints must stay above greyline).
        if (ShowGreylineOverlay)
            DrawGreylineOverlay(context, MapDisplayUtc, w, h, palette, centreLon);

        if (GroundStation is { } gs)
        {
            var (gx, gy) = EquirectangularProjection.GeoToPixel(
                gs.LatitudeDeg, gs.LongitudeDeg, w, h, centreLon);
            DrawGroundStationDot(context, gx, gy, palette);
        }

        if (RemoteStation is { } remote)
        {
            var (rx, ry) = EquirectangularProjection.GeoToPixel(
                remote.LatitudeDeg, remote.LongitudeDeg, w, h, centreLon);
            foreach (var xOffset in GetSubpointWrapOffsets(rx, w))
                PlotMarkerDrawing.DrawRemoteStationMarker(context, rx + xOffset, ry, _renderCache);
        }

        var states = TrackStates;
        if (states is null)
        {
            DrawStationMarkers(context, w, h, centreLon, palette);
            _lastRenderedSubpoints.Clear();
            if (ShowGreylineOverlay)
                _lastGreylineUtcMinuteTicks = FloorToUtcMinuteTicks(MapDisplayUtc);
            return;
        }

        // Evict stale footprint cache entries
        if (_footprintGeometryCache.Count > 0)
        {
            // Use reusable buffer instead of allocating new array every render frame
            var keyCount = _footprintGeometryCache.Count;
            if (_footprintCacheKeysBuffer.Length < keyCount)
                Array.Resize(ref _footprintCacheKeysBuffer, Math.Max(keyCount, _footprintCacheKeysBuffer.Length * 2));
            
            _footprintGeometryCache.Keys.CopyTo(_footprintCacheKeysBuffer, 0);
            
            for (var ki = 0; ki < keyCount; ki++)
            {
                var key = _footprintCacheKeysBuffer[ki];
                var found = false;
                for (var i = 0; i < states.Count; i++)
                {
                    if (states[i].NoradId == key)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    _footprintGeometryCache.Remove(key);
            }
            
            // Clear unused buffer slots to avoid retaining stale key references
            for (var ki = keyCount; ki < _footprintCacheKeysBuffer.Length; ki++)
                _footprintCacheKeysBuffer[ki] = null!;
        }

        // Evict stale ground track split cache entries
        if (_groundTrackSplitCache.Count > 0)
        {
            // Use reusable buffer instead of allocating new array every render frame
            var keyCount = _groundTrackSplitCache.Count;
            if (_groundTrackCacheKeysBuffer.Length < keyCount)
                Array.Resize(ref _groundTrackCacheKeysBuffer, Math.Max(keyCount, _groundTrackCacheKeysBuffer.Length * 2));
            
            _groundTrackSplitCache.Keys.CopyTo(_groundTrackCacheKeysBuffer, 0);
            
            for (var ki = 0; ki < keyCount; ki++)
            {
                var key = _groundTrackCacheKeysBuffer[ki];
                var found = false;
                for (var i = 0; i < states.Count; i++)
                {
                    if (states[i].NoradId == key)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    _groundTrackSplitCache.Remove(key);
            }
            
            // Clear unused buffer slots to avoid retaining stale key references
            for (var ki = keyCount; ki < _groundTrackCacheKeysBuffer.Length; ki++)
                _groundTrackCacheKeysBuffer[ki] = null!;
        }

        // Pass 1: tracks, footprints, and subpoints (no labels yet).
        // Sub-pass 1a: Next-orbit ground track for focused satellite (faded, behind current track)
        if (ShowMultiTrackOverlay)
        {
            for (var i = 0; i < states.Count; i++)
            {
                var state = states[i];
                if (state.NoradId != FocusedNoradId)
                    continue;
                if (state.NextOrbitGroundTrack.Count < 2)
                    continue;

                DrawCachedGroundTrack(
                    context,
                    state.NoradId + "_next",
                    state.NextOrbitGroundTrack,
                    w,
                    h,
                    centreLon,
                    palette.MapNextOrbitGroundTrackStroke,
                    1);
            }
        }

        // Sub-pass 1b: Focused ground track (on top), footprints, and subpoints.
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            if (!TrackingPlotAccessibility.IsPlotSatelliteVisible(SoloFocusedSatellite, FocusedNoradId, state.NoradId))
                continue;

            var color = PlotColors.ForIndex(state.PaletteIndex);
            var isFocused = state.NoradId == FocusedNoradId;

            if (isFocused)
            {
                DrawCachedGroundTrack(
                    context,
                    state.NoradId,
                    state.GroundTrack,
                    w,
                    h,
                    centreLon,
                    palette.MapGroundTrackStroke,
                    2,
                    palette.MapGroundTrackOutline);
            }

            var (sx, sy) = EquirectangularProjection.GeoToPixel(
                state.Subpoint.LatitudeDeg, state.Subpoint.LongitudeDeg, w, h, centreLon);

            if (state.Footprint.Count >= 3)
            {
                var fill = Color.FromArgb(72, color.R, color.G, color.B);
                var stroke = Color.FromArgb(200, color.R, color.G, color.B);
                DrawFootprint(
                    context,
                    state.NoradId,
                    state.Footprint,
                    state.Subpoint,
                    state.FootprintRadiusDeg,
                    w,
                    h,
                    centreLon,
                    fill,
                    stroke,
                    2);

                var heading = MotionArrowHeadingDeg(state);
                if (heading is { } headingDeg)
                {
                    foreach (var xOffset in GetSubpointWrapOffsets(sx, w))
                    {
                        PlotMarkerDrawing.DrawFootprintMotionArrow(
                            context,
                            state.Subpoint,
                            headingDeg,
                            state.FootprintRadiusDeg,
                            sx + xOffset,
                            sy,
                            w,
                            h,
                            centreLon,
                            color,
                            isFocused,
                            _renderCache);
                    }
                }
            }

            foreach (var xOffset in GetSubpointWrapOffsets(sx, w))
            {
                PlotMarkerDrawing.DrawSatelliteMarker(
                    context, sx + xOffset, sy, color, isFocused, _renderCache);
            }
        }

        DrawStationMarkers(context, w, h, centreLon, palette);

        // Pass 2: labels on top (non-focused first, focused last).
        // Use the same subpoint projection and map-wrap copies as the markers in pass 1.
        _labelOrderBuffer.Build(states, FocusedNoradId, SoloFocusedSatellite);

        foreach (var i in _labelOrderBuffer.Indices)
        {
            if ((uint)i >= (uint)states.Count)
                continue;

            var state = states[i];
            var isFocused = string.Equals(state.NoradId, FocusedNoradId, StringComparison.Ordinal);
            var (sx, sy) = EquirectangularProjection.GeoToPixel(
                state.Subpoint.LatitudeDeg, state.Subpoint.LongitudeDeg, w, h, centreLon);
            var labelBelow = IsNorthboundHeadingDeg(MotionArrowHeadingDeg(state));

            foreach (var xOffset in GetSubpointWrapOffsets(sx, w))
            {
                DrawSatelliteLabel(
                    context,
                    state.Name,
                    sx + xOffset,
                    sy,
                    palette,
                    isFocused ? 12 : 11,
                    labelBelow);
            }
        }

        _labelCache.Evict(states);

        // Update subpoint cache after a full paint so the 1 px throttle can skip quiet ticks.
        _lastRenderedSubpoints.Clear();
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            var (sx, sy) = EquirectangularProjection.GeoToPixel(
                state.Subpoint.LatitudeDeg, state.Subpoint.LongitudeDeg, w, h, centreLon);
            _lastRenderedSubpoints[state.NoradId] = (sx, sy);
        }

        if (ShowGreylineOverlay)
            _lastGreylineUtcMinuteTicks = FloorToUtcMinuteTicks(MapDisplayUtc);
    }

    private string? HitTestSatellite(Point pos, double w, double h)
    {
        var states = TrackStates;
        if (states is null || w <= 0 || h <= 0)
            return null;

        var centreLon = EffectiveCentreLongitude;
        var palette = UiPaletteResolver.Current;

        // Labels are painted on top, and the focused label is painted last.
        var focusedLabel = HitTestLabel(states, pos, w, h, centreLon, palette, focusedOnly: true);
        if (focusedLabel is not null)
            return focusedLabel;

        var label = HitTestLabel(states, pos, w, h, centreLon, palette, focusedOnly: false);
        if (label is not null)
            return label;

        var marker = HitTestMarker(states, pos, w, h, centreLon);
        if (marker is not null)
            return marker;

        var (clickLat, clickLon) = PixelToGeo(pos.X, pos.Y, w, h, centreLon);
        var footprintHits = new List<(string NoradId, double RadiusDeg, double AngularDistanceDeg)>();
        foreach (var state in states)
        {
            if (!TrackingPlotAccessibility.IsPlotSatelliteVisible(SoloFocusedSatellite, FocusedNoradId, state.NoradId))
                continue;

            if (state.Footprint.Count < 3)
                continue;

            var radiusDeg = state.FootprintRadiusDeg > 0
                ? state.FootprintRadiusDeg
                : FootprintGeometry.EstimateRingRadiusDeg(state.Subpoint, state.Footprint);
            if (radiusDeg <= 0)
                continue;

            var angular = SphericalGeo.AngularDistanceDeg(
                clickLat,
                clickLon,
                state.Subpoint.LatitudeDeg,
                state.Subpoint.LongitudeDeg);
            footprintHits.Add((state.NoradId, radiusDeg, angular));
        }

        return ChooseSmallestFootprintHit(footprintHits);
    }

    private string? HitTestLabel(
        IReadOnlyList<SatelliteTrackState> states,
        Point pos,
        double w,
        double h,
        double centreLon,
        UiPalette palette,
        bool focusedOnly)
    {
        string? bestId = null;
        var bestDist = double.MaxValue;

        foreach (var state in states)
        {
            var isFocused = string.Equals(state.NoradId, FocusedNoradId, StringComparison.Ordinal);
            if (focusedOnly)
            {
                if (!isFocused)
                    continue;
            }
            else if (isFocused)
            {
                continue;
            }

            if (!TrackingPlotAccessibility.IsPlotSatelliteVisible(SoloFocusedSatellite, FocusedNoradId, state.NoradId))
                continue;

            if (string.IsNullOrEmpty(state.Name))
                continue;

            var text = _labelCache.Get(state.Name, isFocused ? 12 : 11, palette);
            var (sx, sy) = EquirectangularProjection.GeoToPixel(
                state.Subpoint.LatitudeDeg, state.Subpoint.LongitudeDeg, w, h, centreLon);
            var labelBelow = IsNorthboundHeadingDeg(MotionArrowHeadingDeg(state));

            foreach (var xOffset in GetSubpointWrapOffsets(sx, w))
            {
                var bounds = SatelliteLabelBounds(sx + xOffset, sy, text.Width, text.Height, labelBelow);
                if (!bounds.Contains(pos))
                    continue;

                var dx = pos.X - bounds.Center.X;
                var dy = pos.Y - bounds.Center.Y;
                var dist = dx * dx + dy * dy;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestId = state.NoradId;
                }
            }
        }

        return bestId;
    }

    private string? HitTestMarker(
        IReadOnlyList<SatelliteTrackState> states,
        Point pos,
        double w,
        double h,
        double centreLon)
    {
        string? bestId = null;
        var bestDist = double.MaxValue;

        foreach (var state in states)
        {
            if (!TrackingPlotAccessibility.IsPlotSatelliteVisible(SoloFocusedSatellite, FocusedNoradId, state.NoradId))
                continue;

            var (sx, sy) = EquirectangularProjection.GeoToPixel(
                state.Subpoint.LatitudeDeg, state.Subpoint.LongitudeDeg, w, h, centreLon);

            foreach (var xOffset in GetSubpointWrapOffsets(sx, w))
            {
                var dx = sx + xOffset;
                if (dx < -WrapEdgeMarginPx || dx > w + WrapEdgeMarginPx)
                    continue;

                var dist = Math.Sqrt(Math.Pow(pos.X - dx, 2) + Math.Pow(pos.Y - sy, 2));
                if (dist <= HitRadiusPx && dist < bestDist)
                {
                    bestDist = dist;
                    bestId = state.NoradId;
                }
            }
        }

        return bestId;
    }

    /// <summary>
    /// Heading used for the footprint motion arrow, or null when no arrow is drawn for this
    /// satellite. Shared by the arrow, the label placement and label hit-testing so they agree.
    /// </summary>
    private double? MotionArrowHeadingDeg(SatelliteTrackState state)
    {
        if (!ShowFootprintMotionArrows || state.Footprint.Count < 3)
            return null;

        return state.MotionHeadingDeg
            ?? GroundTrackHeading.EstimateHeadingDeg(state.Subpoint, state.GroundTrack);
    }

    /// <summary>
    /// True when a heading points into the top half of the screen (any northward component), so a
    /// name drawn above the subpoint would cover the motion arrow and should go below instead.
    /// Expects a heading in [0, 360) degrees clockwise from north, as produced by
    /// <see cref="SphericalGeo.InitialBearingDeg"/>.
    /// </summary>
    internal static bool IsNorthboundHeadingDeg(double? headingDeg) =>
        headingDeg is { } heading && (heading < 90.0 || heading > 270.0);

    /// <summary>
    /// Screen rectangle of the name drawn at a subpoint, including its padding. The label sits
    /// above the subpoint, or below it when <paramref name="below"/> is set (northbound satellites).
    /// </summary>
    internal static Rect SatelliteLabelBounds(
        double anchorX, double anchorY, double textWidth, double textHeight, bool below = false)
    {
        var tx = anchorX - textWidth / 2;
        var ty = below ? anchorY + 8 : anchorY - textHeight - 8;
        return new Rect(tx - 4, ty - 2, textWidth + 8, textHeight + 4);
    }

    internal static (double LatitudeDeg, double LongitudeDeg) PixelToGeo(
        double x,
        double y,
        double width,
        double height,
        double centerLongitudeDeg)
    {
        var lon = x / width * 360.0 - 180.0 + centerLongitudeDeg;
        var lat = 90.0 - y / height * 180.0;
        return (Math.Clamp(lat, -90, 90), lon);
    }

    /// <summary>
    /// When several footprints contain the click, the smallest one is the satellite the operator aimed at.
    /// </summary>
    internal static string? ChooseSmallestFootprintHit(
        IReadOnlyList<(string NoradId, double RadiusDeg, double AngularDistanceDeg)> hits)
    {
        string? bestId = null;
        var bestRadius = double.MaxValue;
        var bestDist = double.MaxValue;

        foreach (var hit in hits)
        {
            if (hit.RadiusDeg <= 0 || hit.AngularDistanceDeg > hit.RadiusDeg)
                continue;

            var closerRadius = hit.RadiusDeg < bestRadius - 1e-6;
            var sameRadiusCloser = Math.Abs(hit.RadiusDeg - bestRadius) <= 1e-6
                && hit.AngularDistanceDeg < bestDist;
            if (!closerRadius && !sameRadiusCloser)
                continue;

            bestId = hit.NoradId;
            bestRadius = hit.RadiusDeg;
            bestDist = hit.AngularDistanceDeg;
        }

        return bestId;
    }

    private void EnsureMapLoaded()
    {
        if (_mapBitmap is not null)
            return;

        try
        {
            var uri = new Uri("avares://OscarWatch/Assets/Maps/world_map.jpg");
            using var stream = AssetLoader.Open(uri);
            _mapBitmap = Bitmap.DecodeToWidth(stream, MapDecodeMaxWidth);
        }
        catch
        {
            // map asset missing
        }
    }

    /// <summary>
    /// Draws the equirectangular basemap, optionally scrolled so
    /// <paramref name="centerLongitudeDeg"/> sits at mid-width.
    /// Uses a source-split (not two full overlapping copies) so the wrap join
    /// does not leave a hairline vertical seam.
    /// </summary>
    private static void DrawBasemap(
        DrawingContext context,
        Bitmap mapBitmap,
        double w,
        double h,
        double centerLongitudeDeg)
    {
        var srcW = (double)mapBitmap.PixelSize.Width;
        var srcH = (double)mapBitmap.PixelSize.Height;
        var fullSrc = new Rect(0, 0, srcW, srcH);
        var offset = EquirectangularProjection.BasemapScrollOffsetPx(centerLongitudeDeg, w);

        if (offset <= 0.5 || offset >= w - 0.5)
        {
            context.DrawImage(mapBitmap, fullSrc, new Rect(0, 0, w, h));
            return;
        }

        // Snap the join to whole destination pixels; left + right widths must equal w.
        var rightDestW = Math.Clamp(Math.Round(offset), 1, w - 1);
        var leftDestW = w - rightDestW;
        var splitSrcX = rightDestW / w * srcW;

        // Left of viewport: source from centre seam → right edge of image
        context.DrawImage(
            mapBitmap,
            new Rect(splitSrcX, 0, srcW - splitSrcX, srcH),
            new Rect(0, 0, leftDestW, h));
        // Right of viewport: source from left edge → centre seam
        context.DrawImage(
            mapBitmap,
            new Rect(0, 0, splitSrcX, srcH),
            new Rect(leftDestW, 0, rightDestW, h));
    }

    private static (double MinX, double MaxX) GetPixelXRange(
        IReadOnlyList<GeoCoordinate> points,
        double w,
        double h,
        double centerLongitudeDeg)
    {
        var minX = double.MaxValue;
        var maxX = double.MinValue;

        foreach (var p in points)
        {
            var (x, _) = EquirectangularProjection.GeoToPixel(
                p.LatitudeDeg, p.LongitudeDeg, w, h, centerLongitudeDeg);
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
        }

        return (minX, maxX);
    }

    /// <summary>
    /// Duplicate geometry only when the path crosses the antimeridian, not when it merely spans
    /// wide longitude near a pole (which already fills the map width in pixel space).
    /// </summary>
    private static IEnumerable<double> GetHorizontalWrapOffsets(
        IReadOnlyList<GeoCoordinate> points,
        double w,
        double h,
        double centerLongitudeDeg)
    {
        yield return 0;

        if (!EquirectangularProjection.CrossesAntimeridian(points))
            yield break;

        var (minX, maxX) = GetPixelXRange(points, w, h, centerLongitudeDeg);
        if (minX < WrapEdgeMarginPx)
            yield return w;
        if (maxX > w - WrapEdgeMarginPx)
            yield return -w;
    }

    /// <summary>
    /// Footprints are projected in a local plane at the subpoint; duplicate when the subpoint
    /// or projected ring reaches the map edge, but not for polar caps that already span the width.
    /// </summary>
    private static IEnumerable<double> GetFootprintWrapOffsets(
        GeoCoordinate subpoint,
        IReadOnlyList<GeoCoordinate> footprintRing,
        IReadOnlyList<(double X, double Y)> pixels,
        double footprintRadiusDeg,
        double w,
        double h,
        double centerLongitudeDeg)
    {
        var minX = double.MaxValue;
        var maxX = double.MinValue;
        foreach (var p in pixels)
        {
            minX = Math.Min(minX, p.X);
            maxX = Math.Max(maxX, p.X);
        }

        var span = maxX - minX;
        var isPolarCap = FootprintGeometry.ContainsNorthPole(subpoint, footprintRadiusDeg)
            || FootprintGeometry.ContainsSouthPole(subpoint, footprintRadiusDeg);

        if (isPolarCap || span > w * 0.75)
        {
            yield return 0;
            yield break;
        }

        var offsets = new HashSet<double> { 0 };

        var (sx, _) = EquirectangularProjection.GeoToPixel(
            subpoint.LatitudeDeg, subpoint.LongitudeDeg, w, h, centerLongitudeDeg);
        foreach (var offset in GetSubpointWrapOffsets(sx, w))
            offsets.Add(offset);

        var allowPixelWrap = span < w * 0.85
            || EquirectangularProjection.CrossesAntimeridian(footprintRing);

        if (allowPixelWrap)
        {
            if (minX < WrapEdgeMarginPx)
                offsets.Add(w);
            if (maxX > w - WrapEdgeMarginPx)
                offsets.Add(-w);
        }

        foreach (var offset in offsets)
            yield return offset;
    }

    /// <summary>
    /// Subpoints and labels use a single lon→x projection; near the map edges duplicate them
    /// with the same ±width offsets as footprints so they stay aligned with the visible wrap.
    /// </summary>
    private static IEnumerable<double> GetSubpointWrapOffsets(double sx, double w)
    {
        yield return 0;

        if (sx < WrapEdgeMarginPx)
            yield return w;

        if (sx > w - WrapEdgeMarginPx)
            yield return -w;
    }

    /// <summary>
    /// Draw the visibility footprint as a geographic ring (equirectangular), with a dedicated
    /// polar-cap shape when the footprint includes a pole.
    /// </summary>
    private void DrawFootprint(
        DrawingContext context,
        string noradId,
        IReadOnlyList<GeoCoordinate> footprint,
        GeoCoordinate subpoint,
        double footprintRadiusDeg,
        double w,
        double h,
        double centerLongitudeDeg,
        Color fillColor,
        Color strokeColor,
        double strokeWidth)
    {
        if (footprint.Count < 3)
            return;

        if (w <= 0 || h <= 0)
            return;

        var radiusDeg = footprintRadiusDeg > 0
            ? footprintRadiusDeg
            : FootprintGeometry.EstimateRingRadiusDeg(subpoint, footprint);

        var fillBrush = _renderCache.GetBrush(fillColor);
        var pen = _renderCache.GetPen(strokeColor, strokeWidth);

        // Check footprint geometry cache
        if (_footprintGeometryCache.TryGetValue(noradId, out var entry)
            && ReferenceEquals(entry.SourceFootprint, footprint)
            && entry.Width == w
            && entry.Height == h
            && entry.CenterLongitudeDeg == centerLongitudeDeg)
        {
            // Cache hit: reuse cached geometries
            for (var i = 0; i < entry.CachedGeometries.Count; i++)
            {
                var (geometry, xOffset) = entry.CachedGeometries[i];
                context.DrawGeometry(fillBrush, pen, geometry);
            }

            return;
        }

        // Cache miss: compute pixels and build geometry
        var pixels = FootprintGeometry.ProjectRingToMap(
            subpoint, footprint, radiusDeg, w, h, centerLongitudeDeg);
        if (pixels.Count < 3)
            return;

        var newEntry = new FootprintGeometryEntry
        {
            SourceFootprint = footprint,
            Width = w,
            Height = h,
            CenterLongitudeDeg = centerLongitudeDeg
        };
        newEntry.CachedGeometries.Clear();

        foreach (var xOffset in GetFootprintWrapOffsets(
                     subpoint, footprint, pixels, radiusDeg, w, h, centerLongitudeDeg))
        {
            var geometry = BuildFootprintGeometry(pixels, xOffset);
            newEntry.CachedGeometries.Add((geometry, xOffset));
            context.DrawGeometry(fillBrush, pen, geometry);
        }

        _footprintGeometryCache[noradId] = newEntry;
    }

    private static StreamGeometry BuildFootprintGeometry(
        IReadOnlyList<(double X, double Y)> pixels,
        double xOffset)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.SetFillRule(FillRule.NonZero);
            var first = pixels[0];
            g.BeginFigure(new Point(first.X + xOffset, first.Y), true);
            for (var i = 1; i < pixels.Count; i++)
            {
                var p = pixels[i];
                g.LineTo(new Point(p.X + xOffset, p.Y));
            }

            g.EndFigure(true);
        }

        return geometry;
    }

    private void DrawGreylineOverlay(
        DrawingContext context,
        DateTime mapUtc,
        double w,
        double h,
        UiPalette palette,
        double centerLongitudeDeg)
    {
        if (w <= 0 || h <= 0)
            return;

        var geometry = DayNightTerminator.GetGeometry(
            DateTime.SpecifyKind(mapUtc, DateTimeKind.Utc));

        var fillBrush = _renderCache.GetBrush(palette.GreylineNightFill);
        DrawNightFillScanlines(context, geometry, w, h, fillBrush, centerLongitudeDeg);

        if (geometry.DrawTerminatorLine && geometry.Terminator.Count >= 2)
        {
            DrawPolylineSegments(
                context,
                geometry.Terminator,
                w,
                h,
                centerLongitudeDeg,
                palette.GreylineTerminatorStroke,
                1.0);
        }
    }

    /// <summary>
    /// Shade the night hemisphere column-by-column. Avoids polygon fill on the full-world
    /// equirectangular map, which breaks apart under antimeridian splitting.
    /// </summary>
    private void DrawNightFillScanlines(
        DrawingContext context,
        DayNightGeometry geometry,
        double w,
        double h,
        IBrush fillBrush,
        double centerLongitudeDeg)
    {
        if (geometry.FullNightHalf)
        {
            var y0 = geometry.NightTowardSouth ? h * 0.5 : 0;
            var y1 = geometry.NightTowardSouth ? h : h * 0.5;
            context.FillRectangle(fillBrush, new Rect(0, y0, w, y1 - y0));
            return;
        }

        if (geometry.Terminator.Count < 2)
            return;

        var lonStep = DayNightTerminator.LongitudeStepDeg;
        var columnCount = (int)Math.Ceiling(360.0 / lonStep);
        var stripWidth = w / Math.Max(1, columnCount - 1) + 1.5;
        const double twilightFadePx = 28;
        const int twilightBands = 5;

        var twilightBrushes = fillBrush is SolidColorBrush solid
            ? GetTwilightBrushes(solid, twilightBands)
            : ImmutableArray<SolidColorBrush>.Empty;

        for (var i = 0; i < columnCount; i++)
        {
            var lon = -180.0 + i * (360.0 / (columnCount - 1));
            var termLat = InterpolateTerminatorLatitude(geometry.Terminator, lon);
            var (x, yTerm) = EquirectangularProjection.GeoToPixel(
                termLat, lon, w, h, centerLongitudeDeg);
            // Keep columns in the viewport when the map is recentred.
            x = ((x % w) + w) % w;

            if (geometry.NightTowardSouth)
                DrawNightColumnSouth(context, fillBrush, x, yTerm, h, w, stripWidth, twilightFadePx, twilightBrushes);
            else
                DrawNightColumnNorth(context, fillBrush, x, yTerm, h, w, stripWidth, twilightFadePx, twilightBrushes);
        }
    }

    private ImmutableArray<SolidColorBrush> GetTwilightBrushes(SolidColorBrush baseBrush, int twilightBands)
    {
        if (_twilightBrushes.Length == twilightBands
            && _cachedTwilightBaseColor == baseBrush.Color
            && _cachedTwilightBandCount == twilightBands)
            return _twilightBrushes;

        var brushes = new SolidColorBrush[twilightBands];
        for (var band = 0; band < twilightBands; band++)
        {
            var t = (band + 0.5) / twilightBands;
            var alpha = (byte)(baseBrush.Color.A * (0.15 + 0.35 * t));
            brushes[band] = new SolidColorBrush(
                Color.FromArgb(alpha,
                    baseBrush.Color.R,
                    baseBrush.Color.G,
                    baseBrush.Color.B));
        }

        _cachedTwilightBaseColor = baseBrush.Color;
        _cachedTwilightBandCount = twilightBands;
        _twilightBrushes = ImmutableArray.Create(brushes);
        return _twilightBrushes;
    }

    private static void DrawNightColumnSouth(
        DrawingContext context,
        IBrush baseBrush,
        double x,
        double yTerm,
        double h,
        double w,
        double stripWidth,
        double twilightFadePx,
        ImmutableArray<SolidColorBrush> twilightBrushes)
    {
        var twilightBands = twilightBrushes.Length;
        var bodyStart = Math.Min(h, yTerm + twilightFadePx);
        if (h - bodyStart >= 0.5)
            DrawNightStrip(context, baseBrush, x, bodyStart, h, w, stripWidth);

        if (twilightBands == 0)
            return;

        var bandHeight = twilightFadePx / twilightBands;
        for (var band = 0; band < twilightBands; band++)
        {
            var y0 = yTerm + band * bandHeight;
            var y1 = yTerm + (band + 1) * bandHeight;
            DrawNightStrip(context, twilightBrushes[band], x, y0, y1, w, stripWidth);
        }
    }

    private static void DrawNightColumnNorth(
        DrawingContext context,
        IBrush baseBrush,
        double x,
        double yTerm,
        double h,
        double w,
        double stripWidth,
        double twilightFadePx,
        ImmutableArray<SolidColorBrush> twilightBrushes)
    {
        var twilightBands = twilightBrushes.Length;
        var bodyEnd = Math.Max(0, yTerm - twilightFadePx);
        if (bodyEnd >= 0.5)
            DrawNightStrip(context, baseBrush, x, 0, bodyEnd, w, stripWidth);

        if (twilightBands == 0)
            return;

        var bandHeight = twilightFadePx / twilightBands;
        for (var band = 0; band < twilightBands; band++)
        {
            var y1 = yTerm - band * bandHeight;
            var y0 = yTerm - (band + 1) * bandHeight;
            DrawNightStrip(context, twilightBrushes[band], x, y0, y1, w, stripWidth);
        }
    }

    private static void DrawNightStrip(
        DrawingContext context,
        IBrush brush,
        double x,
        double yStart,
        double yEnd,
        double w,
        double stripWidth)
    {
        if (yEnd - yStart < 0.5)
            return;

        var rect = new Rect(x - stripWidth * 0.5, yStart, stripWidth, yEnd - yStart);
        context.FillRectangle(brush, rect);

        if (x < WrapEdgeMarginPx)
            context.FillRectangle(brush, new Rect(rect.X + w, rect.Y, rect.Width, rect.Height));
        if (x > w - WrapEdgeMarginPx)
            context.FillRectangle(brush, new Rect(rect.X - w, rect.Y, rect.Width, rect.Height));
    }

    private static double InterpolateTerminatorLatitude(
        IReadOnlyList<GeoCoordinate> terminator,
        double longitudeDeg)
    {
        if (terminator.Count == 0)
            return 0;

        var lo = 0;
        var hi = terminator.Count - 1;

        // Early-out: outside the stored longitude range.
        if (longitudeDeg <= terminator[lo].LongitudeDeg)
            return terminator[lo].LatitudeDeg;
        if (longitudeDeg >= terminator[hi].LongitudeDeg)
            return terminator[hi].LatitudeDeg;

        // Binary search for bracketing pair.
        while (hi - lo > 1)
        {
            var mid = (lo + hi) >> 1;
            if (terminator[mid].LongitudeDeg <= longitudeDeg)
                lo = mid;
            else
                hi = mid;
        }

        var before = terminator[lo];
        var after = terminator[hi];
        if (Math.Abs(after.LongitudeDeg - before.LongitudeDeg) < 0.01)
            return before.LatitudeDeg;

        var t = (longitudeDeg - before.LongitudeDeg)
              / (after.LongitudeDeg - before.LongitudeDeg);
        return before.LatitudeDeg + t * (after.LatitudeDeg - before.LatitudeDeg);
    }

    private void DrawPolylineSegments(
        DrawingContext context,
        IReadOnlyList<GeoCoordinate> points,
        double w,
        double h,
        double centerLongitudeDeg,
        Color color,
        double thickness,
        bool close = false)
    {
        if (points.Count < 2)
            return;

        var pen = _renderCache.GetPen(color, thickness);

        foreach (var xOffset in GetHorizontalWrapOffsets(points, w, h, centerLongitudeDeg))
            DrawPolylineOffset(context, points, w, h, centerLongitudeDeg, xOffset, pen, close);
    }

    private static void DrawPolylineOffset(
        DrawingContext context,
        IReadOnlyList<GeoCoordinate> points,
        double w,
        double h,
        double centerLongitudeDeg,
        double xOffset,
        Pen pen,
        bool close)
    {
        var maxDx = w / 2.0;
        var maxDy = h / 3.0;

        foreach (var chain in EquirectangularProjection.SplitForMapDraw(
                     points, w, h, centerLongitudeDeg))
        {
            if (chain.Count < 2)
                continue;

            for (var i = 0; i < chain.Count - 1; i++)
            {
                var p0 = chain[i];
                var p1 = chain[i + 1];
                context.DrawLine(
                    pen,
                    new Point(p0.X + xOffset, p0.Y),
                    new Point(p1.X + xOffset, p1.Y));
            }

            if (close && chain.Count >= 2)
            {
                var first = chain[0];
                var last = chain[^1];
                if (Math.Abs(first.X - last.X) <= maxDx && Math.Abs(first.Y - last.Y) <= maxDy)
                {
                    context.DrawLine(
                        pen,
                        new Point(last.X + xOffset, last.Y),
                        new Point(first.X + xOffset, first.Y));
                }
            }
        }
    }

    private void DrawCachedGroundTrack(
        DrawingContext context,
        string noradId,
        IReadOnlyList<GeoCoordinate> track,
        double w,
        double h,
        double centerLongitudeDeg,
        Color color,
        double thickness,
        Color? outlineColor = null)
    {
        if (track.Count < 2)
            return;

        var splitResult = GetOrComputeGroundTrackSplit(noradId, track, w, h, centerLongitudeDeg);
        var strokePen = _renderCache.GetPen(color, thickness);
        var outlinePen = outlineColor is { } outline
            ? _renderCache.GetPen(outline, thickness + 2)
            : null;

        foreach (var chain in splitResult)
        {
            if (chain.Count < 2)
                continue;

            // Draw the chain at all wrap offsets where any segment is visible.
            // ProjectGroundTrackForDraw uses unwrapped longitude, so a single chain
            // can extend well beyond [0, w]. Drawing at multiple offsets ensures the
            // full track wraps correctly on the map.
            foreach (var xOffset in GetGroundTrackChainWrapOffsets(chain, w))
            {
                for (var i = 0; i < chain.Count - 1; i++)
                {
                    var p0 = chain[i];
                    var p1 = chain[i + 1];

                    // Skip segments entirely outside the viewport for this offset
                    var x0 = p0.X + xOffset;
                    var x1 = p1.X + xOffset;
                    if ((x0 < -WrapEdgeMarginPx && x1 < -WrapEdgeMarginPx) ||
                        (x0 > w + WrapEdgeMarginPx && x1 > w + WrapEdgeMarginPx))
                        continue;

                    var start = new Point(x0, p0.Y);
                    var end = new Point(x1, p1.Y);
                    if (outlinePen is not null)
                        context.DrawLine(outlinePen, start, end);
                    context.DrawLine(strokePen, start, end);
                }
            }
        }
    }

    /// <summary>
    /// Returns the wrap offsets at which a ground-track chain has visible segments.
    /// Since ProjectGroundTrackForDraw uses unwrapped longitude, the chain's X extent
    /// may span well beyond [0, w]. We draw at each offset where the chain overlaps the viewport.
    /// </summary>
    private static IEnumerable<double> GetGroundTrackChainWrapOffsets(
        IReadOnlyList<(double X, double Y)> chain,
        double w)
    {
        var minX = double.MaxValue;
        var maxX = double.MinValue;
        foreach (var p in chain)
        {
            minX = Math.Min(minX, p.X);
            maxX = Math.Max(maxX, p.X);
        }

        // Check each offset to see if the chain's X range overlaps the viewport [0, w]
        foreach (var offset in new[] { 0.0, w, -w })
        {
            var shiftedMin = minX + offset;
            var shiftedMax = maxX + offset;

            // Chain overlaps viewport if shiftedMax > 0 and shiftedMin < w
            if (shiftedMax > -WrapEdgeMarginPx && shiftedMin < w + WrapEdgeMarginPx)
                yield return offset;
        }
    }

    /// <summary>
    /// Picks one horizontal wrap for a ground-track chain. Avoids duplicate edge stubs from
    /// drawing the same chain at 0 and ±width.
    /// </summary>
    internal static double? SelectGroundTrackWrapOffset(
        IReadOnlyList<(double X, double Y)> chain,
        double w)
    {
        if (chain.Count < 2)
            return null;

        var minVisibleSpanPx = Math.Max(WrapEdgeMarginPx * 2, w * 0.08);
        double? bestOffset = null;
        var bestVisibleSpan = 0.0;

        foreach (var offset in new[] { 0.0, w, -w })
        {
            var visibleSpan = VisibleHorizontalSpan(chain, offset, w);
            if (visibleSpan < minVisibleSpanPx)
                continue;

            if (visibleSpan > bestVisibleSpan)
            {
                bestVisibleSpan = visibleSpan;
                bestOffset = offset;
            }
        }

        return bestOffset ?? 0.0;
    }

    private static double VisibleHorizontalSpan(
        IReadOnlyList<(double X, double Y)> chain,
        double xOffset,
        double w)
    {
        var minX = double.MaxValue;
        var maxX = double.MinValue;
        foreach (var p in chain)
        {
            var x = p.X + xOffset;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
        }

        var left = Math.Max(0, minX);
        var right = Math.Min(w, maxX);
        return Math.Max(0, right - left);
    }

    private IReadOnlyList<IReadOnlyList<(double X, double Y)>> GetOrComputeGroundTrackSplit(
        string noradId,
        IReadOnlyList<GeoCoordinate> track,
        double w,
        double h,
        double centerLongitudeDeg)
    {
        if (_groundTrackSplitCache.TryGetValue(noradId, out var entry)
            && ReferenceEquals(entry.SourceTrack, track)
            && entry.Width == w
            && entry.Height == h
            && entry.CenterLongitudeDeg == centerLongitudeDeg)
        {
            return entry.SplitResult;
        }

        var splitResult = EquirectangularProjection.ProjectGroundTrackForDraw(
            track, w, h, centerLongitudeDeg);

        _groundTrackSplitCache[noradId] = new GroundTrackSplitEntry
        {
            SourceTrack = track,
            Width = w,
            Height = h,
            CenterLongitudeDeg = centerLongitudeDeg,
            SplitResult = splitResult
        };

        return splitResult;
    }

    private void DrawGroundStationDot(DrawingContext context, double x, double y, UiPalette palette)
    {
        PlotMarkerDrawing.DrawGroundStationMarker(context, x, y, palette, _renderCache);
    }

    private void DrawSatelliteLabel(
        DrawingContext context,
        string name,
        double x,
        double y,
        UiPalette palette,
        double fontSize = 12,
        bool below = false)
    {
        var text = _labelCache.Get(name, fontSize, palette);
        var bg = SatelliteLabelBounds(x, y, text.Width, text.Height, below);
        context.FillRectangle(_labelCache.GetBackgroundBrush(palette), bg);
        context.DrawText(text, new Point(bg.X + 4, bg.Y + 2));
    }

    private sealed class FootprintGeometryEntry
    {
        public IReadOnlyList<GeoCoordinate> SourceFootprint = [];
        public double Width;
        public double Height;
        public double CenterLongitudeDeg;
        public List<(StreamGeometry Geometry, double XOffset)> CachedGeometries = new();
    }

    private sealed class GroundTrackSplitEntry
    {
        public IReadOnlyList<GeoCoordinate> SourceTrack = [];
        public double Width;
        public double Height;
        public double CenterLongitudeDeg;
        public IReadOnlyList<IReadOnlyList<(double X, double Y)>> SplitResult = [];
    }
}
