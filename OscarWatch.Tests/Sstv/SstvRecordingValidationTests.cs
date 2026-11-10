using System.Text.Json;
using System.Text.Json.Serialization;
using OscarWatch.Core.Sstv;
using Xunit.Abstractions;

namespace OscarWatch.Tests.Sstv;

/// <summary>
/// Decodes real recordings when OSCARWATCH_SSTV_FIXTURES names a folder of WAV or MP3 files.
/// Audio is loaded the way the app loads it (<see cref="SstvRecordingLoader"/>), so MP3 goes
/// through ffmpeg at the decoder rate. Pictures go to OSCARWATCH_SSTV_OUTPUT (or a temp
/// folder) for checking by eye.
///
/// Per-picture metrics are compared with Fixtures/sstv_recordings_baseline.json. A change
/// fails if a baseline picture disappears, goes from complete to partial, or loses lock.
/// Set OSCARWATCH_SSTV_WRITE_BASELINE to a file path to write this run's metrics there
/// (use it to record a new baseline). Recordings are not committed, so this does nothing by default.
/// Set OSCARWATCH_SSTV_BASELINE to a baseline file to check a different set of recordings.
///
/// Set OSCARWATCH_SSTV_REFERENCES to a folder of the original pictures the station sent (for
/// example the ARISS images of the event) to score every decode against its original (PSNR
/// and SSIM). Set OSCARWATCH_SSTV_MMSSTV to a folder of MMSSTV decodes of the same recordings
/// to score those too, so the two decoders can be compared picture by picture.
/// </summary>
public sealed class SstvRecordingValidationTests(ITestOutputHelper output)
{
    /// <summary>Percentage points of lock a baseline picture may lose before the test fails.</summary>
    private const double LockDropAllowed = 1.0;

    /// <summary>How far apart in time a decoded picture may start and still be the same picture.</summary>
    private const double MatchWindowSeconds = 30;

