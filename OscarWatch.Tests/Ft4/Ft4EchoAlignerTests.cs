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
    public void Tone_offset_from_the_marker_is_still_found()
    {
        const int rate = 12000;
        const double hz = 1650;
        var onset = (int)(1.6 * rate);
        var samples = new float[rate * 7];
        for (var i = onset; i < onset + rate * 4 && i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * hz * i / rate);

        Assert.True(Ft4EchoAligner.TryFindToneOnset(samples, rate, 1500, out var found));
        Assert.InRange(found, onset - rate / 5, onset + rate / 5);
    }

    [Fact]
    public void Waterfall_peak_reports_a_tone_off_the_tx_marker()
    {
        const int rate = 12000;
        const double hz = 1640;
        var samples = new float[rate * 7];
        var from = (int)(1.2 * rate);
        var to = (int)(4.8 * rate);
        for (var i = from; i < to; i++)
            samples[i] = 0.4f * (float)Math.Sin(2 * Math.PI * hz * i / rate);

        Assert.True(Ft4EchoAligner.TryMeasurePeakHz(samples, rate, 1780, out var peak));
        Assert.InRange(peak, hz - 8, hz + 8);
    }

    [Fact]
    public void Silence_has_no_waterfall_peak()
    {
        var samples = new float[12000 * 7];
        Assert.False(Ft4EchoAligner.TryMeasurePeakHz(samples, 12000, 1780, out _));
    }

    [Fact]
    public void Forced_alignments_are_offered_when_onset_is_muddy_but_the_tone_is_visible()
    {
        const int rate = 12000;
        const double hz = 1760;
        var samples = new float[rate * 7];
        // Ramp in slowly so the sharp onset detector is unsure, but power is obvious.
        var from = (int)(1.4 * rate);
        var to = (int)(5.0 * rate);
        for (var i = from; i < to; i++)
        {
            var t = (i - from) / (double)rate;
            var env = Math.Min(1.0, t / 0.4);
            samples[i] = (float)(0.35 * env * Math.Sin(2 * Math.PI * hz * i / rate));
        }

        var alignments = Ft4EchoAligner.EnumerateEchoAlignments(samples, rate, 1780).ToList();
        Assert.NotEmpty(alignments);
        Assert.All(alignments, a =>
        {
            Assert.True(a.ShiftSeconds >= 0.15);
            Assert.True(a.Samples.Length >= (int)(5.3 * rate));
        });
    }

    [Fact]
    public void Silence_has_no_echo_alignments()
    {
        var samples = new float[12000 * 7];
        Assert.Empty(Ft4EchoAligner.EnumerateEchoAlignments(samples, 12000, 1780));
    }

    [Fact]
    public void Silence_has_no_tone_onset()
    {
        var samples = new float[12000 * 2];
        Assert.False(Ft4EchoAligner.TryFindToneOnset(samples, 12000, 1500, out _));
    }
}
