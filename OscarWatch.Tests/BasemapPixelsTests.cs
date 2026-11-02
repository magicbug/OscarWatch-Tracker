using OscarWatch.Controls;

namespace OscarWatch.Tests;

public sealed class BasemapPixelsTests
{
    private const int W = 4;
    private const int H = 2;

    // Row 0 (north) then row 1 (south). Every pixel is opaque and the colours are all different.
    private static readonly uint[] Grid =
    [
        0xFF102030, 0xFF405060, 0xFF708090, 0xFFA0B0C0,
        0xFF010203, 0xFF040506, 0xFF070809, 0xFF0A0B0C
    ];

    private static BasemapPixels Map() => new(Grid, W, H);

    private static double LonOfPixel(int x) => (x + 0.5) / W * 360.0 - 180.0;

    private static double LatOfPixel(int y) => 90.0 - (y + 0.5) / H * 180.0;

    [Fact]
    public void Exact_pixel_centres_return_that_pixel()
    {
        var map = Map();

        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                Assert.Equal(Grid[y * W + x], map.Sample(LatOfPixel(y), LonOfPixel(x)));
            }
        }
    }

    [Fact]
    public void Halfway_between_two_pixels_in_a_row_is_their_channel_by_channel_average()
    {
        var map = new BasemapPixels([0xFF204060, 0xFF602040, 0xFF204060, 0xFF602040], 2, 2);

        // Between pixel 0 and pixel 1 horizontally, on the centre line of the top row.
        var lat = 90.0 - 0.5 / 2 * 180.0;
        var value = map.Sample(lat, 0.0);

        // Channels, low to high byte: 0x60/0x40 -> 0x50, 0x40/0x20 -> 0x30, 0x20/0x60 -> 0x40.
        Assert.Equal(0xFF403050u, value);
    }

    [Fact]
    public void Halfway_between_two_rows_is_their_average()
    {
        var map = new BasemapPixels([0xFF000000, 0xFF000000, 0xFF404040, 0xFF404040], 2, 2);

        var value = map.Sample(0.0, LonOfPixelOfWidth(0, 2));

        Assert.Equal(0xFF202020u, value);
    }

    [Fact]
    public void Alpha_stays_opaque_when_blending()
    {
        var map = Map();

        for (var i = 0; i < 50; i++)
        {
            var value = map.Sample(-80 + i * 3.1, -170 + i * 6.9);
            Assert.Equal(0xFFu, value >> 24);
        }
    }

    [Fact]
    public void Longitude_wraps_across_the_antimeridian()
    {
        var map = Map();

        // At exactly 180 degrees, the position is halfway between the last and first columns.
        var lat = LatOfPixel(0);
        var expected = AverageOf(Grid[W - 1], Grid[0]);

        Assert.Equal(expected, map.Sample(lat, 180.0));
        Assert.Equal(expected, map.Sample(lat, -180.0));
    }

    [Theory]
    [InlineData(0.0, 360.0)]
    [InlineData(0.0, -360.0)]
    [InlineData(100.0, 460.0)]
    [InlineData(-100.0, 620.0)]
    [InlineData(179.0, -541.0)]
    public void Longitudes_a_whole_number_of_turns_apart_sample_identically(double lon, double sameLon)
    {
        var map = Map();
        var lat = LatOfPixel(0) * 0.9;

        Assert.Equal(map.Sample(lat, lon), map.Sample(lat, sameLon));
    }

    [Fact]
    public void Latitude_is_clamped_at_the_poles()
    {
        var map = Map();
        var lon = LonOfPixel(2);

        Assert.Equal(Grid[2], map.Sample(90.0, lon));
        Assert.Equal(Grid[2], map.Sample(120.0, lon));       // beyond the pole
        Assert.Equal(Grid[W + 2], map.Sample(-90.0, lon));
        Assert.Equal(Grid[W + 2], map.Sample(-200.0, lon));
    }

    [Theory]
    [InlineData(double.NaN, 0.0)]
    [InlineData(0.0, double.NaN)]
    [InlineData(double.PositiveInfinity, 0.0)]
    [InlineData(0.0, double.NegativeInfinity)]
    public void Non_finite_positions_return_zero(double lat, double lon) =>
        Assert.Equal(0u, Map().Sample(lat, lon));

    [Fact]
    public void Unavailable_map_reports_so_and_samples_as_zero()
    {
        var map = BasemapPixels.Unavailable;

        Assert.False(map.IsAvailable);
        Assert.Equal(0u, map.Sample(10.0, 20.0));
    }

    [Fact]
    public void Real_map_reports_its_size_and_availability()
    {
        var map = Map();

        Assert.True(map.IsAvailable);
        Assert.Equal(W, map.Width);
        Assert.Equal(H, map.Height);
    }

    [Fact]
    public void Constructor_rejects_a_pixel_array_of_the_wrong_length() =>
        Assert.Throws<ArgumentException>(() => new BasemapPixels(new uint[5], 2, 2));

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    [InlineData(-1, 2)]
    public void Constructor_rejects_non_positive_dimensions(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BasemapPixels([], width, height));

    [Fact]
    public void Constructor_rejects_null_pixels() =>
        Assert.Throws<ArgumentNullException>(() => new BasemapPixels(null!, 2, 2));

    [Fact]
    public void Decode_width_matches_the_source_image_width()
    {
        // The source world_map.jpg is 4096 pixels wide. Decoding wider would only add memory.
        Assert.Equal(4096, BasemapPixels.DecodeWidth);
    }

    private static double LonOfPixelOfWidth(int x, int width) => (x + 0.5) / width * 360.0 - 180.0;

    private static uint AverageOf(uint a, uint b)
    {
        uint result = 0;
        for (var shift = 0; shift < 32; shift += 8)
        {
            var ca = (a >> shift) & 0xFF;
            var cb = (b >> shift) & 0xFF;
            result |= ((ca + cb + 1) / 2) << shift;
        }

        return result;
    }
}
