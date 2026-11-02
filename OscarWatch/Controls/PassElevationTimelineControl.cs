using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using OscarWatch.Core.Display;
using OscarWatch.Core.Models;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using System.Globalization;

namespace OscarWatch.Controls;

/// <summary>
/// Custom Avalonia control that renders upcoming satellite passes as filled elevation
/// "mountain" shapes on a horizontal time axis. Integrates as a collapsible panel
/// below the world map with click-to-focus and hover tooltips.
/// </summary>
public sealed class PassElevationTimelineControl : ThemeAwareControl
{
    /// <summary>Reserved space at the top so peak satellite labels do not touch the panel edge.</summary>
    internal const double LabelTopPadding = 18;

    /// <summary>Reserved space at the bottom for the time axis.</summary>
    internal const double TimeAxisBottomPadding = 16;

    /// <summary>Reserved space on the left for the elevation scale.</summary>
    internal const double ElevationScaleLeftPadding = 34;

    private const double LiveWindowAlignmentMinutes = 0.5;
    private const double ElevationLabelMinSpacing = 11;

    // --- Object pools for memory optimization ---
    private static readonly ThreadLocal<List<ElevationSample>> _profileBuffer = 
        new(() => new List<ElevationSample>(capacity: 64));

    // --- Styled Properties ---

    public static readonly StyledProperty<IReadOnlyList<PassInfo>?> PassesProperty =
        AvaloniaProperty.Register<PassElevationTimelineControl, IReadOnlyList<PassInfo>?>(nameof(Passes));

    public static readonly StyledProperty<int> TimeWindowMinutesProperty =
        AvaloniaProperty.Register<PassElevationTimelineControl, int>(
            nameof(TimeWindowMinutes),
            TimelineWindowLimits.DefaultMinutes);

    public static readonly StyledProperty<string?> FocusedNoradIdProperty =
        AvaloniaProperty.Register<PassElevationTimelineControl, string?>(nameof(FocusedNoradId));

    public static readonly StyledProperty<GroundStation?> GroundStationProperty =
        AvaloniaProperty.Register<PassElevationTimelineControl, GroundStation?>(nameof(GroundStation));

    public static readonly StyledProperty<DateTime> MapDisplayUtcProperty =
        AvaloniaProperty.Register<PassElevationTimelineControl, DateTime>(nameof(MapDisplayUtc));

    public static readonly StyledProperty<bool> DisplayTimesInUtcProperty =
        AvaloniaProperty.Register<PassElevationTimelineControl, bool>(nameof(DisplayTimesInUtc));

    public static readonly StyledProperty<bool> Use24HourClockProperty =
        AvaloniaProperty.Register<PassElevationTimelineControl, bool>(nameof(Use24HourClock));

    static PassElevationTimelineControl()
    {
        // MapDisplayUtc is handled with a 1 px time throttle (see ShouldInvalidateForTimeAdvance).
        AffectsRender<PassElevationTimelineControl>(
            PassesProperty,
            TimeWindowMinutesProperty,
            FocusedNoradIdProperty,
            GroundStationProperty,
            DisplayTimesInUtcProperty,
            Use24HourClockProperty);
        ClipToBoundsProperty.OverrideDefaultValue<PassElevationTimelineControl>(true);
        MinHeightProperty.OverrideDefaultValue<PassElevationTimelineControl>(80);
    }

    public PassElevationTimelineControl()
    {
        // Poll only: invalidate when the now-line / window drift reaches ≥ 1 px.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) =>
        {
            if (ShouldInvalidateForTimeAdvance())
                InvalidateVisual();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _refreshTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _refreshTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnThemeChanged()
    {
        _renderCache.Clear();
        _labelCache.Clear();
        _tooltipCacheDirty = true;
        _lastRenderedWindowStartUtc = DateTime.MinValue;
        _lastRenderedLiveUtc = DateTime.MinValue;
        base.OnThemeChanged();
    }

    // --- Events ---

    /// <summary>
    /// Raised when the operator clicks on a mountain shape to focus a satellite.
    /// The event argument is the NORAD ID of the selected satellite.
    /// </summary>
    public event EventHandler<string>? SatelliteFocusRequested;

    // --- Render caches ---

    private readonly RenderResourceCache _renderCache = new();
    private readonly FormattedTextCache _labelCache = new();
    private readonly DispatcherTimer _refreshTimer;
    private DateTime _lastRenderedWindowStartUtc = DateTime.MinValue;
    private DateTime _lastRenderedLiveUtc = DateTime.MinValue;

    // --- Cached pass geometry and optimization fields ---

    private Dictionary<string, TimelinePassEntry> _passEntries = new();
    
    // Pre-sorted collections to eliminate LINQ OrderBy().ToList() in render loop
    private readonly List<TimelinePassEntry> _sortedPasses = new();
    private bool _sortedPassesDirty = true;
    
    // Tooltip caching to reduce mouse movement allocations
    private readonly Dictionary<(string NoradId, long AosTicks, bool DisplayUtc, bool Use24Hour), string> _tooltipCache = new();
    private bool _tooltipCacheDirty = true;
    
    // Visible passes buffer for accessibility without LINQ chains
    private readonly List<TimelinePassEntry> _visiblePassesBuffer = new();

    // --- Orbit propagator (injected via property for integration) ---
    private IOrbitPropagator? _propagator;
    private GroundStation? _site;

    /// <summary>
    /// Sets the propagator and site used for elevation profile computation.
    /// Call this after the control is created and services are available.
    /// </summary>
    public void SetPropagator(IOrbitPropagator? propagator, GroundStation? site)
    {
        _propagator = propagator;
        _site = site;
        RecomputeProfiles();
    }

    // --- Properties ---

    public IReadOnlyList<PassInfo>? Passes
    {
        get => GetValue(PassesProperty);
        set => SetValue(PassesProperty, value);
    }

    public int TimeWindowMinutes
    {
        get => GetValue(TimeWindowMinutesProperty);
        set => SetValue(TimeWindowMinutesProperty, value);
    }

    public string? FocusedNoradId
    {
        get => GetValue(FocusedNoradIdProperty);
        set => SetValue(FocusedNoradIdProperty, value);
    }

    public GroundStation? GroundStation
    {
        get => GetValue(GroundStationProperty);
        set => SetValue(GroundStationProperty, value);
    }

    public DateTime MapDisplayUtc
    {
        get => GetValue(MapDisplayUtcProperty);
        set => SetValue(MapDisplayUtcProperty, value);
    }

    public bool DisplayTimesInUtc
    {
        get => GetValue(DisplayTimesInUtcProperty);
        set => SetValue(DisplayTimesInUtcProperty, value);
    }

    public bool Use24HourClock
    {
        get => GetValue(Use24HourClockProperty);
        set => SetValue(Use24HourClockProperty, value);
    }

    // --- Render ---

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PassesProperty)
        {
            RecomputeProfiles();
        }
        else if (change.Property == GroundStationProperty)
        {
            _site = GroundStation;
            RecomputeProfiles();
        }
        else if (change.Property == BoundsProperty)
        {
            InvalidateGeometryCache();
            _lastRenderedWindowStartUtc = DateTime.MinValue;
            _lastRenderedLiveUtc = DateTime.MinValue;
        }
        else if (change.Property == MapDisplayUtcProperty)
        {
            if (ShouldInvalidateForTimeAdvance())
                InvalidateVisual();
        }
        else if (change.Property == TimeWindowMinutesProperty)
        {
            _lastRenderedWindowStartUtc = DateTime.MinValue;
            _lastRenderedLiveUtc = DateTime.MinValue;
        }
        else if (change.Property == DisplayTimesInUtcProperty || 
                 change.Property == Use24HourClockProperty)
        {
            _tooltipCacheDirty = true;
        }
    }

