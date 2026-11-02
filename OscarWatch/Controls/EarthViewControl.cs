using System.Buffers;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using OscarWatch.Core.Geo;
using OscarWatch.Localization;
using Serilog;

namespace OscarWatch.Controls;

/// <summary>
/// The Earth as seen from a satellite. The globe is a raster made from the world map by
/// <see cref="GlobeRasteriser"/> on a background task, and everything else (the limb, the minimum elevation
/// ring, the ground track, the markers and the labels) is drawn on top as vectors, so lines and text stay
/// sharp at any window size.
/// </summary>
/// <remarks>
/// The world map pixels are held through a <see cref="BasemapPixelCache"/> lease that is taken when the
/// control joins the visual tree and released when it leaves, so the memory is used only while the Earth
/// View window is open.
/// <para>
/// Layout: the globe's size is set by the shorter side of the control, and the raster covers the longer side,
/// so a zoomed-in globe fills a window that is not square. Overlays use the same scale as the raster
/// (<see cref="GlobeRasteriser.PixelsPerRadian"/> for the shorter side), which keeps them aligned.
/// </para>
/// </remarks>
public sealed class EarthViewControl : ThemeAwareControl
{
    private static readonly ILogger Log = Serilog.Log.ForContext<EarthViewControl>();

    /// <summary>Largest zoom. At 1 the limb fits the control, and the world map has no more detail than this supports.</summary>
    public const double MaxZoom = 4;

    private const double WheelZoomStep = 1.25;
    private const double ButtonZoomStep = 1.5;

    // Each line is a bright core over a dark outline, so it shows against dark ocean and bright ice alike.
    private const double LimbOutlineThickness = 5.0;
    private const double LimbCoreThickness = 2.5;
    private const double RingOutlineThickness = 4.5;
    private const double RingCoreThickness = 2.5;
    private const double TrackOutlineThickness = 4.5;
    private const double TrackCoreThickness = 2.0;

    private const double TickSpacingPx = 120;
    private const double TickFirstOffsetPx = 60;
    private const double TickLength = 5;
    private const double TickHalfWidth = 4.5;

    private const double LabelFontSize = 12;
    private const double HitRadiusPx = 14;

    /// <summary>Rings closer than this to the limb, or to the centre, are not drawn: they would just double a line.</summary>
    private const double RingMinGapPx = 3;

    public static readonly StyledProperty<EarthViewFrame?> FrameProperty =
        AvaloniaProperty.Register<EarthViewControl, EarthViewFrame?>(nameof(Frame));

    public static readonly StyledProperty<double> ZoomFactorProperty =
        AvaloniaProperty.Register<EarthViewControl, double>(nameof(ZoomFactor), 1.0, coerce: CoerceZoom);

    private readonly RenderResourceCache _renderCache = new();
    private readonly FormattedTextCache _textCache = new();
    private readonly List<(Point Centre, string Text)> _markerHits = [];
    private readonly List<Rect> _labelRects = [];

    private BasemapPixelCache.Lease? _lease;
    private bool _attached;
    private int _leaseRequest;

    private WriteableBitmap? _bitmap;
    private RasterKey? _requestedKey;
    private CancellationTokenSource? _renderCts;
    private int _generation;
    private bool _loggedRenderFailure;

    private bool _showFocus;
    private string? _tipText;

    static EarthViewControl()
    {
        AffectsRender<EarthViewControl>(FrameProperty, ZoomFactorProperty);
    }

