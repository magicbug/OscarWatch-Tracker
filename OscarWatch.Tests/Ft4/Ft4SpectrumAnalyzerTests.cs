using OscarWatch.Core.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft4SpectrumAnalyzerTests
{
    [Fact]
    public void Pixel_to_hz_and_back_round_trips_at_edges()
    {
        Assert.Equal(200, Ft4SpectrumAnalyzer.PixelToHz(0, 400, 200, 3000), 3);
        Assert.Equal(3000, Ft4SpectrumAnalyzer.PixelToHz(400, 400, 200, 3000), 3);
        Assert.Equal(0, Ft4SpectrumAnalyzer.HzToPixel(200, 400, 200, 3000), 3);
        Assert.Equal(400, Ft4SpectrumAnalyzer.HzToPixel(3000, 400, 200, 3000), 3);
    }

    [Fact]
    public void Bracket_centre_outside_the_passband_stays_fully_inside()
    {
        const double half = Ft4SpectrumAnalyzer.Ft4FilterHalfWidthHz;
        Assert.Equal(1500, Ft4SpectrumAnalyzer.VisibleBracketCentreHz(1500, half, 200, 3000), 3);
        Assert.Equal(200 + half, Ft4SpectrumAnalyzer.VisibleBracketCentreHz(0, half, 200, 3000), 3);
        Assert.Equal(3000 - half, Ft4SpectrumAnalyzer.VisibleBracketCentreHz(20000, half, 200, 3000), 3);
        Assert.Equal(1600, Ft4SpectrumAnalyzer.VisibleBracketCentreHz(double.NaN, half, 200, 3000), 3);

        var centre = Ft4SpectrumAnalyzer.VisibleBracketCentreHz(0, half, 200, 3000);
        var left = Ft4SpectrumAnalyzer.HzToPixel(centre - half, 400, 200, 3000);
        var right = Ft4SpectrumAnalyzer.HzToPixel(centre + half, 400, 200, 3000);
        Assert.True(right - left > 1);
        Assert.InRange(left, 0, 400);
        Assert.InRange(right, 0, 400);
    }

    [Fact]
    public void Tone_at_1500_hz_peaks_near_mid_passband()
    {
        const int sampleRate = 12000;
        const int n = 2048;
        var samples = new float[n];
        for (var i = 0; i < n; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * 1500 * i / sampleRate);

        var bins = new float[400];
        Assert.True(Ft4SpectrumAnalyzer.TryComputePassband(samples, sampleRate, bins));

        var peak = 0;
        for (var i = 1; i < bins.Length; i++)
        {
            if (bins[i] > bins[peak])
                peak = i;
        }

        var peakHz = Ft4SpectrumAnalyzer.PixelToHz(peak, bins.Length - 1, 200, 3000);
        Assert.InRange(peakHz, 1400, 1600);
    }

    [Theory]
    [InlineData(-60, -78)]
    [InlineData(-78, -60)]
    public void Noise_floor_settles_within_ten_rows_after_an_agc_step(double startDb, double newFloorDb)
    {
        var floor = startDb;
        for (var row = 0; row < 10; row++)
            floor = Ft4SpectrumAnalyzer.FollowNoiseFloor(floor, newFloorDb);

        Assert.InRange(floor, newFloorDb - 0.5, newFloorDb + 0.5);
    }

    [Fact]
    public void Averaging_several_ffts_steadies_the_noise()
    {
        const int sampleRate = 48000;
        var random = new Random(73);
        var samples = new float[8192];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(random.NextDouble() * 2 - 1) * 0.1f;

        var single = new float[400];
        var averaged = new float[400];
        Assert.True(Ft4SpectrumAnalyzer.TryComputePassband(samples.AsSpan(^4096..), sampleRate, single));
        Assert.True(Ft4SpectrumAnalyzer.TryComputePassband(samples, sampleRate, averaged));

        Assert.True(Spread(averaged) < Spread(single) * 0.8);

        static double Spread(float[] bins)
        {
            var mean = bins.Average();
            return Math.Sqrt(bins.Average(b => (b - mean) * (b - mean)));
        }
    }

    [Fact]
    public void Flatten_baseline_follows_the_filter_slope_but_not_a_signal()
    {
        var bins = new float[400];
        for (var x = 0; x < bins.Length; x++)
            bins[x] = -90f + 20f * x / (bins.Length - 1);
        for (var x = 200; x < 212; x++)
            bins[x] += 30f;

        var floors = new double[Ft4SpectrumAnalyzer.FlattenSegments];
        var baseline = new float[bins.Length];
        Ft4SpectrumAnalyzer.MeasureSegmentFloorsDb(bins, floors);
        Ft4SpectrumAnalyzer.InterpolateBaselineDb(floors, baseline);

        for (var x = 40; x < 360; x++)
        {
            var slope = -90f + 20f * x / (bins.Length - 1);
            Assert.InRange(baseline[x], slope - 2.5f, slope + 2.5f);
        }

        Assert.True(bins[205] - baseline[205] > 25);
    }

    [Fact]
    public void Noise_floor_estimate_is_below_tone_peak()
    {
        const int sampleRate = 12000;
        const int n = 2048;
        var samples = new float[n];
        for (var i = 0; i < n; i++)
            samples[i] = 0.05f * (float)Math.Sin(2 * Math.PI * 1500 * i / sampleRate);

        var bins = new float[400];
        Assert.True(Ft4SpectrumAnalyzer.TryComputePassband(samples, sampleRate, bins));
        var floor = Ft4SpectrumAnalyzer.EstimateNoiseFloorDb(bins);
        var peak = bins.Max();
        Assert.True(peak > floor + 6);
    }
}
