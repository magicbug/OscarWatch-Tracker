using OscarWatch.Ft4.Core.Coding;
using OscarWatch.Ft4.Core.Models;
using OscarWatch.Ft4.Core.Native;
using OscarWatch.Ft4.Core.Services;

namespace OscarWatch.Ft4.Core;

public enum Ft4SenderMode { Off, Tuning, Sending }

public sealed class Ft4Engine : IDisposable
{
    private const int PttOnMarginMs = 150;
    private const int PttOffMarginMs = 300;
    private const int LeadSampleCount = Ft4Constants.SamplingRateHz;
    private const int RampSampleCount = Ft4Constants.SamplesPerSymbol;

    private readonly IFt4Coder _coder;
    private readonly IFt4AudioService _audio;
    private readonly Ft4QsoSequencer _sequencer;
    private readonly object _stateLock = new();

    private float[] _received = [];
    private int _sampleCount;
    private DateTime _dataUtc = DateTime.UtcNow;
    private long _decodedSlotNumber = -1;

    private Ft4SenderMode _senderMode = Ft4SenderMode.Off;
    private Thread? _senderThread;
    private volatile bool _senderStopping;
    private float[] _waveform = new float[Ft4Constants.EncodeSampleCount];
    private float[] _ramp = new float[RampSampleCount];
    private int _waveformIndex;
    private readonly float[] _txScratch = new float[LeadSampleCount];
    private readonly float[] _silence = new float[LeadSampleCount];

    public Ft4SpectrumAnalyzer? Spectrum { get; private set; }
    public Ft4QsoSequencer Sequencer => _sequencer;
    public bool IsRunning { get; private set; }
    public bool IsNativeCoder => _coder.IsNative;
    public Ft4SenderMode SenderMode => _senderMode;
    public bool IsTransmitting { get; private set; }

    public int RxAudioFrequencyHz { get; set; } = Ft4Constants.DefaultAudioFrequencyHz;
    public int TxAudioFrequencyHz { get; set; } = Ft4Constants.DefaultAudioFrequencyHz;
    public int CutoffFrequencyHz { get; set; } = 4000;
    public bool TxOdd { get; set; } = true;
    public float TxGain { get; set; } = 1.0f;
    public string? StatusText { get; private set; }

    public event Action<Ft4DecodeLine>? MessageDecoded;
    public event Action<string>? StatusChanged;
    public event Action? StateChanged;

    public Ft4Engine(IFt4Coder coder, IFt4AudioService audio, string callsign, string gridSquare)
    {
        _coder = coder;
        _audio = audio;
        _sequencer = new Ft4QsoSequencer(callsign, gridSquare);
        GenerateRamp();
    }

    public async Task StartAsync(Ft4Settings settings, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            return;

        RxAudioFrequencyHz = settings.RxAudioFrequencyHz;
        TxAudioFrequencyHz = settings.TxAudioFrequencyHz;
        CutoffFrequencyHz = settings.CutoffFrequencyHz;
        TxOdd = settings.TxOdd;
        TxGain = settings.TxGain;
        _sequencer.MyCall = settings.Callsign;
        _sequencer.MySquare = settings.GridSquare;

        Spectrum = new Ft4SpectrumAnalyzer(800, settings.CutoffFrequencyHz);
        _audio.InputSamples += OnInputSamples;
        await _audio.StartAsync(settings.InputDeviceId, settings.OutputDeviceId, cancellationToken).ConfigureAwait(false);
        IsRunning = true;
        SetStatus(_coder.IsNative ? "FT4 running (native codec)" : "FT4 running (simulated codec)");
        StateChanged?.Invoke();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
            return;

        StopSender();
        _audio.InputSamples -= OnInputSamples;
        await _audio.StopAsync(cancellationToken).ConfigureAwait(false);
        IsRunning = false;
        _sampleCount = 0;
        Spectrum = null;
        SetStatus("FT4 stopped");
        StateChanged?.Invoke();
    }

