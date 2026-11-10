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
        Span<byte> window = stackalloc byte[9];
        for (var y = 0; y < rows; y++)
        {
            var up = Math.Max(0, y - 1) * stride;
            var mid = y * stride;
            var down = Math.Min(rows - 1, y + 1) * stride;
            var dst = (fromRow + y) * stride;
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
                    image.Rgb[dst + c + ch] = window[4];
                }
            }
        }
    }
}
