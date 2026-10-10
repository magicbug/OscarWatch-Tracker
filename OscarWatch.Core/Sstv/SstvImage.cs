namespace OscarWatch.Core.Sstv;

/// <summary>How a transmitted line was placed.</summary>
public enum SstvLineQuality : byte
{
    /// <summary>Not received yet.</summary>
    Missing,
    /// <summary>No usable sync: placed from the line period.</summary>
    Predicted,
    /// <summary>Placed on its own sync pulse.</summary>
    Locked,
}

/// <summary>24-bit RGB picture, row-major.</summary>
public sealed class SstvImage
{
    public SstvImage(int width, int height)
    {
        Width = width;
        Height = height;
        Rgb = new byte[width * height * 3];
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Rgb { get; }

    public void SetPixel(int x, int y, double r, double g, double b)
    {
        var o = (y * Width + x) * 3;
        Rgb[o] = ToByte(r);
        Rgb[o + 1] = ToByte(g);
        Rgb[o + 2] = ToByte(b);
    }

    public (byte R, byte G, byte B) GetPixel(int x, int y)
    {
        var o = (y * Width + x) * 3;
        return (Rgb[o], Rgb[o + 1], Rgb[o + 2]);
    }

    /// <summary>Set rows from <paramref name="fromRow"/> to the bottom to black.</summary>
    public void ClearFromRow(int fromRow)
    {
        fromRow = Math.Clamp(fromRow, 0, Height);
        Array.Clear(Rgb, fromRow * Width * 3, (Height - fromRow) * Width * 3);
    }

    public SstvImage Clone()
    {
        var copy = new SstvImage(Width, Height);
        Buffer.BlockCopy(Rgb, 0, copy.Rgb, 0, Rgb.Length);
        return copy;
    }

    /// <summary>Copy to 32-bit BGRA, the layout Avalonia bitmaps use.</summary>
    public void CopyToBgra(Span<byte> destination, int strideBytes)
    {
        for (var y = 0; y < Height; y++)
        {
            var row = destination.Slice(y * strideBytes, Width * 4);
            var src = y * Width * 3;
            for (var x = 0; x < Width; x++)
            {
                row[x * 4] = Rgb[src + 2];
                row[x * 4 + 1] = Rgb[src + 1];
                row[x * 4 + 2] = Rgb[src];
                row[x * 4 + 3] = 255;
                src += 3;
            }
        }
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
