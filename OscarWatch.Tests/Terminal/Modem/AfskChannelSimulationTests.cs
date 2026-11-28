using OscarWatch.Core.Terminal.Modem;
using Xunit.Abstractions;

namespace OscarWatch.Tests.Terminal.Modem;

/// <summary>Modulate, impair, demodulate.</summary>
public sealed class AfskChannelSimulationTests
{
    private readonly ITestOutputHelper _output;

    public AfskChannelSimulationTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void Clean_frame_decodes_with_full_quality(int rate)
    {
        var frame = ModemTestSignals.UiFrame("=5555.55N/00333.33W-OscarWatch");

        var decoded = AfskReceiver.DecodeAll(ModemTestSignals.Transmission([frame], rate), rate);

        var single = Assert.Single(decoded);
        Assert.Equal(frame, single.Ax25);
        Assert.False(single.Fixed);
        Assert.True(single.Quality >= 80, $"quality {single.Quality}");
    }

    [Fact]
    public void Back_to_back_frames_in_one_transmission_all_decode()
    {
        var frames = Enumerable.Range(0, 5).Select(i => ModemTestSignals.UiFrame($"message {i} CQ IO86")).ToList();

        var decoded = AfskReceiver.DecodeAll(ModemTestSignals.Transmission(frames, 48000), 48000);

        Assert.Equal(frames, decoded.Select(d => d.Ax25));
    }

    [Fact]
    public void De_emphasised_speaker_audio_decodes()
    {
        var frame = ModemTestSignals.UiFrame("speaker audio");
        var audio = ModemTestSignals.Transmission([frame], 48000);
        ModemTestSignals.DeEmphasise(audio, 48000);

        Assert.Contains(AfskReceiver.DecodeAll(audio, 48000), d => d.Ax25.SequenceEqual(frame));
    }

    [Fact]
    public void Pre_emphasised_audio_decodes()
    {
        var frame = ModemTestSignals.UiFrame("mic input audio");
        var audio = ModemTestSignals.Transmission([frame], 48000);
        ModemTestSignals.PreEmphasise(audio);

        Assert.Contains(AfskReceiver.DecodeAll(audio, 48000), d => d.Ax25.SequenceEqual(frame));
    }

    [Theory]
    [InlineData(1.005)]
    [InlineData(0.995)]
    public void Half_percent_clock_offset_decodes(double ratio)
    {
        var frames = Enumerable.Range(0, 3).Select(i => ModemTestSignals.UiFrame($"clock offset test {i} with a longer body to drift")).ToList();
        var audio = ModemTestSignals.Transmission(frames, (int)Math.Round(48000 * ratio));

        var decoded = AfskReceiver.DecodeAll(audio, 48000);

        Assert.Equal(frames, decoded.Select(d => d.Ax25));
    }

    [Fact]
    public void Dc_offset_from_residual_doppler_is_ignored()
    {
        var frame = ModemTestSignals.UiFrame("dc offset");
        var audio = ModemTestSignals.Transmission([frame], 48000, amplitude: 0.3);
        for (var i = 0; i < audio.Length; i++)
            audio[i] += 0.4f;

        Assert.Contains(AfskReceiver.DecodeAll(audio, 48000), d => d.Ax25.SequenceEqual(frame));
    }

    [Fact]
    public void Open_squelch_noise_alone_decodes_nothing()
    {
        var noise = new float[48000 * 20];
        ModemTestSignals.AddNoise(noise, 0.3, seed: 1);

        Assert.Empty(AfskReceiver.DecodeAll(noise, 48000, AfskBitFixMode.SingleAndAdjacentPair));
    }

    /// <summary>
    /// Regression gate on 400-bit frames in white noise. Sigma 0.35, 0.45 and 0.5 are about 13.1, 10.9 and
    /// 10.0 dB Eb/N0; when written the decoder managed 100%, 92% and 70% there.
    /// </summary>
    [Theory]
    [InlineData(0.35, 95)]
    [InlineData(0.45, 80)]
    [InlineData(0.50, 55)]
    public void Decode_rate_in_noise_stays_above_threshold(double sigma, int minimumPercent)
    {
        const int frames = 40;
        var decodedCount = 0;
        var fixedCount = 0;
        for (var i = 0; i < frames; i++)
        {
            var frame = ModemTestSignals.UiFrame($"=5555.55N/00333.33W-noise run {i:000} 73");
            var audio = ModemTestSignals.Transmission([frame], 48000, amplitude: 0.5, silenceSeconds: 0.1);
            ModemTestSignals.AddNoise(audio, sigma, seed: 100 + i);
            var decoded = AfskReceiver.DecodeAll(audio, 48000);
            var hit = decoded.FirstOrDefault(d => d.Ax25.SequenceEqual(frame));
            if (hit is not null)
            {
                decodedCount++;
                if (hit.Fixed)
                    fixedCount++;
            }
        }

        var percent = 100 * decodedCount / frames;
        // Signal power 0.125 over 1200 bit/s; noise sigma² spread over 24 kHz.
        var ebN0Db = 10 * Math.Log10(0.125 / 1200 / (sigma * sigma / 24000));
        _output.WriteLine($"sigma {sigma}: Eb/N0 {ebN0Db:0.0} dB, decoded {percent}% ({fixedCount} by bit fix)");
        Assert.True(percent >= minimumPercent, $"decoded {percent}%, needed {minimumPercent}%");
    }