    /// <summary>
    /// True when the map window start or live-now position has drifted by ≥ 1 pixel
    /// since the last paint (typical 2 h window: several seconds between redraws).
    /// </summary>
    internal bool ShouldInvalidateForTimeAdvance()
    {
        var plotWidth = Bounds.Width - ElevationScaleLeftPadding;
        return HasTimeAdvancedBeyondThreshold(
            MapDisplayUtc,
            DateTime.UtcNow,
            _lastRenderedWindowStartUtc,
            _lastRenderedLiveUtc,
            plotWidth,
            TimeWindowMinutes);
    }

    /// <summary>
    /// Returns true when either the window start or live clock has moved enough that the
    /// corresponding X position would change by at least one pixel.
    /// </summary>
    internal static bool HasTimeAdvancedBeyondThreshold(
        DateTime windowStartUtc,
        DateTime liveUtc,
        DateTime lastRenderedWindowStartUtc,
        DateTime lastRenderedLiveUtc,
        double plotWidth,
        int windowMinutes,
        double thresholdPx = 1.0)
    {
        if (plotWidth <= 0 || windowMinutes <= 0)
            return true;

        if (lastRenderedWindowStartUtc == DateTime.MinValue || lastRenderedLiveUtc == DateTime.MinValue)
            return true;

        var minutesPerPixel = windowMinutes / plotWidth;
        var thresholdMinutes = minutesPerPixel * thresholdPx;
        var windowDelta = Math.Abs((windowStartUtc - lastRenderedWindowStartUtc).TotalMinutes);
        var liveDelta = Math.Abs((liveUtc - lastRenderedLiveUtc).TotalMinutes);
        return windowDelta >= thresholdMinutes || liveDelta >= thresholdMinutes;
    }

    /// <summary>
    /// Recomputes elevation profiles for all passes asynchronously.
    /// </summary>
    private void RecomputeProfiles()
    {
        var passes = Passes;
        var propagator = _propagator;
        var site = _site ?? GroundStation;

        if (passes is null || passes.Count == 0)
        {
            _passEntries.Clear();
            InvalidateCaches();
            InvalidateVisual();
            return;
        }

        var now = DateTime.UtcNow;
        var sampleInterval = TimeSpan.FromSeconds(30);

        // Compute profiles on background thread
        _ = Task.Run(() =>
        {
            var entries = new Dictionary<string, TimelinePassEntry>();
            for (var i = 0; i < passes.Count; i++)
            {
                var pass = passes[i];
                var key = $"{pass.NoradId}_{pass.AosUtc.Ticks}";

                IReadOnlyList<ElevationSample> profile;
                if (propagator is not null && site is not null && propagator.HasSatellite(pass.NoradId))
                {
                    try
                    {
                        profile = ElevationProfileBuilder.Build(pass, propagator, site, sampleInterval, now);
                    }
                    catch
                    {
                        profile = BuildFallbackProfile(pass, now);
                    }
                }
                else
                {
                    profile = BuildFallbackProfile(pass, now);
                }

                entries[key] = new TimelinePassEntry
                {
                    Pass = pass,
                    Profile = profile,
                };
            }

            Dispatcher.UIThread.Post(() =>
            {
                _passEntries = entries;
                InvalidateCaches();
                InvalidateGeometryCache();
                InvalidateVisual();
            });
        });
    }