    public void SetTxMessage(string message)
    {
        var buffer = Ft4MessageBuffer.FormatMessage(message);
        var tone = (float)(TxAudioFrequencyHz - ComputeXitOffset());
        _coder.Encode(buffer, tone, _waveform);
        ApplyTxGain(_waveform);
        _waveformIndex = 0;
        AddTxLine(message);
    }

    public void ApplySequencerMessage()
    {
        if (_sequencer.Message is { } message)
            SetTxMessage(message);
    }

    public void StartTuning() => SetSenderMode(Ft4SenderMode.Tuning);
    public void StartSending() => SetSenderMode(Ft4SenderMode.Sending);
    public void HaltTx() => SetSenderMode(Ft4SenderMode.Off);

    public Ft4Slot GetCurrentSlot() => new() { Utc = DateTime.UtcNow };

    public void Dispose()
    {
        StopSender();
        if (IsRunning)
            _ = StopAsync();
    }

    private void OnInputSamples(float[] samples, DateTime utc)
    {
        Spectrum?.Process(samples);
        _dataUtc = utc;
        AppendSamples(samples);

        while (TryExtractSlot(out var slotSamples))
            DecodeSlot(slotSamples);
    }

    private void AppendSamples(float[] samples)
    {
        lock (_stateLock)
        {
            var needed = _sampleCount + samples.Length;
            if (_received.Length < needed)
                Array.Resize(ref _received, needed);
            Array.Copy(samples, 0, _received, _sampleCount, samples.Length);
            _sampleCount += samples.Length;
        }
    }

    private bool TryExtractSlot(out float[] slotSamples)
    {
        lock (_stateLock)
        {
            var slot = new Ft4Slot { Utc = _dataUtc };
            var slotStartIndex = _sampleCount - slot.SamplesIntoSlot;
            var slotEndIndex = slotStartIndex + Ft4Constants.DecodeSampleCount;

            if (slotStartIndex < 0 || slotEndIndex > _sampleCount)
            {
                slotSamples = [];
                return false;
            }

            if (slot.SlotNumber == _decodedSlotNumber)
            {
                slotSamples = [];
                return false;
            }

            slotSamples = new float[Ft4Constants.DecodeSampleCount];
            Array.Copy(_received, slotStartIndex, slotSamples, 0, Ft4Constants.DecodeSampleCount);

            var keep = _sampleCount - slotEndIndex;
            Array.Copy(_received, slotEndIndex, _received, 0, keep);
            _sampleCount = keep;
            _decodedSlotNumber = slot.SlotNumber;
            return true;
        }
    }

    private void DecodeSlot(float[] samples)
    {
        var max = samples.Max(Math.Abs);
        if (max > 0)
        {
            for (var i = 0; i < samples.Length; i++)
                samples[i] /= max;
        }

        var slot = new Ft4Slot { Utc = _dataUtc };
        var stage = Ft4QsoStage.Calling;
        var rx = RxAudioFrequencyHz;
        var cutoff = CutoffFrequencyHz;
        var myCall = Ft4MessageBuffer.FormatCall(_sequencer.MyCall);
        var hisCall = Ft4MessageBuffer.FormatCall(_sequencer.HisCall ?? "          ");

        _coder.Decode(
            samples,
            ref stage,
            ref rx,
            ref cutoff,
            myCall,
            hisCall,
            raw =>
            {
                var line = Ft4DecodeLine.FromNativeCallback(raw, _dataUtc, slot.SlotNumber);
                MessageDecoded?.Invoke(line);
                _sequencer.ProcessMessage(line, forceReply: false);
            });

        RxAudioFrequencyHz = rx;
        CutoffFrequencyHz = cutoff;
    }

    private void AddTxLine(string message)
    {
        var slot = GetCurrentSlot();
        MessageDecoded?.Invoke(new Ft4DecodeLine
        {
            Utc = DateTime.UtcNow,
            SlotNumber = slot.SlotNumber,
            IsTransmit = true,
            RawText = message,
            Message = message,
            FrequencyHz = TxAudioFrequencyHz,
        });
    }