    private const string BaselineFileName = "sstv_recordings_baseline.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    [Fact]
    public async Task Decode_recordings_from_fixture_folder()
    {
        var folder = Environment.GetEnvironmentVariable("OSCARWATCH_SSTV_FIXTURES");
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        var outDir = Environment.GetEnvironmentVariable("OSCARWATCH_SSTV_OUTPUT");
        if (string.IsNullOrWhiteSpace(outDir))
            outDir = Path.Combine(Path.GetTempPath(), "oscarwatch-sstv-out");
        Directory.CreateDirectory(outDir);

        var loader = new SstvRecordingLoader();
        var run = new SstvMetricsFile
        {
            Loader = "SstvRecordingLoader (ffmpeg to the decoder rate for MP3)",
            Recordings = [],
        };

        var files = Directory.GetFiles(folder)
            .Where(f => Path.GetExtension(f) is ".wav" or ".mp3")
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

        var originals = LoadPictures(Environment.GetEnvironmentVariable("OSCARWATCH_SSTV_REFERENCES"));
        var decodedPictures = new List<(string Label, SstvImage Image)>();
        var combiner = new SstvPictureCombiner();

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var load = await loader.LoadAsync(file);
            Assert.True(load.Samples is not null, $"{name}: could not load ({load.Error})");

            var options = OptionsFromEnvironment();
            options.ExpectedModes = ExpectedModesFromEnvironment(name);
            var decoder = new SstvDecoder(load.SampleRate, options);
            var images = new List<SstvDecodedImage>();
            var abandoned = 0;
            decoder.ImageCompleted += (_, image) => images.Add(image);
            decoder.ImageAbandoned += (_, _) => abandoned++;
            const int chunk = 4096;
            var samples = load.Samples!;
            for (var i = 0; i < samples.Length; i += chunk)
                decoder.Process(samples.AsSpan(i, Math.Min(chunk, samples.Length - i)));
            decoder.Flush();

            images.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
            output.WriteLine($"{name}: {images.Count} pictures, {abandoned} false starts");
            for (var n = 0; n < images.Count; n++)
            {
                var img = images[n];
                output.WriteLine(
                    $"  {n + 1}: {img.Mode.Name} {(img.FromVis ? "VIS" : "sync")} "
                    + $"{TimeSpan.FromSeconds(img.StartSeconds):mm\\:ss}-{TimeSpan.FromSeconds(img.EndSeconds):mm\\:ss} "
                    + $"lock {img.LockPercent:0}% offset {img.OffsetHz:0} Hz clock {img.ClockPpm:0} ppm "
                    + (img.Complete ? "complete" : "partial"));
                var stats = SstvLineStats.From(img.LineQuality, img.LineFailures);
                output.WriteLine(
                    $"     unlocked: {stats.NoPulse} no pulse, {stats.WeakSync} weak sync, {stats.OffGrid} off grid; "
                    + $"{stats.UnlockedRuns} runs, longest {stats.LongestUnlockedRun} lines"
                    + (img.RepairedLines.Count > 0 ? $"; repaired {img.RepairedLines.Count} lines" : ""));
                SstvPngWriter.Save(img.Image, Path.Combine(outDir, $"{name}-{n + 1:00}-{img.Mode.Id}.png"));
                decodedPictures.Add(($"{name}-{n + 1:00}", img.Image));
                if (combiner.Add(img) is { } combined)
                {
                    output.WriteLine($"     merged with {combined.Copies - 1} earlier copies");
                    SstvPngWriter.Save(combined.Image, Path.Combine(outDir, $"{name}-{n + 1:00}-{img.Mode.Id}_combined.png"));
                }
            }

            var metrics = images.Select(ToMetrics).ToList();
            for (var n = 0; n < images.Count && originals.Count > 0; n++)
            {
                var (reference, match) = BestOriginal(images[n].Image, originals);
                metrics[n].Reference = reference;
                metrics[n].ReferencePsnr = Math.Round(match.Psnr, 2);
                metrics[n].ReferenceSsim = Math.Round(match.Ssim, 4);
                output.WriteLine($"    {n + 1}: matches {reference}: PSNR {match.Psnr:0.0} dB, SSIM {match.Ssim:0.000}");
            }

            run.Recordings.Add(new SstvMetricsRecording
            {
                File = Path.GetFileName(file),
                Pictures = metrics,
            });
        }

        if (originals.Count > 0)
        {
            var mmsstv = LoadPictures(Environment.GetEnvironmentVariable("OSCARWATCH_SSTV_MMSSTV"));
            run.Comparison = CompareDecoders(originals, decodedPictures, mmsstv);
            foreach (var row in run.Comparison)
            {
                output.WriteLine(
                    $"{row.Reference}: OscarWatch {Score(row.OscarWatchPsnr, row.OscarWatchSsim)} ({row.OscarWatchPicture ?? "none"}), "
                    + $"MMSSTV {Score(row.MmsstvPsnr, row.MmsstvSsim)} ({row.MmsstvPicture ?? "none"})");
            }

            if (mmsstv.Count > 0)
            {
                var both = run.Comparison.Where(r => r.OscarWatchSsim is not null && r.MmsstvSsim is not null).ToList();
                var better = both.Count(r => r.OscarWatchSsim > r.MmsstvSsim);
                output.WriteLine($"OscarWatch closer to the original on {better} of {both.Count} pictures both decoders received");
            }
        }

