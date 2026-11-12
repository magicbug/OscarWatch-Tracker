using OscarWatch.Core.Sstv;
using Xunit.Abstractions;

namespace OscarWatch.Tests.Sstv;

/// <summary>
/// Each optional decoder improvement against the plain decoder on the simulated satellite
/// link. An option has to help on weak links and must not hurt a clean one to stay on by default.
/// </summary>
public sealed class SstvDecoderOptionGainTests(ITestOutputHelper output)
{
    private static readonly double[] WeakLink = [5, 6, 7, 8, 10];

    /// <summary>Options with every fade defence off, plus whatever <paramref name="enable"/> turns on.</summary>
    internal static SstvDecoderOptions Plain(Action<SstvDecoderOptions>? enable = null)
    {
        var o = new SstvDecoderOptions
        {
            ImpulseSuppression = false,
            AdaptiveBandwidth = false,
            ToneSync = false,
            HoldThroughFades = false,
            DopplerLinePrior = false,
            ToneVis = false,
            StackedSyncSearch = false,
            WeightedPixels = false,
            AdaptiveSmoothing = false,
            NoiseFade = false,
        };
        enable?.Invoke(o);
        return o;
    }

    private (List<SstvChannelSimulationTests.Point> Off, List<SstvChannelSimulationTests.Point> On) Compare(
        SstvMode mode,
        Action<SstvDecoderOptions> enable,
        Func<double, SstvTestEncoder.Settings>? settings = null,
        double[]? cnr = null)
    {
        cnr ??= WeakLink;
        var off = SstvChannelSimulationTests.Curve(mode, cnr, () => Plain(), settings);
        var on = SstvChannelSimulationTests.Curve(mode, cnr, () =>
        {
            var o = Plain();
            enable(o);
            return o;
        }, settings);
        SstvChannelSimulationTests.Report(output, $"{mode.Name} off", off);
        SstvChannelSimulationTests.Report(output, $"{mode.Name} on ", on);
        output.WriteLine($"threshold off {SstvChannelSimulationTests.Threshold(off)} dB, on {SstvChannelSimulationTests.Threshold(on)} dB");
        return (off, on);
    }

    private static double TotalError(IEnumerable<SstvChannelSimulationTests.Point> curve) => curve.Sum(p => p.MeanError);

    [Theory]
    [InlineData(SstvModeId.Robot36)]
    [InlineData(SstvModeId.MartinM1)]
    public void Impulse_suppression_helps_on_a_weak_fm_link(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var (off, on) = Compare(mode, o => o.ImpulseSuppression = true);

        // The gain is small (a few percent near threshold): most noise after the radio's
        // own discriminator cannot be told apart from picture. It must never cost anything.
        Assert.True(TotalError(on) < TotalError(off), $"error {TotalError(on):0.0} on, {TotalError(off):0.0} off");
        Assert.True(SstvChannelSimulationTests.Threshold(on) <= SstvChannelSimulationTests.Threshold(off));
    }

    [Theory]
    [InlineData(SstvModeId.Robot36)]
    [InlineData(SstvModeId.Pd120)]
    public void Impulse_suppression_keeps_clean_pictures(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var off = SstvChannelSimulationTests.Measure(mode, new SstvTestEncoder.Settings(), Plain());
        var on = SstvChannelSimulationTests.Measure(mode, new SstvTestEncoder.Settings(), Plain(o => o.ImpulseSuppression = true));

        Assert.True(on.LockPercent >= off.LockPercent - 0.5);
        Assert.True(on.MeanError <= off.MeanError + 0.5, $"error {on.MeanError:0.00} on, {off.MeanError:0.00} off");
    }

