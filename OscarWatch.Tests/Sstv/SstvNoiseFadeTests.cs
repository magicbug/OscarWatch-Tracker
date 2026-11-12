using OscarWatch.Core.Sstv;

namespace OscarWatch.Tests.Sstv;

/// <summary>
/// Noise decodes as full-strength random colour unless it is faded. The fade has to turn noise into dim
/// grey grain while leaving real pictures, even weak ones, as they were.
/// </summary>
public sealed class SstvNoiseFadeTests
{
    private const int Rate = SstvDecoder.InternalRate;

    private static SstvSignalBuffer DemodulatedNoise(double seconds)
    {
        var rng = new Random(7);
        var noise = new float[(int)(Rate * seconds)];
        for (var i = 0; i < noise.Length; i++)
            noise[i] = (float)(rng.NextDouble() * 2 - 1);

        var hz = new List<float>();
        var envelope = new List<float>();
        new SstvFmDemodulator(Rate).Process(noise, hz, envelope);
        var weights = new List<float>();
        new SstvEnvelopeWeigher(Rate).Process(envelope, weights);
        var buffer = new SstvSignalBuffer();
        buffer.Append(hz, weights);
        return buffer;
    }

    private static (double Saturation, double Brightness) Render(SstvMode mode, SstvSignalBuffer buffer, bool noiseFade)
    {
        var image = new SstvImage(mode.Width, mode.Height);
        var renderer = new SstvLineRenderer(mode, image, chromaSmoothing: true, weightPixels: true, noiseFade);
        var samplesPerMs = Rate / 1000.0;
        var lineSamples = mode.LineMs * samplesPerMs;
        var lines = Math.Min(mode.TransmittedLines, 20);
        // Start a second in, once the envelope level has settled.
        for (var line = 0; line < lines; line++)
            renderer.Render(buffer, buffer.Start + Rate + line * lineSamples, samplesPerMs, 0, line);

        double saturation = 0, brightness = 0;
        var rows = lines * mode.RowsPerLine - 1;
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < mode.Width; x++)
            {
                var (r, g, b) = image.GetPixel(x, y);
                saturation += Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                brightness += (r + g + b) / 3.0;
            }
        }

        var n = (double)rows * mode.Width;
        return (saturation / n, brightness / n);
    }

    [Theory]
    [InlineData(SstvModeId.MartinM1)]
    [InlineData(SstvModeId.ScottieS1)]
    [InlineData(SstvModeId.Robot36)]
    [InlineData(SstvModeId.Pd120)]
    public void Noise_fades_to_dim_grey(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var buffer = DemodulatedNoise(1 + 20 * mode.LineMs / 1000 + 1);

        var off = Render(mode, buffer, noiseFade: false);
        var on = Render(mode, buffer, noiseFade: true);

        Assert.True(off.Saturation > 100, $"noise without fading has saturation {off.Saturation:0}");
        Assert.True(on.Saturation < off.Saturation * 0.25, $"saturation {on.Saturation:0} faded, {off.Saturation:0} not");
        Assert.True(on.Brightness < off.Brightness * 0.7, $"brightness {on.Brightness:0} faded, {off.Brightness:0} not");
    }

    [Theory]
    [InlineData(SstvModeId.MartinM1, 40.0)]
    [InlineData(SstvModeId.MartinM1, 5.0)]
    [InlineData(SstvModeId.Robot36, 40.0)]
    [InlineData(SstvModeId.Robot36, 5.0)]
    [InlineData(SstvModeId.Pd120, 6.0)]
    public void Pictures_keep_their_colour(SstvModeId id, double cnr)
    {
        var mode = SstvModeTable.Get(id);
        var settings = new SstvTestEncoder.Settings { CnrDb = cnr };

        var off = SstvChannelSimulationTests.Measure(mode, settings, new SstvDecoderOptions { NoiseFade = false });
        var on = SstvChannelSimulationTests.Measure(mode, settings, new SstvDecoderOptions());

        Assert.Equal(off.LockPercent, on.LockPercent, 1);
        Assert.True(on.MeanError <= off.MeanError + 0.5, $"error {on.MeanError:0.00} faded, {off.MeanError:0.00} not");
    }

    [Fact]
    public void Full_fade_is_grey_at_half_brightness()
    {
        var (r, g, b) = SstvLineRenderer.Faded(255, 0, 0, 1);

        var grey = 0.299 * 255 * SstvLineRenderer.NoiseGreyLevel;
        Assert.Equal(grey, r, 6);
        Assert.Equal(grey, g, 6);
        Assert.Equal(grey, b, 6);
        Assert.Equal((255.0, 0.0, 0.0), SstvLineRenderer.Faded(255, 0, 0, 0));
    }
}
