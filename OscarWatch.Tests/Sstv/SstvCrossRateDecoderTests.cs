using OscarWatch.Core.Sstv;

namespace OscarWatch.Tests.Sstv;

/// <summary>
/// Validates that the SSTV decoder stays aligned when input is at the internal 12 kHz rate or
/// resampled from common soundcard rates. Synthetic only: does not reproduce external field reports.
/// </summary>
public sealed class SstvCrossRateDecoderTests
{
    /// <summary>
    /// FM + drop-out pass (same spirit as <see cref="SstvPictureCombinerTests"/>).
    /// </summary>
    private static SstvDecodedImage DecodeWithDropout(SstvMode mode, int rate, int seed)
    {
        var settings = new SstvTestEncoder.Settings
        {
            CnrDb = 12,
            Seed = seed,
            DropoutStartSeconds = mode.TransmittedLines * mode.LineMs / 1000.0 * 0.35,
            DropoutEverySeconds = 1000,
            DropoutSeconds = 4,
        };
        var audio = SstvTestEncoder.Encode(mode, rate, settings);
        return SstvRoundTripTests.Decode(audio, new SstvDecoderOptions { HoldThroughFades = true }, rate)
            .Where(i => i.Mode == mode)
            .MaxBy(i => i.LockPercent)!;
    }

    [Theory]
    [InlineData(12000)]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Clean_pd120_decodes_at_common_rates(int rate)
    {
        var mode = SstvModeTable.Pd120;
        var audio = SstvTestEncoder.Encode(mode, rate);
        var result = Assert.Single(SstvRoundTripTests.Decode(audio, new SstvDecoderOptions(), rate));

        Assert.Same(mode, result.Mode);
        Assert.True(result.FromVis);
        Assert.True(result.Complete);
        Assert.True(result.LockPercent > 97, $"lock {result.LockPercent:0.0}% at {rate} Hz");
        Assert.InRange(result.ClockPpm, -250, 250);
        SstvRoundTripTests.AssertImageClose(mode, result.Image, maxMeanError: 10);
    }

    [Theory]
    [InlineData(12000)]
    [InlineData(48000)]
    public void Noisy_pd120_with_dropout_recovers_line_lock(int rate)
    {
        var mode = SstvModeTable.Pd120;
        var result = DecodeWithDropout(mode, rate, seed: 41);

        Assert.True(result.LockPercent > 55, $"lock {result.LockPercent:0.0}% at {rate} Hz");
        Assert.InRange(result.ClockPpm, -400, 400);
    }

    [Theory]
    [InlineData(12000)]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Vertical_edge_stays_aligned_across_rates(int rate)
    {
        var mode = SstvModeTable.MartinM1;
        var edge = mode.Width / 4;
        var audio = SstvTestEncoder.Encode(
            mode,
            (x, _) => x >= edge ? (255.0, 255.0, 255.0) : (0.0, 0.0, 0.0),
            rate,
            new SstvTestEncoder.Settings());
        var image = Assert.Single(SstvRoundTripTests.Decode(audio, new SstvDecoderOptions(), rate)).Image;

        var y = mode.Height / 2;
        var found = -1;
        for (var x = 1; x < mode.Width && found < 0; x++)
        {
            var (r, g, b) = image.GetPixel(x, y);
            if ((r + g + b) / 3 >= 128)
                found = x;
        }

        Assert.InRange(found, edge - 1, edge + 1);
    }

    /// <summary>
    /// <see cref="SstvResampler.DelayOutputSamples"/> is documented for timestamp alignment.
    /// After leading silence, the first significant energy in the resampled stream should land near
    /// the nominal 12:48 time ratio plus that delay (decoder timing is still anchored on VIS/sync).
    /// </summary>
    [Fact]
    public void Resampler_onset_matches_documented_delay_at_48k()
    {
        const int inputRate = 48000;
        const int outputRate = SstvDecoder.InternalRate;
        var resampler = new SstvResampler(inputRate, outputRate);
        var documentedDelay = resampler.DelayOutputSamples;
        Assert.True(documentedDelay > 5, $"unexpected delay {documentedDelay}");

        const int leadInInput = inputRate / 2;
        var input = new float[leadInInput + inputRate / 2];
        for (var i = leadInInput; i < input.Length; i++)
            input[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * (i - leadInInput) / inputRate));

        var output = new List<float>(outputRate);
        resampler.Process(input.AsSpan(), output);

        const float threshold = 0.04f;
        var first = -1;
        for (var i = 0; i < output.Count; i++)
        {
            if (Math.Abs(output[i]) > threshold)
            {
                first = i;
                break;
            }
        }

        Assert.True(first >= 0, "no energy in resampler output");
        var expectedStart = leadInInput * (double)outputRate / inputRate + documentedDelay;
        Assert.InRange(first, (int)Math.Floor(expectedStart - 120), (int)Math.Ceiling(expectedStart + 120));
    }

    [Fact]
    public void Resampler_at_native_rate_is_pass_through_without_delay()
    {
        var resampler = new SstvResampler(SstvDecoder.InternalRate, SstvDecoder.InternalRate);
        Assert.Equal(0, resampler.DelayOutputSamples);
        var input = new float[SstvDecoder.InternalRate];
        for (var i = 0; i < input.Length; i++)
            input[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 1000 * i / (double)SstvDecoder.InternalRate));
        var output = new List<float>();
        resampler.Process(input.AsSpan(), output);
        Assert.Equal(input.Length, output.Count);
    }
}