    [Theory]
    [InlineData(SstvModeId.Robot36)]
    [InlineData(SstvModeId.MartinM1)]
    [InlineData(SstvModeId.Pd120)]
    public void Weighted_pixels_help_on_a_weak_fm_link(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var (off, on) = Compare(mode, o => o.WeightedPixels = true);

        Assert.True(TotalError(on) < TotalError(off), $"error {TotalError(on):0.0} on, {TotalError(off):0.0} off");
        Assert.True(on.Sum(p => p.LockPercent) >= off.Sum(p => p.LockPercent), "lock fell");
    }

    [Theory]
    [InlineData(SstvModeId.Robot36)]
    [InlineData(SstvModeId.Pd120)]
    [InlineData(SstvModeId.MartinM1)]
    public void Weighted_pixels_keep_clean_pictures(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var off = SstvChannelSimulationTests.Measure(mode, new SstvTestEncoder.Settings(), Plain());
        var on = SstvChannelSimulationTests.Measure(mode, new SstvTestEncoder.Settings(), Plain(o => o.WeightedPixels = true));

        Assert.Equal(off.LockPercent, on.LockPercent, 1);
        Assert.True(on.MeanError <= off.MeanError + 0.5, $"error {on.MeanError:0.00} on, {off.MeanError:0.00} off");
    }

    [Fact]
    public void Weighted_pixels_do_not_hurt_with_audio_clicks_and_bursts()
    {
        var mode = SstvModeTable.Pd120;
        var settings = new SstvTestEncoder.Settings
        {
            NoiseRms = 0.1,
            ClicksPerSecond = 80,
            ClickAmplitude = 2,
            BurstEveryMs = 1500,
            BurstMs = 120,
            BurstNoiseRms = 1.2,
        };
        var off = SstvChannelSimulationTests.Measure(mode, settings, Plain());
        var on = SstvChannelSimulationTests.Measure(mode, settings, Plain(o => o.WeightedPixels = true));
        output.WriteLine($"off lock {off.LockPercent:0.0}% error {off.MeanError:0.0}; on lock {on.LockPercent:0.0}% error {on.MeanError:0.0}");

        Assert.True(on.MeanError <= off.MeanError * 1.05, $"error {on.MeanError:0.0} on, {off.MeanError:0.0} off");
        Assert.Equal(off.LockPercent, on.LockPercent, 1);
    }

    [Theory]
    [InlineData(SstvModeId.MartinM1)]
    [InlineData(SstvModeId.Pd90)]
    public void Adaptive_bandwidth_helps_slow_modes_on_a_weak_fm_link(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        Assert.True(mode.PictureBandwidthHz < 1100, $"{mode.Name} {mode.PictureBandwidthHz:0} Hz");
        var (off, on) = Compare(mode, o => o.AdaptiveBandwidth = true);

        Assert.True(TotalError(on) < TotalError(off), $"error {TotalError(on):0.0} on, {TotalError(off):0.0} off");
        Assert.True(SstvChannelSimulationTests.Threshold(on) <= SstvChannelSimulationTests.Threshold(off));
    }

    [Theory]
    [InlineData(SstvModeId.MartinM1)]
    [InlineData(SstvModeId.Pd90)]
    [InlineData(SstvModeId.Robot36)]
    public void Adaptive_bandwidth_keeps_clean_pictures_in_place(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var off = SstvChannelSimulationTests.Measure(mode, new SstvTestEncoder.Settings(), Plain());
        var on = SstvChannelSimulationTests.Measure(mode, new SstvTestEncoder.Settings(), Plain(o => o.AdaptiveBandwidth = true));

        Assert.True(on.LockPercent >= off.LockPercent - 0.5);
        Assert.True(on.MeanError <= off.MeanError + 1.5, $"error {on.MeanError:0.00} on, {off.MeanError:0.00} off");
    }

    [Theory]
    [InlineData(SstvModeId.Robot36)]
    [InlineData(SstvModeId.MartinM1)]
    public void Tone_sync_holds_lock_on_a_weak_fm_link(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var (off, on) = Compare(mode, o => o.ToneSync = true, cnr: [3, 4, 5, 6, 7]);

        Assert.True(on.Sum(p => p.LockPercent) >= off.Sum(p => p.LockPercent), "lock fell");
        Assert.True(TotalError(on) <= TotalError(off) * 1.02, $"error {TotalError(on):0.0} on, {TotalError(off):0.0} off");
    }

