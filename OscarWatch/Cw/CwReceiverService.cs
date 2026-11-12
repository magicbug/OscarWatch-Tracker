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
    private const double SpectrumRowsPerSecond = 10;
    private const int MaxQueuedSpectrumRows = 50;
    private const int ChunkSamples = 2048;
    private const double ResampleCutoffHz = 1400;
    private const int ResampleTaps = 127;
    /// <summary>Per-row smoothing of the note finder's averaged spectrum (about two seconds).</summary>
    private const double ToneAverageAlpha = 0.05;

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

    /// <summary>Strongest steady note, in hertz, or NaN when nothing stands out.</summary>
    public double ToneHz => Volatile.Read(ref _toneHz);

    /// <summary>Note offset from rig control, in hertz, removed from the audio before decoding.</summary>
    public double FeedForwardHz
    {
        get => Volatile.Read(ref _feedForwardHz);
        set => Volatile.Write(ref _feedForwardHz, double.IsFinite(value) ? value : 0);
    }

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
        CwStreamDecoder? decoder = null;
        try
        {
            var model = LoadModel();
            decoder = new CwStreamDecoder(model.Metadata, model);
            var toWaterfall = new SstvResampler(rate, WaterfallRate);
            var toModel = new CwResampler(WaterfallRate, model.Metadata.SampleRate, ResampleCutoffHz, ResampleTaps);
            var shifter = new CwFrequencyShifter(model.Metadata.SampleRate);
            var tap = new SstvSpectrumTap(WaterfallRate, SpectrumRowsPerSecond, WaterfallMinHz, WaterfallMaxHz, WaterfallColumns);
            var toneAverage = new float[WaterfallColumns];
            var rows = new List<float[]>(4);
            var at8k = new List<float>(ChunkSamples);
            var atModel = new List<float>(ChunkSamples);
            var buffer = new float[ChunkSamples];
            var stall = new CaptureStallWatch(DateTime.UtcNow);
            _captureHealth = new CwCaptureHealth(true, false);

            while (!token.IsCancellationRequested)
            {
                if (_resetRequested)
                {
                    _resetRequested = false;
                    decoder.Reset();
                }

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
                    PublishSpectrum(rows);
                    UpdateTone(rows, toneAverage);
                }

                atModel.Clear();
                toModel.Process(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(at8k), atModel);
                shifter.ShiftHz = -FeedForwardHz;
                shifter.Process(atModel);
                decoder.Append(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(atModel));
                if (decoder.Step() is { } update)
                    TextDecoded?.Invoke(this, update);
            }

            var rest = decoder.Finish();
            TextDecoded?.Invoke(this, new CwDecodeUpdate(rest, CwLaneText.Empty));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CW live decode stopped");
            Faulted?.Invoke(this, ex);
        }
        finally
        {
            _inputSilent = false;
            Volatile.Write(ref _toneHz, double.NaN);
            Stopped?.Invoke(this, session);
        }
    }

    private void UpdateTone(List<float[]> rows, float[] average)
    {
        foreach (var row in rows)
        {
            for (var i = 0; i < average.Length && i < row.Length; i++)
            {
                var power = (float)Math.Pow(10, row[i] / 10.0);
                average[i] = average[i] == 0 ? power : average[i] + (float)ToneAverageAlpha * (power - average[i]);
            }
        }

        var binHz = (WaterfallMaxHz - WaterfallMinHz) / WaterfallColumns;
        var tone = CwToneFinder.Find(average, binHz);
        Volatile.Write(ref _toneHz, tone ?? double.NaN);
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
