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
    private const int Window = 2048;
    private const int Hop = Window / 2;

    private readonly float[] _window = new float[Window];
    private readonly float[] _bins = new float[Columns];
    private readonly double[] _power = new double[Columns];
    private readonly int _rate;
    private readonly int _interval;
    private int _filled;
    private int _sinceHop;
    private int _sinceRow;
    private int _segments;

    public SstvSpectrumTap(int sampleRate, double rowsPerSecond)
    {
        _rate = sampleRate;
        _interval = Math.Max(Hop, (int)Math.Round(sampleRate / rowsPerSecond));
    }

    /// <summary>Add audio and append every row (dB per column) that falls due to <paramref name="rows"/>.</summary>
    public void Add(ReadOnlySpan<float> samples, ICollection<float[]> rows)
    {
        while (!samples.IsEmpty)
        {
            var take = Math.Min(samples.Length, Hop - _sinceHop);
            Push(samples[..take]);
            samples = samples[take..];
            _sinceHop += take;
            _sinceRow += take;

            if (_sinceHop == Hop)
            {
                _sinceHop = 0;
                if (_filled == Window && Ft4SpectrumAnalyzer.TryComputePassband(_window, _rate, _bins, MinHz, MaxHz))
                {
                    for (var i = 0; i < Columns; i++)
                        _power[i] += Math.Pow(10, _bins[i] / 10.0);
                    _segments++;
                }
            }

            if (_sinceRow < _interval)
                continue;

            _sinceRow -= _interval;
            if (_segments == 0)
                continue;

            var row = new float[Columns];
            for (var i = 0; i < Columns; i++)
                row[i] = (float)(10.0 * Math.Log10(Math.Max(_power[i] / _segments, 1e-10)));
            Array.Clear(_power);
            _segments = 0;
            rows.Add(row);
        }
    }

    private void Push(ReadOnlySpan<float> samples)
    {
        Array.Copy(_window, samples.Length, _window, 0, Window - samples.Length);
        samples.CopyTo(_window.AsSpan(Window - samples.Length));
        _filled = Math.Min(Window, _filled + samples.Length);
    }
}
