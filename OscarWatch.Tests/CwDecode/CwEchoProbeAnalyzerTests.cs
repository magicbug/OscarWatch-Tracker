using OscarWatch.Core.CwDecode;
using OscarWatch.Cw;
using OscarWatch.Sstv;
using Xunit;
using Xunit.Abstractions;

namespace OscarWatch.Tests.CwDecode;

public sealed class CwEchoProbeAnalyzerTests(ITestOutputHelper output)
{
    private const double BinHz =
        (CwReceiverService.WaterfallMaxHz - CwReceiverService.WaterfallMinHz) / CwReceiverService.WaterfallColumns;

    private static CwOnnxModel LoadModel()
    {
        var dir = CwTestAudio.FindModelDirectory();
        Assert.NotNull(dir);
        return CwOnnxModel.Load(dir);
    }

    private static List<float[]> BuildWaterfallRows(ReadOnlySpan<float> samplesAt8k)
    {
        var tap = new SstvSpectrumTap(
            CwReceiverService.WaterfallRate,
            CwReceiverService.SpectrumRowsPerSecond,
            CwReceiverService.WaterfallMinHz,
            CwReceiverService.WaterfallMaxHz,
            CwReceiverService.WaterfallColumns,
            windowSamples: 1024,
            hopSamples: 256);
        var rows = new List<float[]>();
        tap.Add(samplesAt8k, rows);
        return rows;
    }

    private static float[] Mix(params float[][] tracks)
    {
        var length = tracks.Max(t => t.Length);
        var mixed = new float[length];
        foreach (var track in tracks)
        {
            for (var i = 0; i < track.Length; i++)
                mixed[i] += track[i];
        }

        return mixed;
    }

    [Theory]
    [InlineData(700.0)]
    [InlineData(950.0)]
    [InlineData(1450.0)]
    [InlineData(280.0)]
    public void Single_own_call_echo_matches_and_measures_frequency_accurately(double toneHz)
    {
        using var model = LoadModel();
        var audio = CwTestAudio.Morse(
            "MM9SQL",
            wpm: 22,
            toneHz: toneHz,
            sampleRate: CwEchoProbeAnalyzer.SampleRate,
            leadSeconds: 0.6,
            tailSeconds: 1.2,
            noiseRms: 0.02,
            seed: 42);
        var rows = BuildWaterfallRows(audio);

        var result = CwEchoProbeAnalyzer.Analyze(
            audio,
            rows,
            BinHz,
            expectedCallsign: "MM9SQL",
            model.Metadata,
            model,
            preferredBandCentreHz: 800.0);

        output.WriteLine($"Target {toneHz} Hz -> outcome={result.Outcome}, echo={result.EchoHz:F1} Hz, conf={result.MeanConfidence:F2}, text='{result.DecodedPreview}'");

        Assert.Equal(CwEchoProbeOutcome.Matched, result.Outcome);
        Assert.NotNull(result.EchoHz);
        Assert.InRange(Math.Abs(result.EchoHz!.Value - toneHz), 0, 10.0);
        Assert.True(result.MeanConfidence >= CwEchoProbeAnalyzer.MinMeanConfidence);
    }

    [Fact]
    public void Noise_only_returns_no_echo()
    {
        using var model = LoadModel();
        var rng = new Random(17);
        var audio = new float[CwEchoProbeAnalyzer.SampleRate * 4];
        for (var i = 0; i < audio.Length; i++)
            audio[i] = (float)(0.05 * (rng.NextDouble() * 2 - 1));
        var rows = BuildWaterfallRows(audio);

        var result = CwEchoProbeAnalyzer.Analyze(
            audio,
            rows,
            BinHz,
            expectedCallsign: "MM9SQL",
            model.Metadata,
            model,
            preferredBandCentreHz: 800.0);

        Assert.Equal(CwEchoProbeOutcome.NoEcho, result.Outcome);
    }

    [Fact]
    public void Different_callsign_is_rejected_as_no_echo()
    {
        using var model = LoadModel();
        var audio = CwTestAudio.Morse(
            "G4ABC",
            wpm: 22,
            toneHz: 750.0,
            sampleRate: CwEchoProbeAnalyzer.SampleRate,
            leadSeconds: 0.6,
            tailSeconds: 1.2,
            noiseRms: 0.02,
            seed: 3);
        var rows = BuildWaterfallRows(audio);

        var result = CwEchoProbeAnalyzer.Analyze(
            audio,
            rows,
            BinHz,
            expectedCallsign: "MM9SQL",
            model.Metadata,
            model,
            preferredBandCentreHz: 800.0);

        Assert.Equal(CwEchoProbeOutcome.NoEcho, result.Outcome);
    }