    [Fact]
    public void Blanked_receiver_skips_input_and_recovers_afterwards()
    {
        var first = ModemTestSignals.UiFrame("own transmit");
        var second = ModemTestSignals.UiFrame("digipeat after unkey");
        var receiver = new AfskReceiver(48000);
        var output = new List<AfskDecodedFrame>();

        receiver.Blanked = true;
        receiver.Process(ModemTestSignals.Transmission([first], 48000), output);
        receiver.Blanked = false;
        receiver.Process(ModemTestSignals.Transmission([second], 48000), output);
        receiver.Process(new float[4800], output);
        receiver.Flush(output);

        var single = Assert.Single(output);
        Assert.Equal(second, single.Ax25);
    }

    [Fact]
    public void Dcd_rises_during_a_preamble()
    {
        var audio = ModemTestSignals.Transmission([ModemTestSignals.UiFrame("dcd")], 48000, txDelayMs: 400);
        var receiver = new AfskReceiver(48000);
        var output = new List<AfskDecodedFrame>();
        var dcdSeen = false;
        for (var i = 0; i < audio.Length; i += 480)
        {
            receiver.Process(audio.AsSpan(i, Math.Min(480, audio.Length - i)), output);
            dcdSeen |= receiver.Dcd;
        }

        Assert.True(dcdSeen);
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(6.0)]
    public void Hard_symmetric_clipping_decodes(double drive)
    {
        var frame = ModemTestSignals.UiFrame("=5555.55N/00333.33W-loud clipped pass");
        var audio = ModemTestSignals.Transmission([frame], 48000, amplitude: 0.5);
        for (var i = 0; i < audio.Length; i++)
            audio[i] = Math.Clamp((float)(audio[i] * drive), -1f, 1f);

        Assert.Contains(AfskReceiver.DecodeAll(audio, 48000), d => d.Ax25.SequenceEqual(frame));
    }

    [Fact]
    public void Asymmetric_clipping_with_dc_bias_decodes()
    {
        var frame = ModemTestSignals.UiFrame("=5555.55N/00333.33W-asymmetric clip 2400Hz harmonic");
        var audio = ModemTestSignals.Transmission([frame], 48000, amplitude: 0.5);
        for (var i = 0; i < audio.Length; i++)
            audio[i] = Math.Clamp(audio[i] * 4.0f + 0.55f, -1f, 1f);

        Assert.Contains(AfskReceiver.DecodeAll(audio, 48000), d => d.Ax25.SequenceEqual(frame));
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.70)]
    public void Open_squelch_noise_transitioning_to_quieting_packet_decodes(double packetAmplitude)
    {
        const int rate = 48000;
        var frame = ModemTestSignals.UiFrame("=5555.55N/00333.33W-open squelch burst");
        var noiseLead = new float[rate];
        ModemTestSignals.AddNoise(noiseLead, 0.45, seed: 77);
        for (var i = 0; i < noiseLead.Length; i++)
            noiseLead[i] = Math.Clamp(noiseLead[i], -1f, 1f);

        // Loud open-squelch hiss drops abruptly as a full-quieting satellite packet with speaker
        // de-emphasis and residual +175 Hz offset keys up.
        var packet = ModemTestSignals.Transmission([frame], rate, amplitude: packetAmplitude, txDelayMs: 150, silenceSeconds: 0.0, toneOffsetHz: 175);
        ModemTestSignals.DeEmphasise(packet, rate);
        ModemTestSignals.Normalise(packet, packetAmplitude);

        var combined = new float[noiseLead.Length + packet.Length + rate / 5];
        noiseLead.CopyTo(combined, 0);
        packet.CopyTo(combined, noiseLead.Length);

        Assert.Contains(AfskReceiver.DecodeAll(combined, rate), d => d.Ax25.SequenceEqual(frame));
    }

    [Theory]
    [InlineData(-175)]
    [InlineData(175)]
    [InlineData(-350)]
    [InlineData(350)]
    public void Combined_frequency_offset_and_de_emphasis_decodes(int offsetHz)
    {
        var frame = ModemTestSignals.UiFrame($"offset {offsetHz} plus speaker de-emphasis");
        var audio = ModemTestSignals.Transmission([frame], 48000, toneOffsetHz: offsetHz);
        ModemTestSignals.DeEmphasise(audio, 48000);

        Assert.Contains(AfskReceiver.DecodeAll(audio, 48000), d => d.Ax25.SequenceEqual(frame));
    }
}
