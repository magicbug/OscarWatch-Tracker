using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OscarWatch.Core.Models;
using OscarWatch.Localization;

namespace OscarWatch.Controls;

/// <summary>
/// Polar azimuth/elevation sky plot (north up). Center = zenith, outer ring = horizon.
/// </summary>
public class SkyPlotControl : ThemeAwareControl
{
    private const double HitRadiusPx = 14;
    /// <summary>Space outside the horizon circle for N/E/S/W labels.</summary>
    private const double LabelMarginPx = 16;

    private INotifyCollectionChanged? _trackStatesSource;
    private readonly RenderResourceCache _renderCache = new();
    private readonly FormattedTextCache _labelCache = new();
    private readonly Dictionary<string, (double X, double Y)> _lastRenderedPositions = new();

    public static readonly StyledProperty<IReadOnlyList<SatelliteTrackState>?> TrackStatesProperty =
        AvaloniaProperty.Register<SkyPlotControl, IReadOnlyList<SatelliteTrackState>?>(nameof(TrackStates));

    public static readonly StyledProperty<string?> FocusedNoradIdProperty =
        AvaloniaProperty.Register<SkyPlotControl, string?>(nameof(FocusedNoradId));

    public static readonly StyledProperty<double> MinimumElevationDegProperty =
        AvaloniaProperty.Register<SkyPlotControl, double>(nameof(MinimumElevationDeg), 5.0);

    public static readonly StyledProperty<bool> SoloFocusedSatelliteProperty =
        AvaloniaProperty.Register<SkyPlotControl, bool>(nameof(SoloFocusedSatellite));

    public static readonly StyledProperty<IReadOnlyList<SkyPlotPathPoint>?> PassPathProperty =
        AvaloniaProperty.Register<SkyPlotControl, IReadOnlyList<SkyPlotPathPoint>?>(nameof(PassPath));

    public static readonly StyledProperty<double?> RotatorAzimuthDegProperty =
        AvaloniaProperty.Register<SkyPlotControl, double?>(nameof(RotatorAzimuthDeg));

    public static readonly StyledProperty<double?> RotatorElevationDegProperty =
        AvaloniaProperty.Register<SkyPlotControl, double?>(nameof(RotatorElevationDeg));

    public static readonly StyledProperty<HorizonMask?> HorizonMaskProperty =
        AvaloniaProperty.Register<SkyPlotControl, HorizonMask?>(nameof(HorizonMask));

    static SkyPlotControl()
    {
        AffectsRender<SkyPlotControl>(
            FocusedNoradIdProperty,
            MinimumElevationDegProperty,
            SoloFocusedSatelliteProperty,
            PassPathProperty,
            RotatorAzimuthDegProperty,
            RotatorElevationDegProperty,
            HorizonMaskProperty);
    }

