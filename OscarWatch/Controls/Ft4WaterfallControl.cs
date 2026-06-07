using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace OscarWatch.Controls;

public sealed class Ft4WaterfallControl : Control
{
    public static readonly StyledProperty<int> RxFrequencyHzProperty =
        AvaloniaProperty.Register<Ft4WaterfallControl, int>(nameof(RxFrequencyHz), 1500);

    public static readonly StyledProperty<int> TxFrequencyHzProperty =
        AvaloniaProperty.Register<Ft4WaterfallControl, int>(nameof(TxFrequencyHz), 1500);

    public static readonly StyledProperty<int> MaxFrequencyHzProperty =
        AvaloniaProperty.Register<Ft4WaterfallControl, int>(nameof(MaxFrequencyHz), 3500);

    private readonly List<byte[]> _rows = [];
    private WriteableBitmap? _bitmap;
    private const int MaxRows = 256;

    public int RxFrequencyHz
    {
        get => GetValue(RxFrequencyHzProperty);
        set => SetValue(RxFrequencyHzProperty, value);
    }

    public int TxFrequencyHz
    {
        get => GetValue(TxFrequencyHzProperty);
        set => SetValue(TxFrequencyHzProperty, value);
    }

    public int MaxFrequencyHz
    {
        get => GetValue(MaxFrequencyHzProperty);
        set => SetValue(MaxFrequencyHzProperty, value);
    }

    public void PushRow(byte[] row)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _rows.Add(row);
            if (_rows.Count > MaxRows)
                _rows.RemoveAt(0);
            RebuildBitmap();
            InvalidateVisual();
        });
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (_bitmap is not null)
            context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height), bounds);

        var maxHz = Math.Max(1, MaxFrequencyHz);
        DrawMarker(context, bounds, RxFrequencyHz, maxHz, Brushes.Lime);
        DrawMarker(context, bounds, TxFrequencyHz, maxHz, Brushes.Coral);
    }

    private void DrawMarker(DrawingContext context, Rect bounds, int frequencyHz, int maxHz, IBrush brush)
    {
        var x = bounds.Width * frequencyHz / maxHz;
        var pen = new Pen(brush, 2);
        context.DrawLine(pen, new Point(x, 0), new Point(x, bounds.Height));
    }

    private void RebuildBitmap()
    {
        if (_rows.Count == 0)
            return;

        var width = _rows[^1].Length;
        var height = _rows.Count;
        _bitmap ??= new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

        using var frame = _bitmap.Lock();
        unsafe
        {
            var ptr = (byte*)frame.Address;
            var stride = frame.RowBytes;
            for (var y = 0; y < height; y++)
            {
                var row = _rows[y];
                var dest = ptr + y * stride;
                for (var x = 0; x < width; x++)
                {
                    var value = row[x];
                    var offset = x * 4;
                    dest[offset] = value;
                    dest[offset + 1] = (byte)Math.Min(255, value + value / 2);
                    dest[offset + 2] = (byte)Math.Min(255, value * 2);
                    dest[offset + 3] = 255;
                }
            }
        }
    }
}
