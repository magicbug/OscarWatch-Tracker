namespace OscarWatch.Core.CwDecode;

/// <summary>
/// Moves every audio frequency by a set number of hertz (single-sideband shift through a Hilbert
/// transformer). Used to put back the note offset left when rig control tunes in steps.
/// The output is always delayed by half the filter length, shifted or not.
/// </summary>
public sealed class CwFrequencyShifter
{
    private const int Taps = 63;
    private readonly double[] _hilbert = new double[Taps];
    private readonly double[] _history = new double[Taps];
    private readonly int _sampleRate;
    private int _historyPos;
    private double _phase;

    public CwFrequencyShifter(int sampleRate)
    {
        _sampleRate = sampleRate;
        var mid = (Taps - 1) / 2;
        for (var i = 0; i < Taps; i++)
        {
            var n = i - mid;
            if (n % 2 == 0)
                continue;

            var window = 0.42
                - 0.5 * Math.Cos(2 * Math.PI * i / (Taps - 1))
                + 0.08 * Math.Cos(4 * Math.PI * i / (Taps - 1));
            _hilbert[i] = 2.0 / (Math.PI * n) * window;
        }
    }

    /// <summary>Hertz added to every frequency. Negative moves the note down.</summary>
    public double ShiftHz { get; set; }

    public void Process(List<float> samples)
    {
        var step = 2 * Math.PI * ShiftHz / _sampleRate;
        var mid = (Taps - 1) / 2;
        for (var s = 0; s < samples.Count; s++)
        {
            _history[_historyPos] = samples[s];
            _historyPos = (_historyPos + 1) % Taps;

            var quadrature = 0.0;
            var idx = _historyPos;
            for (var k = 0; k < Taps; k++)
            {
                quadrature += _hilbert[Taps - 1 - k] * _history[idx];
                if (++idx == Taps)
                    idx = 0;
            }

            var inPhase = _history[(_historyPos + mid) % Taps];
            samples[s] = step == 0
                ? (float)inPhase
                : (float)(inPhase * Math.Cos(_phase) - quadrature * Math.Sin(_phase));

            _phase += step;
            if (_phase > Math.PI)
                _phase -= 2 * Math.PI;
            else if (_phase < -Math.PI)
                _phase += 2 * Math.PI;
        }
    }
}
