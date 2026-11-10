using OscarWatch.Core.Sstv;
using Xunit.Abstractions;

namespace OscarWatch.Tests.Sstv;

/// <summary>VIS headers read from tone power, and pictures found by adding up syncs over several lines.</summary>
public sealed class SstvToneDetectionTests(ITestOutputHelper output)
{
    private const int Rate = 24000;
    private const double LineSeconds = 0.150;
    /// <summary>Encoder lead-in silence before the picture.</summary>
    private const double Lead = 0.5;

    private static bool FoundVis(float[] audio, bool toneVis, IReadOnlyCollection<SstvModeId>? expected = null) =>
        SstvRoundTripTests.Decode(
                audio,
                new SstvDecoderOptions { DetectWithoutVis = false, ToneVis = toneVis, ExpectedModes = expected },
                Rate)
            .Any(i => i.FromVis && i.Mode.Id == SstvModeId.Robot36);

    [Fact]
    public void Tone_vis_reads_headers_the_leader_test_misses_below_the_fm_threshold()
    {
        int leader = 0, tone = 0;
        foreach (var cnr in new[] { 2.0, 3.0 })
        {
            for (var seed = 1; seed <= 6; seed++)
            {
                var audio = SstvTestEncoder.Encode(SstvModeTable.Robot36, Rate, new SstvTestEncoder.Settings { CnrDb = cnr, Seed = seed });
                leader += FoundVis(audio, toneVis: false) ? 1 : 0;
                tone += FoundVis(audio, toneVis: true) ? 1 : 0;
            }
        }

        output.WriteLine($"headers found: leader {leader}/12, tone {tone}/12");
        Assert.True(leader <= 1, $"leader {leader}");
        Assert.True(tone >= 6, $"tone {tone}");
    }

    [Fact]
    public void Tone_vis_only_accepts_a_mode_the_station_is_known_to_send()
    {
        var audio = SstvTestEncoder.Encode(SstvModeTable.Robot36, Rate, new SstvTestEncoder.Settings { CnrDb = 2, Seed = 1 });

        Assert.True(FoundVis(audio, toneVis: true, expected: [SstvModeId.Robot36]));
        Assert.False(FoundVis(audio, toneVis: true, expected: [SstvModeId.Pd120]));
    }

    [Fact]
    public void Noise_alone_starts_no_picture()
    {
        var random = new Random(7);
        var audio = new float[Rate * 120];
        for (var i = 0; i < audio.Length; i++)
        {
            var u1 = 1 - random.NextDouble();
            var u2 = random.NextDouble();
            audio[i] = (float)(0.3 * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }

        Assert.Empty(SstvRoundTripTests.Decode(audio, new SstvDecoderOptions(), Rate));
    }

    [Fact]
    public void Stacked_search_finds_a_picture_when_fades_take_two_syncs_in_five()
    {
        var mode = SstvModeTable.Robot36;
        // Each drop-out runs from just before one sync to just after the next, every five lines.
        var settings = new SstvTestEncoder.Settings
        {
            CnrDb = 20,
            IncludeVis = false,
            DropoutStartSeconds = Lead + 2 * LineSeconds - 0.004,
            DropoutEverySeconds = 5 * LineSeconds,
            DropoutSeconds = LineSeconds + 0.017,
        };
        var audio = SstvTestEncoder.Encode(mode, Rate, settings);

        var off = SstvRoundTripTests.Decode(audio, new SstvDecoderOptions { StackedSyncSearch = false }, Rate);
        var on = SstvRoundTripTests.Decode(audio, new SstvDecoderOptions(), Rate);

        Assert.DoesNotContain(off, i => i.Mode == mode);
        var image = Assert.Single(on, i => i.Mode == mode);
        output.WriteLine($"lock {image.LockPercent:0.0}% start {image.StartSeconds:0.00}s");
        Assert.True(image.LockPercent > 50, $"lock {image.LockPercent:0.0}%");
        Assert.True(image.StartSeconds < Lead + 2 * LineSeconds, $"start {image.StartSeconds:0.00}s");
    }
}
