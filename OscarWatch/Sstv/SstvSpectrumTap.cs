using OscarWatch.Core.Ft4;

namespace OscarWatch.Sstv;

/// <summary>
/// Keeps the most recent audio and turns it into spectrum rows at a steady rate of
/// audio time, so a recording decoded faster than real time still gives evenly spaced rows.
/// </summary>
public sealed class SstvSpectrumTap
{
    public const double MinHz = 900;
    public const double MaxHz = 2600;
    public const int Columns = 340;
    private const int Window = 2048;

    private readonly float[] _window = new float[Window];
    private readonly int _rate;
    private readonly int _interval;
    private int _filled;
    private int _sinceRow;

    public SstvSpectrumTap(int sampleRate, double rowsPerSecond)
    {
        _rate = sampleRate;
        _interval = Math.Max(1, (int)Math.Round(sampleRate / rowsPerSecond));
    }

    /// <summary>Add audio. Returns a new row (dB per column) when one is due, otherwise null.</summary>
    public float[]? Add(ReadOnlySpan<float> samples)
    {
        if (samples.Length >= Window)
        {
            samples[^Window..].CopyTo(_window);
        }
        else
        {
            Array.Copy(_window, samples.Length, _window, 0, Window - samples.Length);
            samples.CopyTo(_window.AsSpan(Window - samples.Length));
        }

        _filled = Math.Min(Window, _filled + samples.Length);
        _sinceRow += samples.Length;
        if (_sinceRow < _interval || _filled < Window)
            return null;

        _sinceRow %= _interval;
        var row = new float[Columns];
        return Ft4SpectrumAnalyzer.TryComputePassband(_window, _rate, row, MinHz, MaxHz) ? row : null;
    }
}