    /// <summary>
    /// Builds a simple triangular fallback profile when the propagator is unavailable.
    /// Uses AOS=0°, peak=MaxElevation, LOS=0°.
    /// </summary>
    private static IReadOnlyList<ElevationSample> BuildFallbackProfile(PassInfo pass, DateTime referenceUtc)
    {
        return new[]
        {
            new ElevationSample((pass.AosUtc - referenceUtc).TotalMinutes, 0.0),
            new ElevationSample((pass.MaxElevationUtc - referenceUtc).TotalMinutes, pass.MaxElevationDeg),
            new ElevationSample((pass.LosUtc - referenceUtc).TotalMinutes, 0.0),
        };
    }

    /// <summary>
    /// Invalidates all cached StreamGeometry objects, forcing rebuild on next render.
    /// </summary>
    private void InvalidateGeometryCache()
    {
        foreach (var entry in _passEntries.Values)
        {
            entry.Geometry = null;
        }
    }

    /// <summary>
    /// Gets the sorted pass entries, using cached version if available.
    /// Eliminates OrderBy().ToList() allocations in render loop.
    /// </summary>
    private IReadOnlyList<TimelinePassEntry> GetSortedPasses()
    {
        if (_sortedPassesDirty)
        {
            _sortedPasses.Clear();
            _sortedPasses.AddRange(_passEntries.Values);
            // Use deterministic sorting with NoradId as tie-breaker for identical AosUtc times
            _sortedPasses.Sort((a, b) =>
            {
                var timeComparison = a.Pass.AosUtc.CompareTo(b.Pass.AosUtc);
                return timeComparison != 0 ? timeComparison : 
                       string.Compare(a.Pass.NoradId, b.Pass.NoradId, StringComparison.Ordinal);
            });
            _sortedPassesDirty = false;
        }
        return _sortedPasses;
    }

    /// <summary>
    /// Invalidates the sorted pass cache.
    /// </summary>
    private void InvalidateCaches()
    {
        _sortedPassesDirty = true;
        _tooltipCacheDirty = true;
    }

    /// <summary>
    /// Gets a reusable List<ElevationSample> buffer from thread-local pool.
    /// </summary>
    private static List<ElevationSample> GetProfileBuffer()
    {
        var buffer = _profileBuffer.Value!;
        buffer.Clear();
        return buffer;
    }

    /// <summary>
    /// Gets cached tooltip text for a pass.
    /// </summary>
    internal string GetCachedTooltip(PassInfo pass)
    {
        if (_tooltipCacheDirty)
        {
            _tooltipCache.Clear();
            _tooltipCacheDirty = false;
        }
        
        var key = (pass.NoradId, pass.AosUtc.Ticks, DisplayTimesInUtc, Use24HourClock);
        if (!_tooltipCache.TryGetValue(key, out var tooltip))
        {
            tooltip = BuildPassToolTip(pass);
            _tooltipCache[key] = tooltip;
        }
        return tooltip;
    }

    /// <summary>
    /// Builds or returns the cached StreamGeometry for a pass entry.
    /// </summary>
    internal static StreamGeometry BuildGeometry(
        TimelinePassEntry entry,
        double width,
        double height,
        double windowMinutes,
        DateTime now)
    {
        if (entry.Geometry is not null
            && Math.Abs(entry.GeometryWidth - width) < 0.5
            && Math.Abs(entry.GeometryHeight - height) < 0.5)
        {
            return entry.Geometry;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var profile = entry.Profile;
            if (profile.Count == 0)
            {
                entry.Geometry = geometry;
                entry.GeometryWidth = width;
                entry.GeometryHeight = height;
                return geometry;
            }

            var (plotLeft, plotTop, plotBottom, plotWidth, plotHeight) = GetPlotLayout(width, height);
            var baselineY = plotBottom;

            // Start at baseline at first sample X
            var x0 = TimeToX(profile[0].MinutesFromNow, width, windowMinutes);
            ctx.BeginFigure(new Point(x0, baselineY), true);

            // Follow elevation samples
            for (var i = 0; i < profile.Count; i++)
            {
                var sample = profile[i];
                var x = TimeToX(sample.MinutesFromNow, width, windowMinutes);
                var y = ElevToYInPlot(sample.ElevationDeg, plotHeight, plotTop);
                ctx.LineTo(new Point(x, y));
            }

            // Close back to baseline at last sample X
            var xN = TimeToX(profile[^1].MinutesFromNow, width, windowMinutes);
            ctx.LineTo(new Point(xN, baselineY));
            ctx.EndFigure(true);
        }

        entry.Geometry = geometry;
        entry.GeometryWidth = width;
        entry.GeometryHeight = height;
        return geometry;
    }

    // --- Coordinate mapping (exposed as internal static for testing) ---

    /// <summary>
    /// Maps a time value (minutes from now) to an X pixel coordinate.
    /// </summary>
    internal static double TimeToX(double minutesFromNow, double width, double windowMinutes)
        => minutesFromNow / windowMinutes * width;

    /// <summary>
    /// Maps an elevation angle (0–90°) to a Y pixel coordinate.
    /// 0° is at the bottom (Y = height), 90° is at the top (Y = 0).
    /// </summary>
    internal static double ElevToY(double elevDeg, double height)
        => height - (elevDeg / 90.0) * height;