    public EarthViewControl()
    {
        ClipToBounds = true;
        MinWidth = 160;
        MinHeight = 160;
        Focusable = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new EarthViewAutomationPeer(this);

    /// <summary>Raised after the zoom changes, so the window can enable or disable its zoom buttons.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>What to draw. Null draws only the background.</summary>
    public EarthViewFrame? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    /// <summary>1 fits the limb to the control. Larger values zoom in, up to <see cref="MaxZoom"/>.</summary>
    public double ZoomFactor
    {
        get => GetValue(ZoomFactorProperty);
        set => SetValue(ZoomFactorProperty, value);
    }

    public bool CanZoomIn => ZoomFactor < MaxZoom - 1e-9;

    public bool CanZoomOut => ZoomFactor > 1 + 1e-9;

    public bool IsViewChanged => CanZoomOut;

    public void ZoomIn() => ZoomFactor *= ButtonZoomStep;

    public void ZoomOut() => ZoomFactor /= ButtonZoomStep;

    public void ResetView() => ZoomFactor = 1;

    private static double CoerceZoom(AvaloniaObject sender, double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 1, MaxZoom) : 1;

    // ---- life cycle and the raster pipeline ---------------------------------------------------------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        BeginAcquireLease();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _leaseRequest++; // an acquisition still in flight must dispose its lease instead of using it
        _generation++;
        _renderCts?.Cancel();
        _renderCts = null;
        _requestedKey = null;

        _lease?.Dispose();
        _lease = null;
        _bitmap?.Dispose();
        _bitmap = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnThemeChanged()
    {
        _renderCache.Clear();
        _textCache.Clear();
        _requestedKey = null; // the fallback sea colour follows the theme
        RequestRaster();
        base.OnThemeChanged();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FrameProperty)
        {
            RequestRaster();
        }
        else if (change.Property == ZoomFactorProperty)
        {
            RequestRaster();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RequestRaster();
    }

    /// <summary>
    /// Takes the world map lease on a background thread, because the first lease decodes a large JPEG and
    /// that must not freeze the window as it opens.
    /// </summary>
    private void BeginAcquireLease()
    {
        var request = ++_leaseRequest;
        Task.Run(() => BasemapPixelCache.Default.Acquire())
            .ContinueWith(
                task => Dispatcher.UIThread.Post(() => OnLeaseAcquired(request, task)),
                TaskScheduler.Default);
    }

    private void OnLeaseAcquired(int request, Task<BasemapPixelCache.Lease> task)
    {
        if (task.IsFaulted || task.IsCanceled)
        {
            Log.Warning(task.Exception, "Earth View could not take the world map lease");
            return;
        }

        var lease = task.Result;
        if (!_attached || request != _leaseRequest)
        {
            lease.Dispose(); // the window closed while the map was loading
            return;
        }

        _lease = lease;
        _requestedKey = null;
        RequestRaster();
    }

    private RasterKey? DesiredKey()
    {
        var frame = Frame;
        var zoom = ZoomFactor;
        var minSide = Math.Min(Bounds.Width, Bounds.Height);
        var maxSide = Math.Max(Bounds.Width, Bounds.Height);
        if (frame is null || _lease is null || minSide < 16 || !double.IsFinite(zoom) || zoom <= 0)
            return null;

        // The raster covers the longer side. Scaling the zoom by min/max keeps the globe the size the
        // overlays expect, which is set by the shorter side.
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        return new RasterKey(
            frame.Camera,
            GlobeRasterPolicy.RasterSize(maxSide, scaling),
            GlobeRasterPolicy.EffectiveZoom(zoom, Bounds.Width, Bounds.Height),
            GlobeRasterPolicy.UtcMinute(frame.DisplayUtc),
            Application.Current?.ActualThemeVariant == ThemeVariant.Dark,
            _lease.Pixels.IsAvailable);
    }

    /// <summary>Starts a background render if the picture on screen is no longer good enough. UI thread only.</summary>
    private void RequestRaster()
    {
        if (!_attached || _lease is null || Frame is not { } frame)
            return;

        if (DesiredKey() is not { } desired || !GlobeRasterPolicy.NeedsRender(_requestedKey, desired))
            return;

        _requestedKey = desired;
        _renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        var generation = ++_generation;

        var camera = desired.Camera;
        var size = desired.SizePx;
        var zoom = desired.Zoom;
        var subsolar = frame.Subsolar;
        var pixels = _lease.Pixels;
        var palette = GlobePalette.From(UiPaletteResolver.Current);
        var buffer = ArrayPool<uint>.Shared.Rent(size * size);

        Task.Run(() =>
        {
            try
            {
                _rasteriser.Render(camera, size, zoom, subsolar, shadeNight: true, palette, pixels, buffer, cts.Token);
            }
            catch (OperationCanceledException)
            {
                ArrayPool<uint>.Shared.Return(buffer);
                return;
            }
            catch (Exception ex)
            {
                ArrayPool<uint>.Shared.Return(buffer);
                Dispatcher.UIThread.Post(() => OnRenderFailed(generation, ex));
                return;
            }

            Dispatcher.UIThread.Post(() => ApplyRaster(generation, buffer, size));
        });
    }

    private readonly GlobeRasteriser _rasteriser = new();

    private void OnRenderFailed(int generation, Exception ex)
    {
        if (generation != _generation)
            return;

        _requestedKey = null; // allow a retry on the next change
        if (_loggedRenderFailure)
            return;

        _loggedRenderFailure = true;
        Log.Warning(ex, "Earth View could not draw the globe");
    }

    private unsafe void ApplyRaster(int generation, uint[] buffer, int size)
    {
        try
        {
            if (!_attached || generation != _generation)
                return; // closed, or a newer render is already on its way

            if (_bitmap is null || _bitmap.PixelSize.Width != size)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(
                    new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            }

            using (var fb = _bitmap.Lock())
            {
                for (var y = 0; y < size; y++)
                {
                    var row = new Span<uint>((byte*)fb.Address + (long)y * fb.RowBytes, size);
                    buffer.AsSpan(y * size, size).CopyTo(row);
                }
            }

            InvalidateVisual();
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(buffer);
        }
    }

    // ---- input ----------------------------------------------------------------------------------------------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0)
            return;

        ZoomFactor *= Math.Pow(WheelZoomStep, e.Delta.Y);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                ZoomIn();
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract:
                ZoomOut();
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0:
                ResetView();
                e.Handled = true;
                break;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        // Show the ring for keyboard navigation only, as the Fluent theme does, not after a mouse click.
        _showFocus = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional;
        InvalidateVisual();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _showFocus = false;
        InvalidateVisual();
    }

