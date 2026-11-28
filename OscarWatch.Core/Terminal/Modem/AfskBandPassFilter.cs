namespace OscarWatch.Core.Terminal.Modem;

/// <summary>
/// Band-pass for 1200 baud AFSK at 12 kHz (650 to 2750 Hz). Shared by all tone-offset decoder banks so
/// residual FM mistune appears as tone shift, not a new DC component, with steeper 61-tap skirts to keep
/// noise bandwidth tight while rejecting hum, CTCSS tones, and 3rd harmonics (3600 Hz).
/// </summary>
public sealed class AfskBandPassFilter
{
    private const int Taps = 61;

    private static readonly float[] Coefficients = DesignBandPass(Taps, 650, 2750);

    private readonly float[] _history = new float[Taps * 2];
    private int _index;

    public float Last { get; private set; }

    public void Reset()
    {
        Array.Clear(_history);
        _index = 0;
        Last = 0;
    }

    public float Process(float sample)
    {
        _index = (_index + Taps - 1) % Taps;
        _history[_index] = sample;
        _history[_index + Taps] = sample;
        var x = 0f;
        for (var i = 0; i < Taps; i++)
            x += Coefficients[i] * _history[_index + i];
        Last = x;
        return x;
    }

    internal static float[] DesignBandPass(int taps, double lowHz, double highHz)
    {
        var h = new float[taps];
        var centre = (taps - 1) / 2.0;
        var fl = lowHz / AfskDemodulator.SampleRate;
        var fh = highHz / AfskDemodulator.SampleRate;
        var raw = new double[taps];
        for (var i = 0; i < taps; i++)
        {
            var x = i - centre;
            double LowPass(double fc) => Math.Abs(x) < 1e-9 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
            var w = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (taps - 1));
            raw[i] = (LowPass(fh) - LowPass(fl)) * w;
        }

        var re = 0.0;
        var im = 0.0;
        var omega = 2 * Math.PI * 1700 / AfskDemodulator.SampleRate;
        for (var i = 0; i < taps; i++)
        {
            re += raw[i] * Math.Cos(omega * i);
            im += raw[i] * Math.Sin(omega * i);
        }

        var peak = Math.Sqrt(re * re + im * im);
        for (var i = 0; i < taps; i++)
            h[i] = (float)(raw[i] / peak);
        return h;
    }
}
