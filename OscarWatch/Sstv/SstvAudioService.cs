using System.Collections.Concurrent;
using OscarWatch.Core.Services;
using OscarWatch.Recording;
using PortAudioSharp;
using Serilog;
using PaStream = PortAudioSharp.Stream;

namespace OscarWatch.Sstv;

/// <summary>PortAudio receive-only capture for OscarWatch SSTV (separate from FT4 and pass recording).</summary>
public sealed class SstvAudioService : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SstvAudioService>();
    private readonly object _gate = new();
    private readonly ConcurrentQueue<float> _captureRing = new();
    private PaStream? _input;
    private bool _portAudioReady;
    private int _captureSampleRate = 48000;
    private float _peak;

    public bool IsAvailable
    {
        get
        {
            EnsurePortAudio();
            return _portAudioReady;
        }
    }

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
        EnsurePortAudio();
        if (!_portAudioReady)
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
                var lowLatency = ResolveDeviceIndex(deviceId, deviceDisplayName, preferLowLatencyShared: true);
                var shared = ResolveDeviceIndex(deviceId, deviceDisplayName, preferLowLatencyShared: false);
                if (shared < 0 || shared == lowLatency)
                    throw;

                Log.Warning(ex, "SSTV low-latency capture open failed; retrying the shareable device");
                StopCaptureUnlocked();
                OpenCaptureUnlocked(deviceId, deviceDisplayName, preferLowLatencyShared: false);
            }
        }
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

    private void OpenCaptureUnlocked(string? deviceId, string? deviceDisplayName, bool preferLowLatencyShared)
    {
        var deviceIndex = ResolveDeviceIndex(deviceId, deviceDisplayName, preferLowLatencyShared);
        if (deviceIndex < 0)
            throw new InvalidOperationException("The SSTV input soundcard is no longer available. Re-select the input device.");

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
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        _input = new PaStream(
            inParams: param,
            outParams: null,
            sampleRate: _captureSampleRate,
            framesPerBuffer: 1024,
            streamFlags: StreamFlags.ClipOff,
            callback: OnInput,
            userData: IntPtr.Zero);
        _input.Start();
        Log.Information("SSTV capture started on '{Device}' (index {Index}) at {Rate} Hz", info.name, deviceIndex, _captureSampleRate);
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
            while (_captureRing.Count > _captureSampleRate * 30)
                _captureRing.TryDequeue(out _);
        }

        return StreamCallbackResult.Continue;
    }

    private void StopCaptureUnlocked()
    {
        try { _input?.Stop(); } catch { /* ignore */ }
        try { _input?.Dispose(); } catch { /* ignore */ }
        _input = null;
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
            Log.Warning(ex, "SSTV PortAudio init failed");
            _portAudioReady = false;
        }
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
    }
}
