using OscarWatch.Core.Sstv;
using Xunit.Abstractions;

namespace OscarWatch.Tests.Sstv;

/// <summary>Picking up and holding pictures through carrier drop-outs on the simulated FM link.</summary>
public sealed class SstvFadeAcquisitionTests(ITestOutputHelper output)
{
    private const int Rate = 24000;
    private const double LineSeconds = 0.150;
    /// <summary>Encoder lead-in silence before the VIS.</summary>
    private const double Lead = 0.5;
    /// <summary>Start of the picture after the 910 ms VIS.</summary>
    private const double PictureStart = Lead + 0.91;

    [Fact]
    public void Vis_is_read_when_a_fade_takes_out_part_of_the_leader()
    {
        // The second leader runs from 0.81 s to 1.11 s after the lead-in; drop 80 ms of it.
        var settings = new SstvTestEncoder.Settings
        {
            CnrDb = 20,
            DropoutStartSeconds = Lead + 0.43,
            DropoutEverySeconds = 1000,
            DropoutSeconds = 0.08,
        };
        var audio = SstvTestEncoder.Encode(SstvModeTable.Robot36, Rate, settings);

        var images = SstvRoundTripTests.Decode(audio, new SstvDecoderOptions { DetectWithoutVis = false }, Rate);

        var image = Assert.Single(images);
        Assert.Equal(SstvModeId.Robot36, image.Mode.Id);
        Assert.True(image.FromVis);
        Assert.True(image.LockPercent > 90, $"lock {image.LockPercent:0.0}%");
    }

    [Fact]
    public void Picture_content_is_not_read_as_a_vis_after_a_faded_leader()
    {
        // No header at all: a sync-started picture full of steady tones must not become a VIS picture.
        var settings = new SstvTestEncoder.Settings { CnrDb = 20, IncludeVis = false };
        foreach (var mode in new[] { SstvModeTable.Robot36, SstvModeTable.Pd120, SstvModeTable.MartinM1 })
        {
            var audio = SstvTestEncoder.Encode(mode, Rate, settings);
            var images = SstvRoundTripTests.Decode(audio, new SstvDecoderOptions(), Rate);
            Assert.DoesNotContain(images, i => i.FromVis);
        }
    }

    [Fact]
    public void Sync_train_is_found_when_every_third_sync_is_lost()
    {
        var mode = SstvModeTable.Robot36;
        var settings = new SstvTestEncoder.Settings
        {
            CnrDb = 20,
            IncludeVis = false,
            DropoutStartSeconds = Lead + 2 * LineSeconds - 0.004,
            DropoutEverySeconds = 3 * LineSeconds,
            DropoutSeconds = 0.018,
        };
        var audio = SstvTestEncoder.Encode(mode, Rate, settings);

        var images = SstvRoundTripTests.Decode(audio, new SstvDecoderOptions(), Rate);

        var image = images.Where(i => i.Mode == mode).MaxBy(i => i.LockPercent);
        Assert.NotNull(image);
        output.WriteLine($"lock {image.LockPercent:0.0}% error {SstvRoundTripTests.MeanError(mode, image.Image):0.0}");
        Assert.True(image.LockPercent > 55, $"lock {image.LockPercent:0.0}%");
        Assert.True(SstvRoundTripTests.MeanError(mode, image.Image) < 20);
    }

    [Fact]
    public void Robot36_colours_survive_drop_outs_over_the_chroma_separators()
    {
        var mode = SstvModeTable.Robot36;
        // The separator sits 100 ms into each line; drop it on every third line.
        var settings = new SstvTestEncoder.Settings
        {
            CnrDb = 20,
            DropoutStartSeconds = PictureStart + 0.099,
            DropoutEverySeconds = 3 * LineSeconds,
            DropoutSeconds = 0.0065,
        };
        var audio = SstvTestEncoder.Encode(mode, Rate, settings);

        var image = SstvRoundTripTests.Decode(audio, new SstvDecoderOptions(), Rate).Single(i => i.Mode == mode);
        var error = SstvRoundTripTests.MeanError(mode, image.Image);
        output.WriteLine($"error {error:0.0}");
        Assert.True(error < 12, $"error {error:0.0}");
    }

    [Fact]
    public void Held_picture_keeps_its_rows_after_a_long_fade_mid_picture()
    {
        var mode = SstvModeTable.Pd120;
        var settings = new SstvTestEncoder.Settings
        {
            CnrDb = 16,
            DropoutStartSeconds = 30,
            DropoutEverySeconds = 1000,
            DropoutSeconds = 25,
        };
        var audio = SstvTestEncoder.Encode(mode, Rate, settings);

        var image = SstvRoundTripTests.Decode(audio, new SstvDecoderOptions(), Rate)
            .Where(i => i.Mode == mode)
            .MaxBy(i => i.LockPercent);

        Assert.NotNull(image);
        // Rows sent after the fade ended must be in this picture, not cut off at the fade.
        Assert.Equal(SstvLineQuality.Locked, image.LineQuality[^2]);
        Assert.True(image.LockPercent > 70, $"lock {image.LockPercent:0.0}%");
    }

    [Fact]
    public void Picture_that_ends_in_a_long_fade_drops_the_noise_after_its_last_sync()
    {
        var mode = SstvModeTable.Robot36;
        var settings = new SstvTestEncoder.Settings
        {
            CnrDb = 18,
            DropoutStartSeconds = PictureStart + 10,
            DropoutEverySeconds = 1000,
            DropoutSeconds = 1000,
        };
        var audio = SstvTestEncoder.Encode(mode, Rate, settings);

        var image = SstvRoundTripTests.Decode(audio, new SstvDecoderOptions(), Rate).Single(i => i.Mode == mode);

        Assert.False(image.Complete);
        Assert.Equal(SstvLineQuality.Missing, image.LineQuality[^1]);
        Assert.InRange(image.LineQuality.Count(q => q == SstvLineQuality.Locked), 55, 75);
    }
}
