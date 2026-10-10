using OscarWatch.Core.Sstv;

namespace OscarWatch.Tests.Sstv;

public sealed class SstvDspTests
{
    [Theory]
    [InlineData(1100)]
    [InlineData(1200)]
    [InlineData(1500)]
    [InlineData(1900)]
    [InlineData(2300)]
    public void Demodulator_reports_tone_frequency(double hz)
    {
        const int rate = SstvDecoder.InternalRate;
        var demod = new SstvFmDemodulator(rate);
        var output = new List<float>();
        demod.Process(Tone(hz, rate, 0.5), output);

        var settled = output.Skip(rate / 10).Average(v => (double)v);
        Assert.InRange(settled, hz - 2, hz + 2);
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(22050)]
    public void Resampler_keeps_tone_frequency(int inputRate)
    {
        var resampler = new SstvResampler(inputRate, SstvDecoder.InternalRate);
        var resampled = new List<float>();
        resampler.Process(Tone(1700, inputRate, 1.0), resampled);

        Assert.InRange(resampled.Count, SstvDecoder.InternalRate - 2, SstvDecoder.InternalRate + 2);
        var demod = new SstvFmDemodulator(SstvDecoder.InternalRate);
        var output = new List<float>();
        demod.Process(resampled.ToArray(), output);
        var settled = output.Skip(SstvDecoder.InternalRate / 10).Average(v => (double)v);
        Assert.InRange(settled, 1698, 1702);
    }

    [Theory]
    [InlineData(SstvModeId.Robot36)]
    [InlineData(SstvModeId.MartinM1)]
    [InlineData(SstvModeId.ScottieS1)]
    [InlineData(SstvModeId.Pd120)]
    [InlineData(SstvModeId.WraaseSc2180)]
    public void Vis_detector_reads_code(SstvModeId id)
    {
        var mode = SstvModeTable.Get(id);
        var buffer = Demodulate(Header(mode.VisCode, flipParity: false, offsetHz: 0));
        var vis = new SstvVisDetector(SstvDecoder.InternalRate);

        Assert.True(vis.TryFind(buffer, requireFullHeader: false, out var result));
        Assert.Equal(mode.VisCode, result.Code);
        Assert.Same(mode, result.Mode);
    }

    [Fact]
    public void Vis_detector_measures_leader_offset()
    {
        var buffer = Demodulate(Header(SstvModeTable.Pd120.VisCode, flipParity: false, offsetHz: 180));
        var vis = new SstvVisDetector(SstvDecoder.InternalRate);

        Assert.True(vis.TryFind(buffer, requireFullHeader: true, out var result));
        Assert.Equal(SstvModeTable.Pd120.VisCode, result.Code);
        Assert.InRange(result.OffsetHz, 170, 190);
    }

    [Fact]
    public void Vis_detector_rejects_bad_parity()
    {
        var buffer = Demodulate(Header(SstvModeTable.Robot36.VisCode, flipParity: true, offsetHz: 0));
        var vis = new SstvVisDetector(SstvDecoder.InternalRate);

        Assert.False(vis.TryFind(buffer, requireFullHeader: false, out _));
    }

    [Fact]
    public void Vis_detector_ignores_leader_without_header()
    {
        var rate = SstvDecoder.InternalRate;
        var audio = Tone(1900, rate, 1.0).Concat(Tone(1500, rate, 1.0)).ToArray();
        var vis = new SstvVisDetector(rate);

        Assert.False(vis.TryFind(Demodulate(audio), requireFullHeader: false, out _));
    }

    [Fact]
    public void Wav_reader_round_trips_pcm16()
    {
        var samples = Tone(1000, 8000, 0.1);
        using var stream = new MemoryStream();
        WriteWav16(stream, samples, 8000);
        stream.Position = 0;

        var (read, rate) = SstvWavReader.Read(stream);

        Assert.Equal(8000, rate);
        Assert.Equal(samples.Length, read.Length);
        Assert.InRange(read.Zip(samples, (a, b) => Math.Abs(a - b)).Max(), 0, 1e-4);
    }

    private static float[] Header(int code, bool flipParity, double offsetHz)
    {
        var mode = SstvModeTable.FromVis(code)!;
        var all = SstvTestEncoder.Encode(mode, SstvDecoder.InternalRate, new SstvTestEncoder.Settings
        {
            FlipParity = flipParity,
            OffsetHz = offsetHz,
        });
        // Header plus a little picture is all the detector needs.
        return all.Take(SstvDecoder.InternalRate * 2).ToArray();
    }

    private static SstvSignalBuffer Demodulate(float[] audio)
    {
        var demod = new SstvFmDemodulator(SstvDecoder.InternalRate);
        var output = new List<float>();
        demod.Process(audio, output);
        var buffer = new SstvSignalBuffer();
        buffer.Append(output);
        return buffer;
    }

    private static float[] Tone(double hz, int rate, double seconds)
    {
        var n = (int)(rate * seconds);
        var samples = new float[n];
        for (var i = 0; i < n; i++)
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / rate));
        return samples;
    }

    private static void WriteWav16(Stream stream, float[] samples, int rate)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8);
        w.Write(36 + samples.Length * 2);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples.Length * 2);
        foreach (var s in samples)
            w.Write((short)Math.Round(s * 32767));
    }
}
