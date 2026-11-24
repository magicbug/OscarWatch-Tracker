using OscarWatch.Core.CwDecode;
using Xunit;

namespace OscarWatch.Tests.CwDecode;

public sealed class CwDecodeSettingsTests
{
    [Fact]
    public void CopyTextSizeStartsAtTheDefault()
    {
        Assert.Equal(CwDecodeSettings.DefaultCopyFontSize, new CwDecodeSettings().CopyFontSize);
    }

    [Theory]
    [InlineData(0, CwDecodeSettings.MinCopyFontSize)]
    [InlineData(99, CwDecodeSettings.MaxCopyFontSize)]
    [InlineData(21.4, 21)]
    [InlineData(double.NaN, CwDecodeSettings.DefaultCopyFontSize)]
    public void NormaliseKeepsTheCopyTextSizeInRange(double saved, double expected)
    {
        var settings = new CwDecodeSettings { CopyFontSize = saved };

        settings.Normalise();

        Assert.Equal(expected, settings.CopyFontSize);
    }

    [Theory]
    [InlineData(0, CwDecodeSettings.MinWaterfallHeight)]
    [InlineData(500, CwDecodeSettings.MaxWaterfallHeight)]
    [InlineData(150.4, 150)]
    [InlineData(double.NaN, CwDecodeSettings.DefaultWaterfallHeight)]
    public void ClampWaterfallHeightKeepsThePanelInRange(double saved, int expected)
    {
        Assert.Equal(expected, CwDecodeSettings.ClampWaterfallHeight(saved));
    }

    [Fact]
    public void NormaliseKeepsASavedWaterfallHeightInRange()
    {
        var settings = new CwDecodeSettings { WaterfallHeight = 40 };

        settings.Normalise();

        Assert.Equal(CwDecodeSettings.MinWaterfallHeight, settings.WaterfallHeight);
    }
}
