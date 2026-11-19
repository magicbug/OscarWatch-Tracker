using OscarWatch.Core.Ft4;
using OscarWatch.Ft4;

namespace OscarWatch.Tests.Ft4;

public sealed class Ft8NativeRoundTripTests
{
    private static bool RequireNativeOrReturn()
    {
        // Linux/macOS CI may not ship the native library into test output yet.
        return Ft8Native.IsAvailable;
    }

    [Fact]
    public void Native_library_is_available_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(
            Ft8Native.IsAvailable,
            "win-x64 oscarwatch_ft8.dll must be present under runtimes for Windows tests.");
    }

    [Fact]
    public void Native_library_is_available_on_linux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        Assert.True(
            Ft8Native.IsAvailable,
            "linux-x64 oscarwatch_ft8.so must be present under runtimes for Linux CI tests.");
    }

    [Fact]
    public void Encode_then_decode_standard_cq()
    {
        if (!RequireNativeOrReturn())
            return;

        const string message = "CQ MM9SQL IO85";
        var pcm = Ft8Native.EncodeFt4(message, freqHz: 1200f);
        Assert.NotNull(pcm);
        Assert.True(pcm!.Length > 12000 * 4);

        var decoded = Ft8Native.DecodeFt4(pcm);
        Assert.NotEmpty(decoded);
        Assert.Contains(decoded, d => d.text.Contains("MM9SQL", StringComparison.OrdinalIgnoreCase) && d.ap == 0);
        Assert.Contains(decoded, d => d.text.Contains("CQ", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("MM9SQL", "IO85", "POTA", "CQ POTA MM9SQL IO85")]
    [InlineData("W1AW", "FN20", "DX", "CQ DX W1AW FN20")]
    [InlineData("K1ABC", "FN42", "290", "CQ 290 K1ABC FN42")]
    [InlineData("MM9SQL/M", "IO85", "POTA", "CQ MM9SQL/M")]
    public void Directed_cq_round_trips_as_built(string call, string grid, string modifier, string expected)
    {
        if (!RequireNativeOrReturn())
            return;

        var message = Ft4MessageCodec.BuildCq(call, grid, modifier);
        Assert.Equal(expected, message);
        Assert.True(Ft8Native.TryEncodeFt4(message, 1500f, 12000, out var pcm, out var error), error);

        var decoded = Ft8Native.DecodeFt4(pcm!, 12000, centreHz: 1500);
        Assert.Contains(decoded, d => d.text == expected && d.ap == 0);
    }

    [Theory]
    [InlineData("<R0CM/4> MM9SQL RR73")]
    [InlineData("<R0CM/4> MM9SQL R-12")]
    [InlineData("R0CM/4 MM9SQL IO87")]
    public void Encode_accepts_hashed_compound_call_with_or_without_brackets(string message)
    {
        if (!RequireNativeOrReturn())
            return;

        Assert.True(Ft8Native.TryEncodeFt4(message, 1500f, 12000, out var pcm, out var error), error);

        var decoded = Ft8Native.DecodeFt4(pcm!, 12000, centreHz: 1500);
        Assert.Contains(decoded, d => d.text.Contains("R0CM/4", StringComparison.OrdinalIgnoreCase)
            && d.text.Contains("MM9SQL", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("CQ MM9SQL/M", "CQ MM9SQL/M")]
    [InlineData("CQ 3DA0ABCD", "CQ 3DA0ABCD")]
    [InlineData("G4ABC MM9SQL/M", "<G4ABC> MM9SQL/M")]
    [InlineData("G4ABC MM9SQL/M RR73", "<G4ABC> MM9SQL/M RR73")]
    [InlineData("<G4ABC> PA/DL1ABC 73", "<G4ABC> PA/DL1ABC 73")]
    [InlineData("PA/DL1ABC <G4ABC> RR73", "PA/DL1ABC <G4ABC> RR73")]
    [InlineData("G4ABC <MM9SQL/M> -05", "G4ABC <MM9SQL/M> -05")]
    public void Non_standard_call_goes_out_in_full_unless_bracketed(string message, string expected)
    {
        if (!RequireNativeOrReturn())
            return;

        Assert.True(Ft8Native.TryEncodeFt4(message, 1500f, 12000, out var pcm, out var error), error);

        var decoded = Ft8Native.DecodeFt4(pcm!, 12000, centreHz: 1500);
        Assert.Contains(decoded, d => d.text == expected);
    }

    [Fact]
    public void Full_call_arrives_at_a_station_that_has_never_heard_it()
    {
        if (!RequireNativeOrReturn())
            return;

        Assert.True(Ft8Native.TryEncodeFt4("G4ABC MM9SQL/M RR73", 1500f, 12000, out var pcm, out var error), error);
        Ft8Native.ClearCallsigns();

        var decoded = Ft8Native.DecodeFt4(pcm!, 12000, centreHz: 1500);
        Assert.Contains(decoded, d => d.text == "<...> MM9SQL/M RR73");
    }

    [Fact]
    public void Encode_report_message()
    {
        if (!RequireNativeOrReturn())
            return;

        var pcm = Ft8Native.EncodeFt4("G4ABC MM9SQL +05", freqHz: 1500f);
        Assert.NotNull(pcm);
        var decoded = Ft8Native.DecodeFt4(pcm!);
        Assert.Contains(decoded, d =>
            d.text.Contains("G4ABC", StringComparison.OrdinalIgnoreCase)
            && d.text.Contains("MM9SQL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Encode_portable_callsign_MM9SQL_M()
    {
        if (!RequireNativeOrReturn())
            return;

        // Hashed portable calls are valid FT4; pack77 needs remember_callsign first.
        Assert.True(
            Ft8Native.TryEncodeFt4("CQ MM9SQL/M IO85", 1200f, 12000, out var pcm, out var error),
            error);
        Assert.NotNull(pcm);
        Assert.True(pcm!.Length > 12000 * 4);
    }

    [Fact]
    public void Decode_search_band_around_operating_tone_finds_message()
    {
        if (!RequireNativeOrReturn())
            return;

        const float toneHz = 1500f;
        var pcm = Ft8Native.EncodeFt4("CQ MM9SQL IO85", toneHz);
        Assert.NotNull(pcm);

        var inBand = Ft8Native.DecodeFt4(pcm!, 12000, centreHz: toneHz, halfWidthHz: 700);
        Assert.Contains(inBand, d => d.text.Contains("MM9SQL", StringComparison.OrdinalIgnoreCase));

        // Far from the tone: Costas search must not see 1500 Hz.
        var outOfBand = Ft8Native.DecodeFt4(pcm!, 12000, centreHz: 2800, halfWidthHz: 150);
        Assert.DoesNotContain(outOfBand, d => d.text.Contains("MM9SQL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Waterfall_search_covers_the_whole_display()
    {
        Ft8Native.ResolveWaterfallSearchBand(out var fMin, out var fMax);
        Assert.Equal(200, fMin);
        Assert.Equal(3000, fMax);
    }

    [Fact]
    public void Search_band_spans_rx_and_tx_when_hold_tx_separates_them()
    {
        Ft8Native.ResolveSearchBand(rxHz: 500, txHz: 2000, out var fMin, out var fMax);
        Assert.True(fMin <= 500 - 100);
        Assert.True(fMax >= 2000 + 100);
        Assert.True(fMax - fMin > 1400);
    }

    [Fact]
    public void Availability_probe_does_not_clear_remembered_callsigns()
    {
        if (!RequireNativeOrReturn())
            return;

        Ft8Native.ClearCallsigns();
        Ft8Native.RememberCallsign("MM9SQL/M");
        Assert.True(Ft8Native.IsAvailable);
        Assert.True(
            Ft8Native.TryEncodeFt4("CQ MM9SQL/M IO85", 1200f, 12000, out var pcm, out var error),
            error);
        Assert.NotNull(pcm);
    }

    [Fact]
    public void Late_start_within_extended_window_decodes_without_echo_alignment()
    {
        if (!RequireNativeOrReturn())
            return;

        const int rate = 12000;
        const float toneHz = 1500f;
        var pcm = Ft8Native.EncodeFt4("CQ MM9SQL IO85", toneHz);
        Assert.NotNull(pcm);

        // Encoder already pads ~0.5 s. Push the burst start to ~1.8 s, still inside
        // the extended FT4 candidate window (~2.5 s), so no EchoAligner pass.
        var extraLead = (int)(1.3 * rate);
        var shifted = new float[pcm!.Length + extraLead];
        Array.Copy(pcm, 0, shifted, extraLead, pcm.Length);

        var onsetSec = 0.5 + 1.3;
        Assert.True(onsetSec < Ft4EchoAligner.NativeWindowSeconds);

        var decoded = Ft8Native.DecodeFt4(shifted, rate, centreHz: toneHz);
        Assert.Contains(decoded, d => d.text.Contains("MM9SQL", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Ft4EchoAligner.EnumerateEchoAlignments(shifted, rate, toneHz));
    }

    [Fact]
    public async Task Parallel_encode_and_decode_do_not_crash()
    {
        if (!RequireNativeOrReturn())
            return;

        var encodeA = Task.Run(() => Ft8Native.EncodeFt4("CQ MM9SQL IO85", 1200f));
        var encodeB = Task.Run(() => Ft8Native.EncodeFt4("G4ABC MM9SQL +05", 1500f));
        var pcmA = await encodeA;
        var pcmB = await encodeB;

        Assert.NotNull(pcmA);
        Assert.NotNull(pcmB);

        var decodeA = Task.Run(() => Ft8Native.DecodeFt4(pcmA!, 12000, 1200));
        var decodeB = Task.Run(() => Ft8Native.DecodeFt4(pcmB!, 12000, 1500));
        var textA = await decodeA;
        var textB = await decodeB;

        Assert.Contains(textA, d => d.text.Contains("MM9SQL", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(textB, d => d.text.Contains("G4ABC", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(-12)]
    [InlineData(0)]
    [InlineData(10)]
    public void Decoded_snr_follows_2500_hz_reference(int snrDb)
    {
        if (!RequireNativeOrReturn())
            return;

        const int rate = 12000;
        var pcm = Ft8Native.EncodeFt4("CQ MM9SQL IO85", freqHz: 1500f);
        Assert.NotNull(pcm);

        var noisy = AddWhiteNoise(pcm!, rate, snrDb, seed: 7);
        var decoded = Ft8Native.DecodeFt4(noisy, rate, 200f, 2800f);
        var hit = Assert.Single(decoded, d => d.text.Contains("MM9SQL", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(hit.snr, snrDb - 3f, snrDb + 3f);
    }

    private static float[] AddWhiteNoise(float[] pcm, int rate, int snrDb, int seed)
    {
        double power = 0;
        var active = 0;
        foreach (var sample in pcm)
        {
            if (Math.Abs(sample) <= 1e-4f)
                continue;
            power += sample * (double)sample;
            active++;
        }

        power /= active;
        var snrLin = Math.Pow(10.0, snrDb / 10.0);
        var sigma = Math.Sqrt(power * rate / (snrLin * 5000.0));
        var rng = new Random(seed);
        var mixed = new float[pcm.Length];
        for (var i = 0; i < pcm.Length; i++)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = 1.0 - rng.NextDouble();
            var gauss = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            mixed[i] = pcm[i] + (float)(sigma * gauss);
        }

        return mixed;
    }

    [Fact]
    public void Ap_hints_recover_a_weak_message_plain_decode_misses()
    {
        if (!RequireNativeOrReturn())
            return;

        const int rate = 12000;
        const string message = "MM9SQL G4ABC RR73";
        var pcm = Ft8Native.EncodeFt4(message, freqHz: 1500f);
        Assert.NotNull(pcm);

        var hints = ApHintsFor("MM9SQL", "G4ABC");
        var recovered = 0;
        var plainHits = 0;
        for (var seed = 1; seed <= 6; seed++)
        {
            var noisy = AddWhiteNoise(pcm!, rate, snrDb: -18, seed);
            var plain = Ft8Native.DecodeFt4(noisy, rate, 200f, 2800f);
            if (plain.Any(d => d.text.Contains("RR73", StringComparison.Ordinal)))
                plainHits++;

            var hinted = Ft8Native.DecodeFt4(noisy, rate, 200f, 2800f, deep: false, hints, apCentreHz: 1500f);
            if (hinted.Any(d => d.text.Contains("RR73", StringComparison.Ordinal) && d.ap != 0))
                recovered++;
        }

        Assert.True(recovered >= 4, $"a priori recovered {recovered}/6 at -18 dB (plain {plainHits}/6)");
        Assert.True(plainHits <= 2, $"plain decode was unexpectedly reliable at -18 dB ({plainHits}/6)");
    }

    [Fact]
    public void Ap_hints_near_the_plain_threshold_are_crc_checked()
    {
        if (!RequireNativeOrReturn())
            return;

        const int rate = 12000;
        const string message = "MM9SQL G4ABC RR73";
        var pcm = Ft8Native.EncodeFt4(message, freqHz: 1500f);
        Assert.NotNull(pcm);

        var hints = ApHintsFor("MM9SQL", "G4ABC");
        var checkedHits = 0;
        for (var seed = 1; seed <= 6; seed++)
        {
            var noisy = AddWhiteNoise(pcm!, rate, snrDb: -16, seed);
            var hinted = Ft8Native.DecodeFt4(noisy, rate, 200f, 2800f, deep: false, hints, apCentreHz: 1500f);
            if (hinted.Any(d => d.text == message && d.ap == Ft8Native.ApCrcChecked))
                checkedHits++;
            Assert.DoesNotContain(hinted, d => d.ap != 0 && d.text != message);
        }

        Assert.True(checkedHits >= 2, $"CRC-checked hints {checkedHits}/6 at -16 dB");
    }

    [Fact]
    public void Ap_hints_do_not_invent_a_message_from_noise()
    {
        if (!RequireNativeOrReturn())
            return;

        var hints = ApHintsFor("MM9SQL", "G4ABC");
        for (var seed = 1; seed <= 4; seed++)
        {
            var noise = UnitNoise(12000 * 15 / 2, seed);
            var decoded = Ft8Native.DecodeFt4(noise, 12000, 200f, 2800f, deep: false, hints, apCentreHz: 1500f);
            Assert.Empty(decoded);
        }
    }

    [Fact]
    public void Ap_hints_do_not_invent_a_second_message_on_a_decoded_burst()
    {
        if (!RequireNativeOrReturn())
            return;

        const int rate = 12000;
        var pcm = Ft8Native.EncodeFt4("R5AO R8CEL -11", freqHz: 1500f);
        Assert.NotNull(pcm);
        var hints = ApHintsFor("GW4VXE", "R8CEL");

        for (var seed = 1; seed <= 4; seed++)
        {
            var noisy = AddWhiteNoise(pcm!, rate, snrDb: -8, seed);
            var decoded = Ft8Native.DecodeFt4(noisy, rate, 200f, 2800f, deep: false, hints, apCentreHz: 1500f);
            Assert.Contains(decoded, d => d.text.Contains("R5AO", StringComparison.Ordinal));
            Assert.DoesNotContain(decoded, d => d.text.Contains("GW4VXE", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Weak_station_under_a_decoded_burst_still_appears_when_hints_are_on()
    {
        if (!RequireNativeOrReturn())
            return;

        var strong = Ft8Native.EncodeFt4("R5AO R8CEL -11", freqHz: 1500f);
        var weak = Ft8Native.EncodeFt4("CQ G4ABC IO91", freqHz: 1500f);
        Assert.NotNull(strong);
        Assert.NotNull(weak);

        var length = Math.Min(strong!.Length, weak!.Length);
        var mixed = new float[length];
        for (var i = 0; i < length; i++)
            mixed[i] = strong[i] + 0.5f * weak[i];

        var noisy = AddWhiteNoise(mixed, 12000, snrDb: 0, seed: 11);
        var hints = ApHintsFor("GW4VXE", "R8CEL");
        var decoded = Ft8Native.DecodeFt4(noisy, 12000, 200f, 2800f, deep: false, hints, apCentreHz: 1500f);
        Assert.Contains(decoded, d => d.text.Contains("R5AO", StringComparison.Ordinal));
        Assert.Contains(decoded, d => d.text.Contains("G4ABC", StringComparison.Ordinal));
        Assert.DoesNotContain(decoded, d => d.text.Contains("GW4VXE", StringComparison.Ordinal));
    }

    [Fact]
    public void Ap_hints_do_not_replace_a_different_station()
    {
        if (!RequireNativeOrReturn())
            return;

        const int rate = 12000;
        var pcm = Ft8Native.EncodeFt4("CQ M0XYZ IO91", freqHz: 1500f);
        Assert.NotNull(pcm);
        var noisy = AddWhiteNoise(pcm!, rate, snrDb: -8, seed: 3);
        var hints = ApHintsFor("MM9SQL", "G4ABC");
        var decoded = Ft8Native.DecodeFt4(noisy, rate, 200f, 2800f, deep: false, hints, apCentreHz: 1500f);
        Assert.Contains(decoded, d => d.text.Contains("M0XYZ", StringComparison.Ordinal));
        Assert.DoesNotContain(decoded, d => d.text.Contains("G4ABC", StringComparison.Ordinal));
    }

    [Fact]
    public void Ap_hints_ignore_a_signal_away_from_the_contact()
    {
        if (!RequireNativeOrReturn())
            return;

        const int rate = 12000;
        var pcm = Ft8Native.EncodeFt4("MM9SQL G4ABC RR73", freqHz: 1500f);
        Assert.NotNull(pcm);
        var noisy = AddWhiteNoise(pcm!, rate, snrDb: -18, seed: 2);
        var hints = ApHintsFor("MM9SQL", "G4ABC");
        var plain = Ft8Native.DecodeFt4(noisy, rate, 200f, 2800f);
        var hinted = Ft8Native.DecodeFt4(noisy, rate, 200f, 2800f, deep: false, hints, apCentreHz: 2400f);
        var plainRr73 = plain.Any(d => d.text.Contains("RR73", StringComparison.Ordinal));
        var hintedRr73 = hinted.Any(d => d.text.Contains("RR73", StringComparison.Ordinal));
        Assert.Equal(plainRr73, hintedRr73);
    }

    [Fact]
    public void Strong_signal_is_removed_so_a_weaker_one_underneath_decodes()
    {
        if (!RequireNativeOrReturn())
            return;

        var strong = Ft8Native.EncodeFt4("CQ MM9SQL IO85", freqHz: 1500f);
        var weak = Ft8Native.EncodeFt4("CQ G4ABC IO91", freqHz: 1500f);
        Assert.NotNull(strong);
        Assert.NotNull(weak);

        var length = Math.Min(strong!.Length, weak!.Length);
        var mixed = new float[length];
        for (var i = 0; i < length; i++)
            mixed[i] = strong[i] + 0.12f * weak[i];

        var decoded = Ft8Native.DecodeFt4(mixed, 12000, 200f, 2800f);
        Assert.Contains(decoded, d => d.text.Contains("MM9SQL", StringComparison.Ordinal));
        Assert.Contains(decoded, d => d.text.Contains("G4ABC", StringComparison.Ordinal));
    }

    [Fact]
    public void Noisy_strong_signal_is_removed_so_a_weaker_one_underneath_decodes()
    {
        if (!RequireNativeOrReturn())
            return;

        var strong = Ft8Native.EncodeFt4("CQ MM9SQL IO85", freqHz: 1500f);
        var weak = Ft8Native.EncodeFt4("CQ G4ABC IO91", freqHz: 1500f);
        Assert.NotNull(strong);
        Assert.NotNull(weak);

        var length = Math.Min(strong!.Length, weak!.Length);
        var mixed = new float[length];
        for (var i = 0; i < length; i++)
            mixed[i] = strong[i] + 0.5f * weak[i];

        var noisy = AddWhiteNoise(mixed, 12000, snrDb: 0, seed: 11);
        var decoded = Ft8Native.DecodeFt4(noisy, 12000, 200f, 2800f);
        Assert.Contains(decoded, d => d.text.Contains("MM9SQL", StringComparison.Ordinal));
        Assert.Contains(decoded, d => d.text.Contains("G4ABC", StringComparison.Ordinal));
    }

    private static string ApHintsFor(string myCall, string theirCall)
    {
        var lines = new List<string>();
        for (var snr = -30; snr <= 40; snr++)
        {
            var report = Ft4MessageCodec.FormatSnrReport(snr);
            lines.Add(Ft4MessageCodec.BuildReport(myCall, theirCall, report));
            lines.Add(Ft4MessageCodec.BuildReport(myCall, theirCall, Ft4MessageCodec.FormatRogerReport(snr)));
        }

        lines.Add(Ft4MessageCodec.BuildRr73(myCall, theirCall));
        lines.Add(Ft4MessageCodec.Build73(myCall, theirCall));
        return string.Join('\n', lines);
    }

    private static float[] UnitNoise(int sampleCount, int seed)
    {
        var rng = new Random(seed);
        var noise = new float[sampleCount];
        for (var i = 0; i < noise.Length; i++)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = 1.0 - rng.NextDouble();
            noise[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        return noise;
    }
}
