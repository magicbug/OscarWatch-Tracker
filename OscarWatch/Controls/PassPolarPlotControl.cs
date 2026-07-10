using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using OscarWatch.Core.Display;
using OscarWatch.Core.Models;
using OscarWatch.Localization;

namespace OscarWatch.Controls;

/// <summary>
/// Polar plot for a single satellite pass with sunlit/eclipse segments and mutual-window markers.
/// </summary>
public class PassPolarPlotControl : ThemeAwareControl
{
    private const double LabelMarginPx = 16;
    private const double HoverMarkerRadiusPx = 5.5;
    private static readonly Color EclipsePathColor = Color.Parse("#E05252");
    private static readonly Color HoverMarkerFill = Color.Parse("#4DA3FF");
    private static readonly Color HoverMarkerOutline = Color.Parse("#1a2028");

    private PassPolarPlotHitTest.HoverPoint? _hoverPoint;
    private readonly RenderResourceCache _renderCache = new();
    private readonly FormattedTextCache _textCache = new();

    public static readonly StyledProperty<PassPolarPlotData?> PlotDataProperty =
        AvaloniaProperty.Register<PassPolarPlotControl, PassPolarPlotData?>(nameof(PlotData));

    public static readonly StyledProperty<double> MinimumElevationDegProperty =
        AvaloniaProperty.Register<PassPolarPlotControl, double>(nameof(MinimumElevationDeg), 0.0);

    public static readonly StyledProperty<bool> UseUtcTimeProperty =
        AvaloniaProperty.Register<PassPolarPlotControl, bool>(nameof(UseUtcTime));

    public static readonly StyledProperty<bool> Use24HourClockProperty =
        AvaloniaProperty.Register<PassPolarPlotControl, bool>(nameof(Use24HourClock));

    static PassPolarPlotControl()
    {
        AffectsRender<PassPolarPlotControl>(PlotDataProperty, MinimumElevationDegProperty);
    }

    public PassPolarPlotControl()
    {
        ClipToBounds = true;
        MinHeight = 220;
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
    }

    public PassPolarPlotData? PlotData
    {
        get => GetValue(PlotDataProperty);
        set => SetValue(PlotDataProperty, value);
    }

    public double MinimumElevationDeg
    {
        get => GetValue(MinimumElevationDegProperty);
        set => SetValue(MinimumElevationDegProperty, value);
    }

    public bool UseUtcTime
    {
        get => GetValue(UseUtcTimeProperty);
        set => SetValue(UseUtcTimeProperty, value);
    }

    public bool Use24HourClock
    {
        get => GetValue(Use24HourClockProperty);
        set => SetValue(Use24HourClockProperty, value);
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
        DrawElevationRing(context, cx, cy, plotRadius, 30, palette.SkyPlotRing30, 1);
        DrawElevationRing(context, cx, cy, plotRadius, 60, palette.SkyPlotRing60, 1);

        if (MinimumElevationDeg > 0 && MinimumElevationDeg < 90)
            DrawElevationRing(context, cx, cy, plotRadius, MinimumElevationDeg, palette.SkyPlotMinElRing, 1, dashed: true);

        DrawAzimuthSpokes(context, cx, cy, plotRadius, palette);
        DrawCardinalLabels(context, cx, cy, plotRadius, palette);

        var data = PlotData;
        if (data is null)
            return;

        foreach (var segment in data.Segments)
        {
            if (segment.Points.Count < 2)
                continue;

            var color = segment.IsSunlit ? palette.SunlightTimeline : EclipsePathColor;
            var pen = _renderCache.GetRoundCapPen(color, 2.5);

            var first = segment.Points[0];
            if (!SkyPlotControl.TryAzElToPoint(cx, cy, plotRadius, first.AzimuthDeg, first.ElevationDeg, out var prev))
                continue;

            for (var i = 1; i < segment.Points.Count; i++)
            {
                var point = segment.Points[i];
                if (!SkyPlotControl.TryAzElToPoint(cx, cy, plotRadius, point.AzimuthDeg, point.ElevationDeg, out var next))
                    continue;

                context.DrawLine(pen, new Point(prev.X, prev.Y), new Point(next.X, next.Y));
                prev = next;
            }
        }

        DrawMarker(context, cx, cy, plotRadius, data.MutualStart);
        DrawMarker(context, cx, cy, plotRadius, data.MutualEnd);
        DrawHoverMarker(context, cx, cy, plotRadius);
    }

