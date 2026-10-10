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

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var load = await loader.LoadAsync(file);
            Assert.True(load.Samples is not null, $"{name}: could not load ({load.Error})");

            var decoder = new SstvDecoder(load.SampleRate);
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
                SstvPngWriter.Save(img.Image, Path.Combine(outDir, $"{name}-{n + 1:00}-{img.Mode.Id}.png"));
            }

            run.Recordings.Add(new SstvMetricsRecording
            {
                File = Path.GetFileName(file),
                Pictures = images.Select(ToMetrics).ToList(),
            });
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

        var baselinePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", BaselineFileName);
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

    private static SstvMetricsPicture ToMetrics(SstvDecodedImage image) => new()
    {
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
}
