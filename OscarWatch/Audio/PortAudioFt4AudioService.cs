using System.Runtime.InteropServices;
using OscarWatch.Ft4.Core.Models;
using OscarWatch.Ft4.Core.Native;
using OscarWatch.Ft4.Core.Services;
using PortAudioSharp;
using Serilog;

namespace OscarWatch.Audio;

public sealed class PortAudioFt4AudioService : IFt4AudioService, IDisposable
{
    private const uint FallbackFramesPerBuffer = 1024;
    private const int OutputRingSamples = Ft4Constants.SamplingRateHz * 4;

    private static readonly ILogger Log = Serilog.Log.ForContext<PortAudioFt4AudioService>();

    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _outputLock = new();
    private readonly object _streamLock = new();

    private bool _initialized;
    private string? _initError;
    private PortAudioSharp.Stream? _inputStream;
    private PortAudioSharp.Stream? _outputStream;
    private float[] _outputRing = new float[OutputRingSamples];
    private int _outputRead;
    private int _outputWrite;
    private int _outputCount;
    private byte[]? _inputScratchBytes;
    private short[]? _outputScratchShort;

    public bool IsAvailable => _initialized;
    public string? UnavailableReason => _initialized ? null : _initError ?? "PortAudio is not available.";
    public bool IsRunning { get; private set; }

    public event Action<float[], DateTime>? InputSamples;

    public PortAudioFt4AudioService()
    {
        try
        {
            PortAudio.Initialize();
            _initialized = true;
        }
        catch (Exception ex)
        {
            _initError = ex.Message;
            Log.Warning(ex, "PortAudio initialization failed for FT4 audio");
        }
    }

    public IReadOnlyList<Ft4AudioDevice> ListInputDevices() => ListDevices(input: true);

    public IReadOnlyList<Ft4AudioDevice> ListOutputDevices() => ListDevices(input: false);

