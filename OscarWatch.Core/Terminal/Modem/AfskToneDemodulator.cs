namespace OscarWatch.Core.Terminal.Modem;

/// <summary>
/// Quadrature tone correlators and delay-line discriminator on band-passed audio at
/// <paramref name="MarkHz"/> and <paramref name="SpaceHz"/>. Uses phase-continuous NCOs so
/// arbitrary tone offsets (not only 1200/2200 Hz) stay coherent across sample boundaries.
/// </summary>
public sealed class AfskToneDemodulator
{
    private const int DiscriminatorDelay = 2;
    private const int DiscriminatorWindow = 10;

    private readonly double _markCosStep;
    private readonly double _markSinStep;
    private readonly double _spaceCosStep;
    private readonly double _spaceSinStep;
    private readonly float _discriminatorThreshold;
    private readonly AfskDemodulator.Correlator[] _correlators;
    private double _markRe = 1.0;
    private double _markIm;
    private double _spaceRe = 1.0;
    private double _spaceIm;
    private int _ncoCounter;
    private readonly float[] _discDelay = new float[DiscriminatorDelay];
    private int _discDelayIndex;
    private readonly AfskDemodulator.MovingSum _discProduct = new(DiscriminatorWindow);
    private readonly AfskDemodulator.MovingSum _discPower = new(DiscriminatorWindow);
    private readonly AfskDemodulator.BiquadLowPass _discLpf = new(1600, AfskDemodulator.SampleRate);

    public AfskToneDemodulator(
        double markHz,
        double spaceHz,
        IReadOnlyList<int> correlatorWindows,
        IReadOnlyList<double>? postLpfCutoffsHz = null)
    {
        MarkHz = markHz;
        SpaceHz = spaceHz;
        var markStep = 2 * Math.PI * markHz / AfskDemodulator.SampleRate;
        var spaceStep = 2 * Math.PI * spaceHz / AfskDemodulator.SampleRate;
        _markCosStep = Math.Cos(markStep);
        _markSinStep = Math.Sin(markStep);
        _spaceCosStep = Math.Cos(spaceStep);
        _spaceSinStep = Math.Sin(spaceStep);
        _discriminatorThreshold = (float)((Math.Cos(2 * Math.PI * markHz * DiscriminatorDelay / AfskDemodulator.SampleRate)
                                           + Math.Cos(2 * Math.PI * spaceHz * DiscriminatorDelay / AfskDemodulator.SampleRate)) / 2);
        _correlators = correlatorWindows
            .Select((w, idx) =>
            {
                var cutoff = postLpfCutoffsHz is not null && idx < postLpfCutoffsHz.Count
                    ? postLpfCutoffsHz[idx]
                    : (idx == 0 ? 1600.0 : 0.0);
                return new AfskDemodulator.Correlator(w, cutoff);
            })
            .ToArray();
    }

    public double MarkHz { get; }
    public double SpaceHz { get; }

    public int CorrelatorCount => _correlators.Length;

    public float Discriminator { get; private set; }

    public float MarkAmplitude(int correlator) => _correlators[correlator].Mark;
    public float SpaceAmplitude(int correlator) => _correlators[correlator].Space;
    public float AgcOutput(int correlator) => _correlators[correlator].AgcOutput;

    public void Reset()
    {
        foreach (var c in _correlators)
            c.Reset();
        Array.Clear(_discDelay);
        _discDelayIndex = 0;
        _discProduct.Reset();
        _discPower.Reset();
        _discLpf.Reset();
        Discriminator = 0;
        _markRe = 1.0;
        _markIm = 0.0;
        _spaceRe = 1.0;
        _spaceIm = 0.0;
        _ncoCounter = 0;
    }

    public void ProcessFiltered(float x)
    {
        var im = (float)(x * _markRe);
        var qm = (float)(x * _markIm);
        var isp = (float)(x * _spaceRe);
        var qs = (float)(x * _spaceIm);

        var nextMarkRe = _markRe * _markCosStep - _markIm * _markSinStep;
        _markIm = _markRe * _markSinStep + _markIm * _markCosStep;
        _markRe = nextMarkRe;

        var nextSpaceRe = _spaceRe * _spaceCosStep - _spaceIm * _spaceSinStep;
        _spaceIm = _spaceRe * _spaceSinStep + _spaceIm * _spaceCosStep;
        _spaceRe = nextSpaceRe;

        if ((++_ncoCounter & 255) == 0)
        {
            var mNorm = 1.5 - 0.5 * (_markRe * _markRe + _markIm * _markIm);
            _markRe *= mNorm;
            _markIm *= mNorm;
            var sNorm = 1.5 - 0.5 * (_spaceRe * _spaceRe + _spaceIm * _spaceIm);
            _spaceRe *= sNorm;
            _spaceIm *= sNorm;
        }

        foreach (var c in _correlators)
            c.Push(im, qm, isp, qs);

        var delayed = _discDelay[_discDelayIndex];
        _discDelay[_discDelayIndex] = x;
        _discDelayIndex = (_discDelayIndex + 1) % DiscriminatorDelay;
        var product = _discProduct.Push(x * delayed);
        var power = _discPower.Push(x * x);
        var rawDisc = power > 1e-9f ? product / power - _discriminatorThreshold : 0f;
        Discriminator = _discLpf.Push(rawDisc);
    }
}
