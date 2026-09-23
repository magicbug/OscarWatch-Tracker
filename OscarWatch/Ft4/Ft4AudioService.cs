using System.Collections.Concurrent;
using System.Diagnostics;
using OscarWatch.Core.Services;
using OscarWatch.Recording;
using PortAudioSharp;
using Serilog;
using PaStream = PortAudioSharp.Stream;

namespace OscarWatch.Ft4;

/// <summary>PortAudio capture + playback for the FT4 modem (separate from pass recording).</summary>
public sealed class Ft4AudioService : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<Ft4AudioService>();
    private readonly object _gate = new();
    private readonly ConcurrentQueue<float> _captureRing = new();
    private readonly float[] _monitorRing = new float[8192];
    private int _monitorWrite;
    private int _monitorCount;
    private readonly object _monitorGate = new();
    private PaStream? _input;
    private PaStream? _output;
    private int _outputDeviceIndex = -1;
    private int _outputChannels = 1;
    private TxBuffer? _tx;
    private float _playbackPeak;

    private sealed class TxBuffer
    {
        public TxBuffer(float[] samples) => Samples = samples;

        public float[] Samples { get; }
        public int Index;
        public int FirstToneIndex = -1;
        public long PublishedTimestamp;
        public int ToneLogged;
        /// <summary>When true, playback wraps so a Tune tone stays up until stopped.</summary>
        public bool Loop;
    }
    private bool _portAudioReady;
    private int _captureSampleRate = 48000;
    private int _playbackSampleRate = 48000;

    public bool IsAvailable
    {
        get
        {
            EnsurePortAudio();
            return _portAudioReady;
        }
    }

    public int CaptureSampleRate => _captureSampleRate;
    public int PlaybackSampleRate => _playbackSampleRate;

    /// <summary>Recent TX playback peak (0–1), decays when idle.</summary>
    public double PlaybackPeak
    {
        get
        {
            var peak = Volatile.Read(ref _playbackPeak);
            // Soft decay so the meter falls after the burst ends.
            var next = peak * 0.92f;
            if (next < 0.01f)
                next = 0f;
            Volatile.Write(ref _playbackPeak, next);
            return Math.Clamp(peak, 0, 1);
        }
    }

    public IReadOnlyList<AudioInputDevice> GetInputDevices()
    {
        EnsurePortAudio();
        if (!_portAudioReady)
            return [];

        var candidates = new List<RecordingDeviceCandidate>();
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxInputChannels <= 0)
                continue;
            candidates.Add(new RecordingDeviceCandidate(
                i,
                info.name ?? $"Input {i}",
                info.defaultLowInputLatency));
        }

        return RecordingDeviceListBuilder.Build(candidates);
    }

    public IReadOnlyList<AudioInputDevice> GetOutputDevices()
    {
        EnsurePortAudio();
        if (!_portAudioReady)
            return [];

        var candidates = new List<RecordingDeviceCandidate>();
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxOutputChannels <= 0)
                continue;
            candidates.Add(new RecordingDeviceCandidate(
                i,
                info.name ?? $"Output {i}",
                info.defaultLowOutputLatency));
        }

        return RecordingDeviceListBuilder.Build(candidates);
    }

    public void StartCapture(string? deviceId, string? deviceDisplayName = null)
    {
        lock (_gate)
        {
            EnsurePortAudio();
            if (!_portAudioReady)
                throw new InvalidOperationException("PortAudio is not available.");

            StopCaptureUnlocked();
            while (_captureRing.TryDequeue(out _))
            {
            }

            try
            {
                OpenCaptureUnlocked(deviceId, deviceDisplayName, preferLowLatencyShared: true);
            }
            catch (Exception ex)
            {
                var lowLatency = ResolveDeviceIndex(deviceId, deviceDisplayName, input: true, preferLowLatencyShared: true);
                var shared = ResolveDeviceIndex(deviceId, deviceDisplayName, input: true, preferLowLatencyShared: false);
                if (shared < 0 || shared == lowLatency)
                    throw;

                Log.Warning(ex, "FT4 low-latency capture open failed; retrying the shareable device");
                StopCaptureUnlocked();
                OpenCaptureUnlocked(deviceId, deviceDisplayName, preferLowLatencyShared: false);
            }
        }
    }

    private void OpenCaptureUnlocked(string? deviceId, string? deviceDisplayName, bool preferLowLatencyShared)
    {
        var deviceIndex = ResolveDeviceIndex(deviceId, deviceDisplayName, input: true, preferLowLatencyShared);
        if (deviceIndex < 0)
        {
            throw new InvalidOperationException(
                "FT4 input soundcard is no longer available. " +
                "Open FT4 Settings, click Refresh, and re-select the input device.");
        }

        var info = PortAudio.GetDeviceInfo(deviceIndex);
        _captureSampleRate = (int)Math.Round(info.defaultSampleRate);
        if (_captureSampleRate < 8000)
            _captureSampleRate = 48000;

        var param = new StreamParameters
        {
            device = deviceIndex,
            channelCount = 1,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };

        _input = new PaStream(
            inParams: param,
            outParams: null,
            sampleRate: _captureSampleRate,
            framesPerBuffer: 256,
            streamFlags: StreamFlags.ClipOff,
            callback: OnInput,
            userData: IntPtr.Zero);
        _input.Start();
        Log.Information(
            "FT4 capture started on '{Device}' (index {Index}) at {Rate} Hz",
            info.name,
            deviceIndex,
            _captureSampleRate);
    }

    public void StopCapture()
    {
        lock (_gate)
            StopCaptureUnlocked();
    }

    public int ReadCaptureSamples(Span<float> destination)
    {
        var n = 0;
        while (n < destination.Length && _captureRing.TryDequeue(out var sample))
            destination[n++] = sample;
        return n;
    }

    /// <summary>Copy the most recent monitor samples without consuming the decode queue.</summary>
    public int CopyMonitorSamples(Span<float> destination)
    {
        lock (_monitorGate)
        {
            var n = Math.Min(destination.Length, _monitorCount);
            if (n == 0)
                return 0;

            var start = (_monitorWrite - n + _monitorRing.Length) % _monitorRing.Length;
            for (var i = 0; i < n; i++)
                destination[i] = _monitorRing[(start + i) % _monitorRing.Length];
            return n;
        }
    }

    /// <summary>
    /// Open the TX output and leave it running with silence. Called when the modem starts
    /// so the soundcard is already awake at the next slot boundary.
    /// </summary>
    public void StartOutput(string? deviceId, string? deviceDisplayName = null)
    {
        lock (_gate)
        {
            EnsurePortAudio();
            if (!_portAudioReady)
                return;

            try
            {
                EnsureOutputUnlocked(deviceId, deviceDisplayName);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "FT4 output open failed");
            }
        }
    }

    /// <summary>
    /// Resample and scale a 12 kHz burst for the open output. Does not start playback.
    /// Used so the slot boundary only has to hand the buffer to the soundcard.
    /// </summary>
    public bool TryPreparePlayback(
        float[] samples12k,
        double level,
        string? deviceId,
        string? deviceDisplayName,
        out float[] devicePcm,
        out int sampleRate)
    {
        devicePcm = [];
        sampleRate = 0;
        lock (_gate)
        {
            try
            {
                EnsurePortAudio();
                if (!_portAudioReady)
                    return false;

                EnsureOutputUnlocked(deviceId, deviceDisplayName);
                devicePcm = ScaleForOutput(samples12k, level);
                sampleRate = _playbackSampleRate;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "FT4 prepare playback failed");
                return false;
            }
        }
    }

    public void PlayPcm(float[] samples12k, double level, string? deviceId, string? deviceDisplayName = null)
    {
        lock (_gate)
        {
            EnsurePortAudio();
            if (!_portAudioReady)
                throw new InvalidOperationException("PortAudio is not available.");

            EnsureOutputUnlocked(deviceId, deviceDisplayName);
            PublishPlayback(ScaleForOutput(samples12k, level), loop: false);
        }
    }

    /// <summary>
    /// Continuous sine at <paramref name="hz"/> for WSJT-X-style Tune.
    /// Stays up until <see cref="StopPlayback"/> (or a later one-shot burst replaces it).
    /// </summary>
    public void StartContinuousTone(
        double hz,
        double level,
        string? deviceId,
        string? deviceDisplayName = null)
    {
        if (!double.IsFinite(hz))
            hz = 1500;
        hz = Math.Clamp(hz, 200, 3000);

        lock (_gate)
        {
            EnsurePortAudio();
            if (!_portAudioReady)
                throw new InvalidOperationException("PortAudio is not available.");

            EnsureOutputUnlocked(deviceId, deviceDisplayName);
            var rate = _playbackSampleRate;
            if (rate < 8000)
                rate = 48000;

            // One second of tone; the output callback wraps Index while Loop is set.
            var samples = new float[rate];
            var gain = (float)Math.Clamp(level, 0.01, 1.0);
            var omega = 2.0 * Math.PI * hz / rate;
            for (var i = 0; i < samples.Length; i++)
                samples[i] = (float)(gain * Math.Sin(omega * i));

            PublishPlayback(samples, loop: true);
        }
    }

    /// <summary>Start a buffer already at the output sample rate. Returns false if the stream is not at that rate.</summary>
    public bool TryPlayPrepared(float[] devicePcm, int sampleRate)
    {
        lock (_gate)
        {
            if (_output is null || _playbackSampleRate != sampleRate || devicePcm.Length == 0)
                return false;

            PublishPlayback(devicePcm, loop: false);
            return true;
        }
    }

    private float[] ScaleForOutput(float[] samples12k, double level)
    {
        var scaled = Resample(samples12k, 12000, _playbackSampleRate);
        var gain = (float)Math.Clamp(level, 0.01, 1.0);
        for (var i = 0; i < scaled.Length; i++)
            scaled[i] *= gain;
        return scaled;
    }

    private void PublishPlayback(float[] devicePcm, bool loop)
    {
        var firstTone = devicePcm.Length;
        for (var i = 0; i < devicePcm.Length; i++)
        {
            if (Math.Abs(devicePcm[i]) >= 0.02f)
            {
                firstTone = i;
                break;
            }
        }

        Volatile.Write(ref _playbackPeak, 0f);
        // The callback only advances Index, so swapping the reference is the handoff.
        Volatile.Write(ref _tx, new TxBuffer(devicePcm)
        {
            FirstToneIndex = firstTone,
            PublishedTimestamp = Stopwatch.GetTimestamp(),
            Loop = loop
        });
    }

    public bool IsPlaying
    {
        get
        {
            var tx = Volatile.Read(ref _tx);
            if (tx is null)
                return false;
            if (tx.Loop)
                return true;
            return tx.Index < tx.Samples.Length;
        }
    }

    /// <summary>Drop the current burst. The output stream stays open so the next slot does not cold-start.</summary>
    public void StopPlayback()
    {
        lock (_gate)
            SilencePlaybackUnlocked();
    }

    public void StopOutput()
    {
        lock (_gate)
            StopOutputUnlocked();
    }

    private StreamCallbackResult OnInput(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userData)
    {
        if (input == IntPtr.Zero)
            return StreamCallbackResult.Continue;

        unsafe
        {
            var ptr = (float*)input.ToPointer();
            for (var i = 0; i < frameCount; i++)
            {
                var sample = ptr[i];
                _captureRing.Enqueue(sample);
                // Bound memory if the consumer falls behind.
                while (_captureRing.Count > _captureSampleRate * 20)
                    _captureRing.TryDequeue(out _);
            }

            lock (_monitorGate)
            {
                for (var i = 0; i < frameCount; i++)
                {
                    _monitorRing[_monitorWrite] = ptr[i];
                    _monitorWrite = (_monitorWrite + 1) % _monitorRing.Length;
                    if (_monitorCount < _monitorRing.Length)
                        _monitorCount++;
                }
            }
        }

        return StreamCallbackResult.Continue;
    }

    private StreamCallbackResult OnOutput(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userData)
    {
        if (output == IntPtr.Zero)
            return StreamCallbackResult.Complete;

        unsafe
        {
            var ptr = (float*)output.ToPointer();
            var channels = _outputChannels;
            if (channels < 1)
                channels = 1;
            var tx = Volatile.Read(ref _tx);

            for (var frame = 0; frame < frameCount; frame++)
            {
                var sample = 0f;
                if (tx is not null)
                {
                    var i = tx.Index;
                    if (i >= tx.Samples.Length)
                    {
                        if (tx.Loop && tx.Samples.Length > 0)
                        {
                            i = 0;
                            tx.Index = 0;
                        }
                    }

                    if (i < tx.Samples.Length)
                    {
                        sample = tx.Samples[i];
                        tx.Index = i + 1;
                        if (i == tx.FirstToneIndex
                            && Interlocked.CompareExchange(ref tx.ToneLogged, 1, 0) == 0)
                        {
                            var sincePublishMs = Stopwatch.GetElapsedTime(tx.PublishedTimestamp).TotalMilliseconds;
                            var leadInMs = _playbackSampleRate > 0
                                ? i * 1000.0 / _playbackSampleRate
                                : 0;
                            var dacMs = (timeInfo.outputBufferDacTime - timeInfo.currentTime) * 1000.0;
                            Log.Information(
                                "FT4 first tone sample: lead-in {LeadInMs:0} ms, callback {CallbackMs:0} ms after playback start, soundcard holds it {DacMs:0} ms",
                                leadInMs,
                                sincePublishMs,
                                dacMs);
                        }
                    }
                }

                var dest = frame * channels;
                for (var c = 0; c < channels; c++)
                    ptr[dest + c] = sample;

                var abs = Math.Abs(sample);
                if (abs > _playbackPeak)
                    _playbackPeak = abs;
            }
        }

        // Keep the stream running. Closing it at the end of each burst makes the
        // next transmit wait while the soundcard wakes up.
        return StreamCallbackResult.Continue;
    }

    private void StopCaptureUnlocked()
    {
        try { _input?.Stop(); } catch { /* ignore */ }
        try { _input?.Dispose(); } catch { /* ignore */ }
        _input = null;
    }

    private void SilencePlaybackUnlocked()
    {
        Volatile.Write(ref _tx, null);
        Volatile.Write(ref _playbackPeak, 0f);
    }

    private void EnsureOutputUnlocked(string? deviceId, string? deviceDisplayName)
    {
        var deviceIndex = ResolveDeviceIndex(deviceId, deviceDisplayName, input: false);
        if (deviceIndex < 0)
        {
            throw new InvalidOperationException(
                "FT4 output soundcard is no longer available. " +
                "Open FT4 Settings, click Refresh, and re-select the output device.");
        }

        if (_output is not null && _outputDeviceIndex == deviceIndex)
            return;

        StopOutputUnlocked();

        var info = PortAudio.GetDeviceInfo(deviceIndex);
        _playbackSampleRate = (int)Math.Round(info.defaultSampleRate);
        if (_playbackSampleRate < 8000)
            _playbackSampleRate = 48000;

        try
        {
            TryOpenOutputUnlocked(deviceIndex, info);
        }
        catch (Exception ex)
        {
            var shared = ResolveDeviceIndex(deviceId, deviceDisplayName, input: false, preferLowLatencyShared: false);
            if (shared < 0 || shared == deviceIndex)
                throw;

            Log.Warning(ex, "FT4 low-latency output open failed; retrying the shareable device");
            StopOutputUnlocked();
            var sharedInfo = PortAudio.GetDeviceInfo(shared);
            _playbackSampleRate = (int)Math.Round(sharedInfo.defaultSampleRate);
            if (_playbackSampleRate < 8000)
                _playbackSampleRate = 48000;
            TryOpenOutputUnlocked(shared, sharedInfo);
        }
    }

    private void TryOpenOutputUnlocked(int deviceIndex, DeviceInfo info)
    {
        try
        {
            OpenOutputUnlocked(deviceIndex, info, channels: 1);
        }
        catch (Exception ex) when (info.maxOutputChannels >= 2)
        {
            Log.Warning(ex, "FT4 mono output open failed; retrying stereo");
            OpenOutputUnlocked(deviceIndex, info, channels: 2);
        }
    }

    private void OpenOutputUnlocked(int deviceIndex, DeviceInfo info, int channels)
    {
        var param = new StreamParameters
        {
            device = deviceIndex,
            channelCount = channels,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowOutputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };

        PaStream? stream = null;
        try
        {
            stream = new PaStream(
                inParams: null,
                outParams: param,
                sampleRate: _playbackSampleRate,
                framesPerBuffer: PortAudio.FramesPerBufferUnspecified,
                streamFlags: StreamFlags.ClipOff,
                callback: OnOutput,
                userData: IntPtr.Zero);
            stream.Start();
            _output = stream;
            _outputChannels = channels;
            _outputDeviceIndex = deviceIndex;
            stream = null;
        }
        finally
        {
            if (stream is not null)
            {
                try { stream.Dispose(); } catch { /* open failed */ }
            }
        }

        Log.Information(
            "FT4 output open on '{Device}' index {Index} hostApi {HostApi} type {HostApiType} at {Rate} Hz, {Channels} ch, low latency {LatencyMs} ms",
            info.name,
            deviceIndex,
            info.hostApi,
            PortAudioHostApi.GetTypeId(info.hostApi),
            _playbackSampleRate,
            channels,
            info.defaultLowOutputLatency * 1000);
    }

    private void StopOutputUnlocked()
    {
        SilencePlaybackUnlocked();
        try { _output?.Stop(); } catch { /* ignore */ }
        try { _output?.Dispose(); } catch { /* ignore */ }
        _output = null;
        _outputDeviceIndex = -1;
        _outputChannels = 1;
    }

    private void EnsurePortAudio()
    {
        if (_portAudioReady)
            return;

        try
        {
            if (!PortAudioOutOfProcessProbe.TryRun(out _))
            {
                _portAudioReady = false;
                return;
            }

            PortAudio.Initialize();
            _portAudioReady = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FT4 PortAudio init failed");
            _portAudioReady = false;
        }
    }

    private static int ResolveDeviceIndex(
        string? deviceId,
        string? deviceDisplayName,
        bool input,
        bool preferLowLatencyShared = true)
    {
        if (string.IsNullOrWhiteSpace(deviceId) && string.IsNullOrWhiteSpace(deviceDisplayName))
            return input ? PortAudio.DefaultInputDevice : PortAudio.DefaultOutputDevice;

        var snapshots = new List<RecordingDeviceResolver.InputDeviceSnapshot>();
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            var channels = input ? info.maxInputChannels : info.maxOutputChannels;
            if (channels <= 0)
                continue;
            var latency = input ? info.defaultLowInputLatency : info.defaultLowOutputLatency;
            snapshots.Add(new RecordingDeviceResolver.InputDeviceSnapshot(
                i,
                info.name ?? "",
                latency,
                channels,
                PortAudioHostApi.GetTypeId(info.hostApi)));
        }

        // WASAPI when it is available: shared with other apps, and much faster to start
        // than the MME/DirectSound copy the pass-recording list prefers.
        return RecordingDeviceResolver.ResolveIndex(
            deviceId,
            deviceDisplayName,
            snapshots,
            preferLowLatencyShared);
    }

    private static float[] Resample(float[] input, int inRate, int outRate)
    {
        if (inRate == outRate)
            return (float[])input.Clone();

        var outLen = (int)((long)input.Length * outRate / inRate);
        var output = new float[outLen];
        for (var i = 0; i < outLen; i++)
        {
            var srcPos = i * (double)inRate / outRate;
            var i0 = (int)srcPos;
            var i1 = Math.Min(i0 + 1, input.Length - 1);
            var frac = srcPos - i0;
            output[i] = (float)(input[i0] * (1 - frac) + input[i1] * frac);
        }

        return output;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopCaptureUnlocked();
            StopOutputUnlocked();
        }
    }
}
