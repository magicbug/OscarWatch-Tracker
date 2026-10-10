using OscarWatch.Core.Sstv;

namespace OscarWatch.Tests.Sstv;

public sealed class SstvModeTableTests
{
    [Theory]
    [InlineData(SstvModeId.Robot36, 0x08, 320, 240, 150.0)]
    [InlineData(SstvModeId.Robot72, 0x0C, 320, 240, 300.0)]
    [InlineData(SstvModeId.ScottieS1, 0x3C, 320, 256, 428.22)]
    [InlineData(SstvModeId.ScottieS2, 0x38, 320, 256, 277.692)]
    [InlineData(SstvModeId.ScottieDx, 0x4C, 320, 256, 1050.3)]
    [InlineData(SstvModeId.MartinM1, 0x2C, 320, 256, 446.446)]
    [InlineData(SstvModeId.MartinM2, 0x28, 320, 256, 226.798)]
    [InlineData(SstvModeId.WraaseSc2180, 0x37, 320, 256, 711.0225)]
    [InlineData(SstvModeId.Pd50, 0x5D, 320, 256, 388.16)]
    [InlineData(SstvModeId.Pd90, 0x63, 320, 256, 703.04)]
    [InlineData(SstvModeId.Pd120, 0x5F, 640, 496, 508.48)]
    [InlineData(SstvModeId.Pd160, 0x62, 512, 400, 804.416)]
    [InlineData(SstvModeId.Pd180, 0x60, 640, 496, 754.24)]
    [InlineData(SstvModeId.Pd240, 0x61, 640, 496, 1000.0)]
    [InlineData(SstvModeId.Pd290, 0x5E, 800, 616, 937.28)]
    public void Mode_matches_published_timing(SstvModeId id, int vis, int width, int height, double lineMs)
    {
        var mode = SstvModeTable.Get(id);

        Assert.Equal(vis, mode.VisCode);
        Assert.Equal(width, mode.Width);
        Assert.Equal(height, mode.Height);
        Assert.Equal(lineMs, mode.LineMs, 3);
        Assert.Same(mode, SstvModeTable.FromVis(vis));
    }

    [Theory]
    [InlineData(SstvModeId.Robot36, 36.0)]
    [InlineData(SstvModeId.Robot72, 72.0)]
    [InlineData(SstvModeId.MartinM1, 114.3)]
    [InlineData(SstvModeId.ScottieS1, 109.6)]
    [InlineData(SstvModeId.Pd120, 126.1)]
    [InlineData(SstvModeId.Pd180, 187.1)]
    [InlineData(SstvModeId.Pd290, 288.7)]
    public void Image_time_matches_mode_name(SstvModeId id, double seconds)
    {
        Assert.Equal(seconds, SstvModeTable.Get(id).ImageSeconds, 1);
    }

    [Fact]
    public void Table_has_fifteen_modes_with_unique_vis_codes()
    {
        Assert.Equal(15, SstvModeTable.All.Count);
        Assert.Equal(15, SstvModeTable.All.Select(m => m.VisCode).Distinct().Count());
        Assert.All(SstvModeTable.All, m => Assert.InRange(m.VisCode, 0, 0x7F));
    }

    [Fact]
    public void Scottie_sync_sits_before_red_scan()
    {
        var s1 = SstvModeTable.ScottieS1;
        Assert.Equal(1.5 + 138.24 + 1.5 + 138.24, s1.SyncOffsetMs, 3);
        Assert.Equal(9.0, s1.LeadInMs);
        Assert.Equal(0.0, SstvModeTable.MartinM1.SyncOffsetMs);
    }

    [Fact]
    public void Pd_modes_carry_two_rows_per_line()
    {
        Assert.All(
            SstvModeTable.All.Where(m => m.Name.StartsWith("PD", StringComparison.Ordinal)),
            m =>
            {
                Assert.Equal(2, m.RowsPerLine);
                Assert.Equal(20.0, m.SyncMs);
            });
    }
}