    [Theory]
    [InlineData(SstvModeId.Robot36)]
    [InlineData(SstvModeId.Pd120)]
    [InlineData(SstvModeId.ScottieS1)]
    public void Tone_sync_keeps_clean_pictures_in_place(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var off = SstvChannelSimulationTests.Measure(mode, new SstvTestEncoder.Settings(), Plain());
        var on = SstvChannelSimulationTests.Measure(mode, new SstvTestEncoder.Settings(), Plain(o => o.ToneSync = true));

        Assert.True(on.LockPercent >= off.LockPercent - 0.5);
        Assert.True(on.MeanError <= off.MeanError + 1.5, $"error {on.MeanError:0.00} on, {off.MeanError:0.00} off");
    }

    [Fact]
    public void Hold_through_fades_keeps_a_picture_through_a_long_drop_out()
    {
        var mode = SstvModeTable.Pd120;
        var fade = new SstvTestEncoder.Settings
        {
            CnrDb = 16,
            DropoutStartSeconds = 40,
            DropoutEverySeconds = 1000,
            DropoutSeconds = 12,
        };
        var off = SstvChannelSimulationTests.Measure(mode, fade, Plain());
        var on = SstvChannelSimulationTests.Measure(mode, fade, Plain(o => o.HoldThroughFades = true));
        output.WriteLine($"off lock {off.LockPercent:0.0}% error {off.MeanError:0.0}; on lock {on.LockPercent:0.0}% error {on.MeanError:0.0}");

        Assert.True(on.Decoded);
        Assert.True(on.MeanError < off.MeanError - 10, "the rows after the drop-out should be decoded");
    }

    [Fact]
    public void Hold_through_fades_keeps_a_vis_picture_that_fades_straight_after_the_header()
    {
        var mode = SstvModeTable.Robot36;
        var fade = new SstvTestEncoder.Settings
        {
            CnrDb = 16,
            DropoutStartSeconds = 1.6,
            DropoutEverySeconds = 1000,
            DropoutSeconds = 2.4,
        };
        var off = SstvChannelSimulationTests.Measure(mode, fade, Plain());
        var on = SstvChannelSimulationTests.Measure(mode, fade, Plain(o => o.HoldThroughFades = true));
        output.WriteLine($"off lock {off.LockPercent:0.0}% decoded {off.Decoded}; on lock {on.LockPercent:0.0}% error {on.MeanError:0.0}");

        Assert.True(on.Decoded);
        Assert.True(on.MeanError <= off.MeanError);
    }

    [Theory]
    [InlineData(SstvModeId.Robot36)]
    [InlineData(SstvModeId.Pd120)]
    public void Hold_through_fades_keeps_clean_pictures_in_place(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var off = SstvChannelSimulationTests.Measure(mode, new SstvTestEncoder.Settings(), Plain());
        var on = SstvChannelSimulationTests.Measure(mode, new SstvTestEncoder.Settings(), Plain(o => o.HoldThroughFades = true));

        Assert.Equal(off.LockPercent, on.LockPercent, 1);
        Assert.Equal(off.MeanError, on.MeanError, 1);
    }

