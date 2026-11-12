using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OscarWatch.Core.Ft4;
using OscarWatch.Cw;

namespace OscarWatch.Controls;

/// <summary>
/// CW tuning view: a scrolling waterfall from 0 to 2000 Hz with the band the decoder reads
/// shaded, and a marker on the strongest steady note. Click a trace to decode it; right-click
/// to put the band back.
/// </summary>
public sealed class CwWaterfallControl : Control
{
    private const int Columns = CwReceiverService.WaterfallColumns;
    private const double MinHz = CwReceiverService.WaterfallMinHz;
    private const double MaxHz = CwReceiverService.WaterfallMaxHz;
    private const double BandLowHz = 400;
    private const double BandHighHz = 1200;
    /// <summary>Ten seconds at the receiver's row rate.</summary>
    private const int HistoryRows = (int)(CwReceiverService.SpectrumRowsPerSecond * 10);
    private const double RangeDb = 40;
    private const double ScaleHeight = 14;

    private static readonly double[] ScaleMarks = [400, 800, 1200, 1600];
    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(8, 10, 18));
    private static readonly IBrush BandShade = new SolidColorBrush(Color.FromArgb(40, 120, 200, 255));
    private static readonly IPen BandEdge = new Pen(new SolidColorBrush(Color.FromArgb(170, 140, 210, 255)), 1);
    private static readonly IPen ToneInBand = new Pen(new SolidColorBrush(Color.FromRgb(255, 235, 60)), 1.5);
    private static readonly IPen ToneOutOfBand = new Pen(new SolidColorBrush(Color.FromRgb(255, 120, 90)), 1.5, new DashStyle([4, 3], 0));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(230, 230, 230));

    private WriteableBitmap? _bitmap;
    private int _filledRows;
    private bool _primed;
    private double _noiseFloor = -80;
    private float[]? _spectrum;
    private double _toneHz = double.NaN;

    public static readonly DirectProperty<CwWaterfallControl, float[]?> SpectrumProperty =
        AvaloniaProperty.RegisterDirect<CwWaterfallControl, float[]?>(
            nameof(Spectrum),
            o => o._spectrum,
            (o, v) => o.Spectrum = v);

    public static readonly DirectProperty<CwWaterfallControl, double> ToneHzProperty =
        AvaloniaProperty.RegisterDirect<CwWaterfallControl, double>(
            nameof(ToneHz),
            o => o._toneHz,
            (o, v) => o.ToneHz = v);

    public static readonly DirectProperty<CwWaterfallControl, double> BandOffsetHzProperty =
        AvaloniaProperty.RegisterDirect<CwWaterfallControl, double>(
            nameof(BandOffsetHz),
            o => o._bandOffsetHz,
            (o, v) => o.BandOffsetHz = v);

    private double _bandOffsetHz;

    public CwWaterfallControl()
    {
        MinHeight = 80;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    /// <summary>A left click asks to decode the note at this frequency (hertz).</summary>
    public event EventHandler<double>? TuneRequested;

    /// <summary>A right click asks to put the decode band back home.</summary>
    public event EventHandler? TuneResetRequested;

    /// <summary>Hertz the shaded decode band sits above its home of 400 to 1200 Hz.</summary>
    public double BandOffsetHz
    {
        get => _bandOffsetHz;
        set
        {
            if (SetAndRaise(BandOffsetHzProperty, ref _bandOffsetHz, double.IsFinite(value) ? value : 0))
                InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var w = Bounds.Width;
        if (w < 2)
            return;

        if (point.Properties.IsRightButtonPressed)
        {
            TuneResetRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            var hz = MinHz + Math.Clamp(point.Position.X / w, 0, 1) * (MaxHz - MinHz);
            TuneRequested?.Invoke(this, hz);
            e.Handled = true;
        }
    }

    /// <summary>Power in dB from 0 to 2000 Hz. Each new array adds a waterfall row.</summary>
    public float[]? Spectrum
    {
        get => _spectrum;
        set
        {
            SetAndRaise(SpectrumProperty, ref _spectrum, value);
            if (value is { Length: > 0 })
                AddRow(value);
        }
    }

    /// <summary>Strongest steady note in hertz, or NaN for none.</summary>
    public double ToneHz
    {
        get => _toneHz;
        set
        {
            if (SetAndRaise(ToneHzProperty, ref _toneHz, value))
                InvalidateVisual();
        }
    }

    private void AddRow(float[] bins)
    {
        var row = new float[Columns];
        var n = Math.Min(Columns, bins.Length);
        Array.Copy(bins, row, n);
        for (var i = n; i < Columns; i++)
            row[i] = row[Math.Max(0, n - 1)];

        var floor = Ft4SpectrumAnalyzer.EstimateNoiseFloorDb(row, percentile: 0.20f);
        if (!_primed)
        {
            _noiseFloor = floor;
            _primed = true;
        }
        else
        {
            _noiseFloor += (floor - _noiseFloor) * (floor > _noiseFloor ? 0.02 : 0.008);
        }

        PaintRow(row);
        InvalidateVisual();
    }

    private unsafe void PaintRow(float[] row)
    {
        _bitmap ??= new WriteableBitmap(
            new PixelSize(Columns, HistoryRows),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using var fb = _bitmap.Lock();
        var ptr = (byte*)fb.Address.ToPointer();
        var stride = fb.RowBytes;
        if (_filledRows == 0)
            new Span<byte>(ptr, stride * HistoryRows).Clear();
        Buffer.MemoryCopy(ptr, ptr + stride, stride * (HistoryRows - 1), stride * (HistoryRows - 1));
        for (var x = 0; x < Columns; x++)
        {
            var c = MapColour(Math.Clamp((row[x] - _noiseFloor) / RangeDb, 0, 1));
            var p = ptr + x * 4;
            p[0] = c.B;
            p[1] = c.G;
            p[2] = c.R;
            p[3] = 255;
        }

        if (_filledRows < HistoryRows)
            _filledRows++;
    }

    private static Color MapColour(double t)
    {
        t = Math.Pow(t, 1.15);
        if (t < 0.25)
            return Color.FromRgb(0, 0, (byte)(30 + 400 * t));
        if (t < 0.5)
        {
            var u = (t - 0.25) / 0.25;
            return Color.FromRgb(0, (byte)(170 * u), (byte)(130 + 80 * u));
        }

        if (t < 0.75)
        {
            var u = (t - 0.5) / 0.25;
            return Color.FromRgb((byte)(210 * u), (byte)(170 + 50 * u), (byte)(210 * (1 - u)));
        }

        var v = (t - 0.75) / 0.25;
        return Color.FromRgb((byte)(210 + 45 * v), (byte)(220 + 35 * v), (byte)(40 + 215 * v));
    }

    private static double X(double hz, double width) => Ft4SpectrumAnalyzer.HzToPixel(hz, width, MinHz, MaxHz);

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 2 || h < ScaleHeight + 2)
            return;

        var waterfall = new Rect(0, ScaleHeight, w, h - ScaleHeight);
        context.FillRectangle(Background, new Rect(0, 0, w, h));
        if (_bitmap is not null && _filledRows > 0)
        {
            context.DrawImage(
                _bitmap,
                new Rect(0, 0, Columns, _filledRows),
                new Rect(0, waterfall.Y, w, waterfall.Height * _filledRows / HistoryRows));
        }

        var bandLeft = Math.Round(X(BandLowHz + _bandOffsetHz, w)) + 0.5;
        var bandRight = Math.Round(X(BandHighHz + _bandOffsetHz, w)) + 0.5;
        context.FillRectangle(BandShade, new Rect(bandLeft, waterfall.Y, bandRight - bandLeft, waterfall.Height));
        context.DrawLine(BandEdge, new Point(bandLeft, waterfall.Y), new Point(bandLeft, h));
        context.DrawLine(BandEdge, new Point(bandRight, waterfall.Y), new Point(bandRight, h));

        foreach (var hz in ScaleMarks)
        {
            var x = Math.Round(X(hz, w)) + 0.5;
            var label = new FormattedText(
                $"{hz:0}",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"),
                12,
                LabelBrush);
            context.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, 0, w - label.Width), -1));
        }

        if (double.IsFinite(_toneHz))
        {
            var inBand = _toneHz >= BandLowHz + _bandOffsetHz && _toneHz <= BandHighHz + _bandOffsetHz;
            var x = Math.Round(X(_toneHz, w)) + 0.5;
            context.DrawLine(inBand ? ToneInBand : ToneOutOfBand, new Point(x, waterfall.Y), new Point(x, h));
        }
    }
}