    [Fact]
    public void Strong_adjacent_station_does_not_prevent_isolating_own_echo()
    {
        using var model = LoadModel();
        var ownEcho = CwTestAudio.Morse(
            "MM9SQL",
            wpm: 22,
            toneHz: 700.0,
            sampleRate: CwEchoProbeAnalyzer.SampleRate,
            leadSeconds: 0.6,
            tailSeconds: 1.2,
            noiseRms: 0.02,
            seed: 5);
        var qrm = CwTestAudio.Morse(
            "CQ DE G4ABC G4ABC",
            wpm: 24,
            toneHz: 1000.0,
            sampleRate: CwEchoProbeAnalyzer.SampleRate,
            leadSeconds: 0.2,
            tailSeconds: 0.6,
            noiseRms: 0.0,
            seed: 6);
        for (var i = 0; i < qrm.Length; i++)
            qrm[i] *= 1.6f;

        var mixed = Mix(ownEcho, qrm);
        var rows = BuildWaterfallRows(mixed);

        var result = CwEchoProbeAnalyzer.Analyze(
            mixed,
            rows,
            BinHz,
            expectedCallsign: "MM9SQL",
            model.Metadata,
            model,
            preferredBandCentreHz: 800.0);

        output.WriteLine($"With QRM at 1000 Hz -> outcome={result.Outcome}, echo={result.EchoHz:F1} Hz, text='{result.DecodedPreview}'");

        Assert.Equal(CwEchoProbeOutcome.Matched, result.Outcome);
        Assert.NotNull(result.EchoHz);
        Assert.InRange(Math.Abs(result.EchoHz!.Value - 700.0), 0, 10.0);
    }

    [Fact]
    public void Two_distinct_tones_with_own_callsign_are_rejected_as_ambiguous()
    {
        using var model = LoadModel();
        var sidetone = CwTestAudio.Morse(
            "MM9SQL",
            wpm: 22,
            toneHz: 700.0,
            sampleRate: CwEchoProbeAnalyzer.SampleRate,
            leadSeconds: 0.5,
            tailSeconds: 1.4,
            noiseRms: 0.015,
            seed: 9);
        var echo = CwTestAudio.Morse(
            "MM9SQL",
            wpm: 22,
            toneHz: 900.0,
            sampleRate: CwEchoProbeAnalyzer.SampleRate,
            leadSeconds: 0.7,
            tailSeconds: 1.2,
            noiseRms: 0.0,
            seed: 10);

        var mixed = Mix(sidetone, echo);
        var rows = BuildWaterfallRows(mixed);

        var result = CwEchoProbeAnalyzer.Analyze(
            mixed,
            rows,
            BinHz,
            expectedCallsign: "MM9SQL",
            model.Metadata,
            model,
            preferredBandCentreHz: 800.0);

        output.WriteLine($"Dual tone outcome={result.Outcome}, freqs=[{string.Join(", ", result.AmbiguousFrequenciesHz ?? [])}]");

        Assert.Equal(CwEchoProbeOutcome.Ambiguous, result.Outcome);
        Assert.NotNull(result.AmbiguousFrequenciesHz);
        Assert.True(result.AmbiguousFrequenciesHz!.Count >= 2);
    }

    [Fact]
    public void Partial_callsign_is_not_accepted()
    {
        using var model = LoadModel();
        var audio = CwTestAudio.Morse(
            "MM9SQ",
            wpm: 22,
            toneHz: 700.0,
            sampleRate: CwEchoProbeAnalyzer.SampleRate,
            leadSeconds: 0.6,
            tailSeconds: 1.2,
            noiseRms: 0.02,
            seed: 12);
        var rows = BuildWaterfallRows(audio);

        var result = CwEchoProbeAnalyzer.Analyze(
            audio,
            rows,
            BinHz,
            expectedCallsign: "MM9SQL",
            model.Metadata,
            model,
            preferredBandCentreHz: 800.0);

        Assert.Equal(CwEchoProbeOutcome.NoEcho, result.Outcome);
    }
}