    /// <summary>A tool tip with the visible label on a marker, so the text does not depend on seeing the label.</summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var position = e.GetPosition(this);
        string? text = null;
        var best = HitRadiusPx;
        foreach (var (centre, label) in _markerHits)
        {
            var distance = Math.Sqrt(Math.Pow(centre.X - position.X, 2) + Math.Pow(centre.Y - position.Y, 2));
            if (distance <= best)
            {
                best = distance;
                text = label;
            }
        }

        if (!string.Equals(text, _tipText, StringComparison.Ordinal))
        {
            _tipText = text;
            ToolTip.SetTip(this, text);
        }
    }

    // ---- drawing -----------------------------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;

        var palette = UiPaletteResolver.Current;
        context.FillRectangle(_renderCache.GetBrush(palette.GlobeBackground), new Rect(0, 0, w, h));

        _markerHits.Clear();
        _labelRects.Clear();

        if (Frame is { } frame)
            DrawFrame(context, palette, frame, w, h);

        if (_showFocus)
            DrawFocusIndicator(context, palette, w, h);
    }

    private void DrawFrame(DrawingContext context, UiPalette palette, EarthViewFrame frame, double w, double h)
    {
        var altitude = frame.Camera.AltitudeKm;
        var scale = GlobeRasteriser.PixelsPerRadian(Math.Min(w, h), ZoomFactor, altitude);
        var limbPx = scale * GlobeProjection.LimbRadius(altitude);
        if (!double.IsFinite(limbPx) || limbPx <= 0)
            return;

        var centre = new Point(w / 2, h / 2);
        DrawGlobe(context, palette, centre, Math.Max(w, h), limbPx);
        DrawMinimumElevationRing(context, palette, frame, centre, scale, limbPx);
        DrawGroundTrack(context, palette, frame, centre, scale);
        DrawLimb(context, palette, centre, limbPx);
        DrawMarkers(context, palette, frame, centre, scale, new Rect(0, 0, w, h));
        DrawNorthIndicator(context, palette);
    }

    /// <summary>The raster stretched over a square of side <paramref name="side"/>, or a plain disc until it arrives.</summary>
    private void DrawGlobe(DrawingContext context, UiPalette palette, Point centre, double side, double limbRadiusPx)
    {
        if (_bitmap is not null)
        {
            var source = new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
            var destination = new Rect(centre.X - side / 2, centre.Y - side / 2, side, side);
            context.DrawImage(_bitmap, source, destination);
            return;
        }

        context.DrawEllipse(_renderCache.GetBrush(palette.GlobeSeaFallback), null, centre, limbRadiusPx, limbRadiusPx);
    }

    /// <summary>
    /// The 0 degree footprint is the edge of the visible disc, so the limb is the footprint outline: a thick
    /// solid line.
    /// </summary>
    private void DrawLimb(DrawingContext context, UiPalette palette, Point centre, double limbRadiusPx)
    {
        context.DrawEllipse(
            null, _renderCache.GetPen(palette.MapGroundTrackOutline, LimbOutlineThickness), centre, limbRadiusPx, limbRadiusPx);
        context.DrawEllipse(
            null, _renderCache.GetPen(palette.GlobeLimbStroke, LimbCoreThickness), centre, limbRadiusPx, limbRadiusPx);
    }

    /// <summary>
    /// The footprint at the operator's minimum elevation, as a dashed ring. Every point at the same ground
    /// distance from the subpoint is the same distance from the centre of the picture, so it is an exact
    /// circle and no polyline is needed. Skipped when it would only trace the limb or the centre.
    /// </summary>
    private void DrawMinimumElevationRing(
        DrawingContext context, UiPalette palette, EarthViewFrame frame, Point centre, double scale, double limbPx)
    {
        var ringPx = scale * GlobeProjection.RingRadius(frame.Camera.AltitudeKm, frame.MinimumElevationRadiusDeg);
        if (!double.IsFinite(ringPx) || ringPx < RingMinGapPx || ringPx > limbPx - RingMinGapPx)
            return;

        context.DrawEllipse(
            null, _renderCache.GetPen(palette.MapGroundTrackOutline, RingOutlineThickness), centre, ringPx, ringPx);
        context.DrawEllipse(
            null, _renderCache.GetDashedPen(palette.GlobeRingStroke, RingCoreThickness), centre, ringPx, ringPx);
    }

    /// <summary>The ground track on the visible hemisphere, with small arrows showing the direction of travel.</summary>
    private void DrawGroundTrack(DrawingContext context, UiPalette palette, EarthViewFrame frame, Point centre, double scale)
    {
        var runs = GlobeViewMath.ProjectTrack(frame.Camera, frame.GroundTrack);
        if (runs.Count == 0)
            return;

        var outlinePen = _renderCache.GetRoundCapPen(palette.MapGroundTrackOutline, TrackOutlineThickness);
        var corePen = _renderCache.GetRoundCapPen(palette.MapGroundTrackStroke, TrackCoreThickness);
        var arrowFill = _renderCache.GetBrush(palette.MapGroundTrackStroke);
        var arrowOutline = _renderCache.GetPen(palette.MapGroundTrackOutline, 1.5);

        foreach (var run in runs)
        {
            var screen = new List<(double X, double Y)>(run.Count);
            foreach (var (x, y) in run)
                screen.Add((centre.X + x * scale, centre.Y - y * scale));

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(screen[0].X, screen[0].Y), false);
                for (var i = 1; i < screen.Count; i++)
                    ctx.LineTo(new Point(screen[i].X, screen[i].Y));
                ctx.EndFigure(false);
            }

            context.DrawGeometry(null, outlinePen, geometry);
            context.DrawGeometry(null, corePen, geometry);

            foreach (var tick in PolylineTicks.Along(screen, TickSpacingPx, TickFirstOffsetPx))
                DrawDirectionArrow(context, tick, arrowFill, arrowOutline);
        }
    }

    private static void DrawDirectionArrow(DrawingContext context, PolylineTick tick, IBrush fill, IPen outline)
    {
        // Tip ahead of the point, two corners behind and to either side.
        var tip = new Point(tick.X + tick.DirX * TickLength, tick.Y + tick.DirY * TickLength);
        var back = new Point(tick.X - tick.DirX * TickLength, tick.Y - tick.DirY * TickLength);
        var left = new Point(back.X - tick.DirY * TickHalfWidth, back.Y + tick.DirX * TickHalfWidth);
        var right = new Point(back.X + tick.DirY * TickHalfWidth, back.Y - tick.DirX * TickHalfWidth);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(tip, true);
            ctx.LineTo(left);
            ctx.LineTo(right);
            ctx.EndFigure(true);
        }

        context.DrawGeometry(fill, outline, geometry);
    }

    /// <summary>
    /// The satellite at the centre (the picture is centred on its subpoint) and the stations that are on the visible
    /// side, each with a text label and a tool tip. A station beyond the limb is not drawn.
    /// </summary>
    private void DrawMarkers(
        DrawingContext context, UiPalette palette, EarthViewFrame frame, Point centre, double scale, Rect bounds)
    {
        PlotMarkerDrawing.DrawSatelliteMarker(
            context, centre.X, centre.Y, PlotColors.ForIndex(0), isFocused: true, _renderCache);
        PlaceLabel(context, palette, frame.SatelliteName, centre, bounds);

        var basis = new GlobeBasis(frame.Camera);
        foreach (var station in frame.Stations)
        {
            var projected = basis.Project(station.LatitudeDeg, station.LongitudeDeg);
            if (!projected.Visible)
                continue;

            var point = new Point(centre.X + projected.X * scale, centre.Y - projected.Y * scale);
            if (!bounds.Inflate(20).Contains(point))
                continue;

            PlotMarkerDrawing.DrawGroundStationMarker(context, point.X, point.Y, palette, _renderCache);
            var text = string.IsNullOrWhiteSpace(station.Label)
                ? LocalizationService.Instance.Get("EarthView.Label.Station")
                : station.Label;
            PlaceLabel(context, palette, text, point, bounds);
        }
    }

    private void PlaceLabel(DrawingContext context, UiPalette palette, string text, Point anchor, Rect bounds)
    {
        var formatted = _textCache.Get(text, LabelFontSize, palette);
        var size = new Size(formatted.Width + 8, formatted.Height + 6);
        var rect = LabelPlacement.Choose(anchor, size, _labelRects, bounds);

        context.DrawRectangle(_textCache.GetBackgroundBrush(palette), null, rect, 3, 3);
        context.DrawText(formatted, new Point(rect.X + 4, rect.Y + 3));

        _labelRects.Add(rect);
        _markerHits.Add((anchor, text));
    }

    /// <summary>
    /// A small compass mark. North is up at the centre of the view; away from the centre the meridians
    /// converge, as they do on any globe.
    /// </summary>
    private void DrawNorthIndicator(DrawingContext context, UiPalette palette)
    {
        var letter = _textCache.Get("N", LabelFontSize, palette);
        var box = new Rect(10, 10, Math.Max(28, letter.Width + 14), 48);
        context.DrawRectangle(_textCache.GetBackgroundBrush(palette), null, box, 4, 4);

        var cx = box.X + box.Width / 2;
        var arrow = new StreamGeometry();
        using (var ctx = arrow.Open())
        {
            ctx.BeginFigure(new Point(cx, box.Y + 6), true);
            ctx.LineTo(new Point(cx + 6, box.Y + 22));
            ctx.LineTo(new Point(cx, box.Y + 18));
            ctx.LineTo(new Point(cx - 6, box.Y + 22));
            ctx.EndFigure(true);
        }

        context.DrawGeometry(_renderCache.GetBrush(palette.MapLabelForeground), null, arrow);
        context.DrawText(letter, new Point(cx - letter.Width / 2, box.Y + 25));
    }

    private void DrawFocusIndicator(DrawingContext context, UiPalette palette, double w, double h)
    {
        var rect = new Rect(3, 3, Math.Max(0, w - 6), Math.Max(0, h - 6));
        context.DrawRectangle(null, _renderCache.GetPen(palette.MapGroundTrackOutline, 5), rect);
        context.DrawRectangle(null, _renderCache.GetPen(palette.GlobeLimbStroke, 2.5), rect);
    }
}

/// <summary>
/// Exposes the globe to screen readers. Without a peer the control's <c>AutomationProperties.Name</c> and
/// <c>HelpText</c> (bound to the view model) have nothing to carry them to the platform.
/// </summary>
internal sealed class EarthViewAutomationPeer : ControlAutomationPeer
{
    public EarthViewAutomationPeer(EarthViewControl owner) : base(owner)
    {
    }

    protected override string? GetNameCore() =>
        AutomationProperties.GetName(Owner) is { Length: > 0 } name
            ? name
            : LocalizationService.Instance.Get("Window.EarthView.Title");

    protected override string? GetHelpTextCore() => AutomationProperties.GetHelpText(Owner);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;
}
