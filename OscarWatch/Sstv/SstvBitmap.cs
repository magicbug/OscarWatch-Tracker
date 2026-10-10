using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OscarWatch.Core.Sstv;

namespace OscarWatch.Sstv;

internal static class SstvBitmap
{
    public static WriteableBitmap Create(SstvImage image)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(image.Width, image.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        CopyInto(bitmap, image);
        return bitmap;
    }

    public static unsafe void CopyInto(WriteableBitmap bitmap, SstvImage image)
    {
        using var buffer = bitmap.Lock();
        var span = new Span<byte>((void*)buffer.Address, buffer.RowBytes * buffer.Size.Height);
        image.CopyToBgra(span, buffer.RowBytes);
    }
}
