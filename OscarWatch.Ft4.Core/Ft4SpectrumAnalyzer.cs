using OscarWatch.Ft4.Core.Native;

namespace OscarWatch.Ft4.Core;

public sealed class Ft4SpectrumAnalyzer
{
    private const int FftSize = 4096;
    private const int HopSize = Ft4Constants.SamplingRateHz / 4;
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _accumulator = new float[FftSize];
    private readonly float[] _real = new float[FftSize];
    private readonly float[] _imag = new float[FftSize];
    private readonly float[] _magnitude = new float[FftSize / 2];
    private int _accumulated;

    public int RowWidth { get; }
    public int MaxFrequencyHz { get; }
    public float Brightness { get; set; } = 1.0f;
    public float Contrast { get; set; } = 1.0f;

    public event Action<byte[]>? SpectrumRowReady;

    public Ft4SpectrumAnalyzer(int rowWidth, int maxFrequencyHz = 3500)
    {
        RowWidth = rowWidth;
        MaxFrequencyHz = maxFrequencyHz;
        for (var i = 0; i < FftSize; i++)
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));
    }

    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _accumulator[_accumulated++] = sample;
            if (_accumulated < HopSize)
                continue;

            ShiftAccumulator();
            ComputeRow();
            _accumulated = 0;
        }
    }

    private void ShiftAccumulator()
    {
        Array.Copy(_accumulator, HopSize, _accumulator, 0, FftSize - HopSize);
        Array.Clear(_accumulator, FftSize - HopSize, HopSize);
    }

    private void ComputeRow()
    {
        for (var i = 0; i < FftSize; i++)
        {
            _real[i] = _accumulator[i] * _window[i];
            _imag[i] = 0;
        }

        FftInPlace(_real, _imag);

        for (var i = 0; i < _magnitude.Length; i++)
            _magnitude[i] = (float)Math.Log10(1e-12 + Math.Sqrt(_real[i] * _real[i] + _imag[i] * _imag[i]));

        var row = new byte[RowWidth];
        var hzPerBin = (double)Ft4Constants.SamplingRateHz / FftSize;
        for (var x = 0; x < RowWidth; x++)
        {
            var freq = x * MaxFrequencyHz / Math.Max(1, RowWidth - 1);
            var bin = (int)(freq / hzPerBin);
            bin = Math.Clamp(bin, 0, _magnitude.Length - 1);
            var value = (_magnitude[bin] + 5.0f) * Contrast * Brightness;
            row[x] = (byte)Math.Clamp((int)(value * 40), 0, 255);
        }

        SpectrumRowReady?.Invoke(row);
    }

    private static void FftInPlace(float[] real, float[] imag)
    {
        var n = real.Length;
        var j = 0;
        for (var i = 0; i < n; i++)
        {
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }

            var m = n >> 1;
            while (m >= 1 && j >= m)
            {
                j -= m;
                m >>= 1;
            }
            j += m;
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2.0 * Math.PI / len;
            var wLenCos = (float)Math.Cos(angle);
            var wLenSin = (float)Math.Sin(angle);
            for (var i = 0; i < n; i += len)
            {
                var wCos = 1f;
                var wSin = 0f;
                for (var k = 0; k < len / 2; k++)
                {
                    var uReal = real[i + k];
                    var uImag = imag[i + k];
                    var vReal = real[i + k + len / 2] * wCos - imag[i + k + len / 2] * wSin;
                    var vImag = real[i + k + len / 2] * wSin + imag[i + k + len / 2] * wCos;
                    real[i + k] = uReal + vReal;
                    imag[i + k] = uImag + vImag;
                    real[i + k + len / 2] = uReal - vReal;
                    imag[i + k + len / 2] = uImag - vImag;
                    var nextCos = wCos * wLenCos - wSin * wLenSin;
                    wSin = wCos * wLenSin + wSin * wLenCos;
                    wCos = nextCos;
                }
            }
        }
    }
}
