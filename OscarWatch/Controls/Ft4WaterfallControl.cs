using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OscarWatch.Core.Ft4;

namespace OscarWatch.Controls;

/// <summary>
/// Scrolling spectrogram for FT4. Frequency left to right (passband), time scrolls down.
/// A left click sets RX, and TX as well unless Hold Tx Freq is on.
/// With Hold Tx Freq, a left click sets RX and a right click sets TX.
/// </summary>
public sealed class Ft4WaterfallControl : Control
{
    public const int HistoryRows = 120;
    public const int SpectrumColumns = 400;

    private WriteableBitmap? _bitmap;
    private readonly float[] _latest = new float[SpectrumColumns];
    private readonly float[] _rows = new float[HistoryRows * SpectrumColumns];
    private int _filledRows;
    private bool _hasSpectrum;
    private bool _levelsPrimed;
    private double _noiseFloor = -80;
    private readonly double[] _segmentFloors = new double[Ft4SpectrumAnalyzer.FlattenSegments];
    private readonly double[] _rowSegmentFloors = new double[Ft4SpectrumAnalyzer.FlattenSegments];
    private readonly float[] _baseline = new float[SpectrumColumns];
    // Per history row (0 = newest): the slot start when that row is the first of a new slot.
    private readonly DateTime?[] _rowSlotStarts = new DateTime?[HistoryRows];
    private DateTime _lastRowSlotStart = DateTime.MinValue;
    private double? _hoverHz;

    /// <summary>Slot length in seconds for the period lines and UTC labels (WSJT-X style). 0 draws none.</summary>
    public static readonly StyledProperty<double> SlotSecondsProperty =
        AvaloniaProperty.Register<Ft4WaterfallControl, double>(nameof(SlotSeconds));

    /// <summary>Run on a double-click, after RX has moved to the clicked frequency.</summary>
    public static readonly StyledProperty<ICommand?> DecodeAgainCommandProperty =
        AvaloniaProperty.Register<Ft4WaterfallControl, ICommand?>(nameof(DecodeAgainCommand));

    /// <summary>
    /// Measure the floor per frequency segment instead of across the whole passband, so the
    /// edges of the radio's SSB filter are not painted darker than the middle (WSJT-X Flatten).
    /// </summary>
    public static readonly StyledProperty<bool> FlattenProperty =
        AvaloniaProperty.Register<Ft4WaterfallControl, bool>(nameof(Flatten));

    /// <summary>dB span above the noise floor. Avoids peak-auto gain blow-out.</summary>
    public static readonly StyledProperty<double> DisplayRangeDbProperty =
        AvaloniaProperty.Register<Ft4WaterfallControl, double>(
            nameof(DisplayRangeDb),
            Ft4Settings.DefaultWaterfallRangeDb);

    public static readonly StyledProperty<double> TxAudioHzProperty =
        AvaloniaProperty.Register<Ft4WaterfallControl, double>(nameof(TxAudioHz), 1500);

    public static readonly StyledProperty<double> RxAudioHzProperty =
        AvaloniaProperty.Register<Ft4WaterfallControl, double>(nameof(RxAudioHz), 1500);

    public static readonly StyledProperty<bool> HoldTxFrequencyProperty =
        AvaloniaProperty.Register<Ft4WaterfallControl, bool>(nameof(HoldTxFrequency));

    public static readonly StyledProperty<double> MinHzProperty =
        AvaloniaProperty.Register<Ft4WaterfallControl, double>(nameof(MinHz), Ft4SpectrumAnalyzer.DefaultMinHz);

    public static readonly StyledProperty<double> MaxHzProperty =
        AvaloniaProperty.Register<Ft4WaterfallControl, double>(nameof(MaxHz), Ft4SpectrumAnalyzer.DefaultMaxHz);

    public static readonly StyledProperty<string?> StatusTextProperty =
        AvaloniaProperty.Register<Ft4WaterfallControl, string?>(nameof(StatusText));

    /// <summary>False for display-only use (Terminal packet): no RX/TX brackets and clicks do not tune.</summary>
    public static readonly StyledProperty<bool> ShowTuningBracketsProperty =
        AvaloniaProperty.Register<Ft4WaterfallControl, bool>(nameof(ShowTuningBrackets), true);

    /// <summary>Fixed tones drawn as labelled dashed lines, for example the 1200 and 2200 Hz AFSK tones.</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> ToneMarkersHzProperty =
        AvaloniaProperty.Register<Ft4WaterfallControl, IReadOnlyList<double>?>(nameof(ToneMarkersHz));

