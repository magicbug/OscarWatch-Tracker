using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OscarWatch.Core.Ft4;
using OscarWatch.Sstv;

namespace OscarWatch.Controls;

/// <summary>
/// SSTV signal view: a live spectrum trace above a scrolling waterfall, with markers at
/// the sync (1200 Hz), black (1500 Hz), VIS leader (1900 Hz) and white (2300 Hz) tones.
/// A good picture shows a steady stripe at 1200 Hz and activity between 1500 and 2300 Hz.
/// </summary>
public sealed class SstvSpectrumControl : Control
{
    private const int Columns = SstvSpectrumTap.Columns;
    private const double MinHz = SstvSpectrumTap.MinHz;
    private const double MaxHz = SstvSpectrumTap.MaxHz;
    private const int HistoryRows = 160;
    private const double RangeDb = 45;
    private const double SpectrumFraction = 0.36;

    private static readonly (double Hz, bool Dashed)[] Markers =
    [
        (1200, false),
        (1500, false),
        (1900, true),
        (2300, false),
    ];

    private readonly float[] _trace = new float[Columns];
    private WriteableBitmap? _bitmap;
    private int _filledRows;
    private bool _primed;
    private double _noiseFloor = -80;
    private float[]? _spectrum;

    public static readonly DirectProperty<SstvSpectrumControl, float[]?> SpectrumProperty =
        AvaloniaProperty.RegisterDirect<SstvSpectrumControl, float[]?>(
            nameof(Spectrum),
            o => o._spectrum,
            (o, v) => o.Spectrum = v);

    public SstvSpectrumControl()
    {
        MinHeight = 120;
        ClipToBounds = true;
    }

    /// <summary>Power in dB across the <see cref="SstvSpectrumTap"/> range. Each new array adds a waterfall row.</summary>
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
            Array.Copy(row, _trace, Columns);
            _primed = true;
        }
        else
        {
            _noiseFloor += (floor - _noiseFloor) * (floor > _noiseFloor ? 0.05 : 0.02);
            for (var i = 0; i < Columns; i++)
                _trace[i] += 0.5f * (row[i] - _trace[i]);
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
            var c = MapColour(Level(row[x]));
            var p = ptr + x * 4;
            p[0] = c.B;
            p[1] = c.G;
            p[2] = c.R;
            p[3] = 255;
        }

        if (_filledRows < HistoryRows)
            _filledRows++;
    }

    private double Level(float db) => Math.Clamp((db - _noiseFloor) / RangeDb, 0, 1);

    private static Color MapColour(double t)
    {
        t = Math.Pow(t, 1.15);
        if (t < 0.25)
        {
            var u = t / 0.25;
            return Color.FromRgb(0, 0, (byte)(30 + 100 * u));
        }

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
        if (w < 2 || h < 2)
            return;

        var topH = Math.Round(h * SpectrumFraction);
        var spectrumRect = new Rect(0, 0, w, topH);
        var waterfallRect = new Rect(0, topH + 2, w, Math.Max(1, h - topH - 2));
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(10, 26, 16)), spectrumRect);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(8, 10, 18)), waterfallRect);

        if (_bitmap is not null && _filledRows > 0)
        {
            var rows = _filledRows;
            context.DrawImage(
                _bitmap,
                new Rect(0, 0, Columns, rows),
                new Rect(0, waterfallRect.Y, w, waterfallRect.Height * rows / HistoryRows));
        }

        if (_primed)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                for (var i = 0; i < Columns; i++)
                {
                    var x = w * i / (Columns - 1);
                    var y = topH - 2 - Level(_trace[i]) * (topH - 14);
                    if (i == 0)
                        g.BeginFigure(new Point(x, y), false);
                    else
                        g.LineTo(new Point(x, y));
                }

                g.EndFigure(false);
            }

            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(255, 235, 60)), 1.2), geometry);
        }

        var markerPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1);
        var dashedPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1, new DashStyle([3, 3], 0));
        var labelBrush = new SolidColorBrush(Color.FromArgb(220, 230, 230, 230));
        foreach (var (hz, dashed) in Markers)
        {
            var x = Math.Round(X(hz, w)) + 0.5;
            context.DrawLine(dashed ? dashedPen : markerPen, new Point(x, 12), new Point(x, topH));
            var label = new FormattedText(
                $"{hz:0}",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"),
                10,
                labelBrush);
            context.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, 0, w - label.Width), 0));
        }
    }
}
