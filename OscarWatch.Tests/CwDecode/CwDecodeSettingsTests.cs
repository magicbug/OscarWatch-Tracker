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
}