    /// <summary>
    /// Lowest level the automatic noise floor may follow. A near-silent input otherwise has its
    /// soundcard hiss stretched across the whole colour range.
    /// </summary>
    public static readonly StyledProperty<double> MinNoiseFloorDbProperty =
        AvaloniaProperty.Register<Ft4WaterfallControl, double>(nameof(MinNoiseFloorDb), double.NegativeInfinity);

    public static readonly DirectProperty<Ft4WaterfallControl, float[]?> SpectrumProperty =
        AvaloniaProperty.RegisterDirect<Ft4WaterfallControl, float[]?>(
            nameof(Spectrum),
            o => o._spectrum,
            (o, v) => o.SetSpectrum(v));

    private float[]? _spectrum;

    public event EventHandler<double>? FrequencySelected;

    static Ft4WaterfallControl()
    {
        AffectsRender<Ft4WaterfallControl>(
            TxAudioHzProperty,
            RxAudioHzProperty,
            HoldTxFrequencyProperty,
            MinHzProperty,
            MaxHzProperty,
            StatusTextProperty,
            ShowTuningBracketsProperty,
            ToneMarkersHzProperty);
        DisplayRangeDbProperty.Changed.AddClassHandler<Ft4WaterfallControl>((control, _) =>
            control.RepaintSpectrum());
        FlattenProperty.Changed.AddClassHandler<Ft4WaterfallControl>((control, _) =>
            control._levelsPrimed = false);
        FocusableProperty.OverrideDefaultValue<Ft4WaterfallControl>(true);
    }

    public Ft4WaterfallControl()
    {
        MinHeight = 140;
        Cursor = new Cursor(StandardCursorType.Cross);
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => SetHoverHz(null);
        ContextRequested += (_, e) => e.Handled = true;
    }

    public double TxAudioHz
    {
        get => GetValue(TxAudioHzProperty);
        set => SetValue(TxAudioHzProperty, value);
    }

    /// <summary>RX decode marker (WSJT-X green). Falls back to TX when unset.</summary>
    public double RxAudioHz
    {
        get => GetValue(RxAudioHzProperty);
        set => SetValue(RxAudioHzProperty, value);
    }

    /// <summary>
    /// When true, waterfall clicks move RX only (TX stays). When false, TX follows RX.
    /// </summary>
    public bool HoldTxFrequency
    {
        get => GetValue(HoldTxFrequencyProperty);
        set => SetValue(HoldTxFrequencyProperty, value);
    }

    public double MinHz
    {
        get => GetValue(MinHzProperty);
        set => SetValue(MinHzProperty, value);
    }

    public double MaxHz
    {
        get => GetValue(MaxHzProperty);
        set => SetValue(MaxHzProperty, value);
    }

    public string? StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public bool ShowTuningBrackets
    {
        get => GetValue(ShowTuningBracketsProperty);
        set => SetValue(ShowTuningBracketsProperty, value);
    }

    public IReadOnlyList<double>? ToneMarkersHz
    {
        get => GetValue(ToneMarkersHzProperty);
        set => SetValue(ToneMarkersHzProperty, value);
    }

    public double MinNoiseFloorDb
    {
        get => GetValue(MinNoiseFloorDbProperty);
        set => SetValue(MinNoiseFloorDbProperty, value);
    }

    public double SlotSeconds
    {
        get => GetValue(SlotSecondsProperty);
        set => SetValue(SlotSecondsProperty, value);
    }

    public ICommand? DecodeAgainCommand
    {
        get => GetValue(DecodeAgainCommandProperty);
        set => SetValue(DecodeAgainCommandProperty, value);
    }

    public bool Flatten
    {
        get => GetValue(FlattenProperty);
        set => SetValue(FlattenProperty, value);
    }

    /// <summary>Decibels above the automatic noise floor painted on the waterfall.</summary>
    public double DisplayRangeDb
    {
        get => GetValue(DisplayRangeDbProperty);
        set => SetValue(DisplayRangeDbProperty, value);
    }

    private void SetSpectrum(float[]? value)
    {
        SetAndRaise(SpectrumProperty, ref _spectrum, value);
        if (value is null)
        {
            _filledRows = 0;
            _hasSpectrum = false;
            _bitmap?.Dispose();
            _bitmap = null;
            _levelsPrimed = false;
            Array.Clear(_rowSlotStarts);
            _lastRowSlotStart = DateTime.MinValue;
            InvalidateVisual();
            return;
        }

        if (value.Length == 0)
            return;

        var n = Math.Min(SpectrumColumns, value.Length);
        Array.Copy(value, _latest, n);
        for (var i = n; i < SpectrumColumns; i++)
            _latest[i] = _latest[Math.Max(0, n - 1)];

        UpdateLevels(_latest);
        PushRow(_latest);
        _hasSpectrum = true;
        InvalidateVisual();
    }

