using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4EchoAlignerTests
{
    [Fact]
    public void Late_tone_onset_is_found_and_shifted_into_the_decoder_window()
    {
        const int rate = 12000;
        const double hz = 1500;
        var onset = (int)(2.0 * rate);
        var samples = new float[rate * 7];
        for (var i = onset; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * hz * (i - onset) / rate);

        Assert.True(Ft4EchoAligner.TryFindToneOnset(samples, rate, hz, out var found));
        Assert.InRange(found, onset - rate / 10, onset + rate / 10);
        Assert.True(found > (int)(Ft4EchoAligner.NativeWindowSeconds * rate));

        var aligned = Ft4EchoAligner.AlignToNativeWindow(samples, rate, found, out var trimmed);
        Assert.True(trimmed > 1.0);
        Assert.True(aligned.Length >= (int)(5.3 * rate));
        var alignedOnset = found - (int)Math.Round(trimmed * rate);
        Assert.InRange(alignedOnset / (double)rate, 0.2, Ft4EchoAligner.NativeWindowSeconds);
    }

    [Fact]
    public void Silence_has_no_tone_onset()
    {
        var samples = new float[12000 * 2];
        Assert.False(Ft4EchoAligner.TryFindToneOnset(samples, 12000, 1500, out _));
    }
}
