using System.Collections.Concurrent;
using OscarWatch.Core.CwDecode;
using OscarWatch.Core.Recording;
using OscarWatch.Core.Sstv;
using OscarWatch.Diagnostics;
using OscarWatch.Sstv;
using Serilog;

namespace OscarWatch.Cw;

/// <summary>Live soundcard health for the CW window (updated on the decoder thread).</summary>
public sealed record CwCaptureHealth(bool IsReceivingAudio, bool IsReconnecting);

/// <summary>
/// Runs the DeepCW decoder on its own thread, fed from the soundcard. Audio goes to 8 kHz for
/// the waterfall and note finder, then to the model rate (3200 Hz), shifted by the rig control
/// note offset. Events are raised on the decoder thread.
/// </summary>
public sealed class CwReceiverService : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<CwReceiverService>();
    private static readonly ThrottledLogger ReopenFailureLog = new(Log);

    public const int WaterfallRate = 8000;
    public const double WaterfallMinHz = 0;
    public const double WaterfallMaxHz = 2000;
    public const int WaterfallColumns = 400;
    public const double SpectrumRowsPerSecond = 25;
    /// <summary>128 ms FFT (7.8 Hz bins), a new one every 32 ms so each 40 ms row holds at least one.</summary>
    private const int SpectrumWindowSamples = 1024;
    private const int SpectrumHopSamples = 256;
    private const int MaxQueuedSpectrumRows = 100;
    private const int ChunkSamples = 1024;
    private const double ResampleCutoffHz = 1400;
    private const int ResampleTaps = 127;
    /// <summary>Per-row smoothing of the note finder's averaged spectrum (about two seconds).</summary>
    private const double ToneAverageAlpha = 0.02;
    /// <summary>Faster average for note following (about half a second), so it keeps up with Doppler.</summary>
    private const double FollowAverageAlpha = 0.08;
    /// <summary>Note following only looks this far either side of the band centre.</summary>
    private const double FollowCaptureHz = 250;
    /// <summary>Fraction of the error corrected per row, and the most it moves per row (125 Hz/s).</summary>
    private const double FollowGain = 0.1;
    private const double FollowMaxStepHz = 5;
    private const double ToneSearchMinHz = 150;
    private const double ToneSearchMaxHz = 1900;

    private readonly CwAudioService _audio;
    private readonly Queue<float[]> _spectrumRows = new();
    private readonly object _modelGate = new();
    private CwOnnxModel? _model;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private volatile bool _resetRequested;
    private volatile bool _inputSilent;
    private CwCaptureHealth _captureHealth = new(true, false);
    private double _feedForwardHz;
    private double _tuneOffsetHz;
    private double _toneHz = double.NaN;

    public CwReceiverService(CwAudioService audio)
    {
        _audio = audio;
    }

    public CwAudioService Audio => _audio;

    public bool IsRunning => _worker is { IsCompleted: false };

    public CwCaptureHealth CaptureHealth => _captureHealth;

    /// <summary>True while live input has carried no audio for a while (likely the wrong soundcard).</summary>
    public bool IsInputSilent => _inputSilent;

    /// <summary>
    /// Strongest steady note, in hertz, or NaN when nothing stands out. Like the waterfall rows,
    /// it is measured after the rig control correction, so it reads where the decoder hears it.
    /// </summary>
    public double ToneHz => Volatile.Read(ref _toneHz);

    /// <summary>Note offset from rig control, in hertz, removed from the audio before decoding.</summary>
    public double FeedForwardHz
    {
        get => Volatile.Read(ref _feedForwardHz);
        set => Volatile.Write(ref _feedForwardHz, double.IsFinite(value) ? value : 0);
    }

    /// <summary>
    /// Hertz above the band centre (plus <see cref="FeedForwardHz"/>) of the note being decoded.
    /// Set by clicking the waterfall, and moved by note following.
    /// </summary>
    public double TuneOffsetHz
    {
        get => Volatile.Read(ref _tuneOffsetHz);
        set => Volatile.Write(ref _tuneOffsetHz, double.IsFinite(value) ? value : 0);
    }

    /// <summary>Keep the steady note near the decode band centre as it drifts.</summary>
    public bool FollowNote { get; set; }

    /// <summary>Total audio offset of the decode band: rig control correction plus tuning.</summary>
    public double DecodeOffsetHz => CwTuner.ClampOffset(FeedForwardHz + TuneOffsetHz);

    /// <summary>Text settled by one decode step, and the tail that may still change.</summary>
    public event EventHandler<CwDecodeUpdate>? TextDecoded;
    public event EventHandler<Exception>? Faulted;
    /// <summary>The worker for session <c>e</c> (see <see cref="SessionId"/>) has finished.</summary>
    public event EventHandler<int>? Stopped;

    /// <summary>Goes up by one each time a live decode starts.</summary>
    public int SessionId { get; private set; }

    /// <summary>
    /// Spectrum rows (dB, <see cref="WaterfallMinHz"/> to <see cref="WaterfallMaxHz"/>) produced
    /// since the last call, oldest first.
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

    /// <summary>Forget the text decoded so far and start the next letter afresh.</summary>
    public void ResetDecoder() => _resetRequested = true;

    public void StartLive(CwDecodeSettings settings)
    {
        Stop();
        TuneOffsetHz = 0;
        FollowNote = settings.FollowNote;
        _audio.StartCapture(settings.InputDeviceId, settings.InputDeviceDisplayName);
        var rate = _audio.CaptureSampleRate;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var session = ++SessionId;
        var inputId = settings.InputDeviceId;
        var inputName = settings.InputDeviceDisplayName;
        _worker = Task.Factory.StartNew(
            () => RunLive(rate, session, inputId, inputName, token),
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
            worker?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Reported through Faulted.
        }

        _audio.StopCapture();
        cts.Dispose();
        _cts = null;
        _worker = null;
    }

    private CwOnnxModel LoadModel()
    {
        lock (_modelGate)
        {
            if (_model is not null)
                return _model;

            var started = DateTime.UtcNow;
            _model = CwOnnxModel.Load();
            Log.Information("CW model loaded from {Dir} in {Ms:0} ms", CwOnnxModel.DefaultDirectory,
                (DateTime.UtcNow - started).TotalMilliseconds);
            return _model;
        }
    }

    private void RunLive(int rate, int session, string? inputId, string? inputName, CancellationToken token)
    {
        var device = string.IsNullOrWhiteSpace(inputName) ? "system default" : inputName;
        var silence = new SstvInputSilence(rate);
        var warnedSilent = false;
        _inputSilent = false;
        Volatile.Write(ref _toneHz, double.NaN);
        BlockingCollection<float[]>? chunks = null;
        Task? decodeTask = null;
        try
        {
            var model = LoadModel();
            var decoder = new CwStreamDecoder(model.Metadata, model);
            chunks = new BlockingCollection<float[]>();
            var queue = chunks;
            decodeTask = Task.Factory.StartNew(
                () => DecodeLoop(decoder, queue),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            var toWaterfall = new SstvResampler(rate, WaterfallRate);
            var toModel = new CwResampler(WaterfallRate, model.Metadata.SampleRate, ResampleCutoffHz, ResampleTaps);
            var tuner = new CwTuner(WaterfallRate, model.Metadata);
            var followAverage = new float[WaterfallColumns];
            var tap = new SstvSpectrumTap(
                WaterfallRate, SpectrumRowsPerSecond, WaterfallMinHz, WaterfallMaxHz, WaterfallColumns,
                SpectrumWindowSamples, SpectrumHopSamples);
            var toneAverage = new float[WaterfallColumns];
            var rows = new List<float[]>(4);
            var at8k = new List<float>(ChunkSamples);
            var atModel = new List<float>(ChunkSamples);
            var buffer = new float[ChunkSamples];
            var stall = new CaptureStallWatch(DateTime.UtcNow);
            _captureHealth = new CwCaptureHealth(true, false);

            while (!token.IsCancellationRequested && !decodeTask.IsCompleted)
            {
                var n = _audio.ReadCaptureSamples(buffer);
                var gotSamples = n > 0;
                var reopen = stall.OnRead(DateTime.UtcNow, gotSamples);
                if (gotSamples)
                    _captureHealth = new CwCaptureHealth(true, false);
                else if (reopen)
                    _captureHealth = new CwCaptureHealth(false, true);

                if (reopen && ReopenStalledCapture(inputId, inputName, stall) && _audio.CaptureSampleRate != rate)
                {
                    Log.Information("CW capture reopened at {Rate} Hz (was {Old} Hz)", _audio.CaptureSampleRate, rate);
                    rate = _audio.CaptureSampleRate;
                    toWaterfall = new SstvResampler(rate, WaterfallRate);
                    silence = new SstvInputSilence(rate);
                }

                if (n == 0)
                {
                    Thread.Sleep(20);
                    continue;
                }

                silence.Add(buffer.AsSpan(0, n));
                _inputSilent = silence.IsSilent;
                if (silence.IsSilent && !warnedSilent)
                {
                    warnedSilent = true;
                    Log.Warning(
                        "CW input '{Device}' has carried no audio for {Seconds:0} s. Check the CW input device is the radio's audio interface",
                        device, SstvInputSilence.SilentSeconds);
                }

                at8k.Clear();
                toWaterfall.Process(buffer.AsSpan(0, n), at8k);
                rows.Clear();
                tap.Add(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(at8k), rows);
                if (rows.Count > 0)
                {
                    var feedForward = FeedForwardHz;
                    if (feedForward != 0)
                    {
                        for (var r = 0; r < rows.Count; r++)
                            rows[r] = CwSpectrumShift.Shift(rows[r], -feedForward, BinHz);
                    }

                    PublishSpectrum(rows);
                    UpdateTone(rows, toneAverage);
                    if (FollowNote)
                        FollowTone(rows, followAverage, tuner.CentreHz);
                }

                tuner.OffsetHz = DecodeOffsetHz;
                tuner.Process(at8k);
                atModel.Clear();
                toModel.Process(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(at8k), atModel);
                if (atModel.Count > 0)
                    chunks.Add(atModel.ToArray());
            }

            chunks.CompleteAdding();
            decodeTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CW live decode stopped");
            Faulted?.Invoke(this, ex);
        }
        finally
        {
            if (chunks is { IsAddingCompleted: false })
                chunks.CompleteAdding();
            try
            {
                decodeTask?.Wait();
            }
            catch (AggregateException)
            {
                // Already reported above.
            }

            chunks?.Dispose();
            _inputSilent = false;
            Volatile.Write(ref _toneHz, double.NaN);
            Stopped?.Invoke(this, session);
        }
    }

    /// <summary>
    /// Runs the model away from the capture loop, so a decode step (a large share of a second on a
    /// slow machine) never holds up the waterfall. Ends with the last letters once capture stops.
    /// </summary>
    private void DecodeLoop(CwStreamDecoder decoder, BlockingCollection<float[]> chunks)
    {
        foreach (var chunk in chunks.GetConsumingEnumerable())
        {
            if (_resetRequested)
            {
                _resetRequested = false;
                decoder.Reset();
            }

            decoder.Append(chunk);
            while (chunks.TryTake(out var more))
                decoder.Append(more);
            if (decoder.Step() is { } update)
                TextDecoded?.Invoke(this, update);
        }

        TextDecoded?.Invoke(this, decoder.FinishWithConfidence());
    }

    private const double BinHz = (WaterfallMaxHz - WaterfallMinHz) / WaterfallColumns;

    private void UpdateTone(List<float[]> rows, float[] average)
    {
        foreach (var row in rows)
            Accumulate(row, average, ToneAverageAlpha);

        var tone = CwToneFinder.Find(average, BinHz, ToneSearchMinHz, ToneSearchMaxHz);
        Volatile.Write(ref _toneHz, tone ?? double.NaN);
    }

    /// <summary>
    /// Nudge the tuning towards the steady note near the band centre, row by row. Rows are
    /// already corrected for rig control steps, so the band centre is home plus the tuning.
    /// </summary>
    private void FollowTone(List<float[]> rows, float[] average, double bandCentreHz)
    {
        foreach (var row in rows)
        {
            Accumulate(row, average, FollowAverageAlpha);
            var centre = bandCentreHz + TuneOffsetHz;
            if (CwToneFinder.Find(average, BinHz, centre - FollowCaptureHz, centre + FollowCaptureHz) is not { } tone)
                continue;

            var step = Math.Clamp((tone - centre) * FollowGain, -FollowMaxStepHz, FollowMaxStepHz);
            TuneOffsetHz = CwTuner.ClampOffset(TuneOffsetHz + step);
        }
    }

    private static void Accumulate(float[] row, float[] average, double alpha)
    {
        for (var i = 0; i < average.Length && i < row.Length; i++)
        {
            var power = (float)Math.Pow(10, row[i] / 10.0);
            average[i] = average[i] == 0 ? power : average[i] + (float)alpha * (power - average[i]);
        }
    }

    private bool ReopenStalledCapture(string? inputId, string? inputName, CaptureStallWatch stall)
    {
        Log.Warning("CW reopening the soundcard '{Device}' after it stopped delivering audio", inputName);
        try
        {
            _audio.StartCapture(inputId, inputName);
            stall.NoteReopenResult(succeeded: true);
            ReopenFailureLog.Reset();
            _captureHealth = new CwCaptureHealth(false, true);
            return true;
        }
        catch (Exception ex)
        {
            stall.NoteReopenResult(succeeded: false);
            _captureHealth = new CwCaptureHealth(false, true);
            ReopenFailureLog.Warning(ex, "CW could not reopen the soundcard '{Device}'", inputName);
            return false;
        }
    }

    private void PublishSpectrum(List<float[]> rows)
    {
        lock (_spectrumRows)
        {
            foreach (var row in rows)
                _spectrumRows.Enqueue(row);
            while (_spectrumRows.Count > MaxQueuedSpectrumRows)
                _spectrumRows.Dequeue();
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_modelGate)
        {
            _model?.Dispose();
            _model = null;
        }
    }
}
