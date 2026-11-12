using OscarWatch.Core.Sstv;

namespace OscarWatch.Core.CwDecode;

/// <summary>
/// Streaming low-pass and linear-interpolation resampler. The low-pass runs at the input rate,
/// so nothing above the cut-off can fold back into the band the model reads.
/// </summary>
public sealed class CwResampler
{
    private readonly double _step;
    private readonly double[] _taps;
    private readonly double[] _history;
    private int _historyPos;
    private double _previous;
    private double _phase = 1.0;

    public CwResampler(int inputRate, int outputRate, double cutoffHz, int taps)
    {
        if (inputRate <= 0 || outputRate <= 0 || outputRate > inputRate)
            throw new ArgumentOutOfRangeException(nameof(outputRate), "Only downsampling is supported.");

        InputRate = inputRate;
        OutputRate = outputRate;
        _step = (double)inputRate / outputRate;
        _taps = SstvFirFilter.LowPass(cutoffHz, inputRate, taps);
        _history = new double[_taps.Length];
    }

    public int InputRate { get; }
    public int OutputRate { get; }

    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
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
                if (++idx == n)
                    idx = 0;
            }

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