    public float[]? Spectrum
    {
        get => _spectrum;
        set => SetSpectrum(value);
    }

    // The range slider sets contrast; the floor follows the band quickly. New rows are
    // coloured once and history is never restretched, so a fast floor does not pump.
    private void UpdateLevels(ReadOnlySpan<float> bins)
    {
        if (Flatten)
        {
            Ft4SpectrumAnalyzer.MeasureSegmentFloorsDb(bins, _rowSegmentFloors);
            for (var k = 0; k < _segmentFloors.Length; k++)
            {
                var floor = Math.Max(_rowSegmentFloors[k], MinNoiseFloorDb);
                _segmentFloors[k] = _levelsPrimed
                    ? Ft4SpectrumAnalyzer.FollowNoiseFloor(_segmentFloors[k], floor)
                    : floor;
            }

            Ft4SpectrumAnalyzer.InterpolateBaselineDb(_segmentFloors, _baseline);
        }
        else
        {
            var floor = Math.Max(Ft4SpectrumAnalyzer.EstimateNoiseFloorDb(bins, percentile: 0.20f), MinNoiseFloorDb);
            _noiseFloor = _levelsPrimed ? Ft4SpectrumAnalyzer.FollowNoiseFloor(_noiseFloor, floor) : floor;
            Array.Fill(_baseline, (float)_noiseFloor);
        }

        _levelsPrimed = true;
    }

