namespace OscarWatch.Core.Sstv;

/// <summary>
/// Streaming conversion from the soundcard rate to <see cref="SstvDecoder.InternalRate"/>.
/// A low-pass at the input rate removes everything above the SSTV band before
/// linear interpolation, so the interpolation cannot alias.
/// </summary>
public sealed class SstvResampler
{
    private readonly double _step;
    private readonly double[] _taps;
    private readonly double[] _history;
    private int _historyPos;
    private double _previous;
    private double _phase;
    private readonly bool _passThrough;

    public SstvResampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputRate));

        InputRate = inputRate;
        _passThrough = inputRate == outputRate;
        _step = (double)inputRate / outputRate;
        var cutoff = Math.Min(3400.0, outputRate * 0.45);
        var taps = Math.Clamp((int)(inputRate / 400.0) | 1, 31, 255);
        _taps = SstvFirFilter.LowPass(cutoff, inputRate, taps);
        _history = new double[_taps.Length];
        _phase = 1.0;
    }

    public int InputRate { get; }

    /// <summary>Output delay in output samples, so callers can align time stamps.</summary>
    public double DelayOutputSamples => _passThrough ? 0 : (_taps.Length - 1) / 2.0 / _step;

    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        if (_passThrough)
        {
            foreach (var s in input)
                output.Add(s);
            return;
        }

        var n = _taps.Length;
        foreach (var sample in input)
        {
            _history[_historyPos] = sample;
            _historyPos = (_historyPos + 1) % n;

            var acc = 0.0;
            var idx = _historyPos;
            for (var k = 0; k < n; k++)
            {
                acc += _taps[k] * _history[idx];
                idx++;
                if (idx == n)
                    idx = 0;
            }

            // _phase is the output position measured from the previous filtered sample (0..1].
            while (_phase <= 1.0)
            {
                output.Add((float)(_previous + (acc - _previous) * _phase));
                _phase += _step;
            }

            _phase -= 1.0;
            _previous = acc;
        }
    }
}
