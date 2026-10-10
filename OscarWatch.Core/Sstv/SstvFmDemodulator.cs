namespace OscarWatch.Core.Sstv;

/// <summary>
/// Streaming quadrature FM discriminator for the SSTV subcarrier. Mixes 1900 Hz down
/// to baseband, low-passes I and Q to drop the image at 3800 Hz and out-of-band noise,
/// then reports the instantaneous audio frequency for every input sample.
/// </summary>
public sealed class SstvFmDemodulator
{
    public const double CentreHz = 1900.0;

    /// <summary>
    /// Lowest and highest frequency reported, so a noise click cannot swamp a pixel average.
    /// Wide enough for a 1100 Hz VIS bit or 2300 Hz white tuned 400 Hz off.
    /// </summary>
    public const float MinReportedHz = 600f;

    public const float MaxReportedHz = 3200f;

    private readonly int _sampleRate;
    private readonly double _omega;
    private readonly double[] _taps;
    private readonly double[] _iHistory;
    private readonly double[] _qHistory;
    private int _historyPos;
    private double _phase;
    private double _prevI;
    private double _prevQ;

    public SstvFmDemodulator(int sampleRate, double bandwidthHz = 1200)
    {
        _sampleRate = sampleRate;
        _omega = 2 * Math.PI * CentreHz / sampleRate;
        var taps = Math.Clamp((int)(sampleRate / 120.0) | 1, 15, 255);
        _taps = SstvFirFilter.LowPass(bandwidthHz, sampleRate, taps);
        _iHistory = new double[_taps.Length];
        _qHistory = new double[_taps.Length];
    }

    /// <summary>Filter delay in samples: output n describes input n minus this.</summary>
    public double DelaySamples => (_taps.Length - 1) / 2.0;

    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        var n = _taps.Length;
        var scale = _sampleRate / (2 * Math.PI);
        foreach (var sample in input)
        {
            _iHistory[_historyPos] = sample * Math.Cos(_phase);
            _qHistory[_historyPos] = -sample * Math.Sin(_phase);
            _historyPos = (_historyPos + 1) % n;
            _phase += _omega;
            if (_phase > 2 * Math.PI)
                _phase -= 2 * Math.PI;

            double i = 0, q = 0;
            var idx = _historyPos;
            for (var k = 0; k < n; k++)
            {
                i += _taps[k] * _iHistory[idx];
                q += _taps[k] * _qHistory[idx];
                idx++;
                if (idx == n)
                    idx = 0;
            }

            var dphi = Math.Atan2(q * _prevI - i * _prevQ, i * _prevI + q * _prevQ);
            _prevI = i;
            _prevQ = q;
            var hz = (float)(CentreHz + dphi * scale);
            output.Add(Math.Clamp(hz, MinReportedHz, MaxReportedHz));
        }
    }
}