    private void EnsureBitmap()
    {
        if (_bitmap is not null)
            return;
        _bitmap = new WriteableBitmap(
            new PixelSize(SpectrumColumns, HistoryRows),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using var fb = _bitmap.Lock();
        unsafe
        {
            var ptr = (byte*)fb.Address.ToPointer();
            var len = fb.RowBytes * HistoryRows;
            new Span<byte>(ptr, len).Clear();
        }
    }

    private void PushRow(ReadOnlySpan<float> bins)
    {
        var occupied = Math.Min(_filledRows, HistoryRows - 1);
        if (occupied > 0)
        {
            Array.Copy(_rows, 0, _rows, SpectrumColumns, occupied * SpectrumColumns);
            Array.Copy(_rowSlotStarts, 0, _rowSlotStarts, 1, occupied);
        }

        _rowSlotStarts[0] = null;
        if (SlotSeconds > 0)
        {
            var slotStart = Ft4SlotClock.SlotStartUtc(Ft4Clock.UtcNow, SlotSeconds);
            if (_lastRowSlotStart != DateTime.MinValue && slotStart != _lastRowSlotStart)
                _rowSlotStarts[0] = slotStart;
            _lastRowSlotStart = slotStart;
        }
        else
        {
            _lastRowSlotStart = DateTime.MinValue;
        }

        for (var x = 0; x < SpectrumColumns; x++)
            _rows[x] = bins[x];

        if (_filledRows < HistoryRows)
            _filledRows++;

        ScrollAndPaintNewest(bins);
    }

    private void ScrollAndPaintNewest(ReadOnlySpan<float> bins)
    {
        EnsureBitmap();
        var range = Ft4Settings.ClampWaterfallRangeDb(DisplayRangeDb);
        using var fb = _bitmap!.Lock();
        unsafe
        {
            var ptr = (byte*)fb.Address.ToPointer();
            var stride = fb.RowBytes;
            Buffer.MemoryCopy(ptr, ptr + stride, stride * (HistoryRows - 1), stride * (HistoryRows - 1));
            PaintSamples(ptr, bins, range);
        }

        InvalidateVisual();
    }

    /// <summary>Recolour stored rows when the operator changes the range. Not used for live updates.</summary>
    private void RepaintSpectrum()
    {
        if (_filledRows == 0)
            return;

        EnsureBitmap();
        var range = Ft4Settings.ClampWaterfallRangeDb(DisplayRangeDb);
        using var fb = _bitmap!.Lock();
        unsafe
        {
            var ptr = (byte*)fb.Address.ToPointer();
            var stride = fb.RowBytes;
            for (var y = 0; y < HistoryRows; y++)
            {
                var row = ptr + (y * stride);
                if (y >= _filledRows)
                {
                    new Span<byte>(row, SpectrumColumns * 4).Clear();
                    continue;
                }

                PaintSamples(row, _rows.AsSpan(y * SpectrumColumns, SpectrumColumns), range);
            }
        }

        InvalidateVisual();
    }

    private unsafe void PaintSamples(byte* row, ReadOnlySpan<float> bins, double range)
    {
        for (var x = 0; x < SpectrumColumns; x++)
        {
            var color = MapColor(bins[x] - _baseline[x], range);
            var pixel = row + (x * 4);
            pixel[0] = color.B;
            pixel[1] = color.G;
            pixel[2] = color.R;
            pixel[3] = 255;
        }
    }

    private static Color MapColor(float dbAboveFloor, double rangeDb)
    {
        // Map noise floor → +range into 0…1. Strong tones clip at white without
        // dragging the whole passband up (the previous peak-tracker caused that).
        var t = dbAboveFloor / rangeDb;
        t = Math.Clamp(t, 0, 1);
        // Mild gamma so mid-level noise stays blue/cyan rather than yellow.
        t = Math.Pow(t, 1.15);

        // Dark → blue → cyan → yellow → white
        if (t < 0.25)
        {
            var u = t / 0.25;
            return Color.FromRgb(0, 0, (byte)(20 + 100 * u));
        }

        if (t < 0.5)
        {
            var u = (t - 0.25) / 0.25;
            return Color.FromRgb(0, (byte)(160 * u), (byte)(120 + 80 * u));
        }

        if (t < 0.75)
        {
            var u = (t - 0.5) / 0.25;
            return Color.FromRgb((byte)(200 * u), (byte)(160 + 60 * u), (byte)(180 * (1 - u)));
        }

        {
            var u = (t - 0.75) / 0.25;
            return Color.FromRgb((byte)(200 + 55 * u), (byte)(220 + 35 * u), (byte)(40 + 215 * u));
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ShowTuningBrackets)
            return;

        var props = e.GetCurrentPoint(this).Properties;
        var right = props.IsRightButtonPressed;
        var left = props.IsLeftButtonPressed;
        if (!left && !(right && HoldTxFrequency))
            return;

        var hz = PointerHz(e);
        if (right && !left && HoldTxFrequency)
            TxAudioHz = hz;
        else
        {
            RxAudioHz = hz;
            if (!HoldTxFrequency)
                TxAudioHz = hz;
        }

        FrequencySelected?.Invoke(this, hz);
        e.Handled = true;
        InvalidateVisual();

        if (left && e.ClickCount == 2 && DecodeAgainCommand is { } decode && decode.CanExecute(null))
            decode.Execute(null);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e) =>
        SetHoverHz(ShowTuningBrackets ? PointerHz(e) : null);

    private void SetHoverHz(double? hz)
    {
        if (_hoverHz == hz)
            return;
        _hoverHz = hz;
        InvalidateVisual();
    }

    private double PointerHz(PointerEventArgs e)
    {
        var hz = Ft4SpectrumAnalyzer.PixelToHz(e.GetPosition(this).X, Bounds.Width, MinHz, MaxHz);
        hz = Math.Round(hz / 10.0) * 10.0;
        return Math.Clamp(hz, MinHz, MaxHz);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(8, 10, 18)), bounds);