    /// <summary>Decode with the decoder told the simulator's Doppler sweep, as the orbit model would.</summary>
    private static (double Lock, double Error) DecodeWithDoppler(SstvMode mode, SstvTestEncoder.Settings settings, SstvDecoderOptions options)
    {
        const int rate = 24000;
        var audio = SstvTestEncoder.Encode(mode, rate, settings);
        var decoder = new SstvDecoder(rate, options);
        var images = new List<SstvDecodedImage>();
        decoder.ImageCompleted += (_, image) => images.Add(image);
        const int chunk = 1024;
        for (var i = 0; i < audio.Length; i += chunk)
        {
            decoder.TimeCompressionPpm = settings.TimeCompressionPpm * (1 - 2.0 * i / audio.Length);
            decoder.Process(audio.AsSpan(i, Math.Min(chunk, audio.Length - i)));
        }

        decoder.Flush();
        var best = images.Where(i => i.Mode == mode).OrderByDescending(i => i.LockPercent).FirstOrDefault();
        return best is null ? (0, 128) : (best.LockPercent, SstvRoundTripTests.MeanError(mode, best.Image));
    }

    [Theory]
    [InlineData(SstvModeId.Pd120)]
    [InlineData(SstvModeId.Robot36)]
    public void Doppler_prior_does_no_harm_through_a_fade_on_a_fast_sweep(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        // Far steeper than any real pass (about ±23 ppm at most), so the effect shows in one picture.
        var settings = new SstvTestEncoder.Settings
        {
            CnrDb = 14,
            TimeCompressionPpm = 600,
            DropoutStartSeconds = mode.TransmittedLines * mode.LineMs / 2000.0,
            DropoutEverySeconds = 1000,
            DropoutSeconds = 6,
        };
        var off = DecodeWithDoppler(mode, settings, Plain(o => o.HoldThroughFades = true));
        var on = DecodeWithDoppler(mode, settings, Plain(o => { o.HoldThroughFades = true; o.DopplerLinePrior = true; }));
        output.WriteLine($"{mode.Name}: off lock {off.Lock:0.0}% error {off.Error:0.0}; on lock {on.Lock:0.0}% error {on.Error:0.0}");

        // The finished picture is redrawn from a fit centred on each line, which already bends
        // with the sweep, so no gain is expected here: only the live rows can benefit.
        Assert.True(on.Error <= off.Error + 0.5, $"error {on.Error:0.0} on, {off.Error:0.0} off");
        Assert.True(on.Lock >= off.Lock - 0.5);
    }

    [Fact]
    public void Doppler_prior_with_no_doppler_changes_nothing()
    {
        var mode = SstvModeTable.Robot36;
        var settings = new SstvTestEncoder.Settings { CnrDb = 10 };
        var off = DecodeWithDoppler(mode, settings, Plain());
        var on = DecodeWithDoppler(mode, settings, Plain(o => o.DopplerLinePrior = true));
        Assert.Equal(off, on);
    }

    [Theory]
    [InlineData(SstvModeId.Pd120)]
    [InlineData(SstvModeId.Robot36)]
    public void Aos_fragment_is_moved_down_to_the_rows_it_belongs_on(SstvModeId id)
    {
        const int rate = 24000;
        var mode = SstvModeTable.Get(id);
        var full = SstvTestEncoder.Encode(mode, rate, new SstvTestEncoder.Settings { IncludeVis = false });
        // Pick the picture up 40% of the way through, as at AOS, then the sender stops.
        var from = (int)(full.Length * 0.4);
        var audio = new float[full.Length - from + rate * 12];
        Array.Copy(full, from, audio, 0, full.Length - from);

        SstvDecodedImage Decode(bool align) => SstvRoundTripTests.Decode(audio, new SstvDecoderOptions { AlignAosFragments = align }, rate)
            .Where(i => i.Mode == mode).MaxBy(i => i.LockPercent)!;
        var off = Decode(false);
        var on = Decode(true);

        var bottom = BottomError(mode, on.Image);
        output.WriteLine($"{mode.Name}: bottom rows error {BottomError(mode, off.Image):0.0} off, {bottom:0.0} on");
        Assert.True(bottom < 15, $"bottom rows error {bottom:0.0}");
        Assert.True(BottomError(mode, off.Image) > 30);
        Assert.Equal(SstvLineQuality.Missing, on.LineQuality[0]);
        Assert.Equal(SstvLineQuality.Locked, on.LineQuality[^2]);
    }

