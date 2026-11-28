namespace OscarWatch.Core.Terminal.Modem;

/// <summary>
/// Front end at 12 kHz: band-pass prefilter (removes the DC shift that residual Doppler leaves on FM
/// audio, hum and hiss), quadrature tone correlators at 1200 and 2200 Hz for each window length,
/// a per-tone AGC, and a delay-line discriminator for diversity. Positive outputs mean mark.
/// </summary>
public sealed class AfskDemodulator
{
    public const int SampleRate = 12000;
    public const int LoTableLength = 60;

    private readonly AfskBandPassFilter _filter = new();
    private readonly AfskToneDemodulator _tone;

    public AfskDemodulator(IReadOnlyList<int> correlatorWindows)
        : this(1200, 2200, correlatorWindows)
    {
    }

    public AfskDemodulator(double markHz, double spaceHz, IReadOnlyList<int> correlatorWindows) =>
        _tone = new AfskToneDemodulator(markHz, spaceHz, correlatorWindows);

    public int CorrelatorCount => _tone.CorrelatorCount;

    /// <summary>Band-passed input, for level and DCD diagnostics.</summary>
    public float Filtered => _filter.Last;

    public float Discriminator => _tone.Discriminator;

    public float MarkAmplitude(int correlator) => _tone.MarkAmplitude(correlator);
    public float SpaceAmplitude(int correlator) => _tone.SpaceAmplitude(correlator);

    /// <summary>Mark minus space after each tone has its own AGC, so twist is removed before slicing.</summary>
    public float AgcOutput(int correlator) => _tone.AgcOutput(correlator);

    public void Reset()
    {
        _filter.Reset();
        _tone.Reset();
    }

    public void Process(float sample)
    {
        var x = _filter.Process(sample);
        _tone.ProcessFiltered(x);
    }

    internal sealed class Correlator
    {
        private const float AgcFastAttack = 0.50f;
        private const float AgcSlowDecay = 0.0025f;

        private readonly MovingSum _im;
        private readonly MovingSum _qm;
        private readonly MovingSum _is;
        private readonly MovingSum _qs;
        private readonly BiquadLowPass? _markLpf;
        private readonly BiquadLowPass? _spaceLpf;
        private readonly Agc _markAgc = new();
        private readonly Agc _spaceAgc = new();

        public Correlator(int window, double postLpfCutoffHz = 1600)
        {
            _im = new MovingSum(window);
            _qm = new MovingSum(window);
            _is = new MovingSum(window);
            _qs = new MovingSum(window);
            if (postLpfCutoffHz > 0)
            {
                _markLpf = new BiquadLowPass(postLpfCutoffHz, SampleRate);
                _spaceLpf = new BiquadLowPass(postLpfCutoffHz, SampleRate);
            }
        }

        public float Mark { get; private set; }
        public float Space { get; private set; }
        public float AgcOutput { get; private set; }

        public void Push(float im, float qm, float isp, float qs)
        {
            var a = _im.Push(im);
            var b = _qm.Push(qm);
            var c = _is.Push(isp);
            var d = _qs.Push(qs);
            var mark = MathF.Sqrt(a * a + b * b);
            var space = MathF.Sqrt(c * c + d * d);
            Mark = _markLpf is not null ? _markLpf.Push(mark) : mark;
            Space = _spaceLpf is not null ? _spaceLpf.Push(space) : space;
            AgcOutput = _markAgc.Push(Mark) - _spaceAgc.Push(Space);
        }

        public void Reset()
        {
            _im.Reset();
            _qm.Reset();
            _is.Reset();
            _qs.Reset();
            _markLpf?.Reset();
            _spaceLpf?.Reset();
            _markAgc.Reset();
            _spaceAgc.Reset();
            Mark = Space = AgcOutput = 0;
        }

        private sealed class Agc
        {
            private float _peak;
            private float _valley;

            public float Push(float x)
            {
                _peak = x >= _peak ? x * AgcFastAttack + _peak * (1 - AgcFastAttack) : x * AgcSlowDecay + _peak * (1 - AgcSlowDecay);
                _valley = x <= _valley ? x * AgcFastAttack + _valley * (1 - AgcFastAttack) : x * AgcSlowDecay + _valley * (1 - AgcSlowDecay);
                var span = _peak - _valley;
                return span > 1e-9f ? (x - 0.5f * (_peak + _valley)) / span : 0f;
            }

            public void Reset() => _peak = _valley = 0;
        }
    }

    /// <summary>2nd-order Butterworth low-pass filter (Direct Form II transposed) for post-detection smoothing.</summary>
    internal sealed class BiquadLowPass
    {
        private readonly float _b0;
        private readonly float _b1;
        private readonly float _b2;
        private readonly float _a1;
        private readonly float _a2;
        private float _z1;
        private float _z2;

        public BiquadLowPass(double cutoffHz, double sampleRate)
        {
            var k = Math.Tan(Math.PI * cutoffHz / sampleRate);
            var k2 = k * k;
            const double invQ = 1.4142135623730951; // sqrt(2) for Butterworth
            var norm = 1.0 / (1.0 + invQ * k + k2);
            _b0 = (float)(k2 * norm);
            _b1 = (float)(2.0 * k2 * norm);
            _b2 = _b0;
            _a1 = (float)(2.0 * (k2 - 1.0) * norm);
            _a2 = (float)((1.0 - invQ * k + k2) * norm);
        }

        public float Push(float x)
        {
            var y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }

        public void Reset() => _z1 = _z2 = 0;
    }

    internal sealed class MovingSum
    {
        private readonly float[] _buffer;
        private int _index;
        private double _sum;

        public MovingSum(int length) => _buffer = new float[Math.Max(1, length)];

        public float Push(float x)
        {
            _sum += x - _buffer[_index];
            _buffer[_index] = x;
            _index = (_index + 1) % _buffer.Length;
            return (float)_sum;
        }

        public void Reset()
        {
            Array.Clear(_buffer);
            _index = 0;
            _sum = 0;
        }
    }
}
