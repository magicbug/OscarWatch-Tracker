using System.Collections.Concurrent;
using OscarWatch.Diagnostics;
using OscarWatch.Core.Recording;
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

/// <summary>Live soundcard health for the SSTV window (updated on the decoder thread).</summary>
public sealed record SstvCaptureHealth(bool IsReceivingAudio, bool IsReconnecting, int ReconnectAttempts);

/// <summary>A picture handed to the window: the decode plus where it was saved, if it was.</summary>
public sealed record SstvReceivedPicture(SstvDecodedImage Decoded, DateTime ReceivedUtc, string? Satellite, string? SavedPath);

/// <summary>
/// Runs the SSTV decoder on its own thread, fed from the soundcard (live) or from a file
/// (decode a recording). Events are raised on that thread.
/// </summary>
public sealed class SstvReceiverService : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SstvReceiverService>();
    private static readonly ThrottledLogger ReopenFailureLog = new(Log);
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
    private SstvCaptureHealth _captureHealth = new(true, false, 0);
    private volatile bool _inputSilent;
    private double _feedForwardHz;
    private double _timeCompressionPpm;
    private const int MaxQueuedSpectrumRows = 50;
    private readonly Queue<float[]> _spectrumRows = new();

    private readonly SstvPictureUploader? _shareQueue;

    public SstvReceiverService(SstvAudioService audio, SstvPictureUploader? shareQueue = null)
    {
        _audio = audio;
        _shareQueue = shareQueue;
    }

    public SstvAudioService Audio => _audio;

    public bool IsRunning => _worker is { IsCompleted: false };

    /// <summary>True while a recording (not the soundcard) is being decoded.</summary>
    public bool IsDecodingFile { get; private set; }

    public SstvLiveStatus? Status => _status;

    public SstvCaptureHealth CaptureHealth => _captureHealth;

    /// <summary>True while live input has carried no audio for a while (likely the wrong soundcard).</summary>
    public bool IsInputSilent => _inputSilent;

    /// <summary>Fraction of the file decoded so far (0 to 1) while decoding a recording.</summary>
    public double FileProgress { get; private set; }

    /// <summary>Path of the session WAV being written, or null.</summary>
    public string? SessionAudioPath { get; private set; }

    /// <summary>Session WAV from the most recent live session, kept after it stops so it can be decoded again.</summary>
    public string? LastSessionAudioPath { get; private set; }

    /// <summary>SSTV modes the satellite was expected to send during the most recent live session.</summary>
    public IReadOnlyCollection<SstvModeId>? LastSessionExpectedModes { get; private set; }

    /// <summary>Called on the decoder thread for the satellite name to put in file names.</summary>
    public Func<string?>? SatelliteNameProvider { get; set; }

    /// <summary>Called on the decoder thread for the NORAD id of the satellite being received.</summary>
    public Func<string?>? SatelliteNoradProvider { get; set; }

    /// <summary>
    /// Called on the decoder thread for the SSTV modes the tracked satellite is known to send
    /// (null for no preference). Live decoding only.
    /// </summary>
    public Func<IReadOnlyCollection<SstvModeId>?>? ExpectedModesProvider { get; set; }

    /// <summary>Merge repeat copies of a picture and save the merged picture too (with automatic saving).</summary>
    public bool CombineRepeats { get; set; }

    /// <summary>Picture combiners by satellite, used only on the decoder thread.</summary>
    private readonly Dictionary<string, SstvPictureCombiner> _combiners = new();

    /// <summary>Audio offset from rig control, applied to the live decoder.</summary>
    public double FeedForwardHz
    {
        get => Volatile.Read(ref _feedForwardHz);
        set => Volatile.Write(ref _feedForwardHz, double.IsFinite(value) ? value : 0);
    }

    /// <summary>
    /// Doppler time compression of the received audio in ppm (range rate over the speed of light,
    /// positive while the satellite moves away), applied to the live decoder's line timing.
    /// </summary>
    public double TimeCompressionPpm
    {
        get => Volatile.Read(ref _timeCompressionPpm);
        set => Volatile.Write(ref _timeCompressionPpm, double.IsFinite(value) ? value : 0);
    }

    /// <summary>Time compression in ppm for a range rate in km/s (positive when receding).</summary>
    public static double TimeCompressionPpmFromRangeRate(double rangeRateKmPerSec) =>
        double.IsFinite(rangeRateKmPerSec) ? rangeRateKmPerSec / 299792.458 * 1e6 : 0;

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

    /// <summary>
    /// Spectrum rows (dB, <see cref="SstvSpectrumTap.MinHz"/> to <see cref="SstvSpectrumTap.MaxHz"/>)
    /// produced since the last call, oldest first. Every row is kept so the waterfall scrolls evenly
    /// whether the UI tick lands just before or just after a row.
    /// </summary>
    public float[][] TakeSpectrumRows()
    {
        lock (_spectrumRows)
        {
            if (_spectrumRows.Count == 0)
                return [];
            var rows = _spectrumRows.ToArray();
            _spectrumRows.Clear();
            return rows;
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
        var outputDir = ResolveOutputDirectory(settings.OutputFolder);
        string? wavPath = null;
        if (saveSessionAudio)
        {
            wavPath = Path.Combine(
                outputDir,
                SstvPaths.SessionAudioStem(DateTime.UtcNow, SatelliteNameProvider?.Invoke()) + ".wav");
        }

        SessionAudioPath = wavPath;
        if (wavPath is not null)
        {
            LastSessionAudioPath = wavPath;
            LastSessionExpectedModes = null;
        }
        IsDecodingFile = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var session = ++SessionId;
        var inputId = settings.InputDeviceId;
        var inputName = settings.InputDeviceDisplayName;
        _worker = Task.Factory.StartNew(
            () => RunLive(rate, options, autoSave, outputDir, wavPath, session, inputId, inputName, token),
            token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void StartFile(
        float[] samples, int sampleRate, SstvSettings settings, string sourceName,
        IReadOnlyCollection<SstvModeId>? expectedModes = null)
    {
        Stop();
        var options = NewOptions(settings);
        options.ExpectedModes = expectedModes;
        var autoSave = settings.AutoSavePictures;
        var outputDir = ResolveOutputDirectory(settings.OutputFolder);
        SessionAudioPath = null;
        IsDecodingFile = true;
        FileProgress = 0;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var session = ++SessionId;
        _worker = Task.Factory.StartNew(
            () => RunFile(samples, sampleRate, options, autoSave, outputDir, sourceName, session, token),
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

    private bool ReopenStalledCapture(string? inputId, string? inputName, CaptureStallWatch stall)
    {
        Log.Warning("SSTV reopening the soundcard '{Device}' after it stopped delivering audio", inputName);
        try
        {
            _audio.StartCapture(inputId, inputName);
            stall.NoteReopenResult(succeeded: true);
            ReopenFailureLog.Reset();
            _captureHealth = new SstvCaptureHealth(false, true, stall.Reopens);
            return true;
        }
        catch (Exception ex)
        {
            stall.NoteReopenResult(succeeded: false);
            _captureHealth = new SstvCaptureHealth(false, true, stall.Reopens);
            ReopenFailureLog.Warning(ex, "SSTV could not reopen the soundcard '{Device}'", inputName);
            return false;
        }
    }

    private void RunLive(
        int rate, SstvDecoderOptions options, bool autoSave, string outputDir, string? wavPath, int session,
        string? inputId, string? inputName, CancellationToken token)
    {
        WavWriter? wav = null;
        var device = string.IsNullOrWhiteSpace(inputName) ? "system default" : inputName;
        var silence = new SstvInputSilence(rate);
        var heardSound = false;
        var loudestPeak = 0f;
        var warnedSilent = false;
        _inputSilent = false;
        try
        {
            var decoder = CreateDecoder(rate, options, autoSave, outputDir, satelliteOverride: null, fileStart: null);
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
            var stall = new CaptureStallWatch(DateTime.UtcNow);
            _captureHealth = new SstvCaptureHealth(true, false, 0);
            var lostSince = DateTime.UtcNow;
            var lostOverflows = 0;
            var lostSamples = 0L;
            while (!token.IsCancellationRequested)
            {
                RunCommands(decoder);
                var n = _audio.ReadCaptureSamples(buffer);
                var gotSamples = n > 0;
                var reopen = stall.OnRead(DateTime.UtcNow, gotSamples);
                if (gotSamples)
                    _captureHealth = new SstvCaptureHealth(true, false, 0);
                else if (reopen)
                    _captureHealth = new SstvCaptureHealth(false, true, stall.Reopens);

                if (reopen && ReopenStalledCapture(inputId, inputName, stall)
                    && _audio.CaptureSampleRate != rate)
                {
                    Log.Information("SSTV capture reopened at {Rate} Hz (was {Old} Hz)", _audio.CaptureSampleRate, rate);
                    rate = _audio.CaptureSampleRate;
                    decoder = CreateDecoder(rate, options, autoSave, outputDir, satelliteOverride: null, fileStart: null);
                    tap = new SstvSpectrumTap(rate, SpectrumRowsPerSecond);
                    silence = new SstvInputSilence(rate);
                    if (wavResampler is not null)
                        wavResampler = new SstvResampler(rate, SstvDecoder.InternalRate);
                }

                if (n == 0)
                {
                    Thread.Sleep(20);
                    continue;
                }

                ReportLostAudio(device, rate, ref lostSince, ref lostOverflows, ref lostSamples);
                silence.Add(buffer.AsSpan(0, n));
                heardSound |= silence.HeardSound;
                loudestPeak = Math.Max(loudestPeak, silence.LoudestPeak);
                _inputSilent = silence.IsSilent;
                if (silence.IsSilent && !warnedSilent)
                {
                    warnedSilent = true;
                    Log.Warning(
                        "SSTV input '{Device}' has carried no audio for {Seconds:0} s. Check the SSTV input device is the radio's audio interface",
                        device, SstvInputSilence.SilentSeconds);
                }

                decoder.FeedForwardHz = FeedForwardHz;
                decoder.TimeCompressionPpm = TimeCompressionPpm;
                decoder.Options.ExpectedModes = ExpectedModesProvider?.Invoke();
                if (wavPath is not null && decoder.Options.ExpectedModes is { } expected)
                    LastSessionExpectedModes = expected;
                decoder.Process(buffer.AsSpan(0, n));
                PublishSpectrum(tap, buffer.AsSpan(0, n));

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

            _inputSilent = false;
            var loudestDb = loudestPeak > 0 ? 20 * Math.Log10(loudestPeak) : double.NegativeInfinity;
            if (heardSound)
            {
                Log.Information("SSTV session on '{Device}' ended; loudest input {Peak:0.0} dBFS", device, loudestDb);
            }
            else if (warnedSilent)
            {
                Log.Warning(
                    "SSTV session on '{Device}' ended without any audio (loudest {Peak:0.0} dBFS); the recording {Path} is silent. Check the SSTV input device",
                    device, loudestDb, wavPath ?? "(not saved)");
            }

            Stopped?.Invoke(this, session);
        }
    }

    private void RunFile(float[] samples, int rate, SstvDecoderOptions options, bool autoSave, string outputDir, string sourceName, int session, CancellationToken token)
    {
        try
        {
            var decoder = CreateDecoder(rate, options, autoSave, outputDir, satelliteOverride: sourceName, fileStart: DateTime.UtcNow);
            var lastPreview = DateTime.MinValue;
            var tap = new SstvSpectrumTap(rate, SpectrumRowsPerSecond);
            for (var i = 0; i < samples.Length && !token.IsCancellationRequested; i += ChunkSamples)
            {
                RunCommands(decoder);
                var chunk = samples.AsSpan(i, Math.Min(ChunkSamples, samples.Length - i));
                decoder.Process(chunk);
                PublishSpectrum(tap, chunk);
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

    private SstvDecoder CreateDecoder(int rate, SstvDecoderOptions options, bool autoSave, string outputDir, string? satelliteOverride, DateTime? fileStart)
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
                saved = SavePicture(image, received, satellite, outputDir);
            if (autoSave && CombineRepeats)
                SaveCombined(image, received, satellite, outputDir);
            QueueForSharing(image, received, satellite, saved, fromRecording: fileStart is not null);
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

    private void QueueForSharing(SstvDecodedImage image, DateTime received, string? satellite, string? saved, bool fromRecording)
    {
        if (_shareQueue is null || !_shareQueue.IsActive || !SstvShareRules.IsShareable(fromRecording, satellite, image))
            return;

        // With automatic saving off, keep a copy only until it has been shared.
        var temporary = saved is null;
        var path = saved ?? SavePicture(image, received, satellite, SstvShareQueueStore.DefaultPictureFolder);
        if (path is null)
            return;

        string? norad = null;
        try
        {
            norad = SatelliteNoradProvider?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SSTV share: NORAD id lookup failed");
        }

        _shareQueue.Enqueue(new SstvShareItem(
            path,
            satellite!.Trim(),
            string.IsNullOrWhiteSpace(norad) ? null : norad.Trim(),
            image.Mode.Name,
            received,
            image.LockPercent,
            temporary));
    }

    /// <summary>
    /// The SSTV save folder for <paramref name="configured"/>, logging a warning when a custom
    /// folder is missing or not writable and the default is used instead.
    /// </summary>
    public static string ResolveOutputDirectory(string? configured)
    {
        var resolved = SstvPaths.ResolveOutputDirectory(configured);
        if (resolved.FellBack)
        {
            Log.Warning(
                "SSTV folder {Requested} is missing or not writable; saving to {Default} instead",
                resolved.Requested,
                resolved.Path);
        }

        return resolved.Path;
    }

    /// <summary>Save as PNG in <paramref name="dir"/>. Returns the path, or null if it failed.</summary>
    /// <summary>
    /// Merge with earlier copies of the same picture from the same satellite (this run of the
    /// app) and save the result next to the picture with a <c>_combined</c> suffix.
    /// </summary>
    private void SaveCombined(SstvDecodedImage image, DateTime utc, string? satellite, string dir)
    {
        if (string.IsNullOrWhiteSpace(satellite))
            return;
        try
        {
            var key = satellite.Trim().ToUpperInvariant();
            if (!_combiners.TryGetValue(key, out var combiner))
                _combiners[key] = combiner = new SstvPictureCombiner();
            if (combiner.Add(image) is not { } combined)
                return;

            Directory.CreateDirectory(dir);
            var stem = SstvPaths.PictureStem(utc, image.Mode, satellite) + "_combined";
            var path = Path.Combine(dir, stem + ".png");
            for (var n = 2; File.Exists(path); n++)
                path = Path.Combine(dir, $"{stem}_{n}.png");
            SstvPngWriter.Save(combined.Image, path);
            Log.Information("SSTV picture merged from {Copies} copies, saved to {Path}", combined.Copies, path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SSTV picture merge failed");
        }
    }

    public static string? SavePicture(SstvDecodedImage image, DateTime utc, string? satellite, string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
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

    private void PublishSpectrum(SstvSpectrumTap tap, ReadOnlySpan<float> samples)
    {
        var rows = new List<float[]>(2);
        tap.Add(samples, rows);
        if (rows.Count == 0)
            return;
        lock (_spectrumRows)
        {
            foreach (var row in rows)
                _spectrumRows.Enqueue(row);
            while (_spectrumRows.Count > MaxQueuedSpectrumRows)
                _spectrumRows.Dequeue();
        }
    }

    /// <summary>
    /// Warns, at most every 30 s, when the soundcard overran or decoding fell behind, so
    /// pictures cut up by lost audio can be traced from the log.
    /// </summary>
    private void ReportLostAudio(string device, int rate, ref DateTime since, ref int overflows, ref long dropped)
    {
        var (o, d) = _audio.TakeLostAudio();
        overflows += o;
        dropped += d;
        var now = DateTime.UtcNow;
        if (now - since < TimeSpan.FromSeconds(30))
            return;
        if (overflows > 0 || dropped > 0)
        {
            Log.Warning(
                "SSTV audio lost on '{Device}' in the last {Seconds:0} s: {Overflows} soundcard input overflows, {Dropped:0.0} s dropped because decoding fell behind. Pictures will be cut up",
                device, (now - since).TotalSeconds, overflows, (double)dropped / rate);
        }

        since = now;
        overflows = 0;
        dropped = 0;
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
        options.HoldThroughFades = settings.HoldThroughFades;
        options.AdaptiveBandwidth = settings.AdaptiveBandwidth;
        options.AlignAosFragments = settings.AlignAosFragments;
        options.RepairBadLines = settings.RepairBadLines;
        options.SmoothAcrossLines = settings.SmoothAcrossLines;
    }

    public void Dispose() => Stop();
}
