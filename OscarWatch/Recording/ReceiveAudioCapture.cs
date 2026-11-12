using System.Collections.Concurrent;
using OscarWatch.Core.Services;
using OscarWatch.Recording;
using PortAudioSharp;
using Serilog;
using PaStream = PortAudioSharp.Stream;

namespace OscarWatch.Recording;

/// <summary>
/// PortAudio receive-only capture for a decoder window (separate from FT4 and pass recording).
/// Each decoder owns its own instance so they can listen to different soundcards.
/// </summary>
public abstract class ReceiveAudioCapture : IDisposable
{
    private readonly ILogger Log;
    private readonly string _label;
    private readonly object _gate = new();
    private readonly ConcurrentQueue<float> _captureRing = new();
    private PaStream? _input;
    private const int FramesPerBuffer = 1024;
    /// <summary>Host buffer, in callback buffers. A smaller one overruns on some ALSA devices.</summary>
    private const int HostBuffers = 4;
    private int _captureSampleRate = 48000;
    private float _peak;
    private int _overflows;
    private long _droppedSamples;

    /// <param name="label">Decoder name for log lines, for example <c>SSTV</c>.</param>
    protected ReceiveAudioCapture(string label)
    {
        _label = label;
        Log = Serilog.Log.ForContext(GetType());
    }

    public bool IsAvailable => PortAudioRuntime.EnsureInitialized();

    public bool IsCapturing
    {
        get
        {
            lock (_gate)
                return _input is not null;
        }
    }

    public int CaptureSampleRate => _captureSampleRate;

    /// <summary>Recent input peak (0 to 1). Reading it lets it decay, for a level meter.</summary>
    public double InputPeak
    {
        get
        {
            var peak = Volatile.Read(ref _peak);
            Volatile.Write(ref _peak, peak * 0.85f);
            return Math.Clamp(peak, 0, 1);
        }
    }

