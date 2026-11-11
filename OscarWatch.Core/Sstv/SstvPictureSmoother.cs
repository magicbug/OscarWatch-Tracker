namespace OscarWatch.Core.Sstv;

/// <summary>
/// Cuts the speckle of a weak picture with a 3 × 3 median: each colour of each pixel becomes the
/// middle value of itself and its eight neighbours, on the rows above and below as well as
/// along the row. FM clicks are lone outliers, so they go, while edges and text stay sharper
/// than with an average. Lines one pixel thin are lost, so it is an option.
/// </summary>
internal static class SstvPictureSmoother
{
    /// <summary>Smooth rows <paramref name="fromRow"/> up to (not including) <paramref name="toRow"/>.</summary>
    public static void Smooth(SstvImage image, int fromRow, int toRow)
    {
        fromRow = Math.Clamp(fromRow, 0, image.Height);
        toRow = Math.Clamp(toRow, fromRow, image.Height);
        var width = image.Width;
        var rows = toRow - fromRow;
        if (rows < 2 || width < 2)
            return;

        var stride = width * 3;
        var src = image.Rgb.AsSpan(fromRow * stride, rows * stride).ToArray();
        Median3x3(src, image.Rgb.AsSpan(fromRow * stride, rows * stride), rows, width);
    }

    /// <summary>
    /// Smooths only where the picture is noisy. The 3 × 3 median of <see cref="Smooth"/> takes the fine detail
    /// off a clean picture as well, so each row is blended towards its median by how noisy its neighbourhood is.
    /// </summary>
    /// <remarks>
    /// Noise is measured from the picture itself: the mean distance of a row's pixels from their 3 × 3 median.
    /// That is a few levels on a clean picture (detail and edges) and rises steadily as the signal weakens, from
    /// about 2 at a carrier to noise ratio of 40 dB, 6 at 14 dB and 15 at 6 dB (measured on twelve test slides sent
    /// over the simulated FM link). The middle of that measure over <see cref="NoiseRegionRows"/> rows each side is
    /// used, so a row of text or a thin line in a clean picture is not taken for noise. Below
    /// <see cref="NoiseLow"/> a row is left alone, above <see cref="NoiseHigh"/> it takes the median, and in
    /// between it blends smoothly.
    /// </remarks>
    public static void SmoothAdaptive(SstvImage image, int fromRow, int toRow)
    {
        fromRow = Math.Clamp(fromRow, 0, image.Height);
        toRow = Math.Clamp(toRow, fromRow, image.Height);
        var width = image.Width;
        var rows = toRow - fromRow;
        if (rows < 2 || width < 2)
            return;

        var stride = width * 3;
        var src = image.Rgb.AsSpan(fromRow * stride, rows * stride).ToArray();
        var med = new byte[src.Length];
        Median3x3(src, med, rows, width);

        var weights = RowWeights(src, med, rows, stride);
        for (var y = 0; y < rows; y++)
        {
            var w = weights[y];
            if (w <= 0f)
                continue;

            var row = (fromRow + y) * stride;
            var at = y * stride;
            for (var i = 0; i < stride; i++)
                image.Rgb[row + i] = (byte)Math.Clamp((int)Math.Round((1 - w) * src[at + i] + w * med[at + i], MidpointRounding.ToEven), 0, 255);
        }
    }

    /// <summary>A row with a regional noise measure at or under this is left as it is.</summary>
    internal const double NoiseLow = 5.0;

    /// <summary>A row with a regional noise measure at or over this takes the full median.</summary>
    internal const double NoiseHigh = 9.0;

    /// <summary>Rows each side of a row whose measures are combined, so one line of text does not count as noise.</summary>
    internal const int NoiseRegionRows = 8;

    /// <summary>How far each row should move towards its median, from 0 (not at all) to 1 (all the way).</summary>
    internal static float[] RowWeights(byte[] src, byte[] med, int rows, int stride)
    {
        var own = new double[rows];
        for (var y = 0; y < rows; y++)
        {
            long sum = 0;
            var at = y * stride;
            for (var i = 0; i < stride; i++)
                sum += Math.Abs(src[at + i] - med[at + i]);
            own[y] = (double)sum / stride;
        }

        var weights = new float[rows];
        var window = new double[2 * NoiseRegionRows + 1];
        for (var y = 0; y < rows; y++)
        {
            var from = Math.Max(0, y - NoiseRegionRows);
            var to = Math.Min(rows - 1, y + NoiseRegionRows);
            var n = to - from + 1;
            Array.Copy(own, from, window, 0, n);
            Array.Sort(window, 0, n);
            var regional = n % 2 == 1 ? window[n / 2] : 0.5 * (window[n / 2 - 1] + window[n / 2]);
            var t = Math.Clamp((regional - NoiseLow) / (NoiseHigh - NoiseLow), 0.0, 1.0);
            weights[y] = (float)(t * t * (3 - 2 * t));
        }

        return weights;
    }

    /// <summary>Each colour of each pixel becomes the middle of itself and its eight neighbours (edges repeat).</summary>
    private static void Median3x3(ReadOnlySpan<byte> src, Span<byte> dst, int rows, int width)
    {
        var stride = width * 3;
        Span<byte> window = stackalloc byte[9];
        for (var y = 0; y < rows; y++)
        {
            var up = Math.Max(0, y - 1) * stride;
            var mid = y * stride;
            var down = Math.Min(rows - 1, y + 1) * stride;
            for (var x = 0; x < width; x++)
            {
                var l = Math.Max(0, x - 1) * 3;
                var c = x * 3;
                var r = Math.Min(width - 1, x + 1) * 3;
                for (var ch = 0; ch < 3; ch++)
                {
                    window[0] = src[up + l + ch];
                    window[1] = src[up + c + ch];
                    window[2] = src[up + r + ch];
                    window[3] = src[mid + l + ch];
                    window[4] = src[mid + c + ch];
                    window[5] = src[mid + r + ch];
                    window[6] = src[down + l + ch];
                    window[7] = src[down + c + ch];
                    window[8] = src[down + r + ch];
                    window.Sort();
                    dst[mid + c + ch] = window[4];
                }
            }
        }
    }
}