    /// <summary>Mean error over the bottom quarter of the picture.</summary>
    private static double BottomError(SstvMode mode, SstvImage image)
    {
        double total = 0;
        var count = 0;
        for (var y = mode.Height * 3 / 4; y < mode.Height - 2; y++)
        {
            for (var x = 8; x < mode.Width - 8; x++)
            {
                var e = SstvTestEncoder.TestPattern(x, y, mode.Width, mode.Height);
                var (r, g, b) = image.GetPixel(x, y);
                total += Math.Abs(r - Math.Clamp(e.R, 0, 255)) + Math.Abs(g - Math.Clamp(e.G, 0, 255)) + Math.Abs(b - Math.Clamp(e.B, 0, 255));
                count += 3;
            }
        }

        return total / count;
    }

    [Theory]
    [InlineData(SstvModeId.Robot36)]
    [InlineData(SstvModeId.Pd120)]
    public void Repair_redraws_lines_lost_in_short_drop_outs(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var line = mode.LineMs / 1000.0;
        var settings = new SstvTestEncoder.Settings
        {
            CnrDb = 18,
            DropoutStartSeconds = 5,
            DropoutEverySeconds = 3.3,
            DropoutSeconds = line * 1.5,
        };
        var off = SstvChannelSimulationTests.Measure(mode, settings, Plain());
        var on = SstvChannelSimulationTests.Measure(mode, settings, Plain(o => o.RepairBadLines = true));
        output.WriteLine($"{mode.Name}: error {off.MeanError:0.0} off, {on.MeanError:0.0} on");

        Assert.True(on.MeanError < off.MeanError * 0.85, $"error {on.MeanError:0.0} on, {off.MeanError:0.0} off");
    }

    [Theory]
    [InlineData(SstvModeId.Robot36)]
    [InlineData(SstvModeId.Pd120)]
    [InlineData(SstvModeId.MartinM1)]
    public void Repair_leaves_a_clean_picture_alone(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var audio = SstvTestEncoder.Encode(mode, 24000, new SstvTestEncoder.Settings { CnrDb = 20 });
        var image = SstvRoundTripTests.Decode(audio, new SstvDecoderOptions { RepairBadLines = true }, 24000).Single(i => i.Mode == mode);

        Assert.Empty(image.RepairedLines);
        Assert.InRange(image.LineNoise.Where(float.IsFinite).Max(), 0, 2.5);
    }

    [Theory]
    [InlineData(SstvModeId.Robot36)]
    [InlineData(SstvModeId.MartinM1)]
    [InlineData(SstvModeId.Pd120)]
    public void Smoothing_across_lines_cuts_speckle_on_a_weak_fm_link(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var (off, on) = Compare(mode, o => o.SmoothAcrossLines = true);

        Assert.True(TotalError(on) < TotalError(off) * 0.85, $"error {TotalError(on):0.0} on, {TotalError(off):0.0} off");
        Assert.Equal(off.Sum(p => p.LockPercent), on.Sum(p => p.LockPercent), 1);
    }

    [Theory]
    [InlineData(SstvModeId.Robot36)]
    [InlineData(SstvModeId.Pd120)]
    public void Smoothing_across_lines_keeps_clean_pictures_close(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var off = SstvChannelSimulationTests.Measure(mode, new SstvTestEncoder.Settings(), Plain());
        var on = SstvChannelSimulationTests.Measure(mode, new SstvTestEncoder.Settings(), Plain(o => o.SmoothAcrossLines = true));

        Assert.Equal(off.LockPercent, on.LockPercent, 1);
        Assert.True(on.MeanError <= off.MeanError + 1.5, $"error {on.MeanError:0.00} on, {off.MeanError:0.00} off");
    }