    internal static (double plotLeft, double plotTop, double plotBottom, double plotWidth, double plotHeight) GetPlotLayout(
        double totalWidth,
        double totalHeight)
    {
        var plotLeft = Math.Min(ElevationScaleLeftPadding, totalWidth / 6);
        var reservedTop = Math.Min(LabelTopPadding, totalHeight / 4);
        var reservedBottom = Math.Min(TimeAxisBottomPadding, totalHeight / 4);
        var plotTop = reservedTop;
        var plotBottom = Math.Max(plotTop + 1, totalHeight - reservedBottom);
        var plotWidth = Math.Max(1, totalWidth - plotLeft);
        var plotHeight = plotBottom - plotTop;
        return (plotLeft, plotTop, plotBottom, plotWidth, plotHeight);
    }

    internal static double GetMinutesFromWindowStart(DateTime utc, DateTime windowStartUtc)
        => (utc - windowStartUtc).TotalMinutes;

    internal static bool IsPassInProgress(PassInfo pass, DateTime activeUtc)
        => pass.AosUtc <= activeUtc && activeUtc < pass.LosUtc;

    internal static bool IsWindowAlignedToLiveUtc(DateTime windowStartUtc, DateTime liveUtc)
        => Math.Abs(GetMinutesFromWindowStart(liveUtc, windowStartUtc)) < LiveWindowAlignmentMinutes;

    internal static string FormatTimeAxisClockLabel(
        DateTime windowStartUtc,
        int minutesFromStart,
        ClockDisplayFormat clockFormat,
        bool useUtc,
        CultureInfo? culture = null)
        => PassDisplayFormat.FormatAxisTime(
            windowStartUtc.AddMinutes(minutesFromStart),
            useUtc,
            clockFormat,
            culture);

    internal static string FormatElevationLabel(int elevationDeg)
        => string.Create(CultureInfo.InvariantCulture, $"{elevationDeg}°");

    internal static int GetTimeAxisIntervalMinutes(int windowMinutes)
    {
        if (windowMinutes <= 30)
            return 5;
        if (windowMinutes <= 60)
            return 10;
        if (windowMinutes <= 90)
            return 15;
        if (windowMinutes <= 180)
            return 30;
        return 60;
    }

    internal static string FormatTimeWindowLabel(int windowMinutes, ILocalizationService? localization = null)
    {
        var loc = localization ?? LocalizationService.Instance;
        if (windowMinutes % 60 == 0)
            return loc.Get("Main.Timeline.WindowHours", windowMinutes / 60);

        return loc.Get("Main.Timeline.WindowMinutes", windowMinutes);
    }

    internal static int[] GetElevationScaleTicks(double plotHeight)
    {
        if (plotHeight < 55)
            return [0, 45, 90];

        if (plotHeight < 80)
            return [0, 30, 60, 90];

        return [0, 30, 45, 60, 90];
    }

    internal static double ElevToYInPlot(double elevDeg, double plotHeight, double plotTop)
        => plotTop + ElevToY(elevDeg, plotHeight);

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;

        var palette = UiPaletteResolver.Current;
        context.FillRectangle(
            _renderCache.GetBrush(palette.SkyPlotBackground),
            new Rect(0, 0, w, h));

        var windowMinutes = TimelineWindowLimits.Clamp(TimeWindowMinutes);
        var (plotLeft, plotTop, plotBottom, plotWidth, plotHeight) = GetPlotLayout(w, h);
        var windowStartUtc = MapDisplayUtc;
        var liveUtc = DateTime.UtcNow;
        var clockFormat = PassDisplayFormat.FromSettings(Use24HourClock);
        var useUtc = DisplayTimesInUtc;

        if (ShouldShowEmptyState(windowMinutes, windowStartUtc))
        {
            DrawEmptyState(context, w, h, palette);
            _lastRenderedWindowStartUtc = windowStartUtc;
            _lastRenderedLiveUtc = liveUtc;
            return;
        }

        // --- Grid lines ---
        DrawGrid(context, w, h, plotLeft, plotTop, plotBottom, plotWidth, plotHeight, windowMinutes, windowStartUtc, liveUtc, clockFormat, useUtc, palette);

        // --- Mountain shapes ---
        DrawMountains(context, w, h, plotLeft, plotTop, plotBottom, plotWidth, plotHeight, windowMinutes, windowStartUtc, palette);

        // --- Live "now" indicator (wall-clock time vs map window) ---
        DrawLiveNowIndicator(context, plotLeft, plotTop, plotBottom, plotWidth, windowMinutes, windowStartUtc, liveUtc, palette);

