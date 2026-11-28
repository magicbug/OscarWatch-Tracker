using OscarWatch.Core.Terminal.Modem;
using Xunit.Abstractions;

namespace OscarWatch.Tests.Terminal.Modem;

public sealed class AfskFrequencyOffsetTests
{
    private readonly ITestOutputHelper _output;

    public AfskFrequencyOffsetTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Zero_offset_still_decodes_with_full_centre_quality()
    {
        var frame = ModemTestSignals.UiFrame("centre bank");
        var decoded = AfskReceiver.DecodeAll(ModemTestSignals.Transmission([frame], 48000), 48000);

        var hit = Assert.Single(decoded);
        Assert.Equal(frame, hit.Ax25);
        Assert.False(hit.Fixed);
        Assert.True(hit.Quality >= 80, $"quality {hit.Quality}");
    }

    [Theory]
    [InlineData(-400)]
    [InlineData(-350)]
    [InlineData(-175)]
    [InlineData(175)]
    [InlineData(350)]
    [InlineData(400)]
    [InlineData(450)]
    public void Tone_shift_sweep_decodes_with_offset_banks(int offsetHz)
    {
        var frame = ModemTestSignals.UiFrame($"offset {offsetHz} Hz");
        var audio = ModemTestSignals.Transmission([frame], 48000, toneOffsetHz: offsetHz);

        var with = AfskReceiver.DecodeAll(audio, 48000, frequencyOffsetDecoders: true);
        Assert.Contains(with, d => d.Ax25.SequenceEqual(frame));

        if (Math.Abs(offsetHz) <= 400)
        {
            var without = AfskReceiver.DecodeAll(audio, 48000, frequencyOffsetDecoders: false);
            Assert.Contains(without, d => d.Ax25.SequenceEqual(frame));
        }
    }

    [Fact]
    public void Fixed_lo_fails_where_offset_banks_succeed()
    {
        var frame = ModemTestSignals.UiFrame("beyond fixed LO");
        var audio = ModemTestSignals.Transmission([frame], 48000, toneOffsetHz: 500);

        Assert.Empty(AfskReceiver.DecodeAll(audio, 48000, frequencyOffsetDecoders: false));
        Assert.Contains(AfskReceiver.DecodeAll(audio, 48000, frequencyOffsetDecoders: true),
            d => d.Ax25.SequenceEqual(frame));
    }

    [Theory]
    [InlineData(-250, 250)]
    [InlineData(-175, 175)]
    public void Drifting_tone_offset_within_frame_decodes(double startHz, double endHz)
    {
        var frame = ModemTestSignals.UiFrame("drift");
        var audio = ModemTestSignals.TransmissionWithToneDrift(frame, 48000, startHz, endHz);

        Assert.Contains(AfskReceiver.DecodeAll(audio, 48000), d => d.Ax25.SequenceEqual(frame));
    }

    [Fact]
    public void Offset_bank_correlator_stays_coherent_past_sixty_sample_wrapped_table_period()
    {
        const int offsetHz = 350;
        const int sampleRate = AfskDemodulator.SampleRate;
        var tone = new AfskToneDemodulator(1200 + offsetHz, 2200 + offsetHz, [12]);
        var markOnly = new float[sampleRate];
        for (var i = 0; i < markOnly.Length; i++)
            markOnly[i] = (float)Math.Sin(2 * Math.PI * (1200 + offsetHz) * i / sampleRate);

        var samplesPerTable = AfskDemodulator.LoTableLength;
        var before = 0f;
        var after = 0f;
        for (var i = 0; i < markOnly.Length; i++)
        {
            tone.ProcessFiltered(markOnly[i]);
            if (i == samplesPerTable - 2)
                before = tone.MarkAmplitude(0);
            if (i == samplesPerTable + 2)
                after = tone.MarkAmplitude(0);
        }

        Assert.True(after > before * 0.9f, $"mark correlator collapsed across former table wrap ({before} -> {after})");
    }

    [Fact]
    public void Open_squelch_noise_does_not_gain_spurious_frames_from_offset_banks()
    {
        var noise = new float[48000 * 20];
        ModemTestSignals.AddNoise(noise, 0.3, seed: 42);

        var with = AfskReceiver.DecodeAll(noise, 48000, AfskBitFixMode.SingleAndAdjacentPair, frequencyOffsetDecoders: true);
        var without = AfskReceiver.DecodeAll(noise, 48000, AfskBitFixMode.SingleAndAdjacentPair, frequencyOffsetDecoders: false);

        Assert.Empty(without);
        Assert.Empty(with);
    }
}