        var summaryPath = Path.Combine(outDir, "sstv-recordings-metrics.json");
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(run, JsonOptions));
        output.WriteLine($"Metrics written to {summaryPath}");

        var writePath = Environment.GetEnvironmentVariable("OSCARWATCH_SSTV_WRITE_BASELINE");
        if (!string.IsNullOrWhiteSpace(writePath))
        {
            File.WriteAllText(writePath, JsonSerializer.Serialize(run, JsonOptions));
            output.WriteLine($"Baseline written to {writePath}");
        }

        // A different recording set (for example saved session audio) needs its own baseline.
        var baselinePath = Environment.GetEnvironmentVariable("OSCARWATCH_SSTV_BASELINE") is { Length: > 0 } custom
            ? custom
            : Path.Combine(AppContext.BaseDirectory, "Fixtures", BaselineFileName);
        if (!File.Exists(baselinePath))
        {
            output.WriteLine($"No baseline at {baselinePath}; regression check skipped.");
            return;
        }

        var baseline = JsonSerializer.Deserialize<SstvMetricsFile>(File.ReadAllText(baselinePath), JsonOptions)
            ?? throw new InvalidDataException($"Could not read {baselinePath}.");
        var failures = Compare(baseline, run);
        Assert.True(failures.Count == 0, "Regression against baseline:\n" + string.Join('\n', failures));
    }

    [Fact]
    public void Regression_check_flags_lost_pictures_and_weaker_pictures()
    {
        var baseline = Metrics(
            Picture(SstvModeId.Pd120, startSeconds: 100, complete: true, lockPercent: 99),
            Picture(SstvModeId.Pd120, startSeconds: 400, complete: true, lockPercent: 96));

        var current = Metrics(
            Picture(SstvModeId.Pd120, startSeconds: 102, complete: true, lockPercent: 99.5),
            Picture(SstvModeId.Pd120, startSeconds: 401, complete: false, lockPercent: 90));

        var failures = Compare(baseline, current);

        Assert.Single(failures, f => f.Contains("was complete, now partial", StringComparison.Ordinal));
        Assert.Single(failures, f => f.Contains("lock fell", StringComparison.Ordinal));
        Assert.Empty(Compare(baseline, Metrics(
            Picture(SstvModeId.Pd120, startSeconds: 100, complete: true, lockPercent: 99),
            Picture(SstvModeId.Pd120, startSeconds: 400, complete: true, lockPercent: 96),
            Picture(SstvModeId.Pd120, startSeconds: 800, complete: true, lockPercent: 95))));

        Assert.Single(Compare(baseline, Metrics(
            Picture(SstvModeId.Pd120, startSeconds: 100, complete: true, lockPercent: 99))));
    }

    [Fact]
    public void Reference_comparison_finds_the_original_and_scores_the_better_decode_higher()
    {
        var mode = SstvModeTable.MartinM1;
        var original = new SstvImage(mode.Width, mode.Height);
        var other = new SstvImage(mode.Width, mode.Height);
        for (var y = 0; y < mode.Height; y++)
        {
            for (var x = 0; x < mode.Width; x++)
            {
                var (r, g, b) = SstvTestEncoder.TestPattern(x, y, mode.Width, mode.Height);
                original.SetPixel(x, y, r, g, b);
                other.SetPixel(x, y, 255 - r, x % 32 * 8, y % 16 * 16);
            }
        }

        var clean = Assert.Single(SstvRoundTripTests.Decode(SstvTestEncoder.Encode(mode, 48000), new SstvDecoderOptions())).Image;
        var noisy = Assert.Single(SstvRoundTripTests.Decode(
            SstvTestEncoder.Encode(mode, 48000, new SstvTestEncoder.Settings { NoiseRms = 0.35 }),
            new SstvDecoderOptions())).Image;
        var originals = new List<(string Name, SstvImage Image)> { ("other", other), ("pattern", original) };

        var (name, cleanMatch) = BestOriginal(clean, originals);
        var (_, noisyMatch) = BestOriginal(noisy, originals);

        Assert.Equal("pattern", name);
        Assert.True(cleanMatch.Psnr > noisyMatch.Psnr, $"clean {cleanMatch.Psnr:0.0} dB, noisy {noisyMatch.Psnr:0.0} dB");
        Assert.True(cleanMatch.Ssim > noisyMatch.Ssim);
        Assert.True(cleanMatch.Ssim > 0.9, $"SSIM {cleanMatch.Ssim:0.000}");
    }

    [Fact]
    public void Reference_comparison_lines_up_a_shifted_and_scaled_copy()
    {
        var mode = SstvModeTable.Pd120;
        var big = new SstvImage(mode.Width, mode.Height);
        for (var y = 0; y < mode.Height; y++)
        {
            for (var x = 0; x < mode.Width; x++)
            {
                var (r, g, b) = SstvTestEncoder.TestPattern(x + 3, y - 2, mode.Width, mode.Height);
                big.SetPixel(x, y, r, g, b);
            }
        }

        var decoded = new SstvImage(mode.Width, mode.Height);
        for (var y = 0; y < mode.Height; y++)
        {
            for (var x = 0; x < mode.Width; x++)
            {
                var (r, g, b) = SstvTestEncoder.TestPattern(x, y, mode.Width, mode.Height);
                decoded.SetPixel(x, y, r, g, b);
            }
        }

        var match = SstvImageCompare.Compare(decoded, SstvImageCompare.Resize(big, 320, 248));

        Assert.InRange(match.DxPixels, -4, -2);
        Assert.InRange(match.DyPixels, 1, 3);
        Assert.True(match.Ssim > 0.9, $"SSIM {match.Ssim:0.000}");
    }

    /// <summary>
    /// Decoder options with the switches named in OSCARWATCH_SSTV_OPTIONS (comma separated,
    /// for example "ImpulseSuppression,HoldThroughFades") turned on. A name starting with "!"
    /// turns that option off, for example "!ToneSync".
    /// </summary>
    private static SstvDecoderOptions OptionsFromEnvironment()
    {
        var options = new SstvDecoderOptions();
        var names = Environment.GetEnvironmentVariable("OSCARWATCH_SSTV_OPTIONS");
        if (string.IsNullOrWhiteSpace(names))
            return options;
        foreach (var name in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var on = !name.StartsWith('!');
            var key = name.TrimStart('!');
            var property = typeof(SstvDecoderOptions).GetProperty(key)
                ?? throw new ArgumentException($"OSCARWATCH_SSTV_OPTIONS: no option called {key}");
            property.SetValue(options, on);
        }

        return options;
    }

    /// <summary>
    /// Expected modes for a recording from OSCARWATCH_SSTV_EXPECTED, matched on part of the
    /// file name, for example "ISS:Pd120,Pd180,Robot36;UmKA:Robot36". Null when nothing matches.
    /// </summary>
    private static SstvModeId[]? ExpectedModesFromEnvironment(string fileName)
    {
        var map = Environment.GetEnvironmentVariable("OSCARWATCH_SSTV_EXPECTED");
        if (string.IsNullOrWhiteSpace(map))
            return null;
        foreach (var entry in map.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && fileName.Contains(parts[0], StringComparison.OrdinalIgnoreCase))
                return parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Enum.Parse<SstvModeId>)
                    .ToArray();
        }

        return null;
    }

    /// <summary>Original whose thumbnail looks most like the decode, scored in full.</summary>
    internal static (string Name, SstvImageMatch Match) BestOriginal(SstvImage decoded, IReadOnlyList<(string Name, SstvImage Image)> originals)
    {
        var best = originals.MaxBy(o => SstvImageCompare.Likeness(decoded, o.Image));
        return (best.Name, SstvImageCompare.Compare(decoded, best.Image));
    }

    /// <summary>For every original, the closest picture from each decoder.</summary>
    private static List<SstvComparisonRow> CompareDecoders(
        IReadOnlyList<(string Name, SstvImage Image)> originals,
        IReadOnlyList<(string Label, SstvImage Image)> oscarWatch,
        IReadOnlyList<(string Name, SstvImage Image)> mmsstv)
    {
        var rows = originals.Select(o => new SstvComparisonRow { Reference = o.Name }).ToList();

        void Assign(IReadOnlyList<(string Name, SstvImage Image)> decodes, bool isOscarWatch)
        {
            foreach (var decode in decodes)
            {
                var (reference, match) = BestOriginal(decode.Image, originals);
                var row = rows.First(r => r.Reference == reference);
                var current = isOscarWatch ? row.OscarWatchSsim : row.MmsstvSsim;
                if (current is not null && current >= match.Ssim)
                    continue;
                if (isOscarWatch)
                {
                    row.OscarWatchPicture = decode.Name;
                    row.OscarWatchPsnr = Math.Round(match.Psnr, 2);
                    row.OscarWatchSsim = Math.Round(match.Ssim, 4);
                }
                else
                {
                    row.MmsstvPicture = decode.Name;
                    row.MmsstvPsnr = Math.Round(match.Psnr, 2);
                    row.MmsstvSsim = Math.Round(match.Ssim, 4);
                }
            }
        }

        Assign(oscarWatch.Select(p => (p.Label, p.Image)).ToList(), isOscarWatch: true);
        Assign(mmsstv, isOscarWatch: false);
        return rows.Where(r => r.OscarWatchPicture is not null || r.MmsstvPicture is not null).ToList();
    }

    private static string Score(double? psnr, double? ssim) =>
        psnr is null || ssim is null ? "not received" : $"PSNR {psnr:0.0} dB, SSIM {ssim:0.000}";

    /// <summary>PNG, JPEG and BMP pictures in <paramref name="folder"/>, empty when it is not set.</summary>
    private static List<(string Name, SstvImage Image)> LoadPictures(string? folder)
    {
        var pictures = new List<(string, SstvImage)>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return pictures;

        foreach (var file in Directory.GetFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetExtension(file).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
                continue;
            using var bitmap = SkiaSharp.SKBitmap.Decode(file);
            if (bitmap is null)
                continue;
            var image = new SstvImage(bitmap.Width, bitmap.Height);
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    image.SetPixel(x, y, c.Red, c.Green, c.Blue);
                }
            }

            pictures.Add((Path.GetFileNameWithoutExtension(file), image));
        }

        return pictures;
    }

    private static SstvMetricsFile Metrics(params SstvMetricsPicture[] pictures) => new()
    {
        Recordings = [new SstvMetricsRecording { File = "pass.mp3", Pictures = pictures.ToList() }],
    };

    private static SstvMetricsPicture Picture(SstvModeId mode, double startSeconds, bool complete, double lockPercent) => new()
    {
        Mode = mode.ToString(),
        Complete = complete,
        LockPercent = lockPercent,
        StartSeconds = startSeconds,
        EndSeconds = startSeconds + 120,
    };

    /// <summary>Every baseline picture must still be found, not weaker than before.</summary>
    internal static List<string> Compare(SstvMetricsFile baseline, SstvMetricsFile current)
    {
        var failures = new List<string>();
        foreach (var recording in baseline.Recordings)
        {
            var match = current.Recordings.FirstOrDefault(r => r.File == recording.File);
            if (match is null)
            {
                failures.Add($"{recording.File}: recording missing from this run");
                continue;
            }

            foreach (var old in recording.Pictures)
            {
                var found = match.Pictures
                    .Where(p => p.Mode == old.Mode && Math.Abs(p.StartSeconds - old.StartSeconds) <= MatchWindowSeconds)
                    .OrderBy(p => Math.Abs(p.StartSeconds - old.StartSeconds))
                    .FirstOrDefault();
                var label = $"{recording.File} {old.Mode} at {old.StartSeconds:0}s";
                if (found is null)
                {
                    failures.Add($"{label}: picture lost");
                    continue;
                }

                if (old.Complete && !found.Complete)
                    failures.Add($"{label}: was complete, now partial");
                if (found.LockPercent < old.LockPercent - LockDropAllowed)
                    failures.Add($"{label}: lock fell from {old.LockPercent:0.0}% to {found.LockPercent:0.0}%");
            }
        }

        return failures;
    }

    [Fact]
    public void Line_failures_say_why_lines_did_not_lock_and_how_they_cluster()
    {
        var mode = SstvModeTable.Pd120;
        var audio = SstvTestEncoder.Encode(mode, 48000, new SstvTestEncoder.Settings
        {
            NoiseRms = 0.05,
            BurstEveryMs = 15000,
            BurstMs = 3000,
            BurstNoiseRms = 3.0,
        });

        var image = Assert.Single(SstvRoundTripTests.Decode(audio, new SstvDecoderOptions()));
        var stats = SstvLineStats.From(image.LineQuality, image.LineFailures);

        Assert.Equal(image.LineQuality.Count, image.LineFailures.Count);
        Assert.True(stats.NoPulse + stats.WeakSync > 0, "bursts should cost some syncs");
        Assert.Equal(stats.Lines - stats.Locked, stats.NoPulse + stats.WeakSync);
        Assert.True(stats.LongestUnlockedRun >= 2, $"longest run {stats.LongestUnlockedRun}");
        Assert.True(stats.UnlockedRuns < stats.Lines - stats.Locked, "failures should cluster in the bursts");
        for (var i = 0; i < image.LineQuality.Count; i++)
        {
            if (image.LineQuality[i] == SstvLineQuality.Locked)
                Assert.True(image.LineFailures[i] is SstvLineFailure.None or SstvLineFailure.OffGrid);
        }
    }

    private static SstvMetricsPicture ToMetrics(SstvDecodedImage image) => ToMetrics(image, SstvLineStats.From(image.LineQuality, image.LineFailures));

    private static SstvMetricsPicture ToMetrics(SstvDecodedImage image, SstvLineStats stats) => new()
    {
        NoPulseLines = stats.NoPulse,
        WeakSyncLines = stats.WeakSync,
        OffGridLines = stats.OffGrid,
        UnlockedRuns = stats.UnlockedRuns,
        LongestUnlockedRun = stats.LongestUnlockedRun,
        Mode = image.Mode.Id.ToString(),
        FromVis = image.FromVis,
        Complete = image.Complete,
        LockPercent = Math.Round(image.LockPercent, 2),
        OffsetHz = Math.Round(image.OffsetHz, 2),
        ClockPpm = Math.Round(image.ClockPpm, 2),
        StartSeconds = Math.Round(image.StartSeconds, 2),
        EndSeconds = Math.Round(image.EndSeconds, 2),
    };
}