    public SkyPlotControl()
    {
        ClipToBounds = true;
        Focusable = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        MinWidth = 0;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, LocalizationService.Instance.Get("Main.SkyPlot.Hint"));
    }

    public IReadOnlyList<SatelliteTrackState>? TrackStates
    {
        get => GetValue(TrackStatesProperty);
        set => SetValue(TrackStatesProperty, value);
    }

    public string? FocusedNoradId
    {
        get => GetValue(FocusedNoradIdProperty);
        set => SetValue(FocusedNoradIdProperty, value);
    }

    public double MinimumElevationDeg
    {
        get => GetValue(MinimumElevationDegProperty);
        set => SetValue(MinimumElevationDegProperty, value);
    }

    public bool SoloFocusedSatellite
    {
        get => GetValue(SoloFocusedSatelliteProperty);
        set => SetValue(SoloFocusedSatelliteProperty, value);
    }

    public IReadOnlyList<SkyPlotPathPoint>? PassPath
    {
        get => GetValue(PassPathProperty);
        set => SetValue(PassPathProperty, value);
    }

    public double? RotatorAzimuthDeg
    {
        get => GetValue(RotatorAzimuthDegProperty);
        set => SetValue(RotatorAzimuthDegProperty, value);
    }

    public double? RotatorElevationDeg
    {
        get => GetValue(RotatorElevationDegProperty);
        set => SetValue(RotatorElevationDegProperty, value);
    }

    public HorizonMask? HorizonMask
    {
        get => GetValue(HorizonMaskProperty);
        set => SetValue(HorizonMaskProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Application.Current is not null)
            Application.Current.ActualThemeVariantChanged += OnThemeChangedClearCache;
        BindTrackStatesSource(TrackStates);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
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

        if (change.Property == TrackStatesProperty || change.Property == FocusedNoradIdProperty)
            TrackingPlotAccessibility.UpdateName(
                this,
                LocalizationService.Instance.Get("Main.SkyPlot"),
                TrackStates,
                FocusedNoradId);
    }

    private bool ShouldRenderForTrackStates()
    {
        var states = TrackStates;
        if (states is null || states.Count == 0)
        {
            if (_lastRenderedPositions.Count > 0)
            {
                _lastRenderedPositions.Clear();
                return true;
            }
            return false;
        }

        var (cx, cy, plotRadius) = GetPlotGeometry(Bounds.Width, Bounds.Height);
        if (plotRadius <= 0)
            return true; // Can't compute positions, render anyway

        var visibleCount = 0;
        foreach (var state in states)
        {
            if (state.LookAngles is not { } la)
                continue;

            if (!TryAzElToPoint(cx, cy, plotRadius, la.AzimuthDeg, la.ElevationDeg, out var point))
                continue;

            visibleCount++;

            if (_lastRenderedPositions.TryGetValue(state.NoradId, out var prev))
            {
                var dx = point.X - prev.X;
                var dy = point.Y - prev.Y;
                if (dx * dx + dy * dy >= 1.0) // >= 1 pixel movement
                    return true;
            }
            else
            {
                return true; // New satellite appeared
            }
        }

        // Check if satellite count changed (satellite disappeared)
        if (_lastRenderedPositions.Count != visibleCount)
            return true;

        return false;
    }

    /// <summary>
    /// Determines whether a render is needed based on position movement.
    /// Extracted as internal static for testability.
    /// </summary>
    internal static bool HasMovedBeyondThreshold(
        IReadOnlyList<SatelliteTrackState> states,
        Dictionary<string, (double X, double Y)> previousPositions,
        double cx, double cy, double plotRadius,
        double thresholdPxSquared = 1.0)
    {
        if (states.Count == 0)
        {
            return previousPositions.Count > 0;
        }

        if (plotRadius <= 0)
            return true;

        var visibleCount = 0;
        foreach (var state in states)
        {
            if (state.LookAngles is not { } la)
                continue;

            if (!TryAzElToPoint(cx, cy, plotRadius, la.AzimuthDeg, la.ElevationDeg, out var point))
                continue;

            visibleCount++;

            if (previousPositions.TryGetValue(state.NoradId, out var prev))
            {
                var dx = point.X - prev.X;
                var dy = point.Y - prev.Y;
                if (dx * dx + dy * dy >= thresholdPxSquared)
                    return true;
            }
            else
            {
                return true; // New satellite appeared
            }
        }

        // Check if satellite count changed (satellite disappeared)
        if (previousPositions.Count != visibleCount)
            return true;

        return false;
    }

    private void BindTrackStatesSource(object? value)
    {
        UnsubscribeTrackStatesSource();
        _trackStatesSource = value as INotifyCollectionChanged;
        if (_trackStatesSource is not null)
            _trackStatesSource.CollectionChanged += OnTrackStatesSourceChanged;
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
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pos = e.GetPosition(this);
        FocusedNoradId = HitTestSatellite(pos);
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
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;

        var palette = UiPaletteResolver.Current;
        var local = new Rect(0, 0, w, h);
        var (cx, cy, plotRadius) = GetPlotGeometry(w, h);

        context.FillRectangle(_renderCache.GetBrush(palette.SkyPlotBackground), local);

        DrawHorizonDisk(context, cx, cy, plotRadius, palette);
        HorizonMaskPlotDrawing.DrawObstruction(context, cx, cy, plotRadius, HorizonMask, _renderCache);
        DrawElevationRing(context, cx, cy, plotRadius, 30, palette.SkyPlotRing30, 1);
        DrawElevationRing(context, cx, cy, plotRadius, 60, palette.SkyPlotRing60, 1);

        if (MinimumElevationDeg > 0 && MinimumElevationDeg < 90)
        {
            DrawElevationRing(context, cx, cy, plotRadius, MinimumElevationDeg, palette.SkyPlotMinElRing, 1, dashed: true);
            DrawMinElevationLabel(context, cx, cy, plotRadius, MinimumElevationDeg, palette);
        }

        DrawAzimuthSpokes(context, cx, cy, plotRadius, palette);
        DrawCardinalLabels(context, cx, cy, plotRadius, palette);

        var states = TrackStates;
        if (states is null)
            return;

        DrawFocusedPassPath(context, cx, cy, plotRadius, states);
        DrawRotatorPosition(context, cx, cy, plotRadius, palette);

        var visibleCount = 0;
        for (var i = 0; i < states.Count; i++)
        {
            if (!TrackingPlotAccessibility.IsPlotSatelliteVisible(SoloFocusedSatellite, FocusedNoradId, states[i].NoradId))
                continue;

            if (states[i].LookAngles is not { } la)
                continue;

            if (!TryAzElToPoint(cx, cy, plotRadius, la.AzimuthDeg, la.ElevationDeg, out var point))
                continue;

            visibleCount++;
            var color = PlotColors.ForIndex(states[i].PaletteIndex);
            var isFocused = states[i].NoradId == FocusedNoradId;
            PlotMarkerDrawing.DrawSatelliteMarker(
                context,
                point.X,
                point.Y,
                color,
                isFocused,
                _renderCache,
                la.ElevationDeg < (HorizonMask?.EffectiveFloor(la.AzimuthDeg, MinimumElevationDeg) ?? MinimumElevationDeg));
        }

        // Update position cache after rendering all satellites
        _lastRenderedPositions.Clear();
        for (var i = 0; i < states.Count; i++)
        {
            if (states[i].LookAngles is not { } la2)
                continue;
            if (!TryAzElToPoint(cx, cy, plotRadius, la2.AzimuthDeg, la2.ElevationDeg, out var cachedPoint))
                continue;
            _lastRenderedPositions[states[i].NoradId] = (cachedPoint.X, cachedPoint.Y);
        }

        if (visibleCount == 0)
            DrawCenterMessage(
                context,
                cx,
                cy,
                LocalizationService.Instance.Get("A11y.SkyPlot.AllBelowHorizon"),
                palette);
    }

    private static (double Cx, double Cy, double PlotRadius) GetPlotGeometry(double width, double height)
    {
        var side = Math.Min(width, height);
        var cx = width / 2;
        var cy = height / 2;
        var plotRadius = Math.Max(0, side / 2 - LabelMarginPx);
        return (cx, cy, plotRadius);
    }

    private string? HitTestSatellite(Point pos)
    {
        var (cx, cy, plotRadius) = GetPlotGeometry(Bounds.Width, Bounds.Height);

        var states = TrackStates;
        if (states is null)
            return null;

        string? bestId = null;
        var bestDist = double.MaxValue;

        for (var i = 0; i < states.Count; i++)
        {
            if (!TrackingPlotAccessibility.IsPlotSatelliteVisible(SoloFocusedSatellite, FocusedNoradId, states[i].NoradId))
                continue;

            if (states[i].LookAngles is not { } la)
                continue;

            if (!TryAzElToPoint(cx, cy, plotRadius, la.AzimuthDeg, la.ElevationDeg, out var point))
                continue;

            var dist = Math.Sqrt(Math.Pow(pos.X - point.X, 2) + Math.Pow(pos.Y - point.Y, 2));
            if (dist <= HitRadiusPx && dist < bestDist)
            {
                bestDist = dist;
                bestId = states[i].NoradId;
            }
        }

        return bestId;
    }

    /// <summary>Maps az/el to plot pixels. Returns false when the satellite is below the horizon.</summary>
    internal static bool TryAzElToPoint(
        double cx,
        double cy,
        double plotRadius,
        double azimuthDeg,
        double elevationDeg,
        out (double X, double Y) point)
    {
        if (elevationDeg < 0)
        {
            point = default;
            return false;
        }

        var el = Math.Clamp(elevationDeg, 0, 90);
        var r = (90.0 - el) / 90.0 * plotRadius;
        var azRad = azimuthDeg * Math.PI / 180.0;
        point = (cx + r * Math.Sin(azRad), cy - r * Math.Cos(azRad));
        return true;
    }

    private void DrawHorizonDisk(DrawingContext context, double cx, double cy, double plotRadius, UiPalette palette)
    {
        var disk = new Rect(cx - plotRadius, cy - plotRadius, plotRadius * 2, plotRadius * 2);
        context.DrawEllipse(
            _renderCache.GetBrush(palette.SkyPlotBackground),
            _renderCache.GetPen(palette.SkyPlotBorder, 1.5),
            disk);
    }

    private void DrawElevationRing(
        DrawingContext context,
        double cx,
        double cy,
        double plotRadius,
        double elevationDeg,
        Color color,
        double thickness,
        bool dashed = false)
    {
        var r = (90.0 - Math.Clamp(elevationDeg, 0, 90)) / 90.0 * plotRadius;
        var pen = dashed
            ? _renderCache.GetDashedPen(color, thickness)
            : _renderCache.GetPen(color, thickness);

        context.DrawEllipse(null, pen, new Rect(cx - r, cy - r, r * 2, r * 2));
    }

    private void DrawAzimuthSpokes(DrawingContext context, double cx, double cy, double plotRadius, UiPalette palette)
    {
        var pen = _renderCache.GetPen(palette.SkyPlotSpoke, 1);
        for (var az = 0; az < 360; az += 45)
        {
            if (!TryAzElToPoint(cx, cy, plotRadius, az, 0, out var spokeEnd))
                continue;

            context.DrawLine(pen, new Point(cx, cy), new Point(spokeEnd.X, spokeEnd.Y));
        }
    }

    private void DrawCardinalLabels(DrawingContext context, double cx, double cy, double plotRadius, UiPalette palette)
    {
        DrawLabel(context, "N", cx, cy - plotRadius - 14, palette);
        DrawLabel(context, "S", cx, cy + plotRadius + 4, palette);
        DrawLabel(context, "E", cx + plotRadius + 6, cy - 5, palette);
        DrawLabel(context, "W", cx - plotRadius - 18, cy - 5, palette);
    }

    private void DrawLabel(DrawingContext context, string text, double x, double y, UiPalette palette)
    {
        var ft = _labelCache.Get(text, 12, palette);
        context.DrawText(ft, new Point(x, y));
    }

    private void DrawMinElevationLabel(
        DrawingContext context,
        double cx,
        double cy,
        double plotRadius,
        double minimumElevationDeg,
        UiPalette palette)
    {
        if (!TryAzElToPoint(cx, cy, plotRadius, 135, minimumElevationDeg, out var anchor))
            return;

        DrawLabel(context, $"{minimumElevationDeg:F0}° min", anchor.X + 6, anchor.Y - 6, palette);
    }

    private void DrawCenterMessage(DrawingContext context, double cx, double cy, string text, UiPalette palette)
    {
        var ft = _labelCache.Get(text, 12, palette);
        context.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
    }

    private void DrawFocusedPassPath(
        DrawingContext context,
        double cx,
        double cy,
        double plotRadius,
        IReadOnlyList<SatelliteTrackState> states)
    {
        var path = PassPath;
        if (path is not { Count: >= 2 } || string.IsNullOrEmpty(FocusedNoradId))
            return;

        Color? pathColor = null;
        for (var i = 0; i < states.Count; i++)
        {
            if (states[i].NoradId != FocusedNoradId)
                continue;

            pathColor = PlotColors.ForIndex(states[i].PaletteIndex);
            break;
        }

        if (pathColor is null)
            return;

        var color = Color.FromArgb(170, pathColor.Value.R, pathColor.Value.G, pathColor.Value.B);
        var pen = _renderCache.GetPen(color, 2.5);
        if (!TryAzElToPoint(cx, cy, plotRadius, path[0].AzimuthDeg, path[0].ElevationDeg, out var prev))
            return;

        for (var i = 1; i < path.Count; i++)
        {
            var point = path[i];
            if (!TryAzElToPoint(cx, cy, plotRadius, point.AzimuthDeg, point.ElevationDeg, out var next))
                continue;

            context.DrawLine(pen, new Point(prev.X, prev.Y), new Point(next.X, next.Y));
            prev = next;
        }
    }

    private void DrawRotatorPosition(
        DrawingContext context,
        double cx,
        double cy,
        double plotRadius,
        UiPalette palette)
    {
        if (RotatorAzimuthDeg is not { } az || RotatorElevationDeg is not { } el)
            return;

        if (!TryAzElToPoint(cx, cy, plotRadius, az, el, out var point))
            return;

        PlotMarkerDrawing.DrawRotatorMarker(context, point.X, point.Y, palette, _renderCache);
    }
}