    private void DrawMarker(
        DrawingContext context,
        double cx,
        double cy,
        double plotRadius,
        PassPolarPlotMarker? marker)
    {
        if (marker is null)
            return;

        if (!SkyPlotControl.TryAzElToPoint(cx, cy, plotRadius, marker.AzimuthDeg, marker.ElevationDeg, out var point))
            return;

        switch (marker.Kind)
        {
            case PassPolarPlotMarkerKind.MutualWindowStart:
                PlotMarkerDrawing.DrawMutualWindowStartMarker(context, point.X, point.Y, _renderCache);
                break;
            case PassPolarPlotMarkerKind.MutualWindowEnd:
                PlotMarkerDrawing.DrawMutualWindowEndMarker(context, point.X, point.Y, _renderCache);
                break;
        }
    }

    private static (double Cx, double Cy, double PlotRadius) GetPlotGeometry(double width, double height)
    {
        var side = Math.Min(width, height);
        var cx = width / 2;
        var cy = height / 2;
        var plotRadius = Math.Max(0, side / 2 - LabelMarginPx);
        return (cx, cy, plotRadius);
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
            if (!SkyPlotControl.TryAzElToPoint(cx, cy, plotRadius, az, 0, out var spokeEnd))
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

    private void DrawHoverMarker(DrawingContext context, double cx, double cy, double plotRadius)
    {
        if (_hoverPoint is not { } hover)
            return;

        if (!SkyPlotControl.TryAzElToPoint(cx, cy, plotRadius, hover.AzimuthDeg, hover.ElevationDeg, out var point))
            return;

        var rect = new Rect(
            point.X - HoverMarkerRadiusPx,
            point.Y - HoverMarkerRadiusPx,
            HoverMarkerRadiusPx * 2,
            HoverMarkerRadiusPx * 2);
        context.DrawEllipse(
            _renderCache.GetBrush(HoverMarkerFill),
            _renderCache.GetPen(HoverMarkerOutline, 2),
            rect);
        context.DrawEllipse(null, _renderCache.GetPen(Colors.White, 1.5), rect);
    }

    private void DrawLabel(DrawingContext context, string text, double x, double y, UiPalette palette)
    {
        var ft = _textCache.Get(text, 12, palette.SkyPlotLabel);
        context.DrawText(ft, new Point(x, y));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlotDataProperty)
            ClearHover();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var data = PlotData;
        if (data is null || data.Samples.Count == 0)
        {
            ClearHover();
            return;
        }

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            ClearHover();
            return;
        }

        var pos = e.GetPosition(this);
        var (cx, cy, plotRadius) = GetPlotGeometry(w, h);
        var hit = PassPolarPlotHitTest.TryHit(data, cx, cy, plotRadius, pos);
        if (hit is null)
        {
            ClearHover();
            return;
        }

        SetHover(hit.Value);

        var timeText = PassDisplayFormat.FormatHoverTime(
            hit.Value.Utc,
            UseUtcTime,
            PassDisplayFormat.FromSettings(Use24HourClock));
        ToolTip.SetTip(
            this,
            LocalizationService.Instance.Get(
                "Mutual.Visualizer.PlotTooltip",
                timeText,
                hit.Value.AzimuthDeg,
                hit.Value.ElevationDeg));
    }

    private void OnPointerExited(object? sender, PointerEventArgs e) => ClearHover();

    private void SetHover(PassPolarPlotHitTest.HoverPoint hover)
    {
        if (_hoverPoint == hover)
            return;

        _hoverPoint = hover;
        InvalidateVisual();
    }

    private void ClearHover()
    {
        ToolTip.SetTip(this, null);
        if (_hoverPoint is null)
            return;

        _hoverPoint = null;
        InvalidateVisual();
    }
}
