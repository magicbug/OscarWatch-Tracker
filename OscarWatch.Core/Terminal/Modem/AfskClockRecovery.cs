namespace OscarWatch.Core.Terminal.Modem;

/// <summary>
/// Digital PLL bit clock. A signed 32-bit counter advances by 2^32·baud/rate per sample and a bit is
/// sampled when it wraps. Each tone transition pulls the counter towards zero, harder while searching
/// than once frames are being received, so it locks within a few flags and still follows a
/// sender or soundcard clock about 1% off.
/// </summary>
public sealed class AfskClockRecovery
{
    public const double LockedInertia = 0.74;
    public const double SearchingInertia = 0.50;

    private readonly int _step;
    private readonly double _lockedInertia;
    private readonly double _searchingInertia;
    private int _pll;
    private bool _prevPositive;
    private bool _lastTone;

    public AfskClockRecovery(
        int sampleRate,
        int baud = 1200,
        double lockedInertia = LockedInertia,
        double searchingInertia = SearchingInertia)
    {
        _step = (int)Math.Round(4294967296.0 * baud / sampleRate);
        _lockedInertia = lockedInertia;
        _searchingInertia = searchingInertia;
    }

    /// <summary>The signed demodulator value at the most recent bit sample (sign = tone, magnitude = confidence).</summary>
    public float LastSampledDemod { get; private set; }

    public void Reset()
    {
        _pll = 0;
        _prevPositive = false;
        _lastTone = false;
        LastSampledDemod = 0;
    }

    /// <summary>Returns -1 when no bit is due, otherwise the NRZI-decoded bit (1 when the tone did not change).</summary>
    public int Push(float demod, bool locked)
    {
        var result = -1;
        var previous = _pll;
        _pll = unchecked(_pll + _step);
        if (previous > 0 && _pll < 0)
        {
            var tone = demod > 0;
            LastSampledDemod = demod;
            result = tone == _lastTone ? 1 : 0;
            _lastTone = tone;
        }

        var positive = demod > 0;
        if (positive != _prevPositive)
        {
            _pll = (int)(_pll * (locked ? _lockedInertia : _searchingInertia));
            _prevPositive = positive;
        }

        return result;
    }
}
