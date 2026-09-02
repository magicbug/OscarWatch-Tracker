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
using OscarWatch.Localization;

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

    private Bitmap? _mapBitmap;
    private INotifyCollectionChanged? _trackStatesSource;
    private Size _lastLayoutInvalidationSize;

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
        AffectsRender<WorldMapControl>(
            TrackStatesProperty,
            GroundStationProperty,
            FocusedNoradIdProperty,
            ShowFootprintMotionArrowsProperty,
            RemoteStationProperty,
            SoloFocusedSatelliteProperty,
            ShowGreylineOverlayProperty,
            ShowMultiTrackOverlayProperty,
            MapDisplayUtcProperty,
            MapCentreLongitudeProperty);
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
            BindTrackStatesSource(change.NewValue);

        if (change.Property == MapCentreLongitudeProperty)
        {
            _footprintGeometryCache.Clear();
            _groundTrackSplitCache.Clear();
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
            InvalidateVisual();
    }

    private void BindTrackStatesSource(object? value)
    {
        UnsubscribeTrackStatesSource();
        _trackStatesSource = value as INotifyCollectionChanged;
        if (_trackStatesSource is not null)
            _trackStatesSource.CollectionChanged += OnTrackStatesSourceChanged;

        _renderCache.Clear();
        _labelCache.Clear();
        InvalidateVisual();
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
        _renderCache.Clear();
        _labelCache.Clear();
        InvalidateVisual();
    }

    private void OnThemeChangedClearCache(object? sender, EventArgs e)
    {
        _renderCache.Clear();
        _labelCache.Clear();
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
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pos = e.GetPosition(this);
        FocusedNoradId = HitTestSatellite(pos, Bounds.Width, Bounds.Height);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
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

        using (context.PushClip(new Rect(bounds.Size)))
        {
            RenderMapContent(context, bounds.Width, bounds.Height);
        }
    }

    private void RenderMapContent(DrawingContext context, double w, double h)
    {
        var palette = UiPaletteResolver.Current;
        var centreLon = MapCentreLongitude;
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
            return;

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

            var color = PlotColors.ForIndex(i);
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

                var heading = ShowFootprintMotionArrows
                    ? state.MotionHeadingDeg
                      ?? GroundTrackHeading.EstimateHeadingDeg(state.Subpoint, state.GroundTrack)
                    : null;
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

            foreach (var xOffset in GetSubpointWrapOffsets(sx, w))
            {
                DrawSatelliteLabel(
                    context,
                    state.Name,
                    sx + xOffset,
                    sy,
                    palette,
                    isFocused ? 12 : 11);
            }
        }

        _labelCache.Evict(states);
    }

    private string? HitTestSatellite(Point pos, double w, double h)
    {
        var states = TrackStates;
        if (states is null)
            return null;

        var centreLon = MapCentreLongitude;
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
        double fontSize = 12)
    {
        var text = _labelCache.Get(name, fontSize, palette);

        var tx = x - text.Width / 2;
        var ty = y - text.Height - 8;
        var bg = new Rect(tx - 4, ty - 2, text.Width + 8, text.Height + 4);
        context.FillRectangle(_labelCache.GetBackgroundBrush(palette), bg);
        context.DrawText(text, new Point(tx, ty));
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