        if (_bitmap is not null && _hasSpectrum)
        {
            context.DrawImage(_bitmap, new Rect(0, 0, SpectrumColumns, HistoryRows), bounds);
            if (SlotSeconds > 0)
                DrawSlotLines(context, bounds);
        }
        else
        {
            var text = StatusText ?? " ";
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                12,
                Brushes.Gray);
            context.DrawText(ft, new Point(10, Math.Max(8, (bounds.Height - ft.Height) / 2)));
        }

        // WSJT-X-style FT4 filter brackets: green around RX, red around TX.
        // Each pair is two vertical legs joined by a horizontal line at the top.
        // TX is always drawn. A centre outside the passband is pulled in so both legs
        // stay on the waterfall (a border-pixel bracket is clipped and looks gone).
        var rxHz = RxAudioHz > 0 ? RxAudioHz : TxAudioHz;
        var txHz = TxAudioHz;
        var half = Ft4SpectrumAnalyzer.Ft4FilterHalfWidthHz;
        var green = new SolidColorBrush(Color.FromRgb(80, 220, 100));
        var red = new SolidColorBrush(Color.FromRgb(255, 80, 80));
        var greenPen = new Pen(green, 1.5);
        var redPen = new Pen(red, 1.5);

        void DrawBracket(Pen pen, double centreHz)
        {
            var centre = Ft4SpectrumAnalyzer.VisibleBracketCentreHz(centreHz, half, MinHz, MaxHz);
            var left = Ft4SpectrumAnalyzer.HzToPixel(centre - half, bounds.Width, MinHz, MaxHz);
            var right = Ft4SpectrumAnalyzer.HzToPixel(centre + half, bounds.Width, MinHz, MaxHz);
            context.DrawLine(pen, new Point(left, 0), new Point(left, bounds.Height));
            context.DrawLine(pen, new Point(right, 0), new Point(right, bounds.Height));
            context.DrawLine(pen, new Point(left, 0), new Point(right, 0));
        }

        if (ShowTuningBrackets)
        {
            DrawBracket(greenPen, rxHz);
            DrawBracket(redPen, txHz);
        }

        if (ToneMarkersHz is { } markers)
        {
            var markerPen = new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 200, 80)), 1, new DashStyle([4, 3], 0));
            var markerBrush = new SolidColorBrush(Color.FromRgb(255, 210, 110));
            foreach (var hz in markers)
            {
                if (hz < MinHz || hz > MaxHz)
                    continue;
                var x = Ft4SpectrumAnalyzer.HzToPixel(hz, bounds.Width, MinHz, MaxHz);
                context.DrawLine(markerPen, new Point(x, 0), new Point(x, bounds.Height - 16));
                var label = new FormattedText(
                    $"{hz:0}",
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Consolas"),
                    12,
                    markerBrush);
                context.DrawText(label, new Point(x + 3, 2));
            }
        }

        // Frequency ticks
        var labelBrush = new SolidColorBrush(Color.FromArgb(200, 220, 220, 220));
        foreach (var hz in new[] { 500.0, 1000.0, 1500.0, 2000.0, 2500.0 })
        {
            if (hz < MinHz || hz > MaxHz)
                continue;
            var tickX = Ft4SpectrumAnalyzer.HzToPixel(hz, bounds.Width, MinHz, MaxHz);
            context.DrawLine(
                new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1),
                new Point(tickX, bounds.Height - 14),
                new Point(tickX, bounds.Height));
            var ft = new FormattedText(
                $"{hz:0}",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"),
                10,
                labelBrush);
            context.DrawText(ft, new Point(tickX + 2, bounds.Height - ft.Height - 1));
        }

        if (ShowTuningBrackets && _hoverHz is { } hoverHz)
            DrawHoverReadout(context, bounds, hoverHz);
    }

    /// <summary>A line under the first row of each slot, labelled with that slot's UTC start.</summary>
    private void DrawSlotLines(DrawingContext context, Rect bounds)
    {
        var rowHeight = bounds.Height / HistoryRows;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1);
        var brush = new SolidColorBrush(Color.FromArgb(220, 230, 230, 230));
        var rows = Math.Min(_filledRows, HistoryRows);
        for (var row = 0; row < rows; row++)
        {
            if (_rowSlotStarts[row] is not { } start)
                continue;
            var y = Math.Round((row + 1) * rowHeight) + 0.5;
            context.DrawLine(pen, new Point(0, y), new Point(bounds.Width, y));
            var label = new FormattedText(
                start.ToString("HH:mm:ss.f", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"),
                10,
                brush);
            context.DrawText(label, new Point(4, Math.Max(0, y - label.Height - 1)));
        }
    }

    /// <summary>Dashed line and audio frequency under the mouse, rounded as a click would set it.</summary>
    private void DrawHoverReadout(DrawingContext context, Rect bounds, double hz)
    {
        var x = Ft4SpectrumAnalyzer.HzToPixel(hz, bounds.Width, MinHz, MaxHz);
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1, new DashStyle([3, 3], 0));
        context.DrawLine(pen, new Point(x, 0), new Point(x, bounds.Height));
        var label = new FormattedText(
            $"{hz:0} Hz",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Consolas"),
            11,
            Brushes.White);
        var labelX = x + 4 + label.Width > bounds.Width ? x - 4 - label.Width : x + 4;
        var origin = new Point(labelX, 4);
        context.FillRectangle(
            new SolidColorBrush(Color.FromArgb(170, 8, 10, 18)),
            new Rect(origin.X - 2, origin.Y - 1, label.Width + 4, label.Height + 2));
        context.DrawText(label, origin);
    }
}
