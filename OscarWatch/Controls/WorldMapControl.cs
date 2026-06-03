using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using OscarWatch.Core.Geo;
using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;

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

    public static readonly StyledProperty<bool> ShowGraylineProperty =
        AvaloniaProperty.Register<WorldMapControl, bool>(nameof(ShowGrayline), true);

    private Bitmap? _mapBitmap;
    private INotifyCollectionChanged? _trackStatesSource;
    private Size _lastLayoutInvalidationSize;

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
            ShowGraylineProperty);
    }

    public bool ShowFootprintMotionArrows
    {
        get => GetValue(ShowFootprintMotionArrowsProperty);
        set => SetValue(ShowFootprintMotionArrowsProperty, value);
    }

    public bool ShowGrayline
    {
        get => GetValue(ShowGraylineProperty);
        set => SetValue(ShowGraylineProperty, value);
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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LayoutUpdated += OnLayoutUpdatedForRender;
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdatedForRender;
        UnsubscribeTrackStatesSource();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TrackStatesProperty)
            BindTrackStatesSource(change.NewValue);

        if (change.Property == TrackStatesProperty || change.Property == FocusedNoradIdProperty)
            TrackingPlotAccessibility.UpdateName(this, "World map", TrackStates, FocusedNoradId);
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

        InvalidateVisual();
    }

    private void UnsubscribeTrackStatesSource()
    {
        if (_trackStatesSource is null)
            return;

        _trackStatesSource.CollectionChanged -= OnTrackStatesSourceChanged;
        _trackStatesSource = null;
    }

    private void OnTrackStatesSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        InvalidateVisual();

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
        EnsureMapLoaded();

        if (_mapBitmap is not null)
        {
            var src = new Rect(0, 0, _mapBitmap.PixelSize.Width, _mapBitmap.PixelSize.Height);
            context.DrawImage(_mapBitmap, src, new Rect(0, 0, w, h));
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

        if (ShowGrayline)
            RenderGrayline(context, w, h);

        if (GroundStation is { } gs)
        {
            var (gx, gy) = EquirectangularProjection.GeoToPixel(gs.LatitudeDeg, gs.LongitudeDeg, w, h);
            DrawGroundStationDot(context, gx, gy, palette);
        }

        if (RemoteStation is { } remote)
        {
            var (rx, ry) = EquirectangularProjection.GeoToPixel(remote.LatitudeDeg, remote.LongitudeDeg, w, h);
            foreach (var xOffset in GetSubpointWrapOffsets(rx, w))
                PlotMarkerDrawing.DrawRemoteStationMarker(context, rx + xOffset, ry);
        }

        var states = TrackStates;
        if (states is null)
            return;

        // Pass 1: tracks, footprints, and subpoints (no labels yet).
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            if (!TrackingPlotAccessibility.IsPlotSatelliteVisible(SoloFocusedSatellite, FocusedNoradId, state.NoradId))
                continue;

            var color = PlotColors.ForIndex(i);
            var isFocused = state.NoradId == FocusedNoradId;

            if (isFocused)
                DrawPolylineSegments(context, state.GroundTrack, w, h, color, 2);

            var (sx, sy) = EquirectangularProjection.GeoToPixel(
                state.Subpoint.LatitudeDeg, state.Subpoint.LongitudeDeg, w, h);

            if (state.Footprint.Count >= 3)
            {
                var fill = Color.FromArgb(72, color.R, color.G, color.B);
                var stroke = Color.FromArgb(200, color.R, color.G, color.B);
                DrawFootprint(
                    context,
                    state.Footprint,
                    state.Subpoint,
                    state.FootprintRadiusDeg,
                    w,
                    h,
                    fill,
                    stroke,
                    2);

                var heading = ShowFootprintMotionArrows
                    ? GroundTrackHeading.EstimateHeadingDeg(state.Subpoint, state.GroundTrack)
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
                            color,
                            isFocused);
                    }
                }
            }

            foreach (var xOffset in GetSubpointWrapOffsets(sx, w))
            {
                PlotMarkerDrawing.DrawSatelliteMarker(
                    context, sx + xOffset, sy, color, isFocused);
            }
        }

        // Pass 2: labels on top (non-focused first, focused last; stagger overlaps).
        var labelPlacements = new List<Rect>();
        var labelOrder = Enumerable.Range(0, states.Count)
            .OrderBy(i => states[i].NoradId == FocusedNoradId ? 1 : 0)
            .ToList();

        foreach (var i in labelOrder)
        {
            var state = states[i];
            if (!TrackingPlotAccessibility.IsPlotSatelliteVisible(SoloFocusedSatellite, FocusedNoradId, state.NoradId))
                continue;

            var color = PlotColors.ForIndex(i);
            var isFocused = state.NoradId == FocusedNoradId;
            var (ax, ay) = GetLabelAnchor(state, w, h);

            foreach (var xOffset in GetSubpointWrapOffsets(ax, w))
            {
                var dx = ax + xOffset;
                if (dx < -WrapEdgeMarginPx || dx > w + WrapEdgeMarginPx)
                    continue;

                var stagger = StaggerLabelY(dx, ay, labelPlacements);
                DrawSatelliteLabel(
                    context,
                    state.Name,
                    dx,
                    ay + stagger,
                    palette,
                    isFocused ? 12 : 11);
                labelPlacements.Add(GetLabelBounds(state.Name, dx, ay + stagger, isFocused ? 12 : 11));
            }
        }
    }

    private string? HitTestSatellite(Point pos, double w, double h)
    {
        var states = TrackStates;
        if (states is null)
            return null;

        string? bestId = null;
        var bestDist = double.MaxValue;

        foreach (var state in states)
        {
            if (!TrackingPlotAccessibility.IsPlotSatelliteVisible(SoloFocusedSatellite, FocusedNoradId, state.NoradId))
                continue;

            var (sx, sy) = EquirectangularProjection.GeoToPixel(
                state.Subpoint.LatitudeDeg, state.Subpoint.LongitudeDeg, w, h);

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

    // ── Grayline ──────────────────────────────────────────────────────────

    private void RenderGrayline(DrawingContext context, double w, double h)
    {
        var utc = DateTime.UtcNow;
        var (subLat, subLon) = GraylineCalculator.GetSubsolarPoint(utc);

        DrawNightSideOverlay(context, w, h, subLat, subLon);
        DrawTerminatorLine(context, w, h, subLat, subLon);
    }

    private static double GetTerminatorLatitude(double lonDeg, double subLatDeg, double subLonDeg)
    {
        var lat = subLatDeg;
        if (Math.Abs(lat) < 0.0001)
            lat = lat >= 0 ? 0.0001 : -0.0001;

        var subLatRad = lat * Math.PI / 180.0;
        var lonRad = lonDeg * Math.PI / 180.0;
        var subLonRad = subLonDeg * Math.PI / 180.0;

        var termLatRad = Math.Atan(-Math.Cos(lonRad - subLonRad) / Math.Tan(subLatRad));
        return termLatRad * 180.0 / Math.PI;
    }

    private static void DrawNightSideOverlay(
        DrawingContext context,
        double w,
        double h,
        double subLatDeg,
        double subLonDeg)
    {
        var geom = new StreamGeometry();
        using (var sgc = geom.Open())
        {
            var isNightBelow = subLatDeg >= 0;

            var startLat = GetTerminatorLatitude(-180.0, subLatDeg, subLonDeg);
            var (startX, startY) = EquirectangularProjection.GeoToPixel(startLat, -180.0, w, h);

            sgc.BeginFigure(new Point(startX, startY), true);

            const int steps = 360;
            for (var i = 1; i <= steps; i++)
            {
                var lon = -180.0 + i * (360.0 / steps);
                var lat = GetTerminatorLatitude(lon, subLatDeg, subLonDeg);
                var (x, y) = EquirectangularProjection.GeoToPixel(lat, lon, w, h);
                sgc.LineTo(new Point(x, y));
            }

            if (isNightBelow)
            {
                sgc.LineTo(new Point(w, h));
                sgc.LineTo(new Point(0, h));
            }
            else
            {
                sgc.LineTo(new Point(w, 0));
                sgc.LineTo(new Point(0, 0));
            }
        }

        var nightBrush = new SolidColorBrush(Color.FromArgb(135, 2, 2, 20));
        context.DrawGeometry(nightBrush, null, geom);
    }

    private static void DrawTerminatorLine(
        DrawingContext context,
        double w,
        double h,
        double subLatDeg,
        double subLonDeg)
    {
        var ring = GraylineCalculator.GetTerminatorRingFromSubsolar(subLatDeg, subLonDeg, 720);
        if (ring.Count < 2)
            return;

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 220, 100)), 1.5);

        var pixels = ring
            .Select(p => EquirectangularProjection.GeoToPixel(p.LatDeg, p.LonDeg, w, h))
            .ToList();

        const double maxJump = 30;
        for (var i = 0; i < pixels.Count - 1; i++)
        {
            var (x0, y0) = pixels[i];
            var (x1, y1) = pixels[i + 1];
            if (Math.Abs(x1 - x0) > maxJump)
                continue;
            context.DrawLine(pen, new Point(x0, y0), new Point(x1, y1));
        }
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

    private static (double MinX, double MaxX) GetPixelXRange(
        IReadOnlyList<GeoCoordinate> points,
        double w,
        double h)
    {
        var minX = double.MaxValue;
        var maxX = double.MinValue;

        foreach (var p in points)
        {
            var (x, _) = EquirectangularProjection.GeoToPixel(p.LatitudeDeg, p.LongitudeDeg, w, h);
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
        double h)
    {
        yield return 0;

        if (!EquirectangularProjection.CrossesAntimeridian(points))
            yield break;

        var (minX, maxX) = GetPixelXRange(points, w, h);
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
        double h)
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
            subpoint.LatitudeDeg, subpoint.LongitudeDeg, w, h);
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
    private static void DrawFootprint(
        DrawingContext context,
        IReadOnlyList<GeoCoordinate> footprint,
        GeoCoordinate subpoint,
        double footprintRadiusDeg,
        double w,
        double h,
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

        var fillBrush = new SolidColorBrush(fillColor);
        var pen = new Pen(new SolidColorBrush(strokeColor), strokeWidth);
        var pixels = FootprintGeometry.ProjectRingToMap(
            subpoint, footprint, radiusDeg, w, h);
        if (pixels.Count < 3)
            return;

        foreach (var xOffset in GetFootprintWrapOffsets(subpoint, footprint, pixels, radiusDeg, w, h))
        {
            var geometry = BuildFootprintGeometry(pixels, xOffset);
            context.DrawGeometry(fillBrush, pen, geometry);
        }
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

    private static void DrawPolylineSegments(
        DrawingContext context,
        IReadOnlyList<GeoCoordinate> points,
        double w,
        double h,
        Color color,
        double thickness,
        bool close = false)
    {
        if (points.Count < 2)
            return;

        var pen = new Pen(new SolidColorBrush(color), thickness);

        foreach (var xOffset in GetHorizontalWrapOffsets(points, w, h))
            DrawPolylineOffset(context, points, w, h, xOffset, pen, close);
    }

    private static void DrawPolylineOffset(
        DrawingContext context,
        IReadOnlyList<GeoCoordinate> points,
        double w,
        double h,
        double xOffset,
        Pen pen,
        bool close)
    {
        var maxDx = w / 2.0;
        var maxDy = h / 3.0;

        foreach (var chain in EquirectangularProjection.SplitForMapDraw(points, w, h))
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

    private static void DrawGroundStationDot(DrawingContext context, double x, double y, UiPalette palette)
    {
        PlotMarkerDrawing.DrawGroundStationMarker(context, x, y, palette);
    }

    private static (double X, double Y) GetLabelAnchor(SatelliteTrackState state, double w, double h)
    {
        return EquirectangularProjection.GeoToPixel(
            state.Subpoint.LatitudeDeg, state.Subpoint.LongitudeDeg, w, h);
    }

    private static double StaggerLabelY(double x, double y, List<Rect> placed)
    {
        var stagger = 0.0;
        const double step = 16;
        const double minDist = 70;

        while (placed.Any(r => RectIntersectsLabelSpot(r, x, y + stagger, minDist)))
            stagger -= step;

        return stagger;
    }

    private static bool RectIntersectsLabelSpot(Rect placed, double x, double y, double minDist)
    {
        var cx = placed.X + placed.Width / 2;
        var cy = placed.Y + placed.Height / 2;
        return Math.Abs(cx - x) < minDist && Math.Abs(cy - y) < minDist;
    }

    private static Rect GetLabelBounds(string name, double x, double y, double fontSize)
    {
        var text = new FormattedText(
            name,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
            fontSize,
            Brushes.White)
        {
            MaxTextWidth = 120
        };

        var tx = x - text.Width / 2;
        var ty = y - text.Height - 8;
        return new Rect(tx - 4, ty - 2, text.Width + 8, text.Height + 4);
    }

    private static void DrawSatelliteLabel(
        DrawingContext context,
        string name,
        double x,
        double y,
        UiPalette palette,
        double fontSize = 12)
    {
        var text = new FormattedText(
            name,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
            fontSize,
            new SolidColorBrush(palette.MapLabelForeground))
        {
            MaxTextWidth = 120
        };

        var tx = x - text.Width / 2;
        var ty = y - text.Height - 8;
        var bg = new Rect(tx - 4, ty - 2, text.Width + 8, text.Height + 4);
        context.FillRectangle(new SolidColorBrush(palette.MapLabelBackground), bg);
        context.DrawText(text, new Point(tx, ty));
    }
}