/// <summary>Per-picture metrics for one run, or the committed baseline.</summary>
internal sealed class SstvMetricsFile
{
    public string Loader { get; set; } = "";
    public List<SstvMetricsRecording> Recordings { get; set; } = [];
    /// <summary>Closest OscarWatch and MMSSTV decode for each original, when references were given.</summary>
    public List<SstvComparisonRow>? Comparison { get; set; }
}

internal sealed class SstvComparisonRow
{
    public string Reference { get; set; } = "";
    public string? OscarWatchPicture { get; set; }
    public double? OscarWatchPsnr { get; set; }
    public double? OscarWatchSsim { get; set; }
    public string? MmsstvPicture { get; set; }
    public double? MmsstvPsnr { get; set; }
    public double? MmsstvSsim { get; set; }
}

internal sealed class SstvMetricsRecording
{
    public string File { get; set; } = "";
    public List<SstvMetricsPicture> Pictures { get; set; } = [];
}

internal sealed class SstvMetricsPicture
{
    public string Mode { get; set; } = "";
    public bool FromVis { get; set; }
    public bool Complete { get; set; }
    public double LockPercent { get; set; }
    public double OffsetHz { get; set; }
    public double ClockPpm { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public int NoPulseLines { get; set; }
    public int WeakSyncLines { get; set; }
    public int OffGridLines { get; set; }
    public int UnlockedRuns { get; set; }
    public int LongestUnlockedRun { get; set; }
    /// <summary>Original picture this decode matched, when references were given.</summary>
    public string? Reference { get; set; }
    public double? ReferencePsnr { get; set; }
    public double? ReferenceSsim { get; set; }
}