        _lastRenderedWindowStartUtc = windowStartUtc;
        _lastRenderedLiveUtc = liveUtc;
    }

    private void DrawGrid(
        DrawingContext context,
        double w,
        double h,
        double plotLeft,
        double plotTop,
        double plotBottom,
        double plotWidth,
        double plotHeight,
        int windowMinutes,
        DateTime windowStartUtc,
        DateTime liveUtc,
        ClockDisplayFormat clockFormat,
        bool useUtc,
        UiPalette palette)
    {
        var gridColor = Color.FromArgb(64, palette.SkyPlotBorder.R, palette.SkyPlotBorder.G, palette.SkyPlotBorder.B);
        var gridPen = _renderCache.GetPen(gridColor, 1);

        var intervalMinutes = GetTimeAxisIntervalMinutes(windowMinutes);
        for (var mins = intervalMinutes; mins < windowMinutes; mins += intervalMinutes)
        {
            var x = plotLeft + (double)mins / windowMinutes * plotWidth;
            context.DrawLine(gridPen, new Point(x, plotTop), new Point(x, plotBottom));
        }

        DrawTimeAxisLabels(context, w, h, plotLeft, plotBottom, plotWidth, windowMinutes, windowStartUtc, liveUtc, clockFormat, useUtc, palette);
        DrawElevationScale(context, w, plotLeft, plotTop, plotBottom, plotHeight, palette);
    }

    private void DrawElevationScale(
        DrawingContext context,
        double width,
        double plotLeft,
        double plotTop,
        double plotBottom,
        double plotHeight,
        UiPalette palette)
    {
        var gridColor = Color.FromArgb(64, palette.SkyPlotBorder.R, palette.SkyPlotBorder.G, palette.SkyPlotBorder.B);
        var gridPen = _renderCache.GetPen(gridColor, 1);
        var dashedPen = _renderCache.GetDashedPen(gridColor, 1);
        var axisColor = Color.FromArgb(120, palette.SkyPlotBorder.R, palette.SkyPlotBorder.G, palette.SkyPlotBorder.B);
        var axisPen = _renderCache.GetPen(axisColor, 1);

        context.DrawLine(axisPen, new Point(plotLeft, plotTop), new Point(plotLeft, plotBottom));

        var ticks = GetElevationScaleTicks(plotHeight);
        var lastLabelY = double.NegativeInfinity;

        foreach (var elevationDeg in ticks)
        {
            var y = ElevToYInPlot(elevationDeg, plotHeight, plotTop);

            if (elevationDeg == 0)
            {
                context.DrawLine(gridPen, new Point(plotLeft, y), new Point(width, y));
            }
            else
            {
                var linePen = elevationDeg == 45 ? dashedPen : gridPen;
                context.DrawLine(linePen, new Point(plotLeft, y), new Point(width, y));
            }

            var label = _labelCache.Get(FormatElevationLabel(elevationDeg), 8, palette);
            var labelY = y - label.Height / 2;

            if (elevationDeg == 0)
                labelY = Math.Min(labelY, plotBottom - label.Height - 1);
            else if (elevationDeg == 90)
                labelY = Math.Max(labelY, plotTop + 1);

            if (elevationDeg is not (0 or 90)
                && Math.Abs(labelY - lastLabelY) < ElevationLabelMinSpacing)
            {
                continue;
            }

            lastLabelY = labelY;
            context.DrawText(label, new Point(Math.Max(2, plotLeft - label.Width - 4), labelY));
        }
    }

    private void DrawTimeAxisLabels(
        DrawingContext context,
        double width,
        double totalHeight,
        double plotLeft,
        double plotBottom,
        double plotWidth,
        int windowMinutes,
        DateTime windowStartUtc,
        DateTime liveUtc,
        ClockDisplayFormat clockFormat,
        bool useUtc,
        UiPalette palette)
    {
        var leftLabel = IsWindowAlignedToLiveUtc(windowStartUtc, liveUtc)
            ? LocalizationService.Instance.Get("Common.Now")
            : FormatTimeAxisClockLabel(windowStartUtc, 0, clockFormat, useUtc);
        var leftText = _labelCache.Get(leftLabel, 9, palette);
        var axisLabelY = plotBottom + Math.Max(0, (totalHeight - plotBottom - leftText.Height) / 2);
        context.DrawText(leftText, new Point(plotLeft + 2, axisLabelY));

        var intervalMinutes = GetTimeAxisIntervalMinutes(windowMinutes);
        for (var mins = intervalMinutes; mins < windowMinutes; mins += intervalMinutes)
        {
            var x = plotLeft + (double)mins / windowMinutes * plotWidth;
            var timeLabel = FormatTimeAxisClockLabel(windowStartUtc, mins, clockFormat, useUtc);
            var text = _labelCache.Get(timeLabel, 9, palette);
            var labelX = Math.Clamp(x - text.Width / 2, plotLeft, Math.Max(plotLeft, width - text.Width));
            context.DrawText(text, new Point(labelX, axisLabelY));
        }

        var zoneLabel = PassDisplayFormat.FormatTimeZoneLabel(useUtc);
        var zoneText = _labelCache.Get(zoneLabel, 8, palette);
        var windowLabel = FormatTimeWindowLabel(windowMinutes);
        var windowText = _labelCache.Get(windowLabel, 8, palette);
        var zoneX = Math.Max(0, width - zoneText.Width - 2);
        var windowX = Math.Max(0, zoneX - windowText.Width - 8);
        context.DrawText(windowText, new Point(windowX, axisLabelY));
        context.DrawText(zoneText, new Point(zoneX, axisLabelY));
    }

    private void DrawMountains(
        DrawingContext context,
        double w,
        double h,
        double plotLeft,
        double plotTop,
        double plotBottom,
        double plotWidth,
        double plotHeight,
        int windowMinutes,
        DateTime windowStartUtc,
        UiPalette palette)
    {
        if (_passEntries.Count == 0)
            return;

        var activeUtc = windowStartUtc;
        var focusedId = FocusedNoradId;

        var sorted = GetSortedPasses();

        foreach (var entry in sorted)
        {
            if (!IsFocusedPass(entry.Pass, focusedId))
                DrawMountain(context, entry, w, h, plotLeft, plotTop, plotBottom, plotWidth, plotHeight, windowMinutes, windowStartUtc, activeUtc, palette, isFocused: false);
        }

        if (!string.IsNullOrWhiteSpace(focusedId))
        {
            foreach (var entry in sorted)
            {
                if (IsFocusedPass(entry.Pass, focusedId))
                    DrawMountain(context, entry, w, h, plotLeft, plotTop, plotBottom, plotWidth, plotHeight, windowMinutes, windowStartUtc, activeUtc, palette, isFocused: true);
            }
        }
    }

    private void DrawMountain(
        DrawingContext context,
        TimelinePassEntry entry,
        double w,
        double h,
        double plotLeft,
        double plotTop,
        double plotBottom,
        double plotWidth,
        double plotHeight,
        int windowMinutes,
        DateTime windowStartUtc,
        DateTime activeUtc,
        UiPalette palette,
        bool isFocused)
    {
        var profile = entry.Profile;
        if (profile.Count < 2)
            return;

        var passAosMinutes = GetMinutesFromWindowStart(entry.Pass.AosUtc, windowStartUtc);
        var passLosMinutes = GetMinutesFromWindowStart(entry.Pass.LosUtc, windowStartUtc);

        if (passLosMinutes < 0 || passAosMinutes > windowMinutes)
            return;

        if (isFocused && !IsFocusedPass(entry.Pass, FocusedNoradId))
            return;

        var currentProfile = GetProfileBuffer();
        foreach (var sample in profile)
        {
            var sampleUtc = entry.Pass.AosUtc + TimeSpan.FromMinutes(
                (sample.MinutesFromNow - profile[0].MinutesFromNow));
            var minutesFromWindowStart = GetMinutesFromWindowStart(sampleUtc, windowStartUtc);
            currentProfile.Add(new ElevationSample(minutesFromWindowStart, sample.ElevationDeg));
        }

        var geo = BuildInlineGeometry(currentProfile, plotWidth, plotTop, plotBottom, plotHeight, windowMinutes);

        var distanceFraction = Math.Clamp(Math.Max(0, passAosMinutes) / windowMinutes, 0, 1);
        var fillOpacity = isFocused
            ? (byte)220
            : (byte)(128 - (int)(64 * distanceFraction));
        var strokeWidth = isFocused ? 2.0 : 1.0;

        var satColor = PlotColors.ForIndex(entry.Pass.PaletteIndex);
        var fillColor = Color.FromArgb(fillOpacity, satColor.R, satColor.G, satColor.B);
        var strokeColor = Color.FromArgb(255, satColor.R, satColor.G, satColor.B);

        var fillBrush = _renderCache.GetBrush(fillColor);
        var strokePen = _renderCache.GetPen(strokeColor, strokeWidth);

        using (context.PushTransform(new Matrix(1, 0, 0, 1, plotLeft, 0)))
        using (context.PushClip(new Rect(0, 0, plotWidth, h)))
        {
            context.DrawGeometry(fillBrush, strokePen, geo);
            DrawInProgressHighlight(
                context,
                entry.Pass,
                geo,
                plotTop,
                plotBottom,
                plotWidth,
                plotHeight,
                windowMinutes,
                windowStartUtc,
                activeUtc,
                satColor,
                strokePen);
        }

        DrawPeakLabel(context, entry, currentProfile, plotLeft, plotWidth, plotTop, plotHeight, windowMinutes, palette, isFocused);
    }

    private void DrawInProgressHighlight(
        DrawingContext context,
        PassInfo pass,
        StreamGeometry geometry,
        double plotTop,
        double plotBottom,
        double plotWidth,
        double plotHeight,
        int windowMinutes,
        DateTime windowStartUtc,
        DateTime activeUtc,
        Color satColor,
        Pen strokePen)
    {
        if (!IsPassInProgress(pass, activeUtc))
            return;

        var aosMinutes = GetMinutesFromWindowStart(pass.AosUtc, windowStartUtc);
        var activeMinutes = GetMinutesFromWindowStart(activeUtc, windowStartUtc);
        var x0 = TimeToX(aosMinutes, plotWidth, windowMinutes);
        var x1 = TimeToX(activeMinutes, plotWidth, windowMinutes);
        if (x1 <= x0)
            return;

        var highlightFill = _renderCache.GetBrush(Color.FromArgb(210, satColor.R, satColor.G, satColor.B));
        using (context.PushClip(new Rect(x0, plotTop, x1 - x0, plotBottom - plotTop)))
        {
            context.DrawGeometry(highlightFill, strokePen, geometry);
        }
    }

    private static bool IsFocusedPass(PassInfo pass, string? focusedNoradId) =>
        !string.IsNullOrWhiteSpace(focusedNoradId)
        && string.Equals(pass.NoradId, focusedNoradId, StringComparison.Ordinal);

    private bool ShouldShowEmptyState(int windowMinutes, DateTime windowStartUtc)
    {
        if (Passes is null || Passes.Count == 0)
            return true;

        if (_passEntries.Count == 0)
            return false;

        return !_passEntries.Values.Any(entry =>
        {
            var passAosMinutes = GetMinutesFromWindowStart(entry.Pass.AosUtc, windowStartUtc);
            var passLosMinutes = GetMinutesFromWindowStart(entry.Pass.LosUtc, windowStartUtc);
            return passLosMinutes > 0 && passAosMinutes < windowMinutes;
        });
    }

    private void DrawEmptyState(DrawingContext context, double width, double height, UiPalette palette)
    {
        var message = LocalizationService.Instance.Get("Main.Pass.None");
        var text = _labelCache.Get(message, 11, palette);
        context.DrawText(
            text,
            new Point(Math.Max(0, (width - text.Width) / 2), Math.Max(0, (height - text.Height) / 2)));
    }

    private static StreamGeometry BuildInlineGeometry(
        List<ElevationSample> profile,
        double width,
        double plotTop,
        double plotBottom,
        double plotHeight,
        double windowMinutes)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if (profile.Count == 0)
                return geometry;

            var baselineY = plotBottom;
            var x0 = TimeToX(profile[0].MinutesFromNow, width, windowMinutes);
            ctx.BeginFigure(new Point(x0, baselineY), true);

            for (var i = 0; i < profile.Count; i++)
            {
                var sample = profile[i];
                var x = TimeToX(sample.MinutesFromNow, width, windowMinutes);
                var y = ElevToYInPlot(sample.ElevationDeg, plotHeight, plotTop);
                ctx.LineTo(new Point(x, y));
            }

            var xN = TimeToX(profile[^1].MinutesFromNow, width, windowMinutes);
            ctx.LineTo(new Point(xN, baselineY));
            ctx.EndFigure(true);
        }

        return geometry;
    }

    private void DrawPeakLabel(
        DrawingContext context,
        TimelinePassEntry entry,
        List<ElevationSample> profile,
        double plotLeft,
        double plotWidth,
        double plotTop,
        double plotHeight,
        double windowMinutes,
        UiPalette palette,
        bool isFocused)
    {
        // Find peak sample
        var peakSample = profile[0];
        for (var i = 1; i < profile.Count; i++)
        {
            if (profile[i].ElevationDeg > peakSample.ElevationDeg)
                peakSample = profile[i];
        }

        var peakX = plotLeft + TimeToX(peakSample.MinutesFromNow, plotWidth, windowMinutes);
        var peakY = ElevToYInPlot(peakSample.ElevationDeg, plotHeight, plotTop);

        // Only draw if peak is within visible area
        if (peakX < plotLeft || peakX > plotLeft + plotWidth)
            return;

        var text = _labelCache.Get(entry.Pass.SatelliteName, isFocused ? 10 : 9, palette);
        var labelX = peakX - text.Width / 2;
        var labelY = peakY - text.Height - 2;

        labelX = Math.Clamp(labelX, plotLeft, Math.Max(plotLeft, plotLeft + plotWidth - text.Width));
        labelY = Math.Max(2, labelY);

        var bg = new Rect(labelX - 3, labelY - 1, text.Width + 6, text.Height + 2);
        context.FillRectangle(_labelCache.GetBackgroundBrush(palette), bg);
        context.DrawText(text, new Point(labelX, labelY));
    }

    private void DrawLiveNowIndicator(
        DrawingContext context,
        double plotLeft,
        double plotTop,
        double plotBottom,
        double plotWidth,
        int windowMinutes,
        DateTime windowStartUtc,
        DateTime liveUtc,
        UiPalette palette)
    {
        var liveMinutes = GetMinutesFromWindowStart(liveUtc, windowStartUtc);
        if (liveMinutes < 0 || liveMinutes > windowMinutes)
            return;

        var x = plotLeft + TimeToX(liveMinutes, plotWidth, windowMinutes);
        var isAligned = IsWindowAlignedToLiveUtc(windowStartUtc, liveUtc);
        var nowColor = Color.FromArgb(isAligned ? (byte)180 : (byte)220, palette.SkyPlotLabel.R, palette.SkyPlotLabel.G, palette.SkyPlotLabel.B);
        var nowPen = isAligned
            ? _renderCache.GetPen(nowColor, 2)
            : _renderCache.GetDashedPen(nowColor, 2);
        context.DrawLine(nowPen, new Point(x, plotTop), new Point(x, plotBottom));
    }

    // --- Pointer interaction ---

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (Math.Abs(e.Delta.Y) < 0.01)
        {
            base.OnPointerWheelChanged(e);
            return;
        }

        var next = TimelineWindowLimits.Zoom(TimeWindowMinutes, Math.Sign(e.Delta.Y));
        if (next != TimeWindowMinutes)
            TimeWindowMinutes = next;

        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var pos = e.GetPosition(this);
        var hit = HitTest(pos.X);
        if (hit is not null)
        {
            RaiseSatelliteFocusRequested(hit.NoradId);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var pos = e.GetPosition(this);
        var hit = HitTest(pos.X);
        if (hit is not null)
        {
            ToolTip.SetTip(this, GetCachedTooltip(hit));
            ToolTip.SetIsOpen(this, true);
        }
        else
        {
            ToolTip.SetIsOpen(this, false);
            ToolTip.SetTip(this, null);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ToolTip.SetIsOpen(this, false);
        ToolTip.SetTip(this, null);
    }

    /// <summary>
    /// Performs hit testing at the given X coordinate. Returns the pass with
    /// the highest elevation at the clicked time, or null if no pass is there.
    /// </summary>
    internal PassInfo? HitTest(double clickX)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || _passEntries.Count == 0)
            return null;

        var windowMinutes = TimelineWindowLimits.Clamp(TimeWindowMinutes);
        var (plotLeft, _, _, plotWidth, _) = GetPlotLayout(w, h);
        if (clickX < plotLeft || clickX > plotLeft + plotWidth)
            return null;

        var clickMinutes = (clickX - plotLeft) / plotWidth * windowMinutes;
        var windowStartUtc = MapDisplayUtc;

        PassInfo? bestPass = null;
        double bestElev = -1;

        foreach (var entry in _passEntries.Values)
        {
            var pass = entry.Pass;
            var passAosMin = GetMinutesFromWindowStart(pass.AosUtc, windowStartUtc);
            var passLosMin = GetMinutesFromWindowStart(pass.LosUtc, windowStartUtc);

            if (clickMinutes < passAosMin || clickMinutes > passLosMin)
                continue;

            var elev = InterpolateElevation(entry.Profile, clickMinutes, pass, windowStartUtc);
            if (elev > 0 && elev > bestElev)
            {
                bestElev = elev;
                bestPass = pass;
            }
        }

        return bestPass;
    }

    /// <summary>
    /// Interpolates the elevation at a given minutes-from-now value using the stored profile.
    /// </summary>
    internal static double InterpolateElevation(
        IReadOnlyList<ElevationSample> profile,
        double targetMinutes,
        PassInfo pass,
        DateTime windowStartUtc)
    {
        if (profile.Count == 0)
            return 0;

        var firstSampleMin = GetMinutesFromWindowStart(pass.AosUtc, windowStartUtc);
        var lastSampleMin = GetMinutesFromWindowStart(pass.LosUtc, windowStartUtc);

        if (targetMinutes < firstSampleMin || targetMinutes > lastSampleMin)
            return 0;

        // Linear interpolation between profile points
        for (var i = 0; i < profile.Count - 1; i++)
        {
            var sampleUtcI = pass.AosUtc + TimeSpan.FromMinutes(
                profile[i].MinutesFromNow - profile[0].MinutesFromNow);
            var sampleUtcNext = pass.AosUtc + TimeSpan.FromMinutes(
                profile[i + 1].MinutesFromNow - profile[0].MinutesFromNow);

            var minI = GetMinutesFromWindowStart(sampleUtcI, windowStartUtc);
            var minNext = GetMinutesFromWindowStart(sampleUtcNext, windowStartUtc);

            if (targetMinutes >= minI && targetMinutes <= minNext)
            {
                var t = (minNext - minI) > 0
                    ? (targetMinutes - minI) / (minNext - minI)
                    : 0;
                return profile[i].ElevationDeg + t * (profile[i + 1].ElevationDeg - profile[i].ElevationDeg);
            }
        }

        return 0;
    }

    // --- Internal helpers ---

    internal void RaiseSatelliteFocusRequested(string noradId)
        => SatelliteFocusRequested?.Invoke(this, noradId);

    private string BuildPassToolTip(PassInfo pass)
    {
        var duration = pass.Duration;
        var minutes = duration.TotalSeconds < 30
            ? 0
            : (int)Math.Round(duration.TotalMinutes, MidpointRounding.AwayFromZero);
        var durationText = minutes == 1
            ? LocalizationService.Instance.Get("Pass.DurationOneMinute")
            : LocalizationService.Instance.Get("Pass.DurationMinutes", minutes);

        var clockFormat = PassDisplayFormat.FromSettings(Use24HourClock);
        var aosText = PassDisplayFormat.FormatHoverTime(pass.AosUtc, DisplayTimesInUtc, clockFormat);
        var losText = PassDisplayFormat.FormatHoverTime(pass.LosUtc, DisplayTimesInUtc, clockFormat);

        return LocalizationService.Instance.Get(
            "Main.Timeline.PassTooltip",
            pass.SatelliteName,
            aosText,
            losText,
            $"{pass.MaxElevationDeg:F1}°",
            durationText);
    }

    // --- Accessibility ---

    protected override AutomationPeer OnCreateAutomationPeer()
        => new PassElevationTimelineAutomationPeer(this);

    /// <summary>
    /// Returns a summary of visible passes for screen reader access.
    /// </summary>
    internal string GetAccessiblePassSummary()
    {
        if (_passEntries.Count == 0)
            return LocalizationService.Instance.Get("Main.Pass.None");

        var windowMinutes = TimelineWindowLimits.Clamp(TimeWindowMinutes);
        var windowStartUtc = MapDisplayUtc;
        
        // Use optimized visible pass filtering
        _visiblePassesBuffer.Clear();
        var sortedPasses = GetSortedPasses();
        foreach (var entry in sortedPasses)
        {
            if (GetMinutesFromWindowStart(entry.Pass.LosUtc, windowStartUtc) > 0 &&
                GetMinutesFromWindowStart(entry.Pass.AosUtc, windowStartUtc) < windowMinutes)
            {
                _visiblePassesBuffer.Add(entry);
                if (_visiblePassesBuffer.Count >= 10) break;
            }
        }

        if (_visiblePassesBuffer.Count == 0)
            return LocalizationService.Instance.Get("Main.Pass.None");

        var clockFormat = PassDisplayFormat.FromSettings(Use24HourClock);
        var useUtc = DisplayTimesInUtc;
        
        // Build parts without LINQ Select()
        var parts = new List<string>(_visiblePassesBuffer.Count);
        foreach (var entry in _visiblePassesBuffer)
        {
            var p = entry.Pass;
            var aosText = PassDisplayFormat.FormatHoverTime(p.AosUtc, useUtc, clockFormat);
            var losText = PassDisplayFormat.FormatHoverTime(p.LosUtc, useUtc, clockFormat);
            parts.Add($"{p.SatelliteName}: {aosText}-{losText}, max {p.MaxElevationDeg:F0}°");
        }

        return $"{_visiblePassesBuffer.Count} passes: " + string.Join("; ", parts);
    }
}

internal sealed class PassElevationTimelineAutomationPeer : ControlAutomationPeer
{
    public PassElevationTimelineAutomationPeer(PassElevationTimelineControl owner)
        : base(owner)
    {
    }

    protected override string GetNameCore()
    {
        if (Owner is PassElevationTimelineControl ctrl)
            return ctrl.GetAccessiblePassSummary();
        return "Pass Elevation Timeline";
    }

    protected override AutomationControlType GetAutomationControlTypeCore()
        => AutomationControlType.Custom;
}

/// <summary>
/// Internal data class holding cached geometry and profile data for a single pass
/// in the elevation timeline.
/// </summary>
internal sealed class TimelinePassEntry
{
    public required PassInfo Pass { get; init; }
    public IReadOnlyList<ElevationSample> Profile { get; set; } = [];
    public StreamGeometry? Geometry { get; set; }
    public double GeometryWidth { get; set; }
    public double GeometryHeight { get; set; }
}
