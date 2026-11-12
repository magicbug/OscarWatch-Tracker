using OscarWatch.Core.CwDecode;
using Xunit;

namespace OscarWatch.Tests.CwDecode;

public sealed class CwSpectrumShiftTests
{
    private const double BinHz = 5;

    private static float[] Peak(int length, int bin)
    {
        var row = Enumerable.Repeat(-100f, length).ToArray();
        row[bin] = 0;
        return row;
    }

    private static int PeakBin(float[] row) => Array.IndexOf(row, row.Max());

    [Theory]
    [InlineData(-50, 140)]
    [InlineData(25, 155)]
    [InlineData(0, 150)]
    public void WholeBinShiftMovesThePeak(double shiftHz, int expectedBin)
    {
        var shifted = CwSpectrumShift.Shift(Peak(400, 150), shiftHz, BinHz);

        Assert.Equal(expectedBin, PeakBin(shifted));
        Assert.Equal(0f, shifted[expectedBin], 3);
    }

    [Fact]
    public void HalfBinShiftSplitsThePeak()
    {
        var shifted = CwSpectrumShift.Shift(Peak(400, 150), 2.5, BinHz);

        Assert.Equal(-50f, shifted[150], 3);
        Assert.Equal(-50f, shifted[151], 3);
    }

    [Fact]
    public void EdgesAreFilledFromTheNearestBin()
    {
        var row = Enumerable.Range(0, 10).Select(i => (float)i).ToArray();

        var down = CwSpectrumShift.Shift(row, -15, BinHz);
        var up = CwSpectrumShift.Shift(row, 15, BinHz);

        Assert.Equal(9f, down[^1]);
        Assert.Equal(0f, up[0]);
        Assert.Equal(row.Length, down.Length);
    }
}