    public async Task StartAsync(string inputDeviceId, string outputDeviceId, CancellationToken cancellationToken = default)
    {
        if (!_initialized)
            throw new InvalidOperationException(UnavailableReason ?? "PortAudio is not available.");

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
                await StopCoreAsync().ConfigureAwait(false);

            if (!int.TryParse(inputDeviceId, out var inputIndex))
                throw new InvalidOperationException($"Invalid FT4 input device id '{inputDeviceId}'.");
            if (!int.TryParse(outputDeviceId, out var outputIndex))
                throw new InvalidOperationException($"Invalid FT4 output device id '{outputDeviceId}'.");

            var inputInfo = PortAudio.GetDeviceInfo(inputIndex);
            var outputInfo = PortAudio.GetDeviceInfo(outputIndex);
            if (inputInfo.maxInputChannels < 1)
                throw new InvalidOperationException($"Device '{inputInfo.name}' has no input channels.");
            if (outputInfo.maxOutputChannels < 1)
                throw new InvalidOperationException($"Device '{outputInfo.name}' has no output channels.");

            _inputScratchBytes = new byte[8192 * 2];
            _outputScratchShort = new short[8192];
            ClearOutputBuffer();

            lock (_streamLock)
            {
                _inputStream = OpenInputStream(inputIndex, inputInfo);
                _outputStream = OpenOutputStream(outputIndex, outputInfo);
                _inputStream.Start();
                _outputStream.Start();
            }

            IsRunning = true;
            Log.Information("FT4 audio started (in={Input}, out={Output})", inputInfo.name, outputInfo.name);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void EnqueueOutputSamples(ReadOnlySpan<float> samples)
    {
        lock (_outputLock)
        {
            foreach (var sample in samples)
            {
                _outputRing[_outputWrite] = sample;
                _outputWrite = (_outputWrite + 1) % _outputRing.Length;
                if (_outputCount < _outputRing.Length)
                    _outputCount++;
                else
                    _outputRead = (_outputRead + 1) % _outputRing.Length;
            }
        }
    }

    public int OutputBufferedSamples
    {
        get
        {
            lock (_outputLock)
                return _outputCount;
        }
    }

    public void ClearOutputBuffer()
    {
        lock (_outputLock)
        {
            _outputRead = 0;
            _outputWrite = 0;
            _outputCount = 0;
            Array.Clear(_outputRing);
        }
    }

    public void Dispose()
    {
        _ = StopAsync();
        _operationLock.Dispose();
    }

    private async Task StopCoreAsync()
    {
        if (!IsRunning)
            return;

        lock (_streamLock)
        {
            try { _inputStream?.Stop(); } catch (Exception ex) { Log.Debug(ex, "FT4 input stream stop"); }
            try { _outputStream?.Stop(); } catch (Exception ex) { Log.Debug(ex, "FT4 output stream stop"); }
            try { _inputStream?.Dispose(); } catch (Exception ex) { Log.Debug(ex, "FT4 input stream dispose"); }
            try { _outputStream?.Dispose(); } catch (Exception ex) { Log.Debug(ex, "FT4 output stream dispose"); }
            _inputStream = null;
            _outputStream = null;
        }

        ClearOutputBuffer();
        IsRunning = false;
        await Task.CompletedTask;
    }

    private PortAudioSharp.Stream OpenInputStream(int deviceIndex, DeviceInfo deviceInfo)
    {
        var input = new StreamParameters
        {
            device = deviceIndex,
            channelCount = 1,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = deviceInfo.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        return new PortAudioSharp.Stream(
            inParams: input,
            outParams: null,
            sampleRate: Ft4Constants.SamplingRateHz,
            framesPerBuffer: FallbackFramesPerBuffer,
            streamFlags: StreamFlags.ClipOff,
            callback: OnInputCallback,
            userData: IntPtr.Zero);
    }

    private PortAudioSharp.Stream OpenOutputStream(int deviceIndex, DeviceInfo deviceInfo)
    {
        var output = new StreamParameters
        {
            device = deviceIndex,
            channelCount = 1,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = deviceInfo.defaultLowOutputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        return new PortAudioSharp.Stream(
            inParams: null,
            outParams: output,
            sampleRate: Ft4Constants.SamplingRateHz,
            framesPerBuffer: FallbackFramesPerBuffer,
            streamFlags: StreamFlags.ClipOff,
            callback: OnOutputCallback,
            userData: IntPtr.Zero);
    }

    private StreamCallbackResult OnInputCallback(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userData)
    {
        if (input == IntPtr.Zero || _inputScratchBytes is null)
            return StreamCallbackResult.Continue;

        var sampleCount = (int)frameCount;
        var byteCount = sampleCount * 2;
        if (_inputScratchBytes.Length < byteCount)
            _inputScratchBytes = new byte[byteCount];

        Marshal.Copy(input, _inputScratchBytes, 0, byteCount);
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BitConverter.ToInt16(_inputScratchBytes, i * 2);
            samples[i] = sample / 32768f;
        }

        try
        {
            InputSamples?.Invoke(samples, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "FT4 input callback handler failed");
        }

        return StreamCallbackResult.Continue;
    }

    private StreamCallbackResult OnOutputCallback(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userData)
    {
        if (output == IntPtr.Zero || _outputScratchShort is null)
            return StreamCallbackResult.Continue;

        var count = (int)frameCount;
        if (_outputScratchShort.Length < count)
            _outputScratchShort = new short[count];

        lock (_outputLock)
        {
            for (var i = 0; i < count; i++)
            {
                float sample = 0;
                if (_outputCount > 0)
                {
                    sample = _outputRing[_outputRead];
                    _outputRead = (_outputRead + 1) % _outputRing.Length;
                    _outputCount--;
                }

                sample = Math.Clamp(sample, -1f, 1f);
                _outputScratchShort[i] = (short)(sample * 32767f);
            }
        }

        Marshal.Copy(_outputScratchShort, 0, output, count);
        return StreamCallbackResult.Continue;
    }

    private static IReadOnlyList<Ft4AudioDevice> ListDevices(bool input)
    {
        var devices = new List<Ft4AudioDevice>();
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (input && info.maxInputChannels <= 0)
                continue;
            if (!input && info.maxOutputChannels <= 0)
                continue;
            devices.Add(new Ft4AudioDevice(i.ToString(), info.name));
        }

        return devices;
    }
}
