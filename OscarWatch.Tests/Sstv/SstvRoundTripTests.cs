using OscarWatch.Core.Sstv;

namespace OscarWatch.Tests.Sstv;

public sealed class SstvRoundTripTests
{
    private const int Rate = 48000;

    public static TheoryData<SstvModeId> AllModes()
    {
        var data = new TheoryData<SstvModeId>();
        foreach (var mode in SstvModeTable.All)
            data.Add(mode.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Every_mode_round_trips_with_auto_mode(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var result = DecodeSingle(mode, new SstvTestEncoder.Settings());

        Assert.Same(mode, result.Mode);
        Assert.True(result.FromVis);
        Assert.True(result.Complete);
        Assert.True(result.LockPercent > 98, $"lock {result.LockPercent:0.0}%");
        AssertImageClose(mode, result.Image, maxMeanError: 8);
    }

    /// <summary>
    /// A sharp black to white edge must land on its own column. Bright picture just before
    /// a long sync pulse once pulled the detected sync late, shifting PD pictures left.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllModes))]
    public void Picture_is_not_shifted_sideways(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var edge = mode.Width / 4;
        var audio = SstvTestEncoder.Encode(
            mode,
            (x, _) => x >= edge ? (255.0, 255.0, 255.0) : (0.0, 0.0, 0.0),
            Rate,
            new SstvTestEncoder.Settings());
        var image = Assert.Single(Decode(audio, new SstvDecoderOptions())).Image;

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

    [Theory]
    [InlineData(SstvModeId.MartinM1, 1.005)]
    [InlineData(SstvModeId.Pd120, 0.995)]
    [InlineData(SstvModeId.ScottieS1, 1.003)]
    [InlineData(SstvModeId.Robot36, 0.996)]
    public void Clock_error_does_not_slant_the_picture(SstvModeId id, double clockRatio)
    {
        var mode = SstvModeTable.Get(id);
        var result = DecodeSingle(mode, new SstvTestEncoder.Settings { ClockRatio = clockRatio });

        AssertImageClose(mode, result.Image, maxMeanError: 9);
        Assert.InRange(result.ClockPpm, (clockRatio - 1) * 1e6 - 300, (clockRatio - 1) * 1e6 + 300);
    }

    [Theory]
    [InlineData(SstvModeId.Pd120, 300)]
    [InlineData(SstvModeId.MartinM1, -300)]
    [InlineData(SstvModeId.Robot36, 250)]
    public void Audio_offset_is_removed(SstvModeId id, double offsetHz)
    {
        var mode = SstvModeTable.Get(id);
        var result = DecodeSingle(mode, new SstvTestEncoder.Settings { OffsetHz = offsetHz });

        AssertImageClose(mode, result.Image, maxMeanError: 9);
        Assert.InRange(result.OffsetHz, offsetHz - 25, offsetHz + 25);
    }

    [Theory]
    [InlineData(SstvModeId.Pd120, -250, 4)]
    [InlineData(SstvModeId.ScottieS1, 200, -3)]
    public void Afc_follows_doppler_drift(SstvModeId id, double startHz, double hzPerSecond)
    {
        var mode = SstvModeTable.Get(id);
        var result = DecodeSingle(mode, new SstvTestEncoder.Settings
        {
            OffsetHz = startHz,
            DriftHzPerSecond = hzPerSecond,
        });

        AssertImageClose(mode, result.Image, maxMeanError: 12);
    }

    /// <summary>
    /// Heavy noise: the picture must stay in sync, unslanted and in tune. The error limit
    /// is the noise floor for the mode, which is higher for Y/colour-difference modes
    /// because the colour conversion amplifies chroma noise.
    /// </summary>
    [Theory]
    [InlineData(SstvModeId.Pd120, 55)]
    [InlineData(SstvModeId.MartinM1, 30)]
    [InlineData(SstvModeId.Robot36, 55)]
    public void Noisy_signal_still_decodes(SstvModeId id, double maxMeanError)
    {
        var mode = SstvModeTable.Get(id);
        var result = DecodeSingle(mode, new SstvTestEncoder.Settings { NoiseRms = 0.35 });

        Assert.True(result.LockPercent > 80, $"lock {result.LockPercent:0.0}%");
        Assert.InRange(result.ClockPpm, -300, 300);
        Assert.InRange(result.OffsetHz, -60, 60);
        AssertImageClose(mode, result.Image, maxMeanError);
    }

    [Theory]
    [InlineData(SstvModeId.Pd120, 0.2, 36)]
    [InlineData(SstvModeId.MartinM1, 0.2, 18)]
    public void Moderate_noise_keeps_detail(SstvModeId id, double noiseRms, double maxMeanError)
    {
        var mode = SstvModeTable.Get(id);
        var result = DecodeSingle(mode, new SstvTestEncoder.Settings { NoiseRms = noiseRms });

        Assert.True(result.LockPercent > 95, $"lock {result.LockPercent:0.0}%");
        AssertImageClose(mode, result.Image, maxMeanError);
    }

    [Theory]
    [InlineData(SstvModeId.Pd120)]
    [InlineData(SstvModeId.MartinM1)]
    [InlineData(SstvModeId.ScottieS1)]
    public void Picture_without_vis_is_found_from_its_sync_pulses(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var result = DecodeSingle(mode, new SstvTestEncoder.Settings { IncludeVis = false });

        Assert.Same(mode, result.Mode);
        Assert.False(result.FromVis);
        AssertImageClose(mode, result.Image, maxMeanError: 10);
    }

    [Fact]
    public void Forced_mode_overrides_vis()
    {
        var mode = SstvModeTable.Pd120;
        var audio = SstvTestEncoder.Encode(mode, Rate, new SstvTestEncoder.Settings
        {
            VisCodeOverride = SstvModeTable.MartinM1.VisCode,
        });

        var images = Decode(audio, new SstvDecoderOptions { ForcedMode = SstvModeId.Pd120 });

        var result = Assert.Single(images);
        Assert.Same(mode, result.Mode);
        AssertImageClose(mode, result.Image, maxMeanError: 8);
    }

    [Fact]
    public void Two_pictures_in_a_row_are_both_decoded()
    {
        var first = SstvTestEncoder.Encode(SstvModeTable.Robot36, Rate);
        var second = SstvTestEncoder.Encode(SstvModeTable.MartinM2, Rate);

        var images = Decode(first.Concat(second).ToArray(), new SstvDecoderOptions());

        Assert.Equal(2, images.Count);
        Assert.Same(SstvModeTable.Robot36, images[0].Mode);
        Assert.Same(SstvModeTable.MartinM2, images[1].Mode);
    }

    [Fact]
    public void Noise_alone_produces_no_picture()
    {
        var random = new Random(7);
        var audio = new float[Rate * 60];
        for (var i = 0; i < audio.Length; i++)
            audio[i] = (float)((random.NextDouble() - 0.5) * 0.6);

        Assert.Empty(Decode(audio, new SstvDecoderOptions()));
    }

    /// <summary>
    /// Open-squelch FM receiver noise is strongest at low audio frequencies, where the
    /// discriminator often reads it as near 1200 Hz. It must not pass as sync, so a picture
    /// whose transmission stops is ended, not filled with noise.
    /// </summary>
    [Fact]
    public void Picture_ends_when_the_transmission_stops_into_low_frequency_noise()
    {
        var mode = SstvModeTable.Pd120;
        var audio = SstvTestEncoder.Encode(mode, Rate, new SstvTestEncoder.Settings { TailSeconds = 0 });
        var picture = audio.Take(audio.Length / 2).ToArray();
        var noise = LowFrequencyNoise(Rate * 90, amplitude: 0.6, cornerHz: 600);

        var result = Assert.Single(Decode(picture.Concat(noise).ToArray(), new SstvDecoderOptions()));

        Assert.False(result.Complete);
        var lastLocked = Array.LastIndexOf(result.LineQuality.ToArray(), SstvLineQuality.Locked);
        Assert.InRange(lastLocked, mode.TransmittedLines / 2 - 8, mode.TransmittedLines / 2 + 2);
        Assert.Equal(SstvLineQuality.Missing, result.LineQuality[^1]);
        Assert.True(result.LockPercent > 90, $"lock {result.LockPercent:0.0}%");
    }

    [Fact]
    public void Low_frequency_noise_alone_produces_no_picture()
    {
        Assert.Empty(Decode(LowFrequencyNoise(Rate * 90, amplitude: 0.6, cornerHz: 600), new SstvDecoderOptions()));
    }

    private static float[] LowFrequencyNoise(int length, double amplitude, double cornerHz)
    {
        var random = new Random(11);
        var a = Math.Exp(-2 * Math.PI * cornerHz / Rate);
        var y = 0.0;
        var output = new float[length];
        for (var i = 0; i < length; i++)
        {
            y = a * y + (1 - a) * (random.NextDouble() - 0.5) * 8 * amplitude;
            output[i] = (float)y;
        }

        return output;
    }

    [Fact]
    public void Cut_short_picture_is_kept_on_flush()
    {
        var mode = SstvModeTable.MartinM1;
        var audio = SstvTestEncoder.Encode(mode, Rate);
        var half = audio.Take(audio.Length / 2).ToArray();

        var result = Assert.Single(Decode(half, new SstvDecoderOptions()));
        Assert.False(result.Complete);
        Assert.Contains(SstvLineQuality.Missing, result.LineQuality);
    }

    internal static SstvDecodedImage DecodeSingle(SstvMode mode, SstvTestEncoder.Settings settings)
    {
        var audio = SstvTestEncoder.Encode(mode, Rate, settings);
        var images = Decode(audio, new SstvDecoderOptions());
        return Assert.Single(images);
    }

    internal static List<SstvDecodedImage> Decode(float[] audio, SstvDecoderOptions options, int rate = Rate)
    {
        var decoder = new SstvDecoder(rate, options);
        var images = new List<SstvDecodedImage>();
        decoder.ImageCompleted += (_, image) => images.Add(image);
        const int chunk = 1024;
        for (var i = 0; i < audio.Length; i += chunk)
            decoder.Process(audio.AsSpan(i, Math.Min(chunk, audio.Length - i)));
        decoder.Flush();
        return images;
    }

    /// <summary>Mean absolute error per channel, ignoring a margin at the image edges.</summary>
    internal static void AssertImageClose(SstvMode mode, SstvImage image, double maxMeanError)
    {
        var margin = Math.Max(2, mode.Width / 64);
        double total = 0;
        var count = 0;
        for (var y = 2; y < mode.Height - 2; y++)
        {
            for (var x = margin; x < mode.Width - margin; x++)
            {
                var expected = SstvTestEncoder.TestPattern(x, y, mode.Width, mode.Height);
                var (r, g, b) = image.GetPixel(x, y);
                total += Math.Abs(r - Math.Clamp(expected.R, 0, 255))
                    + Math.Abs(g - Math.Clamp(expected.G, 0, 255))
                    + Math.Abs(b - Math.Clamp(expected.B, 0, 255));
                count += 3;
            }
        }

        var mean = total / count;
        Assert.True(mean <= maxMeanError, $"{mode.Name}: mean error {mean:0.00} > {maxMeanError}");
    }
}
