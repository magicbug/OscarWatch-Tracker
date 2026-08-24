using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OscarWatch.Core.Recording;
using OscarWatch.Core.Services;
using OscarWatch.Recording;
using PortAudioSharp;
using Serilog;

namespace OscarWatch.Services;

/// <summary>
/// Plays the same generated ding on Windows, macOS, and Linux.
/// Prefers OS players / SoundPlayer, then ffmpeg, then PortAudio output.
/// </summary>
public sealed class PlatformAlertSoundService : IAlertSoundService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PlatformAlertSoundService>();
    private static readonly TimeSpan PlayerTimeout = TimeSpan.FromSeconds(4);
    private const uint MbIconAsterisk = 0x00000040;

    private readonly FfmpegLocator? _ffmpegLocator;
    private readonly object _portAudioGate = new();
    private bool _portAudioReady;
    private bool _portAudioFailed;

    public PlatformAlertSoundService(FfmpegLocator? ffmpegLocator = null)
    {
        _ffmpegLocator = ffmpegLocator;
    }

    public void PlayAlert()
    {
        // Never block the UI thread (Tick / Settings Test sound).
        ThreadPool.QueueUserWorkItem(static state =>
        {
            try
            {
                ((PlatformAlertSoundService)state!).PlayAlertCore();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Scheduled pass alert sound failed");
            }
        }, this);
    }

    private void PlayAlertCore()
    {
        try
        {
            var wav = AlertToneWav.Create();

            if (OperatingSystem.IsWindows() && TryPlayWindows(wav))
                return;

            if (OperatingSystem.IsMacOS() && TryPlayWithTempWav(wav, "/usr/bin/afplay"))
                return;

            if (OperatingSystem.IsLinux())
            {
                if (TryPlayWithTempWav(wav, "pw-play")
                    || TryPlayWithTempWav(wav, "paplay")
                    || TryPlayWithTempWav(wav, "aplay", "-q"))
                    return;
            }

            if (TryPlayWithFfmpeg(wav))
                return;

            if (TryPlayWithPortAudio())
                return;

            if (OperatingSystem.IsWindows())
            {
                MessageBeep(MbIconAsterisk);
                return;
            }

            Log.Warning("Scheduled pass alert sound could not be played on this system");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Scheduled pass alert sound failed");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryPlayWindows(byte[] wav)
    {
        try
        {
            using var ms = new MemoryStream(wav, writable: false);
            using var player = new System.Media.SoundPlayer(ms);
            player.PlaySync();
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Windows SoundPlayer alert failed");
            return false;
        }
    }

    private bool TryPlayWithFfmpeg(byte[] wav)
    {
        if (_ffmpegLocator is null)
            return false;

        FfmpegProbeResult probe;
        try
        {
            probe = _ffmpegLocator.Probe();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ffmpeg probe for alert sound failed");
            return false;
        }

        if (!probe.IsAvailable || string.IsNullOrWhiteSpace(probe.ExecutablePath))
            return false;

        var path = WriteTempWav(wav);
        try
        {
            if (TryRun("ffplay", PlayerTimeout, "-nodisp", "-autoexit", "-loglevel", "quiet", path))
                return true;

            if (OperatingSystem.IsLinux())
            {
                if (TryRun(probe.ExecutablePath, PlayerTimeout,
                        "-nostdin", "-hide_banner", "-loglevel", "error",
                        "-i", path, "-f", "pulse", "default"))
                    return true;

                if (TryRun(probe.ExecutablePath, PlayerTimeout,
                        "-nostdin", "-hide_banner", "-loglevel", "error",
                        "-i", path, "-f", "alsa", "default"))
                    return true;
            }

            return false;
        }
        finally
        {
            TryDelete(path);
        }
    }

    private bool TryPlayWithPortAudio()
    {
        lock (_portAudioGate)
        {
            if (_portAudioFailed)
                return false;

            try
            {
                if (!_portAudioReady)
                {
                    if (!PortAudioOutOfProcessProbe.TryRun(out var probeError))
                    {
                        Log.Debug("PortAudio probe failed for alert sound: {Reason}", probeError);
                        _portAudioFailed = true;
                        return false;
                    }

                    PortAudio.Initialize();
                    _portAudioReady = true;
                }

                return PlayPcmViaPortAudio(AlertToneWav.CreatePcm16());
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "PortAudio alert playback failed");
                _portAudioFailed = true;
                return false;
            }
        }
    }

    private static bool PlayPcmViaPortAudio(short[] pcm)
    {
        var device = PortAudio.DefaultOutputDevice;
        if (device < 0)
            return false;

        var info = PortAudio.GetDeviceInfo(device);
        var channels = Math.Clamp(info.maxOutputChannels, 1, 2);
        var sampleRate = AlertToneWav.SampleRate;
        if (info.defaultSampleRate is > 8000 and < 192000)
            sampleRate = (int)Math.Round(info.defaultSampleRate);

        var playBuffer = sampleRate == AlertToneWav.SampleRate
            ? pcm
            : Resample(pcm, AlertToneWav.SampleRate, sampleRate);

        var offset = 0;
        var finished = new ManualResetEventSlim(false);
        var scratch = new short[4096 * channels];

        StreamCallbackResult Callback(
            IntPtr inputPtr,
            IntPtr outputPtr,
            uint frameCount,
            ref StreamCallbackTimeInfo timeInfo,
            StreamCallbackFlags statusFlags,
            IntPtr userData)
        {
            if (outputPtr == IntPtr.Zero)
            {
                finished.Set();
                return StreamCallbackResult.Complete;
            }

            var frames = (int)frameCount;
            var samplesNeeded = frames * channels;
            if (samplesNeeded > scratch.Length)
                samplesNeeded = scratch.Length - scratch.Length % channels;

            var framesToWrite = samplesNeeded / channels;
            for (var i = 0; i < framesToWrite; i++)
            {
                short sample = 0;
                if (offset < playBuffer.Length)
                    sample = playBuffer[offset++];

                for (var c = 0; c < channels; c++)
                    scratch[i * channels + c] = sample;
            }

            Marshal.Copy(scratch, 0, outputPtr, framesToWrite * channels);

            if (offset >= playBuffer.Length)
            {
                finished.Set();
                return StreamCallbackResult.Complete;
            }

            return StreamCallbackResult.Continue;
        }

        var param = new StreamParameters
        {
            device = device,
            channelCount = channels,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = info.defaultLowOutputLatency
        };

        using var stream = new PortAudioSharp.Stream(
            inParams: null,
            outParams: param,
            sampleRate: sampleRate,
            framesPerBuffer: 256,
            streamFlags: StreamFlags.ClipOff,
            callback: Callback,
            userData: IntPtr.Zero);

        stream.Start();
        finished.Wait(PlayerTimeout);
        try
        {
            stream.Stop();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "PortAudio alert stream stop failed");
        }

        return offset > 0;
    }

    private static short[] Resample(short[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0)
            return input;

        var outLen = (int)((long)input.Length * toRate / fromRate);
        var output = new short[Math.Max(1, outLen)];
        for (var i = 0; i < output.Length; i++)
        {
            var src = i * (double)fromRate / toRate;
            var i0 = (int)src;
            var i1 = Math.Min(i0 + 1, input.Length - 1);
            var frac = src - i0;
            output[i] = (short)(input[i0] * (1 - frac) + input[i1] * frac);
        }

        return output;
    }

    private static bool TryPlayWithTempWav(byte[] wav, string fileName, params string[] extraArgs)
    {
        var path = WriteTempWav(wav);
        try
        {
            var args = new string[extraArgs.Length + 1];
            extraArgs.CopyTo(args, 0);
            args[^1] = path;
            return TryRun(fileName, PlayerTimeout, args);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static string WriteTempWav(byte[] wav)
    {
        var path = Path.Combine(Path.GetTempPath(), $"oscarwatch-ding-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, wav);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static bool TryRun(string fileName, TimeSpan timeout, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return false;

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Alert sound process {File} failed", fileName);
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint uType);
}