    public IReadOnlyList<AudioInputDevice> GetInputDevices()
    {
        if (!PortAudioRuntime.EnsureInitialized())
            return [];

        var candidates = new List<RecordingDeviceCandidate>();
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxInputChannels <= 0)
                continue;
            candidates.Add(new RecordingDeviceCandidate(i, info.name ?? $"Input {i}", info.defaultLowInputLatency));
        }

        return RecordingDeviceListBuilder.Build(candidates);
    }

    public void StartCapture(string? deviceId, string? deviceDisplayName)
    {
        lock (_gate)
        {
            if (!PortAudioRuntime.EnsureInitialized())
                throw new InvalidOperationException("PortAudio is not available.");

            StopCaptureUnlocked();
            while (_captureRing.TryDequeue(out _))
            {
            }

            try
            {
                OpenCaptureWithFallback(deviceId, deviceDisplayName);
            }
            catch (Exception ex) when (TryRefreshAndReopen(deviceId, deviceDisplayName, ex))
            {
            }
        }
    }

    private void OpenCaptureWithFallback(string? deviceId, string? deviceDisplayName)
    {
        try
        {
            OpenCaptureUnlocked(deviceId, deviceDisplayName, preferLowLatencyShared: true);
        }
        catch (Exception ex)
        {
            var lowLatency = ResolveDeviceIndex(deviceId, deviceDisplayName, preferLowLatencyShared: true);
            var shared = ResolveDeviceIndex(deviceId, deviceDisplayName, preferLowLatencyShared: false);
            if (shared < 0 || shared == lowLatency)
                throw;

            Log.Warning(ex, "{Decoder} low-latency capture open failed; retrying the shareable device", _label);
            StopCaptureUnlocked();
            OpenCaptureUnlocked(deviceId, deviceDisplayName, preferLowLatencyShared: false);
        }
    }

    private bool TryRefreshAndReopen(string? deviceId, string? deviceDisplayName, Exception firstError)
    {
        StopCaptureUnlocked();
        if (!PortAudioRuntime.TryRefreshHost())
            throw firstError;

        Log.Information("{Decoder} retrying capture after refreshing the PortAudio host", _label);
        try
        {
            OpenCaptureWithFallback(deviceId, deviceDisplayName);
            return true;
        }
        catch (Exception retryEx)
        {
            throw new AggregateException(firstError, retryEx);
        }
    }

    public void StopCapture()
    {
        lock (_gate)
            StopCaptureUnlocked();
    }

    /// <summary>
    /// Audio lost since the last call: callbacks where the soundcard reported an input overflow,
    /// and samples dropped because decoding fell more than 30 s behind.
    /// </summary>
    public (int Overflows, long DroppedSamples) TakeLostAudio() =>
        (Interlocked.Exchange(ref _overflows, 0), Interlocked.Exchange(ref _droppedSamples, 0));

    public int ReadCaptureSamples(Span<float> destination)
    {
        var n = 0;
        while (n < destination.Length && _captureRing.TryDequeue(out var sample))
            destination[n++] = sample;
        return n;
    }

    private void OpenCaptureUnlocked(string? deviceId, string? deviceDisplayName, bool preferLowLatencyShared)
    {
        var deviceIndex = ResolveDeviceIndex(deviceId, deviceDisplayName, preferLowLatencyShared);
        if (deviceIndex < 0)
            throw new InvalidOperationException($"The {_label} input soundcard is no longer available. Re-select the input device.");

        var info = PortAudio.GetDeviceInfo(deviceIndex);
        _captureSampleRate = (int)Math.Round(info.defaultSampleRate);
        if (_captureSampleRate < 8000)
            _captureSampleRate = 48000;

        var param = new StreamParameters
        {
            device = deviceIndex,
            channelCount = 1,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = Math.Max(info.defaultHighInputLatency, HostBuffers * FramesPerBuffer / (double)_captureSampleRate),
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };
        Interlocked.Exchange(ref _overflows, 0);
        Interlocked.Exchange(ref _droppedSamples, 0);

        try
        {
            _input = new PaStream(
                inParams: param,
                outParams: null,
                sampleRate: _captureSampleRate,
                framesPerBuffer: FramesPerBuffer,
                streamFlags: StreamFlags.ClipOff,
                callback: OnInput,
                userData: IntPtr.Zero);
            _input.Start();
            PortAudioRuntime.NotifyStreamOpened();
        }
        catch
        {
            try { _input?.Dispose(); } catch { /* ignore */ }
            _input = null;
            throw;
        }

        Log.Information(
            "{Decoder} capture started on '{Device}' (index {Index}) at {Rate} Hz, latency asked {Latency:0} ms",
            _label, info.name, deviceIndex, _captureSampleRate, param.suggestedLatency * 1000);
    }

    private StreamCallbackResult OnInput(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userData)
    {
        if ((statusFlags & StreamCallbackFlags.InputOverflow) != 0)
            Interlocked.Increment(ref _overflows);
        if (input == IntPtr.Zero)
            return StreamCallbackResult.Continue;

        unsafe
        {
            var ptr = (float*)input.ToPointer();
            var peak = Volatile.Read(ref _peak);
            for (var i = 0; i < frameCount; i++)
            {
                var sample = ptr[i];
                _captureRing.Enqueue(sample);
                var abs = Math.Abs(sample);
                if (abs > peak)
                    peak = abs;
            }

            Volatile.Write(ref _peak, peak);
            // Bound memory if the decoder falls behind (30 s).
            var dropped = 0;
            while (_captureRing.Count > _captureSampleRate * 30 && _captureRing.TryDequeue(out _))
                dropped++;
            if (dropped > 0)
                Interlocked.Add(ref _droppedSamples, dropped);
        }

        return StreamCallbackResult.Continue;
    }

    private void StopCaptureUnlocked()
    {
        if (_input is null)
            return;

        try { _input.Stop(); } catch { /* ignore */ }
        try { _input.Dispose(); } catch { /* ignore */ }
        _input = null;
        PortAudioRuntime.NotifyStreamClosed();
    }

    private static int ResolveDeviceIndex(string? deviceId, string? deviceDisplayName, bool preferLowLatencyShared)
    {
        if (string.IsNullOrWhiteSpace(deviceId) && string.IsNullOrWhiteSpace(deviceDisplayName))
            return PortAudio.DefaultInputDevice;

        var snapshots = new List<RecordingDeviceResolver.InputDeviceSnapshot>();
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxInputChannels <= 0)
                continue;
            snapshots.Add(new RecordingDeviceResolver.InputDeviceSnapshot(
                i,
                info.name ?? "",
                info.defaultLowInputLatency,
                info.maxInputChannels,
                PortAudioHostApi.GetTypeId(info.hostApi)));
        }

        return RecordingDeviceResolver.ResolveIndex(deviceId, deviceDisplayName, snapshots, preferLowLatencyShared);
    }

    public void Dispose()
    {
        lock (_gate)
            StopCaptureUnlocked();
        GC.SuppressFinalize(this);
    }
}
