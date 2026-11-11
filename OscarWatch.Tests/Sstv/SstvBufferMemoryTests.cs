using OscarWatch.Core.Sstv;

namespace OscarWatch.Tests.Sstv;

/// <summary>
/// The SSTV buffers grow while a long picture is decoded. After a trim they must give the memory back,
/// without changing any value that is still retained or the absolute indices that address it.
/// </summary>
public sealed class SstvBufferMemoryTests
{
    private const int Million = 1_000_000;

    private static float[] Samples(int count, int seed)
    {
        var random = new Random(seed);
        var data = new float[count];
        for (var i = 0; i < count; i++)
            data[i] = 1200 + 1100 * (float)random.NextDouble();
        return data;
    }

    [Fact]
    public void Capacity_policy_leaves_small_and_well_used_buffers_alone()
    {
        Assert.Equal(SstvBufferCapacity.Minimum, SstvBufferCapacity.After(10, SstvBufferCapacity.Minimum));
        Assert.Equal(1 << 20, SstvBufferCapacity.After(1 << 18, 1 << 20));       // exactly a quarter: keep
        Assert.Equal(1 << 20, SstvBufferCapacity.After(600_000, 1 << 20));
    }

    [Fact]
    public void Capacity_policy_shrinks_a_mostly_empty_buffer_with_room_to_spare()
    {
        var shrunk = SstvBufferCapacity.After(168_000, 1 << 22);

        Assert.True(shrunk >= 2 * 168_000, $"shrunk to {shrunk}");
        Assert.True(shrunk < 1 << 22);
        Assert.Equal(1 << 19, shrunk);                                           // a power of two, like growth
        Assert.Equal(SstvBufferCapacity.Minimum, SstvBufferCapacity.After(0, 1 << 22));
    }

    [Fact]
    public void Search_sized_trims_do_not_reallocate_every_time()
    {
        // The search keeps 14 s of 12 kHz audio and trims when 28 s have built up.
        var keep = 14 * 12_000;
        var capacity = SstvBufferCapacity.After(keep, 1 << 19);

        Assert.Equal(1 << 19, capacity);
    }

    [Fact]
    public void Signal_buffer_shrinks_after_a_trim_and_keeps_every_retained_value()
    {
        var data = Samples(Million, 1);
        var weights = Samples(Million, 2).Select(v => v / 2300f).ToArray();
        var trimmed = new SstvSignalBuffer { Weighted = true };
        var reference = new SstvSignalBuffer { Weighted = true };
        trimmed.Append(data, weights);
        reference.Append(data, weights);
        var grown = trimmed.Capacity;

        trimmed.TrimBefore(Million - 100_000);

        Assert.True(trimmed.Capacity < grown, $"capacity {trimmed.Capacity} was {grown}");
        Assert.Equal(Million - 100_000, trimmed.Start);
        Assert.Equal(Million, trimmed.End);
        for (long at = trimmed.Start; at < trimmed.End; at += 997)
        {
            Assert.Equal(reference[at], trimmed[at]);
            Assert.Equal(reference.Weight(at), trimmed.Weight(at));
        }

        foreach (var (from, to) in new[] { (Million - 100_000L, Million - 1L), (Million - 50_000L, Million - 20_000L), (Million - 1000L, (long)Million) })
        {
            Assert.Equal(reference.Mean(from, to), trimmed.Mean(from, to), precision: 6);
            Assert.Equal(reference.StdDev(from, to), trimmed.StdDev(from, to), precision: 6);
            Assert.Equal(reference.MeanStoredWeight(from, to), trimmed.MeanStoredWeight(from, to), precision: 9);
        }
    }

    [Fact]
    public void Signal_buffer_keeps_working_after_it_shrinks()
    {
        var trimmed = new SstvSignalBuffer();
        var reference = new SstvSignalBuffer();
        var first = Samples(Million, 3);
        var more = Samples(300_000, 4);
        trimmed.Append(first);
        reference.Append(first);
        trimmed.TrimBefore(Million - 20_000);
        trimmed.Append(more);
        reference.Append(more);

        Assert.Equal(Million + 300_000, trimmed.End);
        var from = (long)Million - 5_000;
        var to = (long)Million + 250_000;
        Assert.Equal(reference.Mean(from, to), trimmed.Mean(from, to), precision: 6);
        Assert.Equal(reference.StdDev(from, to), trimmed.StdDev(from, to), precision: 6);
        Assert.Equal(reference[Million + 12_345], trimmed[Million + 12_345]);
    }

    [Fact]
    public void Signal_buffer_trim_to_the_end_leaves_it_empty_and_small()
    {
        var buffer = new SstvSignalBuffer();
        buffer.Append(Samples(Million, 5));

        buffer.TrimBefore(Million + 50);

        Assert.Equal(Million, buffer.Start);
        Assert.Equal(Million, buffer.End);
        Assert.Equal(SstvBufferCapacity.Minimum, buffer.Capacity);
        buffer.Append(new float[] { 1900f, 1901f });
        Assert.Equal(1901f, buffer[Million + 1]);
    }

    [Fact]
    public void Baseband_buffer_shrinks_after_a_trim_and_keeps_every_retained_value()
    {
        var baseband = new SstvBasebandBuffer();
        for (var i = 0; i < Million; i++)
            baseband.Add(i, -i);
        var grown = baseband.Capacity;

        baseband.TrimBefore(Million - 50_000);

        Assert.True(baseband.Capacity < grown);
        Assert.Equal(Million - 50_000, baseband.Start);
        for (long at = baseband.Start; at < baseband.End; at += 1009)
        {
            Assert.Equal((float)at, baseband.I(at));
            Assert.Equal((float)-at, baseband.Q(at));
        }

        baseband.Add(7, 8);
        Assert.Equal(Million + 1, baseband.End);
        Assert.Equal(7f, baseband.I(Million));
    }

    [Fact]
    public void Raw_audio_buffer_returns_what_was_appended_and_shrinks_after_a_trim()
    {
        var raw = new SstvRawAudioBuffer();
        var data = Samples(Million, 6);
        raw.Append(data);
        var grown = raw.Capacity;

        raw.TrimBefore(Million - 60_000);

        Assert.True(raw.Capacity < grown);
        Assert.Equal(Million - 60_000, raw.Start);
        Assert.Equal(Million, raw.End);
        for (long at = raw.Start; at < raw.End; at += 991)
            Assert.Equal(data[at], raw[at]);

        raw.Append(new List<float> { 1f, 2f, 3f });
        Assert.Equal(Million + 3, raw.End);
        Assert.Equal(2f, raw[Million + 1]);
    }

    [Fact]
    public void Raw_audio_buffer_matches_the_signal_buffer_it_replaces()
    {
        var raw = new SstvRawAudioBuffer();
        var signal = new SstvSignalBuffer();
        var rng = new Random(9);
        long position = 0;
        for (var round = 0; round < 40; round++)
        {
            var chunk = Samples(rng.Next(1, 40_000), round);
            raw.Append(chunk);
            signal.Append(chunk);
            position += chunk.Length;
            if (round % 7 == 6)
            {
                var cut = position - rng.Next(100, 30_000);
                raw.TrimBefore(cut);
                signal.TrimBefore(cut);
            }

            Assert.Equal(signal.Start, raw.Start);
            Assert.Equal(signal.End, raw.End);
            for (var k = 0; k < 20; k++)
            {
                var at = raw.Start + (long)(rng.NextDouble() * (raw.End - raw.Start - 1));
                Assert.Equal(signal[at], raw[at]);
            }
        }
    }
}
