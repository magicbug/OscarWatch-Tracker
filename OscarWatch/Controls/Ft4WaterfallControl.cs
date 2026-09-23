using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OscarWatch.Core.Ft4;

namespace OscarWatch.Controls;

/// <summary>
/// Scrolling spectrogram for FT4. Frequency left→right (passband), time scrolls down.
/// Click sets RX, and TX as well unless Hold Tx Freq is on.
/// </summary>
public sealed class Ft4WaterfallControl : Control
{
    public const int HistoryRows = 120;
    public const int SpectrumColumns = 400;

    private WriteableBitmap? _bitmap;
    private readonly float[] _latest = new float[SpectrumColumns];
    private bool _hasSpectrum;
    private bool _levelsPrimed;
    private double _noiseFloor = -80;

    /// <summary>Fixed dB span above the noise floor (WSJT-X-style). Avoids peak-auto gain blow-out.</summary>
    public const double DisplayRangeDb = 40.0;

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
            StatusTextProperty);
        FocusableProperty.OverrideDefaultValue<Ft4WaterfallControl>(true);
    }

    public Ft4WaterfallControl()
    {
        MinHeight = 140;
        Cursor = new Cursor(StandardCursorType.Cross);
        PointerPressed += OnPointerPressed;
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

    private void SetSpectrum(float[]? value)
    {
        SetAndRaise(SpectrumProperty, ref _spectrum, value);
        if (value is null || value.Length == 0)
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

    private void UpdateLevels(ReadOnlySpan<float> bins)
    {
        var floor = Ft4SpectrumAnalyzer.EstimateNoiseFloorDb(bins, percentile: 0.20f);
        if (!_levelsPrimed)
        {
            _noiseFloor = floor;
            _levelsPrimed = true;
            return;
        }

        // Slow EMA so a quiet moment does not slam the scale, and a loud tone does not raise the floor.
        _noiseFloor = _noiseFloor * 0.95 + floor * 0.05;
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
        EnsureBitmap();
        using var fb = _bitmap!.Lock();
        unsafe
        {
            var ptr = (byte*)fb.Address.ToPointer();
            var stride = fb.RowBytes;
            // Scroll down one row.
            Buffer.MemoryCopy(ptr, ptr + stride, stride * (HistoryRows - 1), stride * (HistoryRows - 1));
            var row = ptr;
            for (var x = 0; x < SpectrumColumns; x++)
            {
                var color = MapColor(bins[x]);
                row[x * 4 + 0] = color.B;
                row[x * 4 + 1] = color.G;
                row[x * 4 + 2] = color.R;
                row[x * 4 + 3] = 255;
            }
        }
    }

    private Color MapColor(float db)
    {
        // Map noise floor → +DisplayRangeDb into 0…1. Strong tones clip at white without
        // dragging the whole passband up (the previous peak-tracker caused that).
        var t = (db - _noiseFloor) / DisplayRangeDb;
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
        var p = e.GetPosition(this);
        var hz = Ft4SpectrumAnalyzer.PixelToHz(p.X, Bounds.Width, MinHz, MaxHz);
        hz = Math.Round(hz / 10.0) * 10.0;
        hz = Math.Clamp(hz, MinHz, MaxHz);
        RxAudioHz = hz;
        if (!HoldTxFrequency)
            TxAudioHz = hz;
        FrequencySelected?.Invoke(this, hz);
        e.Handled = true;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(8, 10, 18)), bounds);

        if (_bitmap is not null && _hasSpectrum)
        {
            context.DrawImage(_bitmap, new Rect(0, 0, SpectrumColumns, HistoryRows), bounds);
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

        DrawBracket(greenPen, rxHz);
        DrawBracket(redPen, txHz);

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
    }
}