    private void SetSenderMode(Ft4SenderMode mode)
    {
        if (_senderMode == mode)
            return;

        if (mode == Ft4SenderMode.Off)
        {
            StopSender();
            _senderMode = Ft4SenderMode.Off;
            IsTransmitting = false;
            StateChanged?.Invoke();
            return;
        }

        if (_senderMode != Ft4SenderMode.Off)
            return;

        _senderStopping = false;
        _senderThread = new Thread(mode == Ft4SenderMode.Tuning ? TuneLoop : SendLoop)
        {
            IsBackground = true,
            Name = "Ft4Sender",
            Priority = ThreadPriority.AboveNormal,
        };
        _senderThread.Start();
        _senderMode = mode;
        StateChanged?.Invoke();
    }

    private void StopSender()
    {
        if (_senderThread is null)
            return;

        _senderStopping = true;
        _senderThread.Join(TimeSpan.FromSeconds(2));
        _senderThread = null;
        _audio.ClearOutputBuffer();
    }

    private void TuneLoop()
    {
        IsTransmitting = true;
        StateChanged?.Invoke();
        Thread.Sleep(PttOnMarginMs);

        var phase = 0.0;
        var tone = TxAudioFrequencyHz - ComputeXitOffset();
        var phaseInc = 2.0 * Math.PI * tone / Ft4Constants.SamplingRateHz;

        while (!_senderStopping)
        {
            var needed = Math.Max(0, LeadSampleCount - _audio.OutputBufferedSamples);
            if (needed <= 0)
            {
                Thread.Sleep(10);
                continue;
            }

            needed = Math.Min(needed, _txScratch.Length);
            for (var i = 0; i < needed; i++)
            {
                _txScratch[i] = (float)Math.Sin(phase) * TxGain * 0.5f;
                phase += phaseInc;
                if (phase > 2 * Math.PI)
                    phase -= 2 * Math.PI;
            }

            _audio.EnqueueOutputSamples(_txScratch.AsSpan(0, needed));
            Thread.Sleep(5);
        }

        _audio.ClearOutputBuffer();
        Thread.Sleep(PttOffMarginMs);
        IsTransmitting = false;
        _senderMode = Ft4SenderMode.Off;
        StateChanged?.Invoke();
    }

    private void SendLoop()
    {
        _waveformIndex = 0;
        _audio.ClearOutputBuffer();
        var slot = new Ft4Slot();

        while (!_senderStopping)
        {
            slot.Utc = DateTime.UtcNow;
            var startTime = slot.GetTxStartTime(TxOdd);
            var now = DateTime.UtcNow;

            if (now < startTime)
            {
                var waitSamples = (int)((startTime - now).TotalSeconds * Ft4Constants.SamplingRateHz);
                if (waitSamples > 0 && waitSamples < LeadSampleCount * 4)
                    _audio.EnqueueOutputSamples(_silence.AsSpan(0, Math.Min(waitSamples, _silence.Length)));
            }
            else if (_waveformIndex < _waveform.Length)
            {
                if (!IsTransmitting)
                {
                    IsTransmitting = true;
                    StateChanged?.Invoke();
                    Thread.Sleep(PttOnMarginMs);
                }

                var chunk = Math.Min(1024, _waveform.Length - _waveformIndex);
                _audio.EnqueueOutputSamples(_waveform.AsSpan(_waveformIndex, chunk));
                _waveformIndex += chunk;
            }
            else if (_audio.OutputBufferedSamples == 0)
            {
                Thread.Sleep(PttOffMarginMs);
                IsTransmitting = false;
                _waveformIndex = 0;
                ApplySequencerMessage();
                StateChanged?.Invoke();
            }

            Thread.Sleep(10);
        }

        IsTransmitting = false;
        _senderMode = Ft4SenderMode.Off;
        StateChanged?.Invoke();
    }

    private void GenerateRamp()
    {
        for (var i = 0; i < _ramp.Length; i++)
            _ramp[i] = (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / _ramp.Length));
    }

    private void ApplyTxGain(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
            samples[i] *= TxGain;
    }

    private int ComputeXitOffset()
    {
        var hz = TxAudioFrequencyHz % 1000;
        var wholeKhz = TxAudioFrequencyHz - hz;
        return wholeKhz - 1000;
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        StatusChanged?.Invoke(text);
    }
}
