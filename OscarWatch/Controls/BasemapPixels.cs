using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Serilog;

namespace OscarWatch.Controls;

/// <summary>
/// The equirectangular world map as a managed pixel array, so the Earth View can sample it at
/// arbitrary latitudes and longitudes. Pixels are 32 bit BGRA (blue in the lowest byte).
/// </summary>
internal sealed class BasemapPixels
{
    /// <summary>
    /// Width the map is decoded at. The source image is 4096 wide, so this keeps full detail.
    /// Earth View shows only a small patch of the map for low orbits (about 455 pixels across for a
    /// 400 km orbit at this width), so a smaller decode looks soft. Costs about 32 MB while in use.
    /// </summary>
    public const int DecodeWidth = 4096;

    private static readonly ILogger Log = Serilog.Log.ForContext<BasemapPixels>();

    private const string MapUri = "avares://OscarWatch/Assets/Maps/world_map.jpg";

    private readonly uint[] _pixels;

    /// <summary>Wraps an existing pixel array. Used by the loader and by tests.</summary>
    internal BasemapPixels(uint[] pixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
        if (pixels.Length != checked(width * height))
            throw new ArgumentException("Pixel count must equal width times height.", nameof(pixels));

        _pixels = pixels;
        Width = width;
        Height = height;
    }

    private BasemapPixels()
    {
        _pixels = [];
    }

    /// <summary>A basemap that has no pixels. <see cref="Sample"/> returns 0 for it.</summary>
    public static BasemapPixels Unavailable { get; } = new();

    public int Width { get; }

    public int Height { get; }

    /// <summary>False when the map could not be loaded.</summary>
    public bool IsAvailable => _pixels.Length > 0;

    /// <summary>
    /// Bilinear sample of the map at a geographic position, as BGRA. Longitude wraps around the
    /// antimeridian, and latitude is clamped at the poles. Returns 0 when the map is unavailable or
    /// the position is not a finite number.
    /// </summary>
    public uint Sample(double latDeg, double lonDeg)
    {
        if (!IsAvailable || !double.IsFinite(latDeg) || !double.IsFinite(lonDeg))
            return 0;

        // Longitude into [0, 360), so the horizontal wrap below only ever steps by one pixel.
        var lon360 = ((lonDeg + 180.0) % 360.0 + 360.0) % 360.0;
        var lat = Math.Clamp(latDeg, -90.0, 90.0);

        // Pixel centres sit at half-integer positions, hence the 0.5 offset.
        var u = lon360 / 360.0 * Width - 0.5;
        var v = (90.0 - lat) / 180.0 * Height - 0.5;

        var x0f = Math.Floor(u);
        var y0f = Math.Floor(v);
        var wx = (int)((u - x0f) * 256.0);
        var wy = (int)((v - y0f) * 256.0);

        var x0 = WrapX((int)x0f);
        var x1 = WrapX((int)x0f + 1);
        var y0 = Math.Clamp((int)y0f, 0, Height - 1);
        var y1 = Math.Clamp((int)y0f + 1, 0, Height - 1);

        var p00 = _pixels[y0 * Width + x0];
        var p10 = _pixels[y0 * Width + x1];
        var p01 = _pixels[y1 * Width + x0];
        var p11 = _pixels[y1 * Width + x1];

        // Integer weights summing to 65536 keep this fast and exact at pixel centres.
        var w00 = (256 - wx) * (256 - wy);
        var w10 = wx * (256 - wy);
        var w01 = (256 - wx) * wy;
        var w11 = wx * wy;

        return Blend(p00, p10, p01, p11, w00, w10, w01, w11, 0)
            | Blend(p00, p10, p01, p11, w00, w10, w01, w11, 8)
            | Blend(p00, p10, p01, p11, w00, w10, w01, w11, 16)
            | Blend(p00, p10, p01, p11, w00, w10, w01, w11, 24);
    }

    private int WrapX(int x)
    {
        if (x < 0)
            return x + Width;
        return x >= Width ? x - Width : x;
    }

    private static uint Blend(
        uint p00, uint p10, uint p01, uint p11,
        int w00, int w10, int w01, int w11,
        int shift)
    {
        var sum = (long)((p00 >> shift) & 0xFF) * w00
            + (long)((p10 >> shift) & 0xFF) * w10
            + (long)((p01 >> shift) & 0xFF) * w01
            + (long)((p11 >> shift) & 0xFF) * w11;
        return (uint)((sum + 32768) >> 16) << shift;
    }

    /// <summary>
    /// Decodes the bundled world map. Never throws: on any failure it logs one warning and returns
    /// <see cref="Unavailable"/>, so the Earth View can still draw its overlays.
    /// </summary>
    internal static unsafe BasemapPixels LoadFromAsset()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(MapUri));
            using var bitmap = WriteableBitmap.DecodeToWidth(stream, DecodeWidth);
            using var framebuffer = bitmap.Lock();

            var width = framebuffer.Size.Width;
            var height = framebuffer.Size.Height;
            var swapRedBlue = framebuffer.Format == PixelFormat.Rgba8888;
            if (!swapRedBlue && framebuffer.Format != PixelFormat.Bgra8888)
            {
                Log.Warning("Earth View basemap has an unsupported pixel format {Format}", framebuffer.Format);
                return Unavailable;
            }

            var pixels = new uint[checked(width * height)];
            for (var y = 0; y < height; y++)
            {
                var row = new ReadOnlySpan<uint>((byte*)framebuffer.Address + (long)y * framebuffer.RowBytes, width);
                var destination = pixels.AsSpan(y * width, width);
                for (var x = 0; x < width; x++)
                {
                    var p = row[x];
                    if (swapRedBlue)
                        p = (p & 0xFF00FF00u) | ((p & 0x000000FFu) << 16) | ((p >> 16) & 0x000000FFu);

                    destination[x] = p | 0xFF000000u; // the map is opaque
                }
            }

            return new BasemapPixels(pixels, width, height);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Earth View could not load the world map, so the globe will be drawn without it");
            return Unavailable;
        }
    }
}
