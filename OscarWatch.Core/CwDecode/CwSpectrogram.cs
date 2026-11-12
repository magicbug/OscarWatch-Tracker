namespace OscarWatch.Core.CwDecode;

/// <summary>
/// The DeepCW model input: a magnitude spectrogram with reflect padding and a periodic Hann
/// window, cut to the model band and normalised against the noise level.
/// </summary>
public static class CwSpectrogram
{
    private const double NoiseLevelMinRelativeToPeak = 1e-3;
    private const double NoiseLevelMin = 1e-5;

    public static (int StartBin, int StopBin) FrequencyBinRange(CwModelMetadata m)
    {
        var binHz = (double)m.SampleRate / m.FftLength;
        var start = (int)Math.Ceiling(m.SpectrogramMinFreqHz / binHz);
        var stop = (int)Math.Floor(m.SpectrogramMaxFreqHz / binHz) + 1;
        return (start, stop);
    }

    /// <summary>Frames produced for <paramref name="sampleCount"/> samples of audio.</summary>
    public static int FrameCount(int sampleCount, CwModelMetadata m) => 1 + sampleCount / m.HopLength;

    /// <summary>
    /// Normalised spectrogram, row-major [frame, bin]. <paramref name="audio"/> must be at the model
    /// sample rate and at least one FFT long.
    /// </summary>
    public static float[] Compute(ReadOnlySpan<float> audio, CwModelMetadata m, out int frames)
    {
        var raw = ComputeMagnitudes(audio, m, out frames);
        Normalise(raw, m.NoiseLevelQuantile);
        return raw;
    }

    internal static float[] ComputeMagnitudes(ReadOnlySpan<float> audio, CwModelMetadata m, out int frames)
    {
        var n = m.FftLength;
        if (audio.Length < n)
            throw new ArgumentException($"Audio is too short for an FFT of {n}.", nameof(audio));

        var (startBin, stopBin) = FrequencyBinRange(m);
        var bins = stopBin - startBin;
        var pad = n / 2;
        frames = FrameCount(audio.Length, m);

        var window = PeriodicHann(n);
        var fft = new CwFft(n);
        var re = new double[n];
        var im = new double[n];
        var output = new float[frames * bins];

        for (var frame = 0; frame < frames; frame++)
        {
            var start = frame * m.HopLength;
            for (var i = 0; i < n; i++)
            {
                re[i] = ReflectSample(audio, start + i - pad) * window[i];
                im[i] = 0;
            }

            fft.Transform(re, im);
            var row = frame * bins;
            for (var b = 0; b < bins; b++)
            {
                var k = startBin + b;
                output[row + b] = (float)Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            }
        }

        return output;
    }

    /// <summary>In place: divide by the estimated noise level, then log1p.</summary>
    internal static void Normalise(float[] spectrogram, double quantile)
    {
        if (spectrogram.Length == 0)
            return;

        var sorted = (float[])spectrogram.Clone();
        Array.Sort(sorted);
        var rayleighFactor = Math.Sqrt(-2.0 * Math.Log(1.0 - quantile));
        var noise = Math.Max(
            sorted[(int)Math.Floor(quantile * (sorted.Length - 1))] / rayleighFactor,
            Math.Max(sorted[^1] * NoiseLevelMinRelativeToPeak, NoiseLevelMin));
        var scale = (float)noise;
        for (var i = 0; i < spectrogram.Length; i++)
            spectrogram[i] = MathF.Log(1f + spectrogram[i] / scale);
    }

    private static double[] PeriodicHann(int n)
    {
        var w = new double[n];
        for (var i = 0; i < n; i++)
            w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
        return w;
    }

    /// <summary>numpy <c>pad(mode="reflect")</c>: mirror about the edge sample without repeating it.</summary>
    private static float ReflectSample(ReadOnlySpan<float> audio, int index)
    {
        var last = audio.Length - 1;
        while (index < 0 || index > last)
        {
            if (index < 0)
                index = -index;
            if (index > last)
                index = 2 * last - index;
        }

        return audio[index];
    }
}

/// <summary>In-place radix-2 complex FFT.</summary>
internal sealed class CwFft
{
    private readonly int _n;
    private readonly double[] _cos;
    private readonly double[] _sin;
    private readonly int[] _reverse;

    public CwFft(int n)
    {
        if (n < 2 || (n & (n - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(n), "FFT length must be a power of two.");

        _n = n;
        _cos = new double[n / 2];
        _sin = new double[n / 2];
        for (var i = 0; i < n / 2; i++)
        {
            _cos[i] = Math.Cos(-2 * Math.PI * i / n);
            _sin[i] = Math.Sin(-2 * Math.PI * i / n);
        }

        _reverse = new int[n];
        var bits = (int)Math.Log2(n);
        for (var i = 0; i < n; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++)
                r |= ((i >> b) & 1) << (bits - 1 - b);
            _reverse[i] = r;
        }
    }

    public void Transform(double[] re, double[] im)
    {
        for (var i = 0; i < _n; i++)
        {
            var j = _reverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var size = 2; size <= _n; size <<= 1)
        {
            var half = size / 2;
            var step = _n / size;
            for (var start = 0; start < _n; start += size)
            {
                for (var k = 0; k < half; k++)
                {
                    var wr = _cos[k * step];
                    var wi = _sin[k * step];
                    var a = start + k;
                    var b = a + half;
                    var tr = re[b] * wr - im[b] * wi;
                    var ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }
}
