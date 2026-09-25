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
        Assert.Contains(decoded, d => d.text.Contains("MM9SQL", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(decoded, d => d.text.Contains("CQ", StringComparison.OrdinalIgnoreCase));
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
}
