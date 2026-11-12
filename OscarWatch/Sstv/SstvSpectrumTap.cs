using OscarWatch.Core.Ft4;

namespace OscarWatch.Sstv;

/// <summary>
/// Turns audio into spectrum rows at a steady rate of audio time, so a recording decoded
/// faster than real time still gives evenly spaced rows. Each row averages half-overlapping
/// FFTs across all the audio since the previous row; a single FFT covers under half a row
/// and lands on a different part of each SSTV line, so on its own rows flicker.
/// </summary>
public sealed class SstvSpectrumTap
{
    public const double MinHz = 900;
    public const double MaxHz = 2600;
    public const int Columns = 340;
    private const int DefaultWindow = 2048;

    private readonly int _windowLength;
    private readonly int _hop;
    private readonly float[] _window;
    private readonly float[] _bins;
    private readonly double[] _power;
    private readonly int _rate;
    private readonly int _interval;
    private readonly double _minHz;
    private readonly double _maxHz;
    private readonly int _columns;
    private int _filled;
    private int _sinceHop;
    private int _sinceRow;
    private int _segments;

    public SstvSpectrumTap(int sampleRate, double rowsPerSecond)
        : this(sampleRate, rowsPerSecond, MinHz, MaxHz, Columns)
    {
    }

    /// <summary>
    /// A tap over another audio range, for example the CW decoder's 0 to 2000 Hz. FFTs overlap by
    /// half unless <paramref name="hopSamples"/> is given; keep the hop at or under a row's worth of
    /// audio so every row holds at least one FFT.
    /// </summary>
    public SstvSpectrumTap(
        int sampleRate,
        double rowsPerSecond,
        double minHz,
        double maxHz,
        int columns,
        int windowSamples = DefaultWindow,
        int? hopSamples = null)
    {
        _rate = sampleRate;
        _windowLength = windowSamples;
        _hop = Math.Clamp(hopSamples ?? windowSamples / 2, 1, windowSamples);
        _window = new float[windowSamples];
        _interval = Math.Max(_hop, (int)Math.Round(sampleRate / rowsPerSecond));
        _minHz = minHz;
        _maxHz = maxHz;
        _columns = columns;
        _bins = new float[columns];
        _power = new double[columns];
    }

    /// <summary>Add audio and append every row (dB per column) that falls due to <paramref name="rows"/>.</summary>
    public void Add(ReadOnlySpan<float> samples, ICollection<float[]> rows)
    {
        while (!samples.IsEmpty)
        {
            var take = Math.Min(samples.Length, _hop - _sinceHop);
            Push(samples[..take]);
            samples = samples[take..];
            _sinceHop += take;
            _sinceRow += take;

            if (_sinceHop == _hop)
            {
                _sinceHop = 0;
                if (_filled == _windowLength && Ft4SpectrumAnalyzer.TryComputePassband(_window, _rate, _bins, _minHz, _maxHz))
                {
                    for (var i = 0; i < _columns; i++)
                        _power[i] += Math.Pow(10, _bins[i] / 10.0);
                    _segments++;
                }
            }

            if (_sinceRow < _interval)
                continue;

            _sinceRow -= _interval;
            if (_segments == 0)
                continue;

            var row = new float[_columns];
            for (var i = 0; i < _columns; i++)
                row[i] = (float)(10.0 * Math.Log10(Math.Max(_power[i] / _segments, 1e-10)));
            Array.Clear(_power);
            _segments = 0;
            rows.Add(row);
        }
    }

    private void Push(ReadOnlySpan<float> samples)
    {
        Array.Copy(_window, samples.Length, _window, 0, _windowLength - samples.Length);
        samples.CopyTo(_window.AsSpan(_windowLength - samples.Length));
        _filled = Math.Min(_windowLength, _filled + samples.Length);
    }
}
