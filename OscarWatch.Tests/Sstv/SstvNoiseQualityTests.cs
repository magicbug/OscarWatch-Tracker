using OscarWatch.Core.Sstv;

namespace OscarWatch.Tests.Sstv;

/// <summary>Chroma smoothing must lower colour noise in the Robot and PD modes without blurring clean pictures.</summary>
public sealed class SstvNoiseQualityTests
{
    private const int Rate = 48000;

    private static SstvTestEncoder.Settings Condition(string name) => name switch
    {
        "gaussian" => new SstvTestEncoder.Settings { NoiseRms = 0.35 },
        "clicks" => new SstvTestEncoder.Settings { NoiseRms = 0.1, ClicksPerSecond = 60, ClickAmplitude = 1.5 },
        "bursts" => new SstvTestEncoder.Settings { NoiseRms = 0.05, BurstEveryMs = 1000, BurstMs = 80, BurstNoiseRms = 1.0 },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [InlineData(SstvModeId.Pd120, "gaussian")]
    [InlineData(SstvModeId.Pd120, "clicks")]
    [InlineData(SstvModeId.Pd120, "bursts")]
    [InlineData(SstvModeId.Robot36, "gaussian")]
    [InlineData(SstvModeId.Robot36, "clicks")]
    [InlineData(SstvModeId.Robot72, "gaussian")]
    public void Chroma_smoothing_lowers_colour_noise(SstvModeId id, string condition)
    {
        var mode = SstvModeTable.Get(id);
        var audio = SstvTestEncoder.Encode(mode, Rate, Condition(condition));

        var off = Error(mode, audio, chromaSmoothing: false);
        var on = Error(mode, audio, chromaSmoothing: true);

        Assert.True(on < 0.93 * off, $"{mode.Name} {condition}: error {on:0.00} with smoothing, {off:0.00} without");
    }

    [Theory]
    [InlineData(SstvModeId.Robot36)]
    [InlineData(SstvModeId.Robot72)]
    [InlineData(SstvModeId.Pd120)]
    public void Chroma_smoothing_keeps_clean_pictures(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var audio = SstvTestEncoder.Encode(mode, Rate);

        var off = Error(mode, audio, chromaSmoothing: false);
        var on = Error(mode, audio, chromaSmoothing: true);

        Assert.True(on <= off + 0.3, $"{mode.Name}: error {on:0.00} with smoothing, {off:0.00} without");
    }

    [Fact]
    public void Rgb_modes_are_unchanged_by_chroma_smoothing()
    {
        var mode = SstvModeTable.MartinM1;
        var audio = SstvTestEncoder.Encode(mode, Rate, new SstvTestEncoder.Settings { NoiseRms = 0.2 });

        var off = Assert.Single(SstvRoundTripTests.Decode(audio, new SstvDecoderOptions { ChromaSmoothing = false }));
        var on = Assert.Single(SstvRoundTripTests.Decode(audio, new SstvDecoderOptions { ChromaSmoothing = true }));

        Assert.Equal(off.Image.Rgb, on.Image.Rgb);
    }

    private static double Error(SstvMode mode, float[] audio, bool chromaSmoothing)
    {
        var image = Assert.Single(SstvRoundTripTests.Decode(audio, new SstvDecoderOptions { ChromaSmoothing = chromaSmoothing, AdaptiveSmoothing = false }));
        return SstvRoundTripTests.MeanError(mode, image.Image);
    }
}
