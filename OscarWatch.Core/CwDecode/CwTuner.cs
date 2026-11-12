namespace OscarWatch.Core.CwDecode;

/// <summary>
/// Picks the slice of audio the model hears: band-passes the model's band moved by
/// <see cref="OffsetHz"/>, then shifts it back down, so a note at centre plus offset is decoded at
/// the centre. The band-pass stops audio below the shift folding back into the band.
/// </summary>
public sealed class CwTuner
{
    public const double MinOffsetHz = -550;
    public const double MaxOffsetHz = 1000;
    private const int Taps = 255;
    private const double EdgeMarginHz = 100;
    private const double RedesignStepHz = 2;

    private readonly int _sampleRate;
    private readonly double _bandLowHz;
    private readonly double _bandHighHz;
    private readonly double[] _taps = new double[Taps];
    private readonly double[] _history = new double[Taps * 2];
    private readonly CwFrequencyShifter _shifter;
    private int _pos;
    private double _designedOffset = double.NaN;

    public CwTuner(int sampleRate, double bandLowHz, double bandHighHz)
    {
        _sampleRate = sampleRate;
        _bandLowHz = bandLowHz;
        _bandHighHz = bandHighHz;
        _shifter = new CwFrequencyShifter(sampleRate);
    }

    public CwTuner(int sampleRate, CwModelMetadata metadata)
        : this(sampleRate, metadata.SpectrogramMinFreqHz, metadata.SpectrogramMaxFreqHz)
    {
    }

    /// <summary>Middle of the band the model reads.</summary>
    public double CentreHz => (_bandLowHz + _bandHighHz) / 2;

    /// <summary>Hertz above the band centre of the note to decode. Clamped to the waterfall.</summary>
    public double OffsetHz { get; set; }

    public static double ClampOffset(double offsetHz) =>
        double.IsFinite(offsetHz) ? Math.Clamp(offsetHz, MinOffsetHz, MaxOffsetHz) : 0;

    public void Process(List<float> samples)
    {
        var offset = ClampOffset(OffsetHz);
        if (!(Math.Abs(offset - _designedOffset) < RedesignStepHz))
            Design(offset);

        for (var s = 0; s < samples.Count; s++)
        {
            _history[_pos] = samples[s];
            _history[_pos + Taps] = samples[s];
            _pos = (_pos + 1) % Taps;

            var acc = 0.0;
            for (var k = 0; k < Taps; k++)
                acc += _taps[k] * _history[_pos + k];
            samples[s] = (float)acc;
        }

        _shifter.ShiftHz = -offset;
        _shifter.Process(samples);
    }

    /// <summary>Blackman-windowed band-pass (or low-pass when the band reaches down to 0 Hz).</summary>
    private void Design(double offset)
    {
        _designedOffset = offset;
        var high = Math.Min(_sampleRate / 2.0 - 50, _bandHighHz + offset + EdgeMarginHz);
        var low = _bandLowHz + offset - EdgeMarginHz;
        var mid = (Taps - 1) / 2;
        for (var i = 0; i < Taps; i++)
        {
            var n = i - mid;
            var window = 0.42
                - 0.5 * Math.Cos(2 * Math.PI * i / (Taps - 1))
                + 0.08 * Math.Cos(4 * Math.PI * i / (Taps - 1));
            var response = LowPass(n, high) - (low > EdgeMarginHz ? LowPass(n, low) : 0);
            _taps[i] = response * window;
        }
    }

    private double LowPass(int n, double cutoffHz)
    {
        var fc = cutoffHz / _sampleRate;
        return n == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * n) / (Math.PI * n);
    }
}
