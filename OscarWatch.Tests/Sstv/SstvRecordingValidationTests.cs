using OscarWatch.Core.Sstv;
using Xunit.Abstractions;

namespace OscarWatch.Tests.Sstv;

/// <summary>
/// Decodes real recordings when OSCARWATCH_SSTV_FIXTURES names a folder of WAV files.
/// Pictures go to OSCARWATCH_SSTV_OUTPUT (or a temp folder) for checking by eye against
/// another decoder. Recordings are not committed, so this does nothing by default.
/// </summary>
public sealed class SstvRecordingValidationTests(ITestOutputHelper output)
{
    [Fact]
    public void Decode_recordings_from_fixture_folder()
    {
        var folder = Environment.GetEnvironmentVariable("OSCARWATCH_SSTV_FIXTURES");
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        var outDir = Environment.GetEnvironmentVariable("OSCARWATCH_SSTV_OUTPUT");
        if (string.IsNullOrWhiteSpace(outDir))
            outDir = Path.Combine(Path.GetTempPath(), "oscarwatch-sstv-out");
        Directory.CreateDirectory(outDir);

        foreach (var file in Directory.GetFiles(folder, "*.wav").Order())
        {
            var (samples, rate) = SstvWavReader.Read(file);
            var decoder = new SstvDecoder(rate);
            var images = new List<SstvDecodedImage>();
            var abandoned = 0;
            decoder.ImageCompleted += (_, image) => images.Add(image);
            decoder.ImageAbandoned += (_, _) => abandoned++;
            const int chunk = 4096;
            for (var i = 0; i < samples.Length; i += chunk)
                decoder.Process(samples.AsSpan(i, Math.Min(chunk, samples.Length - i)));
            decoder.Flush();

            var name = Path.GetFileNameWithoutExtension(file);
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
        }
    }
}
