using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4EchoChoiceTests
{
    [Fact]
    public void Copy_on_the_tx_marker_replaces_one_10_hz_off()
    {
        Assert.True(Ft4EchoChoice.IsClearerCopy(shownHz: 1240f, candidateHz: 1250f, txHz: 1250));
    }

    [Fact]
    public void Copy_10_hz_off_does_not_replace_the_one_on_the_marker()
    {
        Assert.False(Ft4EchoChoice.IsClearerCopy(shownHz: 1250f, candidateHz: 1240f, txHz: 1250));
    }

    [Fact]
    public void Later_pass_on_the_same_frequency_keeps_the_line_already_shown()
    {
        Assert.False(Ft4EchoChoice.IsClearerCopy(shownHz: 927f, candidateHz: 927f, txHz: 930));
        Assert.False(Ft4EchoChoice.IsClearerCopy(shownHz: 1250f, candidateHz: 1251f, txHz: 1250));
    }
}
