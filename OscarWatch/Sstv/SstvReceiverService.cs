using System.Collections.Concurrent;
using OscarWatch.Core.Sstv;
using OscarWatch.Recording;
using Serilog;

namespace OscarWatch.Sstv;

/// <summary>Live state for the window, copied out of the decoder thread.</summary>
public sealed record SstvLiveStatus(
    SstvDecoderState State,
    SstvMode? Mode,
    bool FromVis,
    int LinesDecoded,
    double LockPercent,
    double OffsetHz,
    double ClockPpm,
    double FeedForwardHz);

/// <summary>A picture handed to the window: the decode plus where it was saved, if it was.</summary>
public sealed record SstvReceivedPicture(SstvDecodedImage Decoded, DateTime ReceivedUtc, string? Satellite, string? SavedPath);

/// <summary>
/// Runs the SSTV decoder on its own thread, fed from the soundcard (live) or from a file
/// (decode a recording). Events are raised on that thread.
/// </summary>
public sealed class SstvReceiverService : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SstvReceiverService>();
    private const int ChunkSamples = 2048;
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(250);
    private const int SpectrumWindow = 2048;
    private const double SpectrumRowsPerSecond = 10;

    private readonly SstvAudioService _audio;
    private readonly ConcurrentQueue<Action<SstvDecoder>> _commands = new();
    private readonly object _previewGate = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private SstvImage? _preview;
    private long _previewVersion;
    private volatile SstvLiveStatus? _status;
    private double _feedForwardHz;
    private readonly object _spectrumGate = new();
    private float[]? _spectrum;
    private long _spectrumVersion;

    public SstvReceiverService(SstvAudioService audio)
    {
        _audio = audio;
    }

    public SstvAudioService Audio => _audio;

    public bool IsRunning => _worker is { IsCompleted: false };

    /// <summary>True while a recording (not the soundcard) is being decoded.</summary>
    public bool IsDecodingFile { get; private set; }

    public SstvLiveStatus? Status => _status;

    /// <summary>Fraction of the file decoded so far (0 to 1) while decoding a recording.</summary>
    public double FileProgress { get; private set; }

    /// <summary>Path of the session WAV being written, or null.</summary>
    public string? SessionAudioPath { get; private set; }

    /// <summary>Called on the decoder thread for the satellite name to put in file names.</summary>
    public Func<string?>? SatelliteNameProvider { get; set; }

    /// <summary>Audio offset from rig control, applied to the live decoder.</summary>
    public double FeedForwardHz
    {
        get => Volatile.Read(ref _feedForwardHz);
        set => Volatile.Write(ref _feedForwardHz, double.IsFinite(value) ? value : 0);
    }

    public event EventHandler<SstvReceivedPicture>? PictureReceived;
    public event EventHandler<SstvImageStartedEventArgs>? PictureStarted;
    public event EventHandler<Exception>? Faulted;
    /// <summary>The worker for session <c>e</c> (see <see cref="SessionId"/>) has finished.</summary>
    public event EventHandler<int>? Stopped;

    /// <summary>Goes up by one each time a live or file decode starts.</summary>
    public int SessionId { get; private set; }

    /// <summary>Copy of the picture being received if it changed since <paramref name="version"/>.</summary>
    public SstvImage? TryGetPreview(ref long version)
    {
        lock (_previewGate)
        {
            if (_previewVersion == version)
                return null;
            version = _previewVersion;
            return _preview?.Clone();
        }
    }

    /// <summary>Latest spectrum row (dB, <see cref="SstvSpectrumTap.MinHz"/> to <see cref="SstvSpectrumTap.MaxHz"/>) if new since <paramref name="version"/>.</summary>
    public float[]? TryGetSpectrum(ref long version)
    {
        lock (_spectrumGate)
        {
            if (_spectrumVersion == version || _spectrum is null)
                return null;
            version = _spectrumVersion;
            return (float[])_spectrum.Clone();
        }
    }

    public void ReSync() => _commands.Enqueue(d => d.ReSync());

    /// <summary>Drop the picture being received and listen for the next one.</summary>
    public void SkipPicture() => _commands.Enqueue(d => d.Reset());

    public void ApplyOptions(SstvSettings settings) => _commands.Enqueue(d => CopyOptions(settings, d.Options));

    public void StartLive(SstvSettings settings, bool saveSessionAudio)
    {
        Stop();
        _audio.StartCapture(settings.InputDeviceId, settings.InputDeviceDisplayName);
        var rate = _audio.CaptureSampleRate;
        var options = NewOptions(settings);
        var autoSave = settings.AutoSavePictures;
        string? wavPath = null;
        if (saveSessionAudio)
        {
            wavPath = Path.Combine(
                SstvPaths.GetDefaultDirectory(),
                SstvPaths.SessionAudioStem(DateTime.UtcNow, SatelliteNameProvider?.Invoke()) + ".wav");
        }

        SessionAudioPath = wavPath;
        IsDecodingFile = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var session = ++SessionId;
        _worker = Task.Factory.StartNew(
            () => RunLive(rate, options, autoSave, wavPath, session, token),
            token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void StartFile(float[] samples, int sampleRate, SstvSettings settings, string sourceName)
    {
        Stop();
        var options = NewOptions(settings);
        var autoSave = settings.AutoSavePictures;
        SessionAudioPath = null;
        IsDecodingFile = true;
        FileProgress = 0;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var session = ++SessionId;
        _worker = Task.Factory.StartNew(
            () => RunFile(samples, sampleRate, options, autoSave, sourceName, session, token),
            token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void Stop()
    {
        var cts = _cts;
        var worker = _worker;
        if (cts is null)
            return;

        cts.Cancel();
        try
        {
            worker?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Reported through Faulted.
        }

        _audio.StopCapture();
        cts.Dispose();
        _cts = null;
        _worker = null;
        IsDecodingFile = false;
    }

    private void RunLive(int rate, SstvDecoderOptions options, bool autoSave, string? wavPath, int session, CancellationToken token)
    {
        WavWriter? wav = null;
        try
        {
            var decoder = CreateDecoder(rate, options, autoSave, satelliteOverride: null, fileStart: null);
            SstvResampler? wavResampler = null;
            var resampled = new List<float>(ChunkSamples);
            byte[] pcm = [];
            if (wavPath is not null)
            {
                wav = new WavWriter(wavPath, SstvDecoder.InternalRate, 1);
                wavResampler = new SstvResampler(rate, SstvDecoder.InternalRate);
                Log.Information("SSTV session audio to {Path}", wavPath);
            }

            var buffer = new float[ChunkSamples];
            var lastPreview = DateTime.MinValue;
            var tap = new SstvSpectrumTap(rate, SpectrumRowsPerSecond);
            while (!token.IsCancellationRequested)
            {
                RunCommands(decoder);
                var n = _audio.ReadCaptureSamples(buffer);
                if (n == 0)
                {
                    Thread.Sleep(20);
                    continue;
                }

                decoder.FeedForwardHz = FeedForwardHz;
                decoder.Process(buffer.AsSpan(0, n));
                PublishSpectrum(tap.Add(buffer.AsSpan(0, n)));

                if (wav is not null && wavResampler is not null)
                {
                    resampled.Clear();
                    wavResampler.Process(buffer.AsSpan(0, n), resampled);
                    if (pcm.Length < resampled.Count * 2)
                        pcm = new byte[resampled.Count * 2];
                    for (var i = 0; i < resampled.Count; i++)
                    {
                        var s = (short)Math.Clamp(Math.Round(resampled[i] * 32767.0), short.MinValue, short.MaxValue);
                        pcm[2 * i] = (byte)s;
                        pcm[2 * i + 1] = (byte)(s >> 8);
                    }

                    wav.WritePcm16(pcm.AsSpan(0, resampled.Count * 2));
                }

                PublishStatus(decoder, ref lastPreview, force: false);
            }

            decoder.Flush();
            PublishStatus(decoder, ref lastPreview, force: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SSTV live decode stopped");
            Faulted?.Invoke(this, ex);
        }
        finally
        {
            try
            {
                wav?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "SSTV session audio close failed");
            }

            Stopped?.Invoke(this, session);
        }
    }

    private void RunFile(float[] samples, int rate, SstvDecoderOptions options, bool autoSave, string sourceName, int session, CancellationToken token)
    {
        try
        {
            var decoder = CreateDecoder(rate, options, autoSave, satelliteOverride: sourceName, fileStart: DateTime.UtcNow);
            var lastPreview = DateTime.MinValue;
            var tap = new SstvSpectrumTap(rate, SpectrumRowsPerSecond);
            for (var i = 0; i < samples.Length && !token.IsCancellationRequested; i += ChunkSamples)
            {
                RunCommands(decoder);
                var chunk = samples.AsSpan(i, Math.Min(ChunkSamples, samples.Length - i));
                decoder.Process(chunk);
                PublishSpectrum(tap.Add(chunk));
                FileProgress = (double)Math.Min(samples.Length, i + ChunkSamples) / samples.Length;
                PublishStatus(decoder, ref lastPreview, force: false);
            }

            decoder.Flush();
            PublishStatus(decoder, ref lastPreview, force: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SSTV recording decode stopped");
            Faulted?.Invoke(this, ex);
        }
        finally
        {
            Stopped?.Invoke(this, session);
        }
    }

    private SstvDecoder CreateDecoder(int rate, SstvDecoderOptions options, bool autoSave, string? satelliteOverride, DateTime? fileStart)
    {
        var decoder = new SstvDecoder(rate, options);
        decoder.ImageStarted += (_, e) => PictureStarted?.Invoke(this, e);
        decoder.ImageCompleted += (_, image) =>
        {
            // For a recording, name pictures after the file and time them by their place in it.
            var received = fileStart is { } start ? start.AddSeconds(image.StartSeconds) : DateTime.UtcNow;
            var satellite = satelliteOverride ?? SatelliteNameProvider?.Invoke();
            string? saved = null;
            if (autoSave)
                saved = SavePicture(image, received, satellite);
            // The decoder has already let go of this picture, so the regular preview copy
            // never sees the last lines or the final re-sync redraw.
            lock (_previewGate)
            {
                _preview = image.Image.Clone();
                _previewVersion++;
            }
            PictureReceived?.Invoke(this, new SstvReceivedPicture(image, received, satellite, saved));
        };
        return decoder;
    }

    /// <summary>Save as PNG in the SSTV folder. Returns the path, or null if it failed.</summary>
    public static string? SavePicture(SstvDecodedImage image, DateTime utc, string? satellite)
    {
        try
        {
            var dir = SstvPaths.GetDefaultDirectory();
            var stem = SstvPaths.PictureStem(utc, image.Mode, satellite);
            var path = Path.Combine(dir, stem + ".png");
            for (var n = 2; File.Exists(path); n++)
                path = Path.Combine(dir, $"{stem}_{n}.png");
            SstvPngWriter.Save(image.Image, path);
            Log.Information("SSTV picture saved to {Path}", path);
            return path;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SSTV picture save failed");
            return null;
        }
    }

    private void PublishSpectrum(float[]? row)
    {
        if (row is null)
            return;
        lock (_spectrumGate)
        {
            _spectrum = row;
            _spectrumVersion++;
        }
    }

    private void RunCommands(SstvDecoder decoder)
    {
        while (_commands.TryDequeue(out var command))
            command(decoder);
    }

    private void PublishStatus(SstvDecoder decoder, ref DateTime lastPreview, bool force)
    {
        _status = new SstvLiveStatus(
            decoder.State,
            decoder.CurrentMode,
            decoder.CurrentFromVis,
            decoder.LinesDecoded,
            decoder.LockPercent,
            decoder.OffsetHz,
            decoder.ClockPpm,
            decoder.FeedForwardHz);

        var now = DateTime.UtcNow;
        if (!force && now - lastPreview < PreviewInterval)
            return;
        lastPreview = now;

        lock (_previewGate)
        {
            var current = decoder.CurrentImage;
            if (current is null)
                return;
            if (_preview is null || _preview.Width != current.Width || _preview.Height != current.Height)
                _preview = current.Clone();
            else
                Buffer.BlockCopy(current.Rgb, 0, _preview.Rgb, 0, current.Rgb.Length);
            _previewVersion++;
        }
    }

    private static SstvDecoderOptions NewOptions(SstvSettings settings)
    {
        var options = new SstvDecoderOptions();
        CopyOptions(settings, options);
        return options;
    }

    private static void CopyOptions(SstvSettings settings, SstvDecoderOptions options)
    {
        options.ForcedMode = settings.GetForcedMode();
        options.AutoSlant = settings.AutoSlant;
        options.SlantTrimPpm = settings.SlantTrimPpm;
        options.AutoTune = settings.AutoTune;
        options.DetectWithoutVis = settings.DetectWithoutVis;
    }

    public void Dispose() => Stop();
}
