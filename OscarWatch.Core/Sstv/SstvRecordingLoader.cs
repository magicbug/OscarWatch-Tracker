using OscarWatch.Core.Recording;

namespace OscarWatch.Core.Sstv;

public sealed record SstvRecordingLoadResult(float[]? Samples, int SampleRate, string? Error);

/// <summary>
/// Loads a recording for decoding again. WAV is read directly; other formats (MP3 and so on)
/// are converted with ffmpeg, which must be on PATH.
/// </summary>
public sealed class SstvRecordingLoader
{
    private static readonly TimeSpan ConvertTimeout = TimeSpan.FromMinutes(5);
    private readonly IExternalProcessRunner _runner;
    private readonly FfmpegLocator _locator;

    public SstvRecordingLoader(IExternalProcessRunner? runner = null, FfmpegLocator? locator = null)
    {
        _runner = runner ?? new ProcessExternalProcessRunner();
        _locator = locator ?? new FfmpegLocator(_runner);
    }

    public async Task<SstvRecordingLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return new SstvRecordingLoadResult(null, 0, "The file was not found.");

        if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var (samples, rate) = await Task.Run(() => SstvWavReader.Read(path), cancellationToken).ConfigureAwait(false);
                return new SstvRecordingLoadResult(samples, rate, null);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException)
            {
                // Odd WAV variants: let ffmpeg have a go.
                if (!(await _locator.ProbeAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).IsAvailable)
                    return new SstvRecordingLoadResult(null, 0, ex.Message);
            }
        }

        var probe = await _locator.ProbeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!probe.IsAvailable || string.IsNullOrWhiteSpace(probe.ExecutablePath))
            return new SstvRecordingLoadResult(null, 0, probe.Detail ?? "ffmpeg was not found on PATH.");

        var temp = Path.Combine(Path.GetTempPath(), $"oscarwatch-sstv-{Guid.NewGuid():N}.wav");
        try
        {
            var result = await _runner.RunAsync(
                probe.ExecutablePath,
                ["-hide_banner", "-loglevel", "error", "-y", "-i", path, "-ac", "1", "-ar", SstvDecoder.InternalRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-sample_fmt", "s16", temp],
                ConvertTimeout,
                cancellationToken).ConfigureAwait(false);
            if (result.TimedOut)
                return new SstvRecordingLoadResult(null, 0, "ffmpeg timed out.");
            if (result.ExitCode != 0 || !File.Exists(temp))
            {
                var detail = string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"ffmpeg exited with code {result.ExitCode}."
                    : result.StandardError.Trim();
                return new SstvRecordingLoadResult(null, 0, detail);
            }

            var (samples, rate) = SstvWavReader.Read(temp);
            return new SstvRecordingLoadResult(samples, rate, null);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // Temp file; the OS cleans up eventually.
            }
        }
    }
}
