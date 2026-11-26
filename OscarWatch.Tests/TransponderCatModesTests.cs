using OscarWatch.Core.Radio;

namespace OscarWatch.Tests;

public class TransponderCatModesTests
{
    [Theory]
    [InlineData("FM-DATA", "DATA-FM")]
    [InlineData("data-fm", "DATA-FM")]
    [InlineData(" USB ", "USB")]
    [InlineData("DSTAR", "DV")]
    [InlineData("D-STAR", "DV")]
    [InlineData("DIGITALVOICE", "DV")]
    [InlineData("dv", "DV")]
    public void Normalize_canonicalizes_mode_strings(string input, string expected) =>
        Assert.Equal(expected, TransponderCatModes.Normalize(input));

    [Fact]
    public void EditorOptions_includes_data_fm() =>
        Assert.Contains("DATA-FM", TransponderCatModes.EditorOptions);

    [Fact]
    public void EditorOptions_includes_dv() =>
        Assert.Contains("DV", TransponderCatModes.EditorOptions);

    [Theory]
    [InlineData("DV", true)]
    [InlineData("D-STAR", true)]
    [InlineData("DATA-USB", false)]
    [InlineData("FM", false)]
    public void IsDigitalVoice_recognises_dv(string mode, bool expected) =>
        Assert.Equal(expected, TransponderCatModes.IsDigitalVoice(mode));

    [Fact]
    public void IsData_excludes_dv() =>
        Assert.False(TransponderCatModes.IsData("DV"));
}