    [Fact]
    public void Smoothing_removes_a_lone_click_and_keeps_a_straight_edge()
    {
        var image = new SstvImage(8, 6);
        for (var y = 0; y < 6; y++)
        {
            for (var x = 0; x < 8; x++)
                image.SetPixel(x, y, x < 4 ? 20 : 220, 100, 100);
        }

        image.SetPixel(1, 2, 255, 255, 255);
        SstvPictureSmoother.Smooth(image, 0, 6);

        Assert.Equal((byte)20, image.GetPixel(1, 2).R);
        Assert.Equal((byte)100, image.GetPixel(1, 2).G);
        for (var y = 0; y < 6; y++)
        {
            Assert.Equal((byte)20, image.GetPixel(3, y).R);
            Assert.Equal((byte)220, image.GetPixel(4, y).R);
        }
    }

    [Fact]
    public void Smoothing_leaves_rows_not_yet_received_black()
    {
        var image = new SstvImage(6, 6);
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 6; x++)
                image.SetPixel(x, y, 200, 200, 200);
        }

        SstvPictureSmoother.Smooth(image, 0, 3);

        Assert.Equal((byte)200, image.GetPixel(2, 2).R);
        Assert.Equal((byte)0, image.GetPixel(2, 3).R);
    }

    [Theory]
    [InlineData(SstvModeId.Robot36)]
    [InlineData(SstvModeId.Pd120)]
    public void Adaptive_smoothing_cuts_speckle_on_a_weak_fm_link(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var (off, on) = Compare(mode, o => o.AdaptiveSmoothing = true);

        Assert.True(TotalError(on) < TotalError(off) * 0.95, $"error {TotalError(on):0.0} on, {TotalError(off):0.0} off");
        Assert.Equal(off.Sum(p => p.LockPercent), on.Sum(p => p.LockPercent), 1);
    }

    [Theory]
    [InlineData(SstvModeId.Robot36)]
    [InlineData(SstvModeId.Pd120)]
    public void Adaptive_smoothing_keeps_clean_pictures_close(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        foreach (var cnr in new[] { 40.0, 30.0 })
        {
            var settings = new SstvTestEncoder.Settings { CnrDb = cnr };
            var off = SstvChannelSimulationTests.Measure(mode, settings, Plain());
            var on = SstvChannelSimulationTests.Measure(mode, settings, Plain(o => o.AdaptiveSmoothing = true));

            Assert.Equal(off.LockPercent, on.LockPercent, 1);
            Assert.True(on.MeanError <= off.MeanError + 0.3, $"{cnr} dB: error {on.MeanError:0.00} on, {off.MeanError:0.00} off");
        }
    }

    [Fact]
    public void Adaptive_smoothing_leaves_a_clean_picture_untouched()
    {
        var image = new SstvImage(32, 40);
        for (var y = 0; y < 40; y++)
        {
            for (var x = 0; x < 32; x++)
                image.SetPixel(x, y, (byte)(x < 16 ? 20 : 220), (byte)(y * 5), 100);
        }

        image.SetPixel(5, 20, 255, 255, 255);
        var before = image.Rgb.ToArray();
        SstvPictureSmoother.SmoothAdaptive(image, 0, 40);

        Assert.Equal(before, image.Rgb);
    }

    [Fact]
    public void Adaptive_smoothing_flattens_a_noisy_picture_and_not_rows_not_yet_received()
    {
        var random = new Random(7);
        var image = new SstvImage(32, 40);
        for (var y = 0; y < 30; y++)
        {
            for (var x = 0; x < 32; x++)
                image.SetPixel(x, y, (byte)random.Next(60, 200), (byte)random.Next(60, 200), (byte)random.Next(60, 200));
        }

        static double Spread(SstvImage i)
        {
            double sum = 0;
            for (var y = 5; y < 25; y++)
            {
                for (var x = 1; x < 31; x++)
                    sum += Math.Abs(i.GetPixel(x, y).G - i.GetPixel(x + 1, y).G);
            }

            return sum;
        }

        var before = Spread(image);
        SstvPictureSmoother.SmoothAdaptive(image, 0, 30);

        Assert.True(Spread(image) < before * 0.7, $"{Spread(image):0} after, {before:0} before");
        Assert.Equal((byte)0, image.GetPixel(3, 35).G);
    }

    [Fact]
    public void Adaptive_smoothing_weights_rise_with_noise_and_stay_in_range()
    {
        const int rows = 30;
        const int width = 10;
        const int stride = width * 3;
        // Pixels alternate either side of 100 along each row, which is what noise looks like: far from the 3 x 3
        // median and from the row's own 3 pixel median.
        var spread = new[] { 0, 3, 6, 8, 12 };
        var weights = new float[spread.Length];
        for (var level = 0; level < spread.Length; level++)
        {
            var src = new byte[rows * stride];
            var med = new byte[rows * stride];
            Array.Fill<byte>(med, 100);
            for (var y = 0; y < rows; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    for (var c = 0; c < 3; c++)
                        src[y * stride + x * 3 + c] = (byte)(100 + (x % 2 == 0 ? spread[level] : -spread[level]));
                }
            }

            weights[level] = SstvPictureSmoother.RowWeights(src, med, rows, stride)[rows / 2];
        }

        Assert.Equal(0f, weights[0]);
        Assert.Equal(0f, weights[1]);
        Assert.Equal(1f, weights[4]);
        for (var i = 1; i < weights.Length; i++)
            Assert.True(weights[i] >= weights[i - 1]);
        Assert.InRange(weights[2], 0.01f, 0.99f);
    }

    [Fact]
    public void Adaptive_smoothing_leaves_fine_horizontal_lines_alone()
    {
        // Lines one row apart are as far from their 3 x 3 median as noise is, but they are the same along the row.
        var image = new SstvImage(32, 40);
        for (var y = 0; y < 40; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                var v = (byte)(y % 2 == 0 ? 20 : 220);
                image.SetPixel(x, y, v, v, v);
            }
        }

        var before = image.Rgb.ToArray();
        SstvPictureSmoother.SmoothAdaptive(image, 0, 40);

        Assert.Equal(before, image.Rgb);
    }

    [Fact]
    public void Picture_bandwidth_follows_the_pixel_rate()
    {
        Assert.Equal(1200, SstvModeTable.Robot36.PictureBandwidthHz);
        Assert.Equal(1200, SstvModeTable.Pd120.PictureBandwidthHz);
        Assert.InRange(SstvModeTable.ScottieDx.PictureBandwidthHz, 600, 680);
        Assert.InRange(SstvModeTable.MartinM1.PictureBandwidthHz, 900, 1000);
    }

    /// <summary>
    /// Clicks added to the audio raise the subcarrier envelope, so power weighting cannot pick
    /// them out (the per-sample clamp in the pixel mean is what limits them). The option must
    /// not make such pictures much worse.
    /// </summary>
    [Fact]
    public void Impulse_suppression_does_not_hurt_with_audio_clicks_and_bursts()
    {
        var mode = SstvModeTable.Pd120;
        var settings = new SstvTestEncoder.Settings
        {
            NoiseRms = 0.1,
            ClicksPerSecond = 80,
            ClickAmplitude = 2,
            BurstEveryMs = 1500,
            BurstMs = 120,
            BurstNoiseRms = 1.2,
        };
        var off = SstvChannelSimulationTests.Measure(mode, settings, Plain());
        var on = SstvChannelSimulationTests.Measure(mode, settings, Plain(o => o.ImpulseSuppression = true));
        output.WriteLine($"off lock {off.LockPercent:0.0}% error {off.MeanError:0.0}; on lock {on.LockPercent:0.0}% error {on.MeanError:0.0}");

        Assert.True(on.MeanError <= off.MeanError * 1.05, $"error {on.MeanError:0.0} on, {off.MeanError:0.0} off");
        Assert.True(on.LockPercent >= off.LockPercent - 2);
    }
}
